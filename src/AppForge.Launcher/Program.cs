using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace AppForge.Launcher
{
    /// <summary>
    /// 单文件 exe 的自解压启动器：
    ///   本 exe = 启动器 + 追加的 payload.zip + 16 字节尾标记
    ///   → 首次运行解压到 %LOCALAPPDATA%\AppForge\payload\&lt;版本号&gt;\
    ///   → 启动真正的 AppForge.exe（参数原样透传）
    /// 升级时 payload 长度变化 ⇒ 新目录，不会撞上旧文件锁。
    /// </summary>
    internal static class Program
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AFSFX001");

        [STAThread]
        private static int Main(string[] args)
        {
            WriteLog("BOOT self=" + (Environment.GetCommandLineArgs()[0]) + " args=" + string.Join(" ", args) + " temp=" + Path.GetTempPath());
            try
            {
                var selfPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(selfPath) || !File.Exists(selfPath))
                    selfPath = Process.GetCurrentProcess().MainModule.FileName;

                long payloadOffset, payloadLength;
                if (!TryReadFooter(selfPath, out payloadOffset, out payloadLength))
                {
                    Fail("单文件包已损坏（找不到内置数据）。请重新下载 AppForge.exe。");
                    return 2;
                }

                WriteLog("FOOTER ok offset=" + payloadOffset + " len=" + payloadLength);
                var cacheRoot = ResolveCacheRoot(selfPath, payloadLength);
                WriteLog("CACHE " + cacheRoot);
                var appExe = Path.Combine(cacheRoot, "AppForge.exe");

                if (!File.Exists(appExe))
                {
                    WriteLog("EXTRACT begin -> " + cacheRoot);
                    ExtractPayload(selfPath, payloadOffset, payloadLength, cacheRoot);
                    WriteLog("EXTRACT ok");
                }

                if (!File.Exists(appExe))
                {
                    Fail("解压失败：找不到 AppForge.exe。");
                    return 3;
                }

                var arguments = BuildArguments(args);
                var startInfo = new ProcessStartInfo(appExe, arguments)
                {
                    WorkingDirectory = cacheRoot,
                    UseShellExecute = false
                };
                WriteLog("OK payload=" + cacheRoot + " args=" + arguments);
                WriteLog("LAUNCH " + appExe);
                Process.Start(startInfo);
                return 0;
            }
            catch (Exception ex)
            {
                Fail("启动失败：" + ex);
                return 1;
            }
        }

        private static bool TryReadFooter(string path, out long payloadOffset, out long payloadLength)
        {
            payloadOffset = 0;
            payloadLength = 0;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length < Magic.Length + 8) return false;

                stream.Seek(-(Magic.Length + 8), SeekOrigin.End);
                var lengthBytes = new byte[8];
                if (stream.Read(lengthBytes, 0, 8) != 8) return false;
                var magicBytes = new byte[Magic.Length];
                if (stream.Read(magicBytes, 0, Magic.Length) != Magic.Length) return false;

                for (var i = 0; i < Magic.Length; i++)
                {
                    if (magicBytes[i] != Magic[i]) return false;
                }

                payloadLength = BitConverter.ToInt64(lengthBytes, 0);
                payloadOffset = stream.Length - Magic.Length - 8 - payloadLength;
                return payloadOffset > 0 && payloadLength > 0;
            }
        }

        /// <summary>
        /// 选一个真正可写的解压目录：
        ///   环境变量覆盖 → %LOCALAPPDATA% → %TEMP% → 程序同目录（最后手段）
        /// 受限环境（沙箱/受控文件夹访问）下 LocalAppData 可能不可写，这里逐个试。
        /// </summary>
        private static string ResolveCacheRoot(string selfPath, long payloadLength)
        {
            var overrideDir = Environment.GetEnvironmentVariable("APPFORGE_PAYLOAD_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                EnsureWritable(overrideDir);
                return overrideDir;
            }

            var stamp = payloadLength.ToString() + "-" + new FileInfo(selfPath).LastWriteTimeUtc.Ticks.ToString();
            var exeDir = Path.GetDirectoryName(selfPath);

            var candidates = new System.Collections.Generic.List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppForge", "payload", stamp),
                Path.Combine(Path.GetTempPath(), "AppForge", "payload", stamp)
            };
            if (!string.IsNullOrEmpty(exeDir))
                candidates.Add(Path.Combine(exeDir, "AppForge-payload", stamp));

            var errors = new System.Text.StringBuilder();
            foreach (var candidate in candidates)
            {
                try
                {
                    EnsureWritable(candidate);
                    return candidate;
                }
                catch (Exception ex)
                {
                    errors.AppendLine("  · " + candidate + " → " + ex.Message);
                }
            }

            throw new IOException(
                "找不到可写的解压目录（可能被安全软件或组策略限制）。已尝试：\r\n" + errors +
                "\r\n可用环境变量 APPFORGE_PAYLOAD_DIR 指定一个可写目录后重试。");
        }

        private static void EnsureWritable(string dir)
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }

        /// <summary>
        /// 直接在内存里解压 payload（不写临时 zip）：
        /// 受限环境（沙箱/受控文件夹）下 %TEMP% 可能不可写，不能依赖它。
        /// </summary>
        private static void ExtractPayload(string sourcePath, long offset, long length, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            var payload = new byte[length];
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                source.Seek(offset, SeekOrigin.Begin);
                var read = 0;
                while (read < length)
                {
                    var want = (int)Math.Min(81920, length - read);
                    var n = source.Read(payload, read, want);
                    if (n <= 0) break;
                    read += n;
                }
            }

            var root = Path.GetFullPath(targetDir);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            using (var memory = new MemoryStream(payload, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项跳过

                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.GetFullPath(Path.Combine(rootPrefix, relative));
                    if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue; // 防路径穿越

                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }
        private static string BuildArguments(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (arg.IndexOf(' ') >= 0 || arg.IndexOf('\t') >= 0 || arg.IndexOf('"') >= 0)
                {
                    sb.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                }
                else
                {
                    sb.Append(arg);
                }
            }
            return sb.ToString();
        }

        private static void Fail(string message)
        {
            WriteLog("ERROR " + message);
            try
            {
                // CLI 模式（--xxx）不弹窗，避免阻塞脚本/自动化
                var cliMode = false;
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.StartsWith("--", StringComparison.Ordinal)) { cliMode = true; break; }
                }
                if (!cliMode)
                {
                    MessageBox.Show(message + "\r\n\r\n日志：%TEMP%\\AppForge\\launcher.log",
                        "AppForge 启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch
            {
                // 连对话框都弹不出来时放弃
            }
        }

        private static void WriteLog(string line)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine;
                var dirs = new System.Collections.Generic.List<string>
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"),
                    Path.Combine(Path.GetTempPath(), "AppForge"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppForge", "logs")
                };
                foreach (var dir in dirs)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.AppendAllText(Path.Combine(dir, "launcher.log"), stamp);
                        return;
                    }
                    catch
                    {
                        // 换下一个位置
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
