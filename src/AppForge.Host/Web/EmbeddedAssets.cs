using System;
using System.IO;
using System.Reflection;
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

            var version = typeof(EmbeddedAssets).Assembly.GetName().Version.ToString();
            var target = Path.Combine(ws.UiCacheDir, "ui-" + version);
            if (File.Exists(Path.Combine(target, "index.html"))) return target;

            Directory.CreateDirectory(target);
            var asm = typeof(EmbeddedAssets).Assembly;
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
            return target;
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
