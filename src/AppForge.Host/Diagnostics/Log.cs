using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AppForge.Shared;

namespace AppForge.Host.Diagnostics
{
    /// <summary>轻量日志：写 logs\app-YYYYMMDD.log；性能打点写 logs\perf.jsonl。</summary>
    public sealed class Log
    {
        private readonly string _dir;
        private readonly object _gate = new object();

        public Log(string logsDir)
        {
            _dir = logsDir;
            try { Directory.CreateDirectory(_dir); }
            catch { /* 忽略 */ }
        }

        private string CurrentFile
        {
            get { return Path.Combine(_dir, "app-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"); }
        }

        public void Info(string message) { Write("INFO ", message, null); }
        public void Warn(string message) { Write("WARN ", message, null); }
        public void Error(string message, Exception ex) { Write("ERROR", message, ex); }

        private void Write(string level, string message, Exception ex)
        {
            lock (_gate)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ');
                    sb.Append(level).Append(' ').Append(message);
                    if (ex != null) sb.AppendLine().Append("        ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    File.AppendAllText(CurrentFile, sb.ToString() + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志失败绝不影响主流程
                }
            }
        }

        public void Perf(string path, IDictionary<string, object> metrics)
        {
            lock (_gate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, JsonLite.Serialize(metrics) + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }
}
