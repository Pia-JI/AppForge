using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Runtime;
using AppForge.Host.Windows;
using AppForge.Shared;

namespace AppForge.Host
{
    internal static class Program
    {
        /// <summary>
        /// 数据目录候选位置的探测结果（"路径 → ok" / "路径 → 失败原因"）。
        /// `--check` 会把它一起输出，用户报"数据目录不可写"时不用猜是哪一层被拦。
        /// </summary>
        internal static readonly List<string> DataDirProbeLog = new List<string>();

        /// <summary>
        /// 确保数据目录可写：默认位置 → %APPDATA% → %USERPROFILE% → exe 同目录 data → %TEMP%，逐个尝试。
        /// （受限环境/受控文件夹访问/安全软件拦截下 %LOCALAPPDATA% 可能不可写，不能让程序因此起不来）
        ///
        /// 判定标准是 <see cref="Workspace.ProbeWritable"/>：**真的写一个文件再删掉**。
        /// 只测 Directory.CreateDirectory 会漏掉"能建目录、不能建文件"的情况
        /// （火绒/360 的文件保护、Defender 的受控文件夹访问都是这个行为），
        /// 那会导致程序选中一个写不进去的目录，然后在启动检查里硬失败。
        ///
        /// 注意：**exe 同目录是单文件启动器的载荷缓存目录**（dist\AppForge-payload\&lt;指纹&gt;），
        /// 换版本就会换成新目录、旧载荷可能被清理 ⇒ 数据放进去等于随时可能丢。
        /// 所以 exe 同目录排在 %APPDATA% / %USERPROFILE% 之后，只作倒数第二选择。
        /// </summary>
        private static string EnsureWritableWorkspace(ref Workspace workspace, string exeDir, ref bool fallbackUsed, out string fallbackNote)
        {
            fallbackNote = null;
            var first = workspace;

            var candidates = new List<Workspace> { first };
            Action<string, bool> add = (root, portable) =>
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                string full;
                try { full = System.IO.Path.GetFullPath(root); }
                catch { return; }
                foreach (var existing in candidates)
                {
                    if (string.Equals(existing.Root, full, StringComparison.OrdinalIgnoreCase)) return;
                }
                candidates.Add(new Workspace(full, portable));
            };

            try { add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppForge"), false); }
            catch { /* 忽略 */ }
            try { add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppForge"), false); }
            catch { /* 忽略 */ }

            // 开发/绿色目录场景：两个入口的位置不一样
            //   · 启动AppForge.cmd → <repo>\src\AppForge.Host\bin\Release\AppForge.exe
            //   · dist\AppForge.exe → <repo>\dist\AppForge-payload\<指纹>\AppForge.exe
            // 但它们向上都能找到项目根目录（含 AppForge.sln）。这里用**同一份 <repo>\data**，
            // 避免"每个 exe 各存一份数据"——那正是"两个入口数据不一样"的直接原因。
            try
            {
                var repoRoot = FindRepoRoot(exeDir);
                if (!string.IsNullOrEmpty(repoRoot)) add(System.IO.Path.Combine(repoRoot, "data"), false);
            }
            catch { /* 忽略 */ }

            add(System.IO.Path.Combine(exeDir, "data"), true);
            try { add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppForge", "data"), true); }
            catch { /* 忽略 */ }

            var errors = new System.Text.StringBuilder();
            DataDirProbeLog.Clear();
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.ProbeWritable();
                    DataDirProbeLog.Add(candidate.Root + " → ok");
                    if (!string.Equals(candidate.Root, first.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackUsed = true;
                        fallbackNote = "首选 " + first.Root + " 写不进去（" + FirstError(errors) + "）";
                    }
                    workspace = candidate;
                    return null;
                }
                catch (Exception ex)
                {
                    DataDirProbeLog.Add(candidate.Root + " → " + ex.Message);
                    errors.AppendLine("  · " + candidate.Root + " → " + ex.Message);
                }
            }

            return "数据目录不可写，已尝试所有候选位置：\r\n" + errors +
                   "\r\n常见原因：火绒/360 的「文件保护」「受控文件夹访问」把程序挡在了写文件之前。\r\n" +
                   "处理：把 AppForge.exe 加入安全软件信任区，或用 --data-dir=<可写目录> 指定一个明确可写的位置启动。";
        }

        private const string MutexName = "Local\\AppForge.SingleInstance.v1";

        /// <summary>
        /// 从 exe 所在目录向上找项目根目录（标志：同目录下有 AppForge.sln）。
        /// 找不到就返回 null——发布出去的程序没有 sln，自然走后面的候选。
        /// </summary>
        private static string FindRepoRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                for (var i = 0; i < 8 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "AppForge.sln"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { /* 忽略 */ }
            return null;
        }

        /// <summary>从错误清单里摘出第一条（默认目录失败的原因），用于回退提示。</summary>
        private static string FirstError(System.Text.StringBuilder errors)
        {
            var text = (errors == null ? string.Empty : errors.ToString()).Trim();
            if (text.Length == 0) return "未知原因";
            var line = text.Split('\n')[0].Trim();
            var arrow = line.IndexOf('→');
            if (arrow >= 0) line = line.Substring(arrow + 1).Trim();
            return TextUtil.Ellipsis(line, 160);
        }

        /// <summary>
        /// 合并"散落在其它数据目录里的应用"到当前数据目录。
        ///
        /// 为什么会有多份：数据目录的解析规则是"能写就行"，而回退链包含"exe 同目录"，
        /// 于是不同入口各存一份（cmd 启动的 dev exe → bin\Release\data；单文件版 → 载荷目录），
        /// 用户看到就是"两个入口数据不一样"。统一指针（<see cref="DataDirPointer"/>）之后，
        /// 这里把其它位置的**应用**并过来，避免"换了个目录，应用全没了"。
        ///
        /// 关键设计：
        ///   · 来源 = 注册表登记过的所有数据目录 + 现场扫到的历史位置（载荷目录 / %LOCALAPPDATA%）；
        ///   · 只补"当前没有的 app id"，已存在的应用一律以本地为准（不覆盖用户刚改过的）；
        ///   · settings.json / secrets.dat 只在本地缺失时才拿（不覆盖当前的 API Key）；
        ///   · 合并到新应用后**备份并删除 index.json**，让 AppRepository 的 `Rebuild()` 按 apps\*\app.json
        ///     重建索引——否则会出现"文件在、索引里没有、界面上看不见"；
        ///   · 全程只复制不删除，任何一步失败都只是跳过，不影响启动。
        /// </summary>
        private static string MergeKnownDataDirs(Workspace target, Diagnostics.Log log)
        {
            try
            {
                if (target == null) return null;

                // 数据来源：① 注册表里记录过的所有数据目录；② 现场扫到的历史位置（载荷目录 / %LOCALAPPDATA%）
                var sources = new List<string>();
                foreach (var root in DataDirPointer.ReadKnown())
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    try { sources.Add(Path.GetFullPath(Path.Combine(root, "data"))); }
                    catch { /* 忽略 */ }
                }
                foreach (var dir in CollectLegacyDataDirs(target))
                {
                    if (!sources.Contains(dir)) sources.Add(dir);
                }

                var targetData = Path.GetFullPath(target.DataDir).TrimEnd(Path.DirectorySeparatorChar);
                var targetApps = Path.Combine(targetData, "apps");

                var copiedApps = 0;
                var copiedFiles = 0;
                var scanned = 0;
                foreach (var source in sources)
                {
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), targetData, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(source)) continue;
                    scanned++;

                    // ① 应用目录：只补"这里还没有"的 app id（同一个 app 以本地已有版本为准，避免覆盖用户刚改过的）
                    var sourceApps = Path.Combine(source, "apps");
                    if (Directory.Exists(sourceApps))
                    {
                        foreach (var appDir in Directory.GetDirectories(sourceApps))
                        {
                            var id = Path.GetFileName(appDir);
                            if (string.IsNullOrEmpty(id)) continue;
                            var dest = Path.Combine(targetApps, id);
                            if (Directory.Exists(dest)) continue;
                            try
                            {
                                CopyDirectoryRecursive(appDir, dest);
                                copiedApps++;
                            }
                            catch (Exception ex)
                            {
                                if (log != null) log.Warn("合并应用失败 " + id + "（" + source + "）：" + ex.Message);
                            }
                        }
                    }

                    // ② 配置与密钥：只在本地还没有时才拿（避免把用户当前的 API Key 换掉）
                    foreach (var name in new[] { "settings.json", "secrets.dat" })
                    {
                        var from = Path.Combine(source, name);
                        var to = Path.Combine(targetData, name);
                        if (!File.Exists(from) || File.Exists(to)) continue;
                        try
                        {
                            File.Copy(from, to, false);
                            copiedFiles++;
                        }
                        catch { /* 忽略 */ }
                    }
                }

                // 补进来新应用后，索引必须重建，否则"文件在、界面上看不见"。
                // 做法：备份并删掉 index.json，AppRepository.Load() 会从 apps\*\app.json 重建（这是既有的自愈路径）。
                if (copiedApps > 0 && File.Exists(target.IndexPath))
                {
                    try
                    {
                        var backup = target.IndexPath + ".merged-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Copy(target.IndexPath, backup, true);
                        File.Delete(target.IndexPath);
                    }
                    catch { /* 删不掉就让旧索引生效，下次再试 */ }
                }

                if (copiedApps == 0 && copiedFiles == 0)
                {
                    if (log != null && scanned > 0) log.Info("合并检查：扫到 " + scanned + " 个其它数据目录，没有需要补充的应用");
                    return null;
                }

                return "已合并其它数据目录：新增 " + copiedApps + " 个应用、补充 " + copiedFiles + " 个配置文件 → " + targetData +
                       (copiedApps > 0 ? "（索引将按 app.json 自动重建）" : string.Empty);
            }
            catch (Exception ex)
            {
                return "合并数据目录跳过（" + ex.Message + "）";
            }
        }

        /// <summary>递归复制目录（目标已存在的文件不覆盖）。</summary>
        private static void CopyDirectoryRecursive(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest, false);
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectoryRecursive(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// 找出所有"可能装着旧数据"的目录（返回的是**数据目录**，即 index.json/apps/ 所在的那层）。
        /// 覆盖三种历史形态：当前载荷目录、%LOCALAPPDATA%\AppForge、同级其它历史载荷指纹目录。
        /// </summary>
        private static List<string> CollectLegacyDataDirs(Workspace target)
        {
            var roots = new List<string>();   // Root 级（数据在 <root>\data）
            var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            roots.Add(Path.Combine(exeDir, "data"));          // Root=<exe>\data ⇒ 数据在 <exe>\data\data
            try
            {
                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppForge"));
            }
            catch { /* 忽略 */ }

            try
            {
                var parent = Path.GetDirectoryName(exeDir);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) &&
                    Path.GetFileName(parent).IndexOf("payload", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (var sibling in Directory.GetDirectories(parent))
                    {
                        if (string.Equals(sibling.TrimEnd(Path.DirectorySeparatorChar), exeDir, StringComparison.OrdinalIgnoreCase)) continue;
                        roots.Add(Path.Combine(sibling, "data"));
                    }
                }
            }
            catch { /* 忽略 */ }

            // 开发场景补充：项目的 dist\AppForge-payload\<指纹>\ 下一堆历史载荷。
            // （单文件版的载荷若落在 %TEMP%，上面的"同级目录"扫不到 dist 下的那些，这里按项目根目录补一遍）
            try
            {
                var repoRoot = FindRepoRoot(exeDir);
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    var distPayload = Path.Combine(repoRoot, "dist", "AppForge-payload");
                    if (Directory.Exists(distPayload))
                    {
                        foreach (var sibling in Directory.GetDirectories(distPayload))
                        {
                            roots.Add(Path.Combine(sibling, "data"));
                        }
                    }
                }
            }
            catch { /* 忽略 */ }

            var result = new List<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string dataDir;
                try { dataDir = Path.GetFullPath(Path.Combine(root, "data")); }
                catch { continue; }

                if (string.Equals(dataDir, target.DataDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(dataDir)) continue;
                if (!Directory.Exists(Path.Combine(dataDir, "apps")) && !File.Exists(Path.Combine(dataDir, "settings.json"))) continue;

                if (!result.Contains(dataDir)) result.Add(dataDir);
            }
            return result;
        }

        /// <summary>
        /// --open-folder=&lt;路径&gt;：只验证"打开目录"链路（外壳 → explorer → rundll32 → 剪贴板）。
        /// 用于在没有桌面会话的机器上排查 0xC0000142 这类环境问题。
        /// </summary>
        /// <summary>--unblock：把 exe 及同目录文件的"来自 Internet"标记清掉（SmartScreen 提示治理）。</summary>
        private static int RunUnblock(Diagnostics.Log log, int alreadyCleared)
        {
            var count = alreadyCleared + MotwCleaner.SelfUnblock();
            log.Info("--unblock 清除标记：" + count + " 个文件");
            ConsoleBridge.WriteLine("{\"mode\":\"unblock\",\"ok\":true,\"cleared\":" + count + "}");
            return 0;
        }

        private static int RunOpenFolder(string path, Diagnostics.Log log)
        {
            bool copied;
            IList<string> stages;
            string verifiedBy;
            var opened = Windows.FolderOpener.Open(path, out copied, out stages, out verifiedBy, log.Warn);

            var stageJson = new System.Text.StringBuilder("[");
            for (var i = 0; i < stages.Count; i++)
            {
                if (i > 0) stageJson.Append(',');
                stageJson.Append('"').Append(stages[i].Replace("\\", "/").Replace("\"", "'")).Append('"');
            }
            stageJson.Append(']');

            var json = "{\"mode\":\"open-folder\",\"ok\":" + (opened ? "true" : "false") +
                       ",\"copied\":" + (copied ? "true" : "false") +
                       ",\"elevated\":" + (Windows.FolderOpener.IsElevated() ? "true" : "false") +
                       ",\"openedBy\":\"" + (verifiedBy ?? string.Empty) + "\"" +
                       ",\"path\":\"" + (path ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"" +
                       ",\"stages\":" + stageJson + "}";
            ConsoleBridge.WriteLine(json);
            return opened ? 0 : 2;
        }

        private static int RunShellDiag(Diagnostics.Log log)
        {
            var d = Windows.FolderOpener.Diagnose();

            var json = new System.Text.StringBuilder("{");
            json.Append("\"mode\":\"shell-diag\"");
            json.Append(",\"user\":\"").Append((d.UserName ?? "").Replace("\\", "/").Replace("\"", "'")).Append('"');
            json.Append(",\"elevated\":").Append(d.Elevated ? "true" : "false");
            json.Append(",\"sessionId\":").Append(d.SessionId);
            json.Append(",\"windowsDir\":\"").Append((d.WindowsDir ?? "").Replace("\\", "/")).Append('"');
            json.Append(",\"explorerExists\":").Append(d.ExplorerExists ? "true" : "false");
            json.Append(",\"canSpawnProcess\":").Append(d.CanSpawnProcess ? "true" : "false");
            json.Append(",\"probeExitCode\":").Append(d.ProbeExitCode);
            json.Append(",\"canSpawnExplorer\":").Append(d.CanSpawnExplorer ? "true" : "false");
            json.Append(",\"explorerExitCode\":").Append(d.ExplorerExitCode);
            json.Append(",\"shellApiOk\":").Append(d.ShellApiOk ? "true" : "false");
            json.Append(",\"shellApiError\":\"").Append((d.ShellApiError ?? "").Replace("\\", "/").Replace("\"", "'")).Append('"');
            json.Append('}');

            ConsoleBridge.WriteLine(json.ToString());

            log.Info("--shell-diag：canSpawnProcess=" + d.CanSpawnProcess + "(" + d.ProbeExitCode + ")" +
                     " canSpawnExplorer=" + d.CanSpawnExplorer + "(" + d.ExplorerExitCode + ")" +
                     " shellApi=" + d.ShellApiOk + " elevated=" + d.Elevated + " user=" + d.UserName);
            return d.CanSpawnExplorer || d.ShellApiOk ? 0 : 2;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            var cli = CliArgs.Parse(args);
            if (cli.CliMode) ConsoleBridge.Ensure();

            // 所有模式都关掉系统级硬错误弹窗（子进程 0xC0000142 之类只留退出码，不再弹框）
            ProcessErrorMode.Apply();

            // 自我解除"来自 Internet"标记：带该标记的未签名程序每次启动都会被 SmartScreen 拦一下
            var unblocked = MotwCleaner.SelfUnblock();

            if (cli.Help)
            {
                ConsoleBridge.WriteLine(CliArgs.Usage());
                return 0;
            }

            var exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // 数据目录解析顺序：
            //   ① --data-dir（显式指定，最高优先级，且不写回指针）
            //   ② exe 同目录有 portable.flag ⇒ 便携模式（数据跟着程序走）
            //   ③ HKCU\Software\AppForge\DataDir（"约定的目录"，保证不同入口用同一份数据）
            //   ④ 默认 %LOCALAPPDATA%\AppForge
            // 没有 ③ 的话，回退链会把不同入口导向不同目录（cmd 启动的 dev exe 与 dist 单文件版各自一份），
            // 用户看到的就是"两个入口数据不一样"。
            var portableMode = false;
            try { portableMode = File.Exists(Path.Combine(exeDir, Workspace.PortableFlag)); }
            catch { /* 忽略 */ }

            var preferredDataDir = cli.DataDir;
            if (string.IsNullOrWhiteSpace(preferredDataDir) && !portableMode)
            {
                var remembered = DataDirPointer.Read();
                if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
                    preferredDataDir = remembered;
            }

            var workspace = Workspace.Resolve(preferredDataDir, exeDir);

            Mutex mutex = null;
            var isFirst = true;
            if (!cli.CliMode)
            {
                mutex = new Mutex(true, MutexName, out isFirst);
                if (!isFirst)
                {
                    // 第二次启动（双击 exe / 桌面快捷方式）：先尝试 WM_COPYDATA 激活已有窗口
                    // （窗口隐藏到托盘时 MainWindowHandle 为 0，所以用 EnumWindows 找窗口）
                    var activated = false;
                    try { activated = SingleInstance.ActivateExisting(cli.OpenApp); }
                    catch { /* 忽略 */ }

                    if (!activated)
                    {
                        // 兜底：写交接文件，由已运行实例的定时器取走
                        try
                        {
                            workspace.EnsureLayout();
                            OpenRequest.Write(workspace, cli.OpenApp);
                        }
                        catch { /* 交接失败不阻塞 */ }
                    }

                    try
                    {
                        var bootLog = new Log(workspace.LogsDir);
                        bootLog.Info("检测到已有实例：activated=" + activated +
                                     " openApp=" + (cli.OpenApp ?? "-"));
                    }
                    catch { /* 忽略 */ }

                    return 0;
                }
            }

            try
            {
                // 数据目录可能不可写（只读配置/受管电脑/安全软件拦文件创建）→ 逐个回退到真正可写的位置
                var fallbackUsed = false;
                string fallbackNote;
                var layoutError = EnsureWritableWorkspace(ref workspace, exeDir, ref fallbackUsed, out fallbackNote);
                if (layoutError != null)
                {
                    if (cli.CliMode)
                    {
                        ConsoleBridge.WriteLine("{\"mode\":\"startup\",\"ok\":false,\"error\":\"" +
                            layoutError.Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"}");
                    }
                    else
                    {
                        MessageBox.Show(layoutError, Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return 3;
                }

                var log = new Log(workspace.LogsDir);
                if (fallbackUsed) log.Warn("默认数据目录不可写，已回退到：" + workspace.Root + "；" + (fallbackNote ?? string.Empty));

                // 修复失效的桌面快捷方式：单文件版换载荷目录后，旧 .lnk 会指向已被清理的
                // AppForge-payload\<旧指纹>\AppForge.exe，双击就弹「Windows 无法访问指定设备、路径或文件」。
                if (!cli.CliMode)
                {
                    try
                    {
                        var self = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        var repaired = ShortcutCreator.RepairStaleShortcuts(ShortcutCreator.ResolveStableLauncher(self), log);
                        if (repaired > 0) log.Info("已修复 " + repaired + " 个失效的桌面快捷方式");
                    }
                    catch { /* 修复失败不影响启动 */ }
                }

                // 把"最终选定的数据目录"记进注册表：以后任何入口（cmd / 单文件 exe / 快捷方式）都读同一个值，
                // 不会再出现"两个入口数据不一样"。--data-dir 显式指定和便携模式不写指针。
                if (string.IsNullOrWhiteSpace(cli.DataDir) && !workspace.Portable && !portableMode)
                {
                    try
                    {
                        if (DataDirPointer.Write(workspace.Root))
                            log.Info("已把数据目录记入注册表（HKCU\\Software\\AppForge\\DataDir）：" + workspace.Root);
                        DataDirPointer.Remember(workspace.Root);
                    }
                    catch { /* 记不住也不影响本次启动 */ }
                }

                // 合并其它历史数据目录里的应用（不同入口曾经各存一份，这里统一到一处）
                var merged = MergeKnownDataDirs(workspace, log);
                if (merged != null) log.Info(merged);
                if (unblocked > 0) log.Info("已清除 " + unblocked + " 个文件的「来自 Internet」标记（SmartScreen 不再提示）");
                log.Info("AppForge 启动，数据目录：" + workspace.Root + "，参数：" + CliArgs.MaskSecrets(args));

                if (cli.Check) return CliRunner.RunCheck(workspace, log);
                if (cli.Selftest) return CliRunner.RunSelftest(workspace, log);
                if (cli.Bench) return CliRunner.RunBench(workspace, log);
                if (cli.SetApiKey != null) return CliRunner.RunSetApiKey(workspace, log, cli.SetApiKey);
                if (cli.ListModels) return CliRunner.RunListModels(workspace, log);
                if (cli.CheckModel) return CliRunner.RunCheckModel(workspace, log);
                if (cli.UiDiag) return CliRunner.RunUiDiag(workspace, log);
                if (cli.Generate != null) return CliGen.Run(workspace, log, cli.Generate, cli.App);
                if (cli.OpenFolder != null) return RunOpenFolder(cli.OpenFolder, log);
                if (cli.ShellDiag) return RunShellDiag(log);
                if (cli.Unblock) return RunUnblock(log, unblocked);

                // ---- 依赖自检（FR-0.4）----
                var deps = StartupChecks.RunAll(workspace);
                var blocking = deps.FirstOrDefault(d => d.Required && !string.Equals(d.State, "ok", StringComparison.Ordinal));
                if (blocking != null)
                {
                    log.Warn("依赖缺失：" + blocking.Name + " / " + blocking.Message);
                    if (string.Equals(blocking.Name, "webview2", StringComparison.Ordinal))
                    {
                        if (!WebView2Installer.GuideInstall(blocking, log))
                        {
                            MessageBox.Show(
                                "缺少 WebView2 运行时，程序无法显示界面。\r\n\r\n" +
                                "可访问 https://developer.microsoft.com/microsoft-edge/webview2/ 手动安装后重试。",
                                Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return 2;
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            blocking.DisplayName + " 检查未通过：\r\n" + blocking.Message,
                            Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 2;
                    }
                    deps = StartupChecks.RunAll(workspace);
                }

                var settingsStore = new SettingsStore(workspace);
                settingsStore.Load();
                var secrets = new SecretsStore(workspace);
                var repo = new AppRepository(workspace);
                repo.Load();
                SeedData.EnsureSeeded(workspace, repo);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 关掉系统级硬错误弹窗（如子进程 explorer.exe 起不来时的 0xc0000142 提示框），
                // 失败改由我们自己的日志 + 界面提示呈现。
                ProcessErrorMode.Apply();
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    log.Error("UI 线程未捕获异常", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    log.Error("未捕获异常", e.ExceptionObject as Exception);
                };

                using (var form = new ShellForm(workspace, log, settingsStore, secrets, repo, deps, cli.OpenApp))
                {
                    Application.Run(form);
                }
                return 0;
            }
            finally
            {
                if (mutex != null)
                {
                    try { if (isFirst) mutex.ReleaseMutex(); } catch { /* 忽略 */ }
                    mutex.Dispose();
                }
            }
        }
    }
}
