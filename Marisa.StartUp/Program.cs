﻿using Marisa.Backend.GoCq;
using Marisa.Backend.Mirai;
using Marisa.BotDriver.DI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using NLog.Web;
using NuGet.Packaging;

namespace Marisa.StartUp;

public static class Program
{
    private static async Task Main(string[] args)
    {
        var useMirai = !(args.Length > 3 && args[3] == "gocq");

        // asp dotnet
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.ConfigLogger();
        builder.Services.AddRange(useMirai ? MiraiBackend.Config(Plugin.Utils.Assembly()) : GoCqBackend.Config(Plugin.Utils.Assembly()));
        builder.WebHost.UseUrls("http://localhost:14311");
        builder.Host.UseNLog();

        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();
        app.UseDeveloperExceptionPage();

        app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")),
            RequestPath  = ""
        });

        app.Services.GetService<DictionaryProvider>()!
            .Add("QQ", long.Parse(args[1]))
            .Add("ServerAddress", args[0])
            .Add("AuthKey", args[2]);

        app.MapGet("/", ctx =>
        {
            ctx.Response.Redirect("/index.html");
            return Task.CompletedTask;
        });
        app.MapFallbackToFile("index.html");

        // run
        await Task.WhenAll(app.RunAsync(), app.Services.GetService<BotDriver.BotDriver>()!.Invoke());
    }
}