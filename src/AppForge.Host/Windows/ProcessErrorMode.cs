using System;
using System.Runtime.InteropServices;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 进程错误模式：默认情况下，子进程（例如我们拉起的 explorer.exe）初始化失败时
    /// Windows 会弹一个系统级"应用程序无法正常启动 0xc0000142"的对话框。
    /// 这里关掉这类硬错误框，让失败变成"我们自己的日志 + 界面提示"，
    /// 而不是甩给用户一个看不懂的系统弹窗。
    /// </summary>
    internal static class ProcessErrorMode
    {
        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        private static bool _applied;

        /// <summary>主窗口创建前调用一次即可（幂等）。</summary>
        public static void Apply()
        {
            if (_applied) return;
            _applied = true;
            try
            {
                SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
            }
            catch
            {
                // 受限环境拿不到也无所谓：只是弹窗体验差一点
            }
        }
    }
}
