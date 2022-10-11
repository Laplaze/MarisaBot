﻿using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using log4net;
using Marisa.Plugin.Shared.Configuration;
using Marisa.Plugin.Shared.Osu.Drawer;
using Marisa.Plugin.Shared.Osu.Entity.Score;
using Marisa.Utils;
using Marisa.Utils.Cacheable;

namespace Marisa.Plugin.Shared.Osu;

public static class PerformanceCalculator
{
    private static string PpCalculator => ConfigurationManager.Configuration.Osu.PpCalculator;

    private static readonly Dictionary<long, object> BeatmapDownloaderLocker = new();

    private static Func<long, string> BeatmapsetPath => beatmapsetId => Path.Join(OsuDrawerCommon.TempPath, "beatmap", beatmapsetId.ToString());

    private static string GetBeatmapPath(long beatmapsetId, string checksum)
    {
        var path = BeatmapsetPath(beatmapsetId);

        foreach (var f in Directory.GetFiles(path, "*.osu", SearchOption.AllDirectories))
        {
            var hash = File.ReadAllText(f).GetMd5Hash();

            if (hash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        throw new FileNotFoundException($"Can not find beatmap with MD5 {checksum}");
    }

    private static string GetBeatmapPath(Beatmap beatmap, bool retry = true)
    {
        var path = BeatmapsetPath(beatmap.BeatmapsetId);

        object l;
        string res;

        // 获取特定 beatmap set 的锁（没有的话创建一个）
        lock (BeatmapDownloaderLocker)
        {
            if (BeatmapDownloaderLocker.ContainsKey(beatmap.BeatmapsetId))
            {
                l = BeatmapDownloaderLocker[beatmap.BeatmapsetId];
            }
            else
            {
                l = BeatmapDownloaderLocker[beatmap.BeatmapsetId] = new object();
            }
        }

        // 套上这个锁，如果同时有两个下载，则会分别走 if 的两个分支
        lock (l)
        {
            if (Directory.Exists(path))
            {
                // 如果谱面更新了，这里会抛异常
                try
                {
                    res = GetBeatmapPath(beatmap.BeatmapsetId, beatmap.Checksum);
                }
                catch (FileNotFoundException)
                {
                    Directory.Delete(path, true);

                    // 重新下载一次，如果还找不到，那就不重试了
                    if (retry)
                    {
                        return GetBeatmapPath(beatmap, false);
                    }

                    throw;
                }
            }
            else
            {
                string download;
                try
                {
                    download = OsuApi.DownloadBeatmap(beatmap.BeatmapsetId, Path.GetDirectoryName(path)!).Result;
                }
                catch (Exception e)
                {
                    LogManager.GetLogger(nameof(PerformanceCalculator)).Error(e.ToString());
                    throw new Exception($"Network Error While Downloading Beatmap: {e.Message}");
                }

                try
                {
                    ZipFile.ExtractToDirectory(download, path);
                }
                catch (Exception e)
                {
                    LogManager.GetLogger(nameof(PerformanceCalculator)).Error(e.ToString());
                    throw new Exception($"A Error Occurred While Extracting Beatmap: {e.Message}");
                }

                File.Delete(download);

                // 删除除了谱面文件（.osu）以外的所有文件，从而减小体积
                Parallel.ForEach(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories), f =>
                {
                    if (f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)) return;

                    File.Delete(f);
                });

                res = GetBeatmapPath(beatmap.BeatmapsetId, beatmap.Checksum);
            }
        }

        // 我们不需要删除字典里的锁，因为下载的谱面总数不会特别巨大

        return res;
    }

    public static double GetStarRating(this OsuScore score)
    {
        var starRatingChangeMods = new[] { "ez", "hr", "fl", "dt", "ht", "nc" };
        var ruleSetChangeMods    = Enumerable.Range(1, 12).Select(i => $"{i}k").ToArray();

        var starRatingChanged = starRatingChangeMods.Any(m1 => score.Mods.Any(m2 => m1.Equals(m2, StringComparison.OrdinalIgnoreCase)));
        var ruleSetChanged    = ruleSetChangeMods.Any(m1 => score.Mods.Any(m2 => m1.Equals(m2, StringComparison.OrdinalIgnoreCase)));

        if (!starRatingChanged && !ruleSetChanged)
        {
            return score.Beatmap.StarRating;
        }

        var cachePath = Path.Join(OsuDrawerCommon.TempPath, $"StarRating-{score.Beatmap.Checksum}-{string.Join(",", score.Mods.OrderBy(x => x))}.txt");

        var starRating = new CacheableText(cachePath, () =>
        {
            string path;
            try
            {
                path = GetBeatmapPath(score.Beatmap);
            }
            catch (Exception e) when (e is FileNotFoundException or HttpRequestException)
            {
                return score.Beatmap.StarRating.ToString("F2");
            }

            var argument = "difficulty ";

            if (ruleSetChanged)
            {
                argument += "-r:3 ";
            }

            argument += $"\"{path}\" -j" + string.Join("", score.Mods
                // .Where(m => ruleSetChangeMode
                //     .All(m2 => !m2.Equals(m, StringComparison.OrdinalIgnoreCase)))
                .Select(m => $" -m {m}"));

            using var p = new Process();

            p.StartInfo.UseShellExecute        = false;
            p.StartInfo.CreateNoWindow         = true;
            p.StartInfo.FileName               = PpCalculator;
            p.StartInfo.Arguments              = argument;
            p.StartInfo.RedirectStandardOutput = true;

            p.Start();
            p.WaitForExit();

            var json = p.StandardOutput.ReadToEnd();

            var regex = new Regex(@"""star_rating"":(.*?),");
            return regex.Match(json).Groups[1].Value;
        }).Value;

        return Convert.ToDouble(starRating);
    }

    public static double GetPerformance(this OsuScore score)
    {
        if (score.Pp != null)
        {
            return (double)score.Pp;
        }

        string path;

        try
        {
            path = GetBeatmapPath(score.Beatmap);
        }
        catch (Exception e) when (e is FileNotFoundException or HttpRequestException)
        {
            return 0;
        }

        // TODO std taiko catch
        if (score.ModeInt != 3) return 0;

        var argument = score.ModeInt switch
        {
            3 => $"simulate mania \"{path}\" -s {score.Score} -j",
            _ => throw new NotImplementedException()
        };

        argument += string.Join("", score.Mods.Select(m => $" -m {m}"));

        using var p = new Process();

        p.StartInfo.UseShellExecute        = false;
        p.StartInfo.CreateNoWindow         = true;
        p.StartInfo.FileName               = PpCalculator;
        p.StartInfo.Arguments              = argument;
        p.StartInfo.RedirectStandardOutput = true;

        p.Start();
        p.WaitForExit();

        var json = p.StandardOutput.ReadToEnd();

        var regex = new Regex(@"""pp"":(.*?)}");

        return double.Parse(regex.Match(json).Groups[1].Value);
    }
}