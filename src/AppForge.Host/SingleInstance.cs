using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppForge.Host
{
    /// <summary>
    /// 单实例激活：
    ///   第二次启动（双击 exe / 桌面快捷方式）时，找到已运行实例的窗口（**包括隐藏到托盘的窗口**），
    ///   用 WM_COPYDATA 把"要打开哪个应用"传过去并把它唤到前台。
    /// 旧实现用 Process.MainWindowHandle —— 窗口 Hide() 后该值为 0，导致第二次双击没反应。
    /// </summary>
    public static class SingleInstance
    {
        public const int WmCopyData = 0x004A;
        public const int CopyDataMagic = 0x41463121; // "AF1!"
        /// <summary>激活请求（不打开具体应用）。</summary>
        public const string ActivateCommand = "__activate__";

        private const int SwRestore = 9;
        private const int SwShow = 5;

        [StructLayout(LayoutKind.Sequential)]
        internal struct CopyDataStruct
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);

        /// <summary>在指定进程里找一个顶层窗口（可见的优先，隐藏的也接受）。</summary>
        public static IntPtr FindWindowOfProcess(uint processId)
        {
            var found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != processId) return true;
                if (GetWindowTextLength(hWnd) == 0) return true; // 跳过无标题的辅助窗口

                if (IsWindowVisible(hWnd))
                {
                    found = hWnd;
                    return false; // 可见窗口最优，停止枚举
                }
                if (found == IntPtr.Zero) found = hWnd; // 先记住隐藏窗口
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>激活已运行实例；appId 非空时顺带让它打开该应用。</summary>
        public static bool ActivateExisting(string appId)
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id == current.Id) continue;
                    if (process.MainWindowHandle == IntPtr.Zero && process.Handle == IntPtr.Zero) continue;

                    var hWnd = FindWindowOfProcess((uint)process.Id);
                    if (hWnd == IntPtr.Zero) continue;

                    SendCommand(hWnd, string.IsNullOrEmpty(appId) ? ActivateCommand : appId);
                    if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
                    else ShowWindow(hWnd, SwShow);
                    SetForegroundWindow(hWnd);
                    return true;
                }
            }
            catch
            {
                // 激活失败交给文件交接兜底
            }
            return false;
        }

        private static void SendCommand(IntPtr hWnd, string command)
        {
            var bytes = Encoding.UTF8.GetBytes(command ?? string.Empty);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                Marshal.WriteByte(ptr, bytes.Length, 0);
                var data = new CopyDataStruct
                {
                    dwData = (IntPtr)CopyDataMagic,
                    cbData = bytes.Length + 1,
                    lpData = ptr
                };
                SendMessage(hWnd, WmCopyData, IntPtr.Zero, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>从 WM_COPYDATA 读取命令。</summary>
        public static bool TryReadCommand(IntPtr lParam, out string command)
        {
            command = null;
            try
            {
                var data = (CopyDataStruct)Marshal.PtrToStructure(lParam, typeof(CopyDataStruct));
                if (data.dwData.ToInt64() != CopyDataMagic || data.lpData == IntPtr.Zero || data.cbData <= 0)
                    return false;

                var bytes = new byte[data.cbData];
                Marshal.Copy(data.lpData, bytes, 0, data.cbData);
                command = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
