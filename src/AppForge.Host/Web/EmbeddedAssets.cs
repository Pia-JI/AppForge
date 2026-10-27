using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AppForge.Shared;

namespace AppForge.Host.Web
{
    /// <summary>
    /// Shell UI 资源：开发态直接用 exe 同目录的 wwwroot（改文件即生效），
    /// 发布态从嵌入资源解包到 ui\&lt;版本&gt;（支撑单文件 exe）。
    /// </summary>
    public sealed class EmbeddedAssets
    {
        private readonly string _diskRoot;

        public EmbeddedAssets(string exeDir)
        {
            var candidate = Path.Combine(exeDir, "wwwroot");
            _diskRoot = Directory.Exists(candidate) ? candidate : null;
        }

        public bool IsDevMode
        {
            get { return _diskRoot != null; }
        }

        /// <summary>返回可用于 SetVirtualHostNameToFolderMapping 的真实目录。</summary>
        public string EnsureFolder(Workspace ws)
        {
            if (_diskRoot != null) return _diskRoot;

            var asm = typeof(EmbeddedAssets).Assembly;
            var version = asm.GetName().Version.ToString();
            var stamp = UiStamp;

            // 目录名带内容指纹：嵌入的 UI 只要有一个字节变化就是新目录，
            // 不会再出现“版本号没变 → 复用旧缓存 → 新 exe 仍加载旧 JS”的问题。
            var target = Path.Combine(ws.UiCacheDir, "ui-" + version + "-" + stamp.Substring(0, 12));
            var readyFile = Path.Combine(target, ".ready");
            if (File.Exists(Path.Combine(target, "index.html")) && File.Exists(readyFile)) return target;

            // 上次解包中断（只有部分文件）→ 清掉重来
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
            catch
            {
                // 被占用时继续尝试就地覆盖；下面的逐个文件写入仍是幂等的
            }
            Directory.CreateDirectory(target);

            foreach (var name in asm.GetManifestResourceNames())
            {
                var normalized = name.Replace('\\', '/');
                if (!normalized.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = normalized.Substring(3);
                var full = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) continue;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        File.WriteAllBytes(full, buffer.ToArray());
                    }
                }
            }
            File.WriteAllText(readyFile, stamp, Encoding.UTF8);

            // 老方案的 ui-<版本> 目录可能装的是旧 UI；能删就删，删不掉也不影响
            try
            {
                var legacy = Path.Combine(ws.UiCacheDir, "ui-" + version);
                if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
            }
            catch
            {
                // 忽略
            }
            return target;
        }

        /// <summary>对全部 ui/ 嵌入资源做 SHA256，作为 UI 内容的指纹。</summary>
        private static string ComputeStamp(Assembly asm)
        {
            using (var sha = SHA256.Create())
            {
                var names = asm.GetManifestResourceNames();
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var normalized = name.Replace('\\', '/');
                    if (!normalized.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) continue;

                    var nameBytes = Encoding.UTF8.GetBytes(normalized);
                    sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                    using (var stream = asm.GetManifestResourceStream(name))
                    {
                        if (stream == null) continue;
                        var buffer = new byte[81920];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }
        private string _uiStamp;

        /// <summary>当前 exe 内嵌 UI 的内容指纹（与缓存目录名一致）。</summary>
        public string UiStamp
        {
            get
            {
                if (_uiStamp == null) _uiStamp = ComputeStamp(typeof(EmbeddedAssets).Assembly);
                return _uiStamp;
            }
        }

        /// <summary>
        /// UI 内容变化时返回 true：调用方据此清空 WebView2 的 HTTP/代码缓存。
        /// 否则即使磁盘上的 JS 已更新，URL 没变时 WebView2 仍可能继续用旧缓存。
        /// </summary>
        public bool ShouldClearWebCache(Workspace ws)
        {
            var stamp = IsDevMode ? ("dev-" + ComputeFolderStamp(_diskRoot)) : UiStamp;
            var marker = Path.Combine(ws.UiCacheDir, ".shell-ui-stamp");
            string previous = null;
            try
            {
                if (File.Exists(marker)) previous = File.ReadAllText(marker, Encoding.UTF8).Trim();
            }
            catch
            {
                // 读不到就当首次
            }
            if (string.Equals(previous, stamp, StringComparison.Ordinal)) return false;

            try
            {
                Directory.CreateDirectory(ws.UiCacheDir);
                File.WriteAllText(marker, stamp, Encoding.UTF8);
            }
            catch
            {
                // 写不了标记不影响本次清缓存
            }
            return true;
        }

        private static string ComputeFolderStamp(string root)
        {
            using (var sha = SHA256.Create())
            {
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var rel = file.Substring(root.Length).Replace('\\', '/');
                    var nameBytes = Encoding.UTF8.GetBytes(rel);
                    sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                    using (var fs = File.OpenRead(file))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                            sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }
        public string ReadText(string relativePath)
        {
            try
            {
                if (_diskRoot != null)
                {
                    var full = PathGuard.ResolveWithin(_diskRoot, relativePath);
                    return File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
                }

                var asm = typeof(EmbeddedAssets).Assembly;
                var wanted = "ui/" + relativePath.Replace('\\', '/').TrimStart('/');
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    using (var stream = asm.GetManifestResourceStream(name))
                    {
                        if (stream == null) return null;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                            return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return null;
        }
    }
}
