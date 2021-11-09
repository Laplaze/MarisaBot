﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flurl;
using Flurl.Http;
using QQBOT.Core.MiraiHttp.Entity;
using QQBOT.Core.Plugin;
using QQBOT.EntityFrameworkCore;
using QQBOT.EntityFrameworkCore.Entity.Audit;

namespace QQBOT.Core.MiraiHttp
{
    public partial class MiraiHttpSession
    {
        private readonly string _serverAddress;
        private readonly long _qq;
        private readonly string _authKey;

        private string _session;
        
        private delegate Task MessageHandler(MiraiHttpSession session, Message message);
        private event MessageHandler OnFriendMessage;
        private event MessageHandler OnGroupMessage;
        private event MessageHandler OnTempMessage;
        private event MessageHandler OnStrangerMessage;

        private void CheckResponse(dynamic response)
        {
            if (response.code != 0)
            {
                throw new Exception($"[Code {response.code}] {response.msg}");
            }
        }

        public long Id => _qq;

        public MiraiHttpSession(string serverAddress, long qq, string authKey)
        {
            _serverAddress = serverAddress;
            _qq            = qq;
            _authKey       = authKey;
        }

        public void AddPlugin(PluginBase plugin)
        {
            OnFriendMessage   += plugin.FriendMessageHandler;
            OnGroupMessage    += plugin.GroupMessageHandler;
            OnTempMessage     += plugin.TempMessageHandler;
            OnStrangerMessage += plugin.StrangerMessageHandler;
        }

        public async Task Init()
        {
            // get session
            var login = await (await $"{_serverAddress}/verify".PostJsonAsync(new
                {
                    verifyKey = _authKey
                }))
                .GetJsonAsync();
            CheckResponse(login);

            _session = login.session;

            CheckResponse(await
                (await $"{_serverAddress}/bind".PostJsonAsync(new { sessionKey = _session, qq = _qq }))
            .GetJsonAsync());
        }

        public async Task Run()
        {
            while (true)
            {
                var msgCnt = await $"{_serverAddress}/countMessage".SetQueryParam("sessionKey", _session).GetJsonAsync();
                CheckResponse(msgCnt);

                if (msgCnt.data == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    continue;
                }

                var msg = await $"{_serverAddress}/fetchMessage"
                    .SetQueryParams(new
                    {
                        sessionKey = _session,
                        count = msgCnt.data
                    }).GetJsonAsync();
                CheckResponse(msg);

                var tasks = ((List<dynamic>)msg.data).Select(async m =>
                {
                    var log = new AuditLog
                    {
                        EventId   = Guid.NewGuid(),
                        EventType = m.type,
                        Message   = Newtonsoft.Json.JsonConvert.SerializeObject(m.messageChain),
                        Time      = DateTime.Now,
                    };

                    var message = new Message
                    {
                        MessageChain = new MessageChain(m.messageChain)
                    };

                    var handler = OnFriendMessage;

                    switch (m.type)
                    {
                        case "FriendMessage":
                            message.GroupInfo = null;
                            message.Sender = new MessageSenderInfo(m.sender.id, m.sender.nickname, m.sender.remark);

                            log.UserId = message.Sender.Id.ToString();
                            log.UserName = message.Sender.Name;
                            log.UserAlias = message.Sender.Remark;

                            handler = OnFriendMessage;
                            break;
                        case "StrangerMessage":
                            message.GroupInfo = null;
                            message.Sender = new MessageSenderInfo(m.sender.id, m.sender.nickname, m.sender.remark);

                            log.UserId = message.Sender.Id.ToString();
                            log.UserName = message.Sender.Name;
                            log.UserAlias = message.Sender.Remark;

                            handler = OnStrangerMessage;
                            break;
                        case "GroupMessage":
                            message.GroupInfo = new GroupInfo(m.sender.group.id, m.sender.group.name,
                                m.sender.group.permission);
                            message.Sender = new MessageSenderInfo(m.sender.id, m.sender.memberName,
                                permission: m.sender.permission);

                            log.UserId = message.Sender.Id.ToString();
                            log.UserName = message.Sender.Name;
                            log.UserAlias = message.Sender.Remark;
                            log.GroupName = message.GroupInfo?.Name;
                            log.GroupId = message.GroupInfo?.Id.ToString();
                            
                            handler = OnGroupMessage;
                            break;
                        case "TempMessage":
                            message.GroupInfo = new GroupInfo(m.sender.group.id, m.sender.group.name,
                                m.sender.group.permission);
                            message.Sender = new MessageSenderInfo(m.sender.id, m.sender.memberName,
                                permission: m.sender.permission);

                            log.UserId = message.Sender.Id.ToString();
                            log.UserName = message.Sender.Name;
                            log.UserAlias = message.Sender.Remark;
                            log.GroupName = message.GroupInfo?.Name;
                            log.GroupId = message.GroupInfo?.Id.ToString();

                            handler = OnTempMessage;
                            break;
                    }


                    await using var dbContext = new BotDbContext();
                    await dbContext.Logs.AddAsync(log);
                    await dbContext.SaveChangesAsync();

                    Console.WriteLine(log.Message.Length < 120
                        ? $"[{DateTime.Now:yyyy-MM-dd hh:mm:ss}][{m.type.PadLeft(15)}] {log.Message}"
                        : $"[{DateTime.Now:yyyy-MM-dd hh:mm:ss}][{m.type.PadLeft(15)}] {log.Message[..120]}...");

                    handler?.Invoke(this, message);
                });
                await Task.WhenAll(tasks);
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }
}
