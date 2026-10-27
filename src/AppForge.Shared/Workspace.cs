using System;
using System.IO;

namespace AppForge.Shared
{
    /// <summary>
    /// 工作区目录布局（02 架构 §8.2）：
    ///   data\index.json / settings.json / secrets.dat / apps\&lt;id&gt;\ / drafts\ / backups\ / audit\
    ///   webview\（WebView2 用户数据）  ui\（Shell UI 解包缓存）  logs\
    /// </summary>
    public sealed class Workspace
    {
        public const string PortableFlag = "portable.flag";

        public string Root { get; private set; }
        public bool Portable { get; private set; }

        public Workspace(string root, bool portable)
        {
            Root = Path.GetFullPath(root);
            Portable = portable;
        }

        public string DataDir { get { return Path.Combine(Root, "data"); } }
        public string AppsDir { get { return Path.Combine(DataDir, "apps"); } }
        public string DraftsDir { get { return Path.Combine(DataDir, "drafts"); } }
        public string TrashDir { get { return Path.Combine(DataDir, "trash"); } }
        public string BackupsDir { get { return Path.Combine(DataDir, "backups"); } }
        public string AuditDir { get { return Path.Combine(DataDir, "audit"); } }
        public string IndexPath { get { return Path.Combine(DataDir, "index.json"); } }
        public string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }
        public string SecretsPath { get { return Path.Combine(DataDir, "secrets.dat"); } }
        public string WebViewDir { get { return Path.Combine(Root, "webview"); } }
        public string UiCacheDir { get { return Path.Combine(Root, "ui"); } }
        public string LogsDir { get { return Path.Combine(Root, "logs"); } }
        public string PerfLogPath { get { return Path.Combine(LogsDir, "perf.jsonl"); } }

        public string AppDir(string appId) { return Path.Combine(AppsDir, appId); }
        public string AppMetaPath(string appId) { return Path.Combine(AppDir(appId), "app.json"); }
        public string VersionsDir(string appId) { return Path.Combine(AppDir(appId), "versions"); }

        public string VersionDir(string appId, int seq)
        {
            return Path.Combine(VersionsDir(appId), "v" + seq.ToString("0000"));
        }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(AppsDir);
            Directory.CreateDirectory(DraftsDir);
            Directory.CreateDirectory(TrashDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(AuditDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(WebViewDir);
            Directory.CreateDirectory(UiCacheDir);
        }

        /// <summary>
        /// 真·可写探测：建目录 → **写一个测试文件** → 删掉。
        ///
        /// 为什么不能只测 Directory.CreateDirectory：
        /// 安全软件（火绒/360 的文件保护、Windows Defender 的「受控文件夹访问」）
        /// 经常**放行"建目录"、拦截"建文件"**。只测建目录会得出"这个目录可用"的错误结论，
        /// 结果就是程序选了个写不进去的目录，等到第一次保存时才炸——
        /// 用户看到的是"对路径 xxx\.write-probe 的访问被拒绝"，然后程序直接起不来。
        ///
        /// 探测文件名故意不用点开头的隐藏文件（`.write-probe` 这类命名在部分安全软件里更容易被判可疑）。
        /// </summary>
        public void ProbeWritable()
        {
            EnsureLayout();

            // 文件名带随机后缀：两个入口/两个自检同时探测时不会互相占用同一个文件，
            // 否则会误报"正由另一进程使用"并把可用目录判成不可写。
            var probe = Path.Combine(Root, "appforge-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(probe, "ok", new System.Text.UTF8Encoding(false));
            }
            finally
            {
                JsonStore.TryDelete(probe);
            }
        }

        /// <summary>解析数据目录：--data-dir 参数 &gt; 便携模式（exe 同目录 portable.flag）&gt; %LOCALAPPDATA%\AppForge</summary>
        public static Workspace Resolve(string overrideDir, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return new Workspace(overrideDir, false);

            var portableFlag = Path.Combine(exeDir, PortableFlag);
            if (File.Exists(portableFlag))
                return new Workspace(Path.Combine(exeDir, "data"), true);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new Workspace(Path.Combine(local, "AppForge"), false);
        }
    }

    /// <summary>路径安全守卫：所有来自模型/前端的相对路径都必须经过它。</summary>
    public static class PathGuard
    {
        public static string ResolveWithin(string rootDir, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new PathGuardException("路径为空");
            var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();

            if (rel.StartsWith(Path.DirectorySeparatorChar.ToString())) throw new PathGuardException("不接受绝对路径");
            if (rel.Length > 1 && rel[1] == ':') throw new PathGuardException("不接受盘符路径");

            var rootFull = EnsureTrailingSeparator(Path.GetFullPath(rootDir));
            var candidate = Path.GetFullPath(Path.Combine(rootFull, rel));
            var candidateWithSep = EnsureTrailingSeparator(candidate);

            if (!candidateWithSep.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new PathGuardException("路径越界：" + relativePath);

            return candidate;
        }

        public static bool IsWithin(string rootDir, string candidatePath)
        {
            try
            {
                var rootFull = EnsureTrailingSeparator(Path.GetFullPath(rootDir));
                var candidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));
                return candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string EnsureTrailingSeparator(string p)
        {
            return p.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? p
                : p + Path.DirectorySeparatorChar;
        }
    }

    public sealed class PathGuardException : Exception
    {
        public PathGuardException(string message) : base(message) { }
    }

    /// <summary>落盘工具：一律"临时文件 + 原子替换"，杜绝半截文件。</summary>
    public static class JsonStore
    {
        public static void WriteAtomic(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));

            if (File.Exists(path))
            {
                var bak = path + ".bak";
                try
                {
                    File.Replace(tmp, path, bak, true);
                    TryDelete(bak);
                }
                catch (Exception)
                {
                    TryDelete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        public static string ReadOrNull(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 忽略 */ }
        }

        public static long DirectoryBytes(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 忽略 */ }
            }
            return total;
        }
    }
}
