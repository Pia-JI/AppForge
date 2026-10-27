using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 打开目录（"在资源管理器中显示"）。
    ///
    /// 为什么不是一行 Process.Start("explorer.exe", path) 就完事：
    /// ① 有些机器上 explorer.exe 起不来——进程能创建，但在 DLL 初始化阶段就崩，退出码 0xC0000142
    ///    （STATUS_DLL_INIT_FAILED），Windows 会弹"explorer.exe - 应用程序无法正常启动"的系统框；
    /// ② 装了火绒/360 这类安全软件的机器，**创建子进程本身就会被拦**（CreateProcess 失败或子进程秒退），
    ///    所以"能起新进程"这件事本身就不能当成前提。
    ///
    /// 因此这里改成分级策略，优先复用已运行的外壳，避免直接创建 explorer.exe：
    ///   ① ShellExecuteEx("open", SEE_MASK_FLAG_NO_UI)：走系统外壳的标准动词，由已运行的资源管理器处理，
    ///      不创建进程，失败也不弹系统错误框（实测在 explorer.exe 报 0xC0000142 的机器上仍能打开）；
    ///   ② Shell.Application COM 的 Navigate2：让已在运行的资源管理器新开窗口；
    ///   ③ SHOpenFolderAndSelectItems（shell32 官方 API）：同样交给已运行的外壳；
    ///   ④⑤ 自己 CreateProcess 起 explorer.exe（普通路径 / /e 视图）；
    ///   ⑥ rundll32.exe url.dll,FileProtocolHandler（走协议处理器）；
    ///   ⑦ 都失败：把路径放进剪贴板并返回 false，由界面降级成应用内文件列表。
    ///
    /// 返回 false 时 copied 表示路径是否已进剪贴板（界面文案据此决定，不谎报）。
    /// stages 记录了每一级的实际结果（HRESULT / Win32 错误码 / 退出码），
    /// 排障时用 `AppForge.exe --open-folder="&lt;路径&gt;"` 一眼就能看出卡在哪一级。
    /// </summary>
    internal static class FolderOpener
    {
        /// <summary>
        /// 尝试打开目录。<paramref name="copied"/> 表示失败时路径是否已放进剪贴板。
        /// </summary>
        /// <param name="path">要打开的目录（不存在直接返回 false）。</param>
        /// <param name="copied">输出：是否已把路径复制到剪贴板。</param>
        /// <param name="warn">日志回调（可为 null）。</param>
        public static bool Open(string path, out bool copied, Action<string> warn)
        {
            IList<string> stages;
            string verifiedBy;
            return Open(path, out copied, out stages, out verifiedBy, warn);
        }

        /// <summary>
        /// 同 <see cref="Open(string, out bool, Action{string})"/>，额外输出每一级的诊断结果与"最终是由哪一级真的打开了窗口"。
        ///
        /// **每一级都必须验证"窗口真的出现了"才算成功**（<see cref="WaitForFolderWindow"/>）：
        /// 被安全软件限制的机器上，ShellExecuteEx / explorer.exe / rundll32 都可能"报告成功但什么也没发生"，
        /// 这时绝不能谎报成功——那正是用户看到的"点了没反应"。
        /// </summary>
        public static bool Open(string path, out bool copied, out IList<string> stages, out string verifiedBy, Action<string> warn)
        {
            copied = false;
            verifiedBy = null;
            stages = new List<string>();
            var log = warn ?? delegate(string s) { };

            if (string.Equals(Environment.GetEnvironmentVariable("APPFORGE_NO_SHELL_OPEN"), "1",
                    StringComparison.OrdinalIgnoreCase))
            {
                stages.Add("disabled: APPFORGE_NO_SHELL_OPEN=1（自检模式，不真的打开资源管理器）");
                log("打开目录：自检模式已禁用系统外壳调用");
                return false;
            }

            // 外壳 COM / shell32 都要求 STA。无论调用方是不是 STA，都必须换到独立工作线程：
            // RPC 可能从线程池（MTA）调用；而 UI/命令行主线程即使已经是 STA，也不能被这里
            // 最多十几秒的窗口探测占住（否则界面无响应，--open-folder 也可能看起来卡死）。
            var opened = false;
            var innerCopied = false;
            IList<string> innerStages = new List<string>();
            var innerVerifiedBy = (string)null;
            var worker = new Thread(delegate()
            {
                opened = OpenOnStaThread(path, out innerCopied, out innerStages, out innerVerifiedBy, log);
            }, 4 * 1024 * 1024);
            worker.IsBackground = true;
            try { worker.SetApartmentState(ApartmentState.STA); } catch { /* 忽略 */ }
            try
            {
                worker.Start();
                if (!worker.Join(15000))
                {
                    innerStages.Add("timeout: 打开目录超过 15 秒，已放弃（窗口可能仍停留在系统外壳里）");
                    log("打开目录：STA 工作线程超时");
                }
            }
            catch (Exception ex)
            {
                innerStages.Add("thread: 启动 STA 线程失败（" + ex.GetType().Name + "：" + ex.Message + "）");
                log("打开目录：启动 STA 线程失败：" + ex.Message);
            }

            copied = innerCopied;
            stages = innerStages;
            verifiedBy = innerVerifiedBy;
            return opened;
        }

        private static bool OpenOnStaThread(string path, out bool copied, out IList<string> stages,
                                            out string verifiedBy, Action<string> log)
        {
            copied = false;
            verifiedBy = null;
            stages = new List<string>();

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                stages.Add("path: 路径不存在 " + (path ?? "(null)"));
                log("打开目录：路径不存在 " + (path ?? "(null)"));
                return false;
            }

            // 先记下当前已打开的资源管理器窗口，后面用"是否多了一个窗口"来判断到底有没有真的打开
            var before = ExplorerWindowTitles();
            stages.Add("windows: 当前资源管理器窗口 " + before.Count + " 个");

            // 本线程的"不弹系统错误框"策略再加固一层：
            // ShellExecuteEx / rundll32 这类调用失败时，Windows 会弹
            // 「Windows 无法访问指定设备、路径或文件」这种标题为调用方 exe 名的系统框，
            // SetErrorMode 管不到 shell 的某些路径，SetThreadErrorMode 才是针对本线程的那一层。
            uint oldMode = 0;
            var modeChanged = false;
            try { modeChanged = SetThreadErrorMode(SilentErrorMode, out oldMode); } catch { /* 忽略 */ }
            try
            {
                return OpenCore(path, before, stages, out copied, out verifiedBy, log);
            }
            finally
            {
                if (modeChanged) { try { SetThreadErrorMode(oldMode, out oldMode); } catch { /* 忽略 */ } }
            }
        }

        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpFaultErrorBox = 0x0002;
        private const uint SemNoOpenFileErrorBox = 0x8000;
        private const uint SilentErrorMode = SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);

        /// <summary>真正的分级尝试（外层已套好"不弹系统框"的策略）。</summary>
        private static bool OpenCore(string path, ISet<string> before, IList<string> stages,
                                     out bool copied, out string verifiedBy, Action<string> log)
        {
            copied = false;
            verifiedBy = null;

            var windowsDir = SafeWorkingDirectory();

            // ---------------- ① ShellExecuteEx("open")：标准且不弹框的一级 ----------------
            // 实测：cmd /c start 在安全软件开启“程序防护”的机器上会弹
            // 「Windows 无法访问指定设备、路径或文件」系统框；同样的动作直接走
            // ShellExecuteEx + SEE_MASK_FLAG_NO_UI 却能正常打开，且失败时没有弹窗。
            var shellExecuteError = 0;
            if (TryShellExecute(path, out shellExecuteError))
            {
                stages.Add("shellExecute: 已提交");
                if (Verify("shellExecute", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("shellExecute: 失败（Win32 " + shellExecuteError + "）");
            }

            // Stage 1b: ask an already-running Explorer window to navigate. Needs neither a new
            // process nor shell32's cross-process API, and works when explorer.exe cannot start.
            string comError0;
            if (TryShellComOpenIsolated(path, out comError0))
            {
                stages.Add("shellNavigate: 已提交");
                if (Verify("shellNavigate", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("shellNavigate: 失败（" + comError0 + "）");
            }

            // ---------------- ② shell32 官方"在资源管理器中显示"（不新建进程） ----------------
            string shellApiError;
            if (TryShellApi(path, out shellApiError, log))
            {
                stages.Add("shellApi: 已提交");
                if (Verify("shellApi", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("shellApi: 失败（" + shellApiError + "）");
            }

            // ---------------- ③ 自己 CreateProcess 起 explorer.exe ----------------
            // 用**完整路径**（C:\Windows\explorer.exe）而不是裸 "explorer.exe"：
            // CreateProcess 传 lpApplicationName=null 时会先在本程序目录和 CWD 里按名字找，
            // 载荷目录里塞着一堆 WebView2 的 dll，交给系统绝对路径能避开这一层不必要的搜索。
            var explorerExe = string.IsNullOrEmpty(windowsDir) ? "explorer.exe" : Path.Combine(windowsDir, "explorer.exe");
            var code = 0;

            if (TryCreateProcess("\"" + explorerExe + "\" \"{0}\"", path, out code))
            {
                stages.Add("explorer: 已提交（退出码 " + code + "）");
                if (Verify("explorer", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("explorer: 失败（退出码 " + code + " / 0x" + code.ToString("X8") + "）");
                if (code == NativeStatusDllInitFailed)
                    log("打开目录：explorer.exe 无法在本机启动（退出码 0xC0000142，DLL 初始化失败）");
            }

            // ③b 换个动词再试一次：/e 打开「资源管理器」视图（有些机器上裸路径会被策略吞掉）
            if (TryCreateProcess("\"" + explorerExe + "\" /e,\"{0}\"", path, out code))
            {
                stages.Add("explorer /e: 已提交（退出码 " + code + "）");
                if (Verify("explorer /e", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("explorer /e: 失败（退出码 " + code + " / 0x" + code.ToString("X8") + "）");
            }

            // ---------------- ④ rundll32 协议处理器 ----------------
            if (TryCreateProcess("rundll32.exe url.dll,FileProtocolHandler \"{0}\"", path, out code))
            {
                stages.Add("rundll32: 已提交（退出码 " + code + "）");
                if (Verify("rundll32", path, before, log, stages, out verifiedBy)) return true;
            }
            else
            {
                stages.Add("rundll32: 失败（退出码 " + code + " / 0x" + code.ToString("X8") + "）");
            }

            // ---------------- ⑥ 兜底：路径进剪贴板，交给界面 ----------------
            if (TrySetClipboardText(path))
            {
                copied = true;
                log("打开目录：自动打开失败，路径已复制到剪贴板：" + path);
            }
            else
            {
                log("打开目录失败（路径也未能写入剪贴板），路径：" + path);
            }
            return false;
        }

        /// <summary>在独立 STA 线程里调用 Shell.Application，避免外壳 COM 卡住时拖死整个流程。</summary>
        private static bool TryShellComOpenIsolated(string path, out string error)
        {
            var opened = false;
            var innerError = (string)null;
            var worker = new Thread(delegate()
            {
                opened = TryShellComOpen(path, out innerError);
            }, 2 * 1024 * 1024);
            worker.IsBackground = true;
            try { worker.SetApartmentState(ApartmentState.STA); } catch { /* 忽略 */ }

            try
            {
                worker.Start();
                if (!worker.Join(8000))
                {
                    error = "Shell.Application 超过 8 秒未返回，已跳过";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "启动 Shell.Application 线程失败（" + ex.GetType().Name + "：" + ex.Message + "）";
                return false;
            }

            error = innerError;
            return opened;
        }

        /// <summary>
        /// 用 Win32 剪贴板 API 写入文本。相比 WinForms Clipboard（内部走 OLE），
        /// 这里可以在剪贴板被别的程序占用时快速失败并重试，不会在无消息泵的线程上无限等待。
        /// </summary>
        private static bool TrySetClipboardText(string text)
        {
            if (text == null) return false;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(50);
                    continue;
                }

                var memory = IntPtr.Zero;
                try
                {
                    if (!EmptyClipboard()) return false;

                    var bytes = (uint)((text.Length + 1) * 2);
                    memory = GlobalAlloc(GMemMoveable, new UIntPtr(bytes));
                    if (memory == IntPtr.Zero) return false;

                    var target = GlobalLock(memory);
                    if (target == IntPtr.Zero) return false;
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(memory);
                    }

                    if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;
                    memory = IntPtr.Zero;   // 所有权已转移给系统
                    return true;
                }
                finally
                {
                    if (memory != IntPtr.Zero) GlobalFree(memory);
                    CloseClipboard();
                }
            }
            return false;
        }

        private const uint CfUnicodeText = 13;
        private const uint GMemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const int NativeStatusDllInitFailed = unchecked((int)0xC0000142);

        // ---------------- 结果验证：窗口到底有没有出现 ----------------
        //
        // 这一层是整个"打开目录"能不能说真话的关键：被安全软件限制的机器上，
        // ShellExecuteEx 会返回 TRUE、explorer.exe 会返回退出码 0/1、rundll32 也会返回 0，
        // 但**一个窗口都没打开**。只按返回值判断就会谎报成功 —— 用户看到的就是"点了没反应"。

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>当前所有可见资源管理器窗口的标题（CabinetWClass = 文件资源管理器，ExploreWClass = 旧式）。</summary>
        private static HashSet<string> ExplorerWindowTitles()
        {
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    var cls = new StringBuilder(64);
                    GetClassName(hWnd, cls, cls.Capacity);
                    var name = cls.ToString();
                    if (!string.Equals(name, "CabinetWClass", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "ExploreWClass", StringComparison.OrdinalIgnoreCase)) return true;

                    var text = new StringBuilder(512);
                    GetWindowText(hWnd, text, text.Capacity);
                    var title = text.ToString();
                    if (title.Length > 0) titles.Add(title);
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* 枚举失败按"没有任何窗口"处理 */ }
            return titles;
        }

        /// <summary>
        /// 打开后最多等约 1.2 秒，看是否真的出现了一个指向该目录的资源管理器窗口。
        /// 判据：① 窗口标题包含目录名；② 或出现了打开前没有的资源管理器窗口。
        /// </summary>
        private static bool WaitForFolderWindow(string path, ISet<string> before)
        {
            var leaf = Path.GetFileName((path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            for (var attempt = 0; attempt < 6; attempt++)
            {
                var now = ExplorerWindowTitles();

                if (!string.IsNullOrEmpty(leaf))
                {
                    foreach (var title in now)
                    {
                        if (title.IndexOf(leaf, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
                foreach (var title in now)
                {
                    if (before == null || !before.Contains(title)) return true;
                }

                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>某一级"报告成功"之后再确认窗口是否真出现；没出现就继续试下一级。</summary>
        private static bool Verify(string stageName, string path, ISet<string> before, Action<string> log,
                                   IList<string> stages, out string verifiedBy)
        {
            verifiedBy = null;
            if (WaitForFolderWindow(path, before))
            {
                verifiedBy = stageName;
                stages.Add("verify: " + stageName + " → 已看到资源管理器窗口");
                log("打开目录：已通过 " + stageName + " 打开 " + path);
                return true;
            }
            stages.Add("verify: " + stageName + " 报告成功，但没等到资源管理器窗口");
            log("打开目录：" + stageName + " 报告成功，但没出现资源管理器窗口（多半是安全软件的「程序防护/启动防护」在拦）");
            return false;
        }

        // ---------------- ② Shell.Application COM（借用已运行的外壳导航） ----------------

        /// <summary>
        /// 用 Shell.Application COM 让一个已经在运行的资源管理器窗口导航到目标目录。
        /// 好处：请求交给**已在运行的** shell 组件，不需要我们创建任何进程
        /// （被"启动防护"限制的机器上，创建进程这条路基本是被堵死的）。
        /// </summary>
        private static bool TryShellComOpen(string path, out string error)
        {
            error = null;
            var hrCom = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            var needUninit = hrCom == 0 || hrCom == 1;

            object shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null)
                {
                    error = "Shell.Application 未注册";
                    return false;
                }

                shell = Activator.CreateInstance(type);
                if (TryNavigateExistingWindows(type, shell, path)) return true;
                // 没有已运行的资源管理器窗口可导航时，不要再调用 Shell.Application.Open：
                // 它在被安全软件限制的机器上会一直挂起，直到外层 8 秒超时，
                // 既打不开目录，又让用户多等一轮。直接交给后面的 shell32/explorer/rundll32。
                error = "没有已运行的资源管理器窗口可导航";
                return false;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                error = inner.GetType().Name + "：" + inner.Message;
                return false;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { /* 忽略 */ }
                }
                if (needUninit) CoUninitialize();
            }
        }

        /// <summary>
        /// Ask an already-running Explorer window to open the folder in a new window.
        /// This avoids creating explorer.exe, which fails with 0xC0000142 on machines where
        /// security software blocks explorer startup but still allows shell COM navigation.
        /// </summary>
        private static bool TryNavigateExistingWindows(Type shellType, object shell, string path)
        {
            object windows = null;
            try
            {
                windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return false;

                var windowsType = windows.GetType();
                var count = Convert.ToInt32(windowsType.InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, windows, null));
                for (var i = 0; i < count; i++)
                {
                    var window = windowsType.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod,
                        null, windows, new object[] { i });
                    if (window == null) continue;

                    var windowType = window.GetType();
                    var locationUrl = windowType.InvokeMember("LocationURL",
                        System.Reflection.BindingFlags.GetProperty, null, window, null) as string;
                    if (string.IsNullOrEmpty(locationUrl) ||
                        !locationUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;

                    // BrowserNavConstants.navOpenInNewWindow = 0x1000
                    windowType.InvokeMember("Navigate2", System.Reflection.BindingFlags.InvokeMethod,
                        null, window, new object[] { path, 0x1000 });
                    return true;
                }
            }
            catch { /* fall back to Shell.Application.Open */ }
            finally
            {
                if (windows != null && Marshal.IsComObject(windows))
                {
                    try { Marshal.ReleaseComObject(windows); } catch { /* ignore */ }
                }
            }
            return false;
        }

        /// <summary>
        /// 当前进程是否以管理员身份运行。
        ///
        /// 这条信息对"打开目录"很关键：Windows 不允许高完整性级别（管理员）进程把 shell 请求
        /// 交给普通权限的资源管理器，表现正是——
        ///   · ShellExecuteEx("open") → Win32 5（拒绝访问）
        ///   · SHOpenFolderAndSelectItems → E_ACCESSDENIED (0x80070005)
        ///   · 自己起 explorer.exe → 0xC0000142（explorer 拒绝以管理员身份初始化）
        /// 这时唯一有效的解决办法是"以普通权限重启 AppForge"，所以要把原因如实告诉用户，
        /// 而不是笼统说一句"explorer 起不来"。
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(identity)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------- 诊断：这个进程还能不能创建进程 / 叫起资源管理器 ----------------

        /// <summary>诊断结果（给 `--shell-diag` 用，界面不显示）。</summary>
        public sealed class ShellDiagnostics
        {
            public string UserName;
            public bool Elevated;
            public int SessionId;
            public string WindowsDir;
            public bool ExplorerExists;
            public bool CanSpawnProcess;      // 能不能创建**任何**进程（探针是 cmd /c exit 7）
            public int ProbeExitCode;
            public bool CanSpawnExplorer;
            public int ExplorerExitCode;
            public bool ShellApiOk;
            public string ShellApiError;
        }

        /// <summary>
        /// 区分三种"打不开目录"的机器：
        ///   ① `CanSpawnProcess=false` ⇒ 本进程被禁止创建任何进程（HIPS/EDR 拦死了，只能加白）；
        ///   ② 探针能跑、`CanSpawnExplorer=false` ⇒ 只针对 explorer.exe 的封堵（策略/安全软件的"启动防护"）；
        ///   ③ 两者都能跑、`ShellApiOk=false` ⇒ 外壳拒绝跨进程请求（多半是权限/完整性级别不匹配）。
        /// </summary>
        public static ShellDiagnostics Diagnose()
        {
            var d = new ShellDiagnostics();
            try { d.UserName = System.Security.Principal.WindowsIdentity.GetCurrent().Name; } catch { /* 忽略 */ }
            d.Elevated = IsElevated();
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) d.SessionId = p.SessionId;
            }
            catch { /* 忽略 */ }

            d.WindowsDir = SafeWorkingDirectory();
            d.ExplorerExists = !string.IsNullOrEmpty(d.WindowsDir) && File.Exists(Path.Combine(d.WindowsDir, "explorer.exe"));

            int code;
            d.CanSpawnProcess = TrySpawnProbe(out code);
            d.ProbeExitCode = code;

            if (d.ExplorerExists)
            {
                // 用系统目录当参数：能起来的话会真的弹出一个资源管理器窗口——这本身就是结论
                var explorerExe = Path.Combine(d.WindowsDir, "explorer.exe");
                d.CanSpawnExplorer = TryCreateProcess("\"" + explorerExe + "\" \"{0}\"", d.WindowsDir, out code);
                d.ExplorerExitCode = code;
            }

            string error;
            d.ShellApiOk = TryShellApi(d.WindowsDir ?? Path.GetPathRoot(Environment.SystemDirectory), out error, delegate(string s) { });
            d.ShellApiError = error;
            return d;
        }

        /// <summary>起一个 `cmd /c exit 7` 探针并等它结束：能拿到退出码 7 就说明"本进程允许创建进程"。</summary>
        private static bool TrySpawnProbe(out int exitCode)
        {
            exitCode = -1;
            try
            {
                var comspec = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(comspec))
                {
                    var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    comspec = Path.Combine(sysDir, "cmd.exe");
                }
                if (!File.Exists(comspec)) return false;

                var command = new StringBuilder("\"" + comspec + "\" /c exit 7");
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, SafeWorkingDirectory(), ref si, out pi))
                    return false;
                try
                {
                    WaitForSingleObject(pi.hProcess, 5000);
                    return GetExitCodeProcess(pi.hProcess, out exitCode);
                }
                finally
                {
                    if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                    if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------- ① ShellExecuteEx("open") ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;   // 失败也不弹系统错误框
        private const uint SEE_MASK_NOASYNC = 0x00000100;      // 同步完成，避免线程退出导致请求丢失
        private const int SW_SHOWNORMAL = 1;

        /// <summary>
        /// 用外壳的 "open" 动词打开目录：由**已在运行的**资源管理器处理，不创建新进程。
        /// 这是 Windows 上"打开文件夹"最标准的方式（双击文件夹走的就是它）。
        /// </summary>
        private static bool TryShellExecute(string path, out int errorCode)
        {
            errorCode = 0;
            var hrCom = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            var needUninit = hrCom == 0 || hrCom == 1;
            try
            {
                var info = new SHELLEXECUTEINFO();
                info.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                info.fMask = SEE_MASK_FLAG_NO_UI;
                info.lpVerb = "open";
                info.lpFile = path;
                info.nShow = SW_SHOWNORMAL;

                if (ShellExecuteEx(ref info)) return true;

                errorCode = Marshal.GetLastWin32Error();
                // hInstApp 在旧接口里可能是 ≤32 的"伪错误码"，比 GetLastError 更具体
                var legacy = info.hInstApp.ToInt64();
                if (errorCode == 0 && legacy > 0 && legacy <= 32) errorCode = (int)legacy;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (needUninit) CoUninitialize();
            }
        }

        // ---------------- ② 交给已在运行的资源管理器（shell32 官方 API） ----------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private const uint CoInitApartmentThreaded = 0x2;
        private const int RpcChangedMode = unchecked((int)0x80010106);   // 线程已是 MTA，改不了

        /// <summary>
        /// 用 shell32 官方接口请资源管理器打开目录（等价于"在资源管理器中显示"）。
        /// 失败时通过 <paramref name="error"/> 带回具体 HRESULT，便于定位。
        /// </summary>
        private static bool TryShellApi(string path, out string error, Action<string> log)
        {
            error = null;

            // 这两个 API 要求调用线程已初始化 COM。UI 线程通常是 STA，但 CLI/后台线程不一定是，
            // 不显式初始化时 SHOpenFolderAndSelectItems 会直接返回失败（表现为"打不开目录"）。
            var hrCom = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            var needUninit = hrCom == 0 || hrCom == 1;   // S_OK / S_FALSE：本线程持有一次引用
            if (hrCom != 0 && hrCom != 1 && hrCom != RpcChangedMode)
                log("打开目录：CoInitializeEx 返回 0x" + hrCom.ToString("X8") + "（继续尝试）");

            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attributes;
                var hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    error = "SHParseDisplayName HRESULT 0x" + hr.ToString("X8");
                    return false;
                }

                // cidl=0 表示"打开该文件夹本身"（不选中某个子项）
                hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                if (hr != 0)
                {
                    error = "SHOpenFolderAndSelectItems HRESULT 0x" + hr.ToString("X8");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
                if (needUninit) CoUninitialize();
            }
        }

        // ---------------- ③④ 自己 CreateProcess（可控错误模式） ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 用 CreateProcess 启动命令行模板（commandTemplate 里的 {0} 替换成带引号的路径）。
        /// 注意：**不传** CREATE_DEFAULT_ERROR_MODE，子进程会继承本进程的错误模式，
        /// 于是 0xC0000142 这类崩溃只体现为退出码，不会再弹系统错误框。
        /// </summary>
        private static bool TryCreateProcess(string commandTemplate, string path, out int exitCode)
        {
            return TryCreateProcess(commandTemplate, path, 0, out exitCode);
        }

        private static bool TryCreateProcess(string commandTemplate, string path, uint creationFlags, out int exitCode)
        {
            exitCode = 0;
            var command = new StringBuilder(commandTemplate.Replace("{0}", "\"" + path.TrimEnd('\\') + "\""));
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;

            var cwd = SafeWorkingDirectory();
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, creationFlags, IntPtr.Zero, cwd, ref si, out pi))
                return false;

            try
            {
                // 资源管理器打开窗口后会一直活着；"把请求转交给已运行实例"时则会很快退出。
                if (WaitForSingleObject(pi.hProcess, 2500) == 0x00000102 /* WAIT_TIMEOUT */) return true;

                if (!GetExitCodeProcess(pi.hProcess, out exitCode)) return false;
                if (exitCode == 259 /* STILL_ACTIVE */) return true;

                // 关键：explorer.exe 接受路径后**正常退出并返回 1**（转交给已运行实例），
                // 0 则是"干净地完成"。两者都是成功；把 0/1 当失败会导致"窗口明明开了却报打不开"。
                return exitCode == 0 || exitCode == 1;
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            }
        }

        private const uint CreateNoWindow = 0x08000000;

        /// <summary>子进程的工作目录固定到系统目录，避免继承 AppForge 自己的载荷目录（DLL 搜索路径卫生）。</summary>
        private static string SafeWorkingDirectory()
        {
            try
            {
                var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Directory.Exists(win) ? win : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
