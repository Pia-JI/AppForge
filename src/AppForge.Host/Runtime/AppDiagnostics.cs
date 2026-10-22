using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AppForge.Shared;

namespace AppForge.Host.Runtime
{
    /// <summary>诊断信息与诊断包（排障时把日志/配置/依赖状态一次性打包）。</summary>
    public static class AppDiagnostics
    {
        public static IDictionary<string, object> BuildInfo(
            Workspace ws,
            AppRepository repo,
            VersionService versions,
            TrashService trash,
            string appVersion,
            IList<DepStatus> deps)
        {
            var apps = repo.List(null);
            var totalVersions = apps.Sum(a => versions.List(a.Id).Count);

            var info = JsonLite.New();
            info["appVersion"] = appVersion;
            info["dataDir"] = ws.Root;
            info["logsDir"] = ws.LogsDir;
            info["exportsDir"] = Path.Combine(ws.Root, "exports");
            info["os"] = Environment.OSVersion.VersionString;
            info["dotnet"] = Environment.Version.ToString();
            info["appCount"] = apps.Count;
            info["versionCount"] = totalVersions;
            info["trashCount"] = trash.List().Count;
            info["autoStart"] = AutoStart.IsEnabled();
            info["hotkey"] = "Ctrl+Alt+Space";

            var depList = new List<object>();
            foreach (var dep in deps) depList.Add(dep.ToJson());
            info["deps"] = depList;

            var appList = new List<object>();
            foreach (var app in apps)
            {
                var d = JsonLite.New();
                d["id"] = app.Id;
                d["name"] = app.Name;
                d["version"] = app.CurrentVersion;
                d["versions"] = versions.List(app.Id).Count;
                d["bytes"] = app.Bytes;
                d["lastOpenedAt"] = app.LastOpenedAt;
                appList.Add(d);
            }
            info["apps"] = appList;
            return info;
        }

        /// <summary>导出诊断包到 exports\appforge-diag-*.zip，返回路径。</summary>
        public static string ExportZip(Workspace ws, IDictionary<string, object> info)
        {
            var exportDir = Path.Combine(ws.Root, "exports");
            Directory.CreateDirectory(exportDir);
            var zipPath = Path.Combine(exportDir, "appforge-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteText(archive, "info.json", JsonLite.Serialize(info));
                WriteText(archive, "settings.json", JsonStore.ReadOrNull(ws.SettingsPath) ?? "{}");
                WriteText(archive, "index.json", JsonStore.ReadOrNull(ws.IndexPath) ?? "{}");

                // 最近两份应用日志
                try
                {
                    if (Directory.Exists(ws.LogsDir))
                    {
                        foreach (var logFile in Directory.GetFiles(ws.LogsDir, "app-*.log")
                                     .OrderByDescending(f => f, StringComparer.Ordinal)
                                     .Take(2))
                        {
                            archive.CreateEntryFromFile(logFile, "logs/" + Path.GetFileName(logFile), CompressionLevel.Optimal);
                        }
                    }
                }
                catch
                {
                    // 日志可选
                }

                // 性能打点只留最后 400 行
                try
                {
                    if (File.Exists(ws.PerfLogPath))
                    {
                        var lines = File.ReadAllLines(ws.PerfLogPath);
                        var tail = lines.Length > 400 ? lines.Skip(lines.Length - 400).ToArray() : lines;
                        WriteText(archive, "logs/perf-tail.jsonl", string.Join("\n", tail));
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            return zipPath;
        }

        private static void WriteText(ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(content ?? string.Empty);
            }
        }
    }
}
