using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AppForge.Shared
{
    /// <summary>
    /// 清除文件上的"来自 Internet"标记（NTFS 备用数据流 Zone.Identifier）。
    ///
    /// 背景：Windows 的 SmartScreen 弹「已保护你的电脑 / 发行者：发布者未知」，
    /// 触发条件是"程序是从网上下载的（带 Zone 标记）+ 没有代码签名/没有信誉"。
    /// 程序自己没法阻止系统在**第一次**运行时弹这个框（那是运行前的检查），
    /// 但可以把自己身上的标记删掉，让**后续每次启动都不再提示**；
    /// 配合一次性的「解除锁定」，用户从此看不到这个框。
    ///
    /// 删不掉（只读目录、被安全软件保护）时静默跳过，绝不影响启动。
    /// </summary>
    public static class MotwCleaner
    {
        private const uint InvalidFileAttributes = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        /// <summary>该文件是否带"来自 Internet"标记。</summary>
        public static bool IsMarked(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return GetFileAttributesW(path + ":Zone.Identifier") != InvalidFileAttributes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删除单个文件的标记。返回 true 表示确实删掉了。</summary>
        public static bool Unblock(string path)
        {
            if (!IsMarked(path)) return false;
            try
            {
                return DeleteFileW(path + ":Zone.Identifier");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把目录下的 exe/dll（不递归）都解除锁定，返回处理数量。</summary>
        public static int UnblockFolder(string dir)
        {
            var count = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".config", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Unblock(file)) count++;
                }
            }
            catch
            {
                // 目录里有个别文件删不掉就跳过
            }
            return count;
        }

        /// <summary>
        /// 清理自身：当前 exe + 所在目录的 exe/dll。返回本次清掉标记的文件数。
        /// 启动时调用一次即可（幂等、失败无副作用）。
        /// </summary>
        public static int SelfUnblock()
        {
            try
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly() != null
                    ? System.Reflection.Assembly.GetEntryAssembly().Location
                    : null;
                if (string.IsNullOrEmpty(exe))
                {
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        if (process.MainModule != null) exe = process.MainModule.FileName;
                    }
                }
                if (string.IsNullOrEmpty(exe)) return 0;

                var count = Unblock(exe) ? 1 : 0;
                count += UnblockFolder(Path.GetDirectoryName(exe));
                return count;
            }
            catch
            {
                return 0;
            }
        }
    }
}
