using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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
                // 自我解除"来自 Internet"标记：SmartScreen 的「已保护你的电脑 / 发布者未知」
                // 只对带该标记的未签名程序弹，删掉标记后下次启动就不再提示。
                try
                {
                    var unblocked = AppForge.Shared.MotwCleaner.SelfUnblock();
                    if (unblocked > 0) WriteLog("MOTW cleared=" + unblocked);
                }
                catch
                {
                    // 只读目录/受保护位置：解不掉就算了，不能影响启动
                }

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
                var cacheRoot = ResolveCacheRoot(selfPath, payloadOffset, payloadLength);
                WriteLog("CACHE " + cacheRoot);
                var appExe = Path.Combine(cacheRoot, "AppForge.exe");
                var readyMarker = Path.Combine(cacheRoot, ".payload-ready");

                if (!File.Exists(appExe) || !File.Exists(readyMarker))
                {
                    WriteLog("EXTRACT begin -> " + cacheRoot);
                    // 上次可能解压到一半被中断（只有 exe、没有 dll）。先清掉残留再完整解压，
                    // 不能只凭 AppForge.exe 存在就当作载荷完整。
                    try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true); }
                    catch (Exception ex) { WriteLog("EXTRACT cleanup skip " + ex.Message); }
                    ExtractPayload(selfPath, payloadOffset, payloadLength, cacheRoot);
                    File.WriteAllText(readyMarker, Path.GetFileName(cacheRoot), Encoding.UTF8);
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

                // 未签名：尾标记就在文件最后 16 字节。
                if (TryDecodeFooter(stream, stream.Length - Magic.Length - 8, out payloadOffset, out payloadLength))
                    return true;

                // 已签名：Authenticode 会把证书块追加在文件末尾，尾标记被挤到证书块前面。
                var certOffset = TryGetCertificateTableOffset(stream);
                if (certOffset > Magic.Length + 8 &&
                    TryDecodeFooter(stream, certOffset - Magic.Length - 8, out payloadOffset, out payloadLength))
                    return true;

                // 个别签名工具会插入对齐填充：在证书块前 64KB 内反向找魔数，兼容这种布局。
                if (certOffset > Magic.Length + 8)
                {
                    var lowest = Math.Max(0, certOffset - 65536);
                    for (var pos = certOffset - Magic.Length; pos >= lowest; pos--)
                    {
                        if (TryDecodeFooter(stream, pos - 8, out payloadOffset, out payloadLength))
                            return true;
                    }
                }
                return false;
            }
        }

        /// <summary>从 footerOffset 读取 [payloadLength(8) + magic(8)]，并校验 payload 边界。</summary>
        private static bool TryDecodeFooter(FileStream stream, long footerOffset, out long payloadOffset, out long payloadLength)
        {
            payloadOffset = 0;
            payloadLength = 0;
            if (footerOffset < 0 || footerOffset + Magic.Length + 8 > stream.Length) return false;

            stream.Seek(footerOffset, SeekOrigin.Begin);
            var bytes = new byte[Magic.Length + 8];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) return false;
                read += n;
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (bytes[8 + i] != Magic[i]) return false;
            }

            payloadLength = BitConverter.ToInt64(bytes, 0);
            payloadOffset = footerOffset - payloadLength;
            return payloadOffset > 0 && payloadLength > 0 && payloadOffset + payloadLength == footerOffset;
        }

        /// <summary>
        /// 读取 PE OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_SECURITY]。
        /// 该目录的 VirtualAddress 实际是文件偏移（不是 RVA），指向 Authenticode 证书块。
        /// </summary>
        private static long TryGetCertificateTableOffset(FileStream stream)
        {
            try
            {
                if (stream.Length < 0x40) return -1;

                stream.Seek(0x3C, SeekOrigin.Begin);
                var peOffsetBytes = new byte[4];
                if (stream.Read(peOffsetBytes, 0, 4) != 4) return -1;
                var peOffset = BitConverter.ToInt32(peOffsetBytes, 0);
                if (peOffset <= 0 || (long)peOffset + 24 > stream.Length) return -1;

                stream.Seek(peOffset, SeekOrigin.Begin);
                var signature = new byte[4];
                if (stream.Read(signature, 0, 4) != 4) return -1;
                if (BitConverter.ToUInt32(signature, 0) != 0x00004550) return -1; // "PE\0\0"

                var coff = new byte[20];
                if (stream.Read(coff, 0, 20) != 20) return -1;
                var optionalSize = BitConverter.ToUInt16(coff, 16);
                var optionalOffset = (long)peOffset + 24;
                if (optionalSize < 64 || optionalOffset + optionalSize > stream.Length) return -1;

                stream.Seek(optionalOffset, SeekOrigin.Begin);
                var magicBytes = new byte[2];
                if (stream.Read(magicBytes, 0, 2) != 2) return -1;
                var magic = BitConverter.ToUInt16(magicBytes, 0);
                var dataDirectories = magic == 0x20B ? 112 : (magic == 0x10B ? 96 : -1);
                if (dataDirectories < 0) return -1;

                var securityEntryOffset = optionalOffset + dataDirectories + 4 * 8; // index 4
                if (securityEntryOffset + 8 > optionalOffset + optionalSize) return -1;
                stream.Seek(securityEntryOffset, SeekOrigin.Begin);
                var entry = new byte[8];
                if (stream.Read(entry, 0, 8) != 8) return -1;

                var fileOffset = BitConverter.ToUInt32(entry, 0);
                var size = BitConverter.ToUInt32(entry, 4);
                if (size == 0 || fileOffset == 0 || (long)fileOffset >= stream.Length) return -1;
                return fileOffset;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 选一个真正可写的解压目录：
        ///   环境变量覆盖 → %LOCALAPPDATA% → %TEMP% → 程序同目录（最后手段）
        /// 受限环境（沙箱/受控文件夹访问）下 LocalAppData 可能不可写，这里逐个试。
        /// </summary>
        private static string ResolveCacheRoot(string selfPath, long payloadOffset, long payloadLength)
        {
            var overrideDir = Environment.GetEnvironmentVariable("APPFORGE_PAYLOAD_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                EnsureWritable(overrideDir);
                return overrideDir;
            }

            var stamp = BuildStamp(selfPath, payloadOffset, payloadLength);
            WriteLog("STAMP " + stamp);
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
                    // 选好目录后，顺手清掉同一批位置里的历史载荷（详见 CleanupStalePayloads 的"绝不删数据"规则）
                    CleanupStalePayloads(candidate);
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

        /// <summary>
        /// 载荷目录的"指纹"：**版本号 + payload 长度**。
        ///
        /// 旧实现用的是"payload 长度 + exe 修改时间 Ticks"，而修改时间会随打包/复制/解压变化，
        /// 于是**每跑一次就多一个目录**（`474581-639283386958141212`、`476465-639286060426838264`…）。
        /// 换成版本+长度后：同一份构建反复运行复用同一个目录；只有真的换了构建才会产生新目录
        /// （这是必要的：正在运行的旧实例会锁住旧目录里的 dll，复用会撞文件锁）。
        /// </summary>
        private static string BuildStamp(string selfPath, long payloadOffset, long payloadLength)
        {
            var version = string.Empty;
            try { version = FileVersionInfo.GetVersionInfo(selfPath).FileVersion ?? string.Empty; }
            catch { /* 忽略 */ }

            version = (version ?? string.Empty).Trim();
            if (version.Length == 0) version = DefaultsVersion;

            // 版本形如 1.0.6.0，去掉末尾的 .0 更好看；同时过滤掉文件名里不允许的字符
            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                version = version.Replace(bad, '_');
            }
            return "v" + version + "-" + payloadLength.ToString() + "-" + PayloadHash(selfPath, payloadOffset, payloadLength);
        }

        /// <summary>
        /// 取载荷前 12 位 SHA256 作为指纹。只靠"版本号 + 长度"在补丁恰好保持同样大小时
        /// 会复用旧载荷；加入内容哈希后，任何代码变化都必然换目录，不会启动到旧文件。
        /// </summary>
        private static string PayloadHash(string selfPath, long payloadOffset, long payloadLength)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(payloadOffset, SeekOrigin.Begin);
                var buffer = new byte[81920];
                var remaining = payloadLength;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = stream.Read(buffer, 0, want);
                    if (read <= 0) break;
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                var hex = new StringBuilder(64);
                for (var i = 0; i < 6 && i < sha.Hash.Length; i++) hex.Append(sha.Hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        private const string DefaultsVersion = "dev";

        /// <summary>
        /// 清理历史载荷目录。
        /// 安全规则（宁可留垃圾，也绝不删数据）：
        ///   · 只处理**我们自己命名的**目录，且必须含 AppForge.exe（确认是解压出来的载荷）；
        ///   · 目录里只要出现过用户数据（index.json / apps 下有应用 / settings.json）就整目录保留；
        ///   · 删除失败（正被运行中的旧实例占用）就跳过，不报错。
        /// </summary>
        private static void CleanupStalePayloads(string currentDir)
        {
            var current = SafeFullPath(currentDir);
            if (current == null) return;

            var parents = new System.Collections.Generic.List<string>();
            var parent = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent)) parents.Add(parent);
            // 另外两个历史根目录也顺手清一下（曾经可写、现在被拦的情况会留下旧目录）
            try { parents.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppForge", "payload")); } catch { }
            try { parents.Add(Path.Combine(Path.GetTempPath(), "AppForge", "payload")); } catch { }

            foreach (var dir in parents)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }

                foreach (var sub in subs)
                {
                    var full = SafeFullPath(sub);
                    if (full == null) continue;
                    if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!LooksLikePayloadDir(full)) continue;

                    if (HasUserData(full))
                    {
                        WriteLog("CLEAN keep(has data) " + full);
                        continue;
                    }

                    try
                    {
                        Directory.Delete(full, true);
                        WriteLog("CLEAN removed " + full);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("CLEAN skip " + full + " → " + ex.Message);
                    }
                }
            }
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return null; }
        }

        /// <summary>是不是我们自己解压出来的载荷目录（有 AppForge.exe，且名字是 v1.0.6-478092 或旧格式 474581-639283360115818632）。</summary>
        private static bool LooksLikePayloadDir(string dir)
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, "AppForge.exe"))) return false;

                var name = Path.GetFileName(dir) ?? string.Empty;
                if (name.StartsWith("v", StringComparison.OrdinalIgnoreCase)) return true;

                var dash = name.IndexOf('-');
                if (dash <= 0 || dash >= name.Length - 1) return false;
                return AllDigits(name.Substring(0, dash)) && AllDigits(name.Substring(dash + 1)) &&
                       dash >= 5 && (name.Length - dash - 1) >= 10;
            }
            catch { return false; }
        }

        private static bool AllDigits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>这个载荷目录里有没有存过用户数据（存过就绝不删除）。</summary>
        private static bool HasUserData(string dir)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "data", "index.json"))) return true;
                if (File.Exists(Path.Combine(dir, "data", "settings.json"))) return true;
                if (File.Exists(Path.Combine(dir, "data", "data", "index.json"))) return true;

                var apps = Path.Combine(dir, "data", "data", "apps");
                if (Directory.Exists(apps) && Directory.GetDirectories(apps).Length > 0) return true;

                var drafts = Path.Combine(dir, "data", "data", "drafts");
                if (Directory.Exists(drafts) && Directory.GetDirectories(drafts).Length > 0) return true;
            }
            catch { /* 判断失败就当有数据，宁可保留 */ return true; }
            return false;
        }

        private static void EnsureWritable(string dir)
        {
            Directory.CreateDirectory(dir);
            // 两个启动器同时解压同一个载荷时，固定探针名会互相占用并误报“不可写”。
            var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
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
