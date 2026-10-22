using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AppForge.Shared;

namespace AppForge.Host.Runtime
{
    /// <summary>把某个版本的完整应用打包成 zip（含运行说明），用户可直接发给别人或备份。</summary>
    public static class AppExporter
    {
        public static string ExportZip(Workspace ws, AppMeta app, int seq, string versionDir, Manifest manifest)
        {
            if (!Directory.Exists(versionDir))
                throw new KeyNotFoundException("版本目录不存在：v" + seq.ToString("0000"));

            var exportDir = Path.Combine(ws.Root, "exports");
            Directory.CreateDirectory(exportDir);

            var safeName = SanitizeFileName(app.Name);
            var zipPath = Path.Combine(exportDir,
                safeName + "-v" + seq.ToString("0000") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories))
                {
                    var rel = file.Substring(versionDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                    archive.CreateEntryFromFile(file, safeName + "/" + rel, CompressionLevel.Optimal);
                }

                var entry = archive.CreateEntry(safeName + "/如何运行.md");
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(BuildReadme(app, manifest, seq));
                }
            }

            return zipPath;
        }

        private static string BuildReadme(AppMeta app, Manifest manifest, int seq)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + app.Name);
            sb.AppendLine();
            sb.AppendLine("> " + (app.Description ?? string.Empty));
            sb.AppendLine();
            sb.AppendLine("这是由 AppForge 生成的应用（版本 v" + seq.ToString("0000") + "）。");
            sb.AppendLine();
            sb.AppendLine("## 怎么运行");
            sb.AppendLine();
            sb.AppendLine("1. **直接双击** " + (manifest != null && !string.IsNullOrWhiteSpace(manifest.Entry) ? manifest.Entry : "index.html") + "，用浏览器打开即可使用（数据存在浏览器本地）。");
            sb.AppendLine("2. 或者在 AppForge 里导入该目录，作为本地应用管理（双击即开、带版本记录）。");
            sb.AppendLine();
            sb.AppendLine("## 文件说明");
            sb.AppendLine();
            sb.AppendLine("- `manifest.json`：应用清单（类型/入口/窗口尺寸/能力），AppForge 据此启动");
            sb.AppendLine("- `index.html`：应用入口，单文件含全部逻辑（无外部依赖，离线可用）");
            sb.AppendLine();
            sb.AppendLine("## 数据");
            sb.AppendLine();
            sb.AppendLine("应用数据保存在浏览器 localStorage 中，key 前缀为 `app:" + app.Id + ":`。");
            return sb.ToString();
        }

        public static string SanitizeFileName(string name)
        {
            var text = string.IsNullOrWhiteSpace(name) ? "app" : name.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars()) text = text.Replace(ch, '_');
            text = text.Replace(' ', '_');
            return text.Length > 40 ? text.Substring(0, 40) : text;
        }
    }
}
