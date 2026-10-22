using System;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AppForge.Host
{
    /// <summary>
    /// WinExe 进程本身没有控制台；CLI 模式通过 AttachConsole 借用父控制台输出。
    /// 直接写 std 句柄，避免 Console.Out 在 Attach 前后缓冲不一致的问题。
    /// </summary>
    internal static class ConsoleBridge
    {
        private static readonly object Gate = new object();
        private static Stream _stdout;
        private static string _teePath;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        /// <summary>
        /// CLI 模式把输出同时写一份到文件：无控制台环境（CI/管道/IDE）也能拿到结果，
        /// 真实终端里 AttachConsole 成功后仍然正常打印。
        /// </summary>
        public static void SetTee(string path)
        {
            Ensure();
            lock (Gate)
            {
                try
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(path, string.Empty); // 每次运行覆盖
                    }
                }
                catch { /* 忽略 */ }
                _teePath = path;
            }
        }

        public static void Ensure()
        {
            lock (Gate)
            {
                if (_stdout != null) return;
                try
                {
                    _stdout = TryGetStandardOutput();
                    if (_stdout != null) return;

                    AttachConsole(-1); // ATTACH_PARENT_PROCESS
                    _stdout = TryGetStandardOutput() ?? Stream.Null;
                }
                catch
                {
                    _stdout = Stream.Null;
                }
            }
        }

        private static Stream TryGetStandardOutput()
        {
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            return new FileStream(new SafeFileHandle(handle, false), FileAccess.Write, 8192, false);
        }

        public static void Write(string text)
        {
            Ensure();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                _stdout.Write(bytes, 0, bytes.Length);
                _stdout.Flush();
            }
            catch
            {
                // 没有控制台时静默
            }

            var tee = _teePath;
            if (tee != null)
            {
                try { File.AppendAllText(tee, text, new UTF8Encoding(false)); }
                catch { /* 忽略 */ }
            }
        }

        public static void WriteLine(string text)
        {
            Write((text ?? string.Empty) + "\r\n");
        }
    }
}
