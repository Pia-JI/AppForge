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
                    if (ex != null) AppendException(sb, ex);
                    File.AppendAllText(CurrentFile, sb.ToString() + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志失败绝不影响主流程
                }
            }
        }

        /// <summary>
        /// 写异常详情（含内部异常与调用栈）。排障时只有类型+消息往往不够——
        /// 例如 WebView2 的 COMException E_UNEXPECTED 必须看栈才知道是哪一步失败的。
        /// </summary>
        private static void AppendException(StringBuilder sb, Exception ex)
        {
            var current = ex;
            var depth = 0;
            while (current != null && depth < 4)
            {
                sb.AppendLine().Append("        ")
                  .Append(depth == 0 ? string.Empty : "→ " )
                  .Append(current.GetType().Name).Append(": ").Append(current.Message);

                var stack = current.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    foreach (var line in stack.Split('\n'))
                    {
                        var text = line.TrimEnd('\r').Trim();
                        if (text.Length == 0) continue;
                        sb.AppendLine().Append("          ").Append(TextUtil.Ellipsis(text, 300));
                    }
                }

                current = current.InnerException;
                depth++;
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
