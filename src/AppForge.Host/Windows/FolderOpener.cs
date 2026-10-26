using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 打开目录（"在资源管理器中显示"）。
    ///
    /// 为什么不是一行 Process.Start("explorer.exe", path) 就完事：
    /// 有些机器上 explorer.exe 起不来——进程能创建，但在 DLL 初始化阶段就崩，退出码 0xC0000142
    /// （STATUS_DLL_INIT_FAILED）；Windows 会弹一个"explorer.exe - 应用程序无法正常启动"的系统框，
    /// 而调用方（我们）还以为成功了。用户看到的就只有那个看不懂的系统弹窗，目录也没打开。
    ///
    /// 因此这里改成分级策略，**首选根本不新起 explorer.exe**：
    ///   ① SHOpenFolderAndSelectItems（shell32 的官方 API）：把"打开这个文件夹"的请求交给
    ///      已在运行的资源管理器，由它自己开窗口——不创建新进程，也就不存在 DLL 初始化失败；
    ///   ② 自己用 CreateProcess 起 explorer.exe（不经 ShellExecute、不传 CREATE_DEFAULT_ERROR_MODE，
    ///      让子进程继承本进程的"不弹硬错误框"策略，失败时静默返回退出码给我们判断）；
    ///   ③ 同上方式起 rundll32.exe url.dll,FileProtocolHandler（走协议处理器）；
    ///   ④ 都失败：把路径放进剪贴板并返回 false，由界面提示用户。
    ///
    /// 返回 false 时 copied 表示路径是否已进剪贴板（界面文案据此决定，不谎报）。
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
            copied = false;
            var log = warn ?? delegate(string s) { };

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                log("打开目录：路径不存在 " + (path ?? "(null)"));
                return false;
            }

            if (TryShellApi(path, log))
            {
                log("打开目录：已交给资源管理器打开 " + path);
                return true;
            }

            // 路径以 \ 结尾时（如 C:\）不能直接加引号：\" 会被当成转义引号
            var quoted = "\"" + path.TrimEnd('\\') + "\"";

            int code;
            if (TryCreateProcess("explorer.exe \"{0}\"", path, out code))
            {
                log("打开目录：已通过 explorer.exe 打开 " + path);
                return true;
            }
            if (code == NativeStatusDllInitFailed)
            {
                // explorer.exe 在这台机器上根本起不来：再试 rundll32 也只会再崩一次，直接给用户退路
                log("打开目录：explorer.exe 无法在本机启动（退出码 0xC0000142，DLL 初始化失败），跳过后续尝试");
            }
            else if (TryCreateProcess("rundll32.exe url.dll,FileProtocolHandler \"{0}\"", path, out code))
            {
                log("打开目录：已通过 rundll32 打开 " + path);
                return true;
            }

            try
            {
                Clipboard.SetText(path);
                copied = true;
                log("打开目录：自动打开失败，路径已复制到剪贴板：" + path);
            }
            catch (Exception ex)
            {
                log("打开目录失败（复制路径也失败：" + ex.GetType().Name + "：" + ex.Message + "），路径：" + path);
            }
            return false;
        }

        private const int NativeStatusDllInitFailed = unchecked((int)0xC0000142);


        // ---------------- ① 交给已在运行的资源管理器（不创建新进程） ----------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        /// <summary>
        /// 用 shell32 官方接口请资源管理器打开目录（等价于"在资源管理器中显示"）。
        /// 成功返回 true；返回 false 说明这台机器的 shell 不接受该请求（例如没有可用桌面）。
        /// </summary>
        private static bool TryShellApi(string path, Action<string> log)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attributes;
                var hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    log("打开目录：SHParseDisplayName 失败（HRESULT 0x" + hr.ToString("X8") + "）");
                    return false;
                }

                // cidl=0 表示"打开该文件夹本身"（不选中某个子项）
                hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                if (hr != 0)
                {
                    log("打开目录：SHOpenFolderAndSelectItems 失败（HRESULT 0x" + hr.ToString("X8") + "）");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                log("打开目录：shell 接口异常（" + ex.GetType().Name + "：" + ex.Message + "）");
                return false;
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            }
        }

        // ---------------- ②③ 自己 CreateProcess（可控错误模式） ----------------

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
            exitCode = 0;
            var command = new StringBuilder(commandTemplate.Replace("{0}", "\"" + path.TrimEnd('\\') + "\""));
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;

            var cwd = SafeWorkingDirectory();
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, cwd, ref si, out pi))
                return false;

            try
            {
                // 资源管理器打开窗口后会一直活着；起不来（DLL 初始化失败）则很快退出
                if (WaitForSingleObject(pi.hProcess, 2500) == 0x00000102 /* WAIT_TIMEOUT */) return true;

                if (GetExitCodeProcess(pi.hProcess, out exitCode))
                {
                    if (exitCode == 259 /* STILL_ACTIVE */) return true;
                    return false;
                }
                return false;
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            }
        }

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
