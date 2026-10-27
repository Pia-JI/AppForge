using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using AppForge.Shared;
using AppForge.Host.Diagnostics;

namespace AppForge.Host.Runtime
{
    /// <summary>
    /// 桌面快捷方式：指向 AppForge.exe --open-app=&lt;id&gt;，
    /// 双击直接进应用（不显示造物台），通过单实例交接实现。
    ///
    /// 注意：**单文件版的真身运行在载荷缓存目录**（`AppForge-payload\&lt;指纹&gt;\AppForge.exe`），
    /// 而载荷目录每次打包都会换一个指纹。如果快捷方式直接指向载荷里的 exe，
    /// 换一次版本（或清理缓存）之后，双击快捷方式就会弹
    /// 「Windows 无法访问指定设备、路径或文件」。
    /// 所以这里优先指向**稳定的启动器**（dist\AppForge.exe），
    /// 启动器会把 --open-app 参数原样透传给载荷里的真身。
    /// </summary>
    public static class ShortcutCreator
    {
        /// <summary>
        /// 把"载荷目录里的 exe"换成"外层稳定的启动器 exe"。
        /// 不是载荷目录（开发/绿色包直接运行）时原样返回。
        /// </summary>
        public static string ResolveStableLauncher(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) return exePath;

                var dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir)) return exePath;

                // 形如 ...\AppForge-payload\<指纹>\AppForge.exe ⇒ 上两层是 dist\
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) ||
                    Path.GetFileName(parent).IndexOf("AppForge-payload", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return exePath;
                }

                var grandParent = Path.GetDirectoryName(parent);
                if (string.IsNullOrEmpty(grandParent)) return exePath;

                var launcher = Path.Combine(grandParent, "AppForge.exe");
                if (File.Exists(launcher) &&
                    !string.Equals(Path.GetFullPath(launcher), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                {
                    return launcher;
                }
                return exePath;
            }
            catch
            {
                return exePath;
            }
        }

        /// <summary>
        /// 修复历史遗留的失效快捷方式：指向 `AppForge-payload\&lt;旧指纹&gt;\AppForge.exe` 且该文件已不存在的 .lnk，
        /// 重新指向当前可用的 exe。只动"目标确实已经不存在"的 AppForge 快捷方式，其它文件一概不碰。
        /// </summary>
        public static int RepairStaleShortcuts(string currentExe, Log log)
        {
            var repaired = 0;
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return 0;

                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return 0;

                object shell = null;
                try
                {
                    shell = Activator.CreateInstance(shellType);

                    foreach (var lnk in Directory.GetFiles(desktop, "*.lnk"))
                    {
                        object shortcut = null;
                        try
                        {
                            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                            if (shortcut == null) continue;

                            var type = shortcut.GetType();
                            var target = type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
                            if (string.IsNullOrEmpty(target)) continue;
                            if (target.IndexOf("AppForge-payload", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (File.Exists(target)) continue;   // 还活着，不动

                            SetProperty(type, shortcut, "TargetPath", currentExe);
                            SetProperty(type, shortcut, "WorkingDirectory", Path.GetDirectoryName(currentExe));
                            SetProperty(type, shortcut, "IconLocation", currentExe + ",0");
                            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                            repaired++;
                            if (log != null) log.Info("已修复失效的桌面快捷方式：" + lnk + " → " + currentExe);
                        }
                        catch
                        {
                            // 单个快捷方式坏掉不影响其它
                        }
                        finally
                        {
                            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                        }
                    }
                }
                finally
                {
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
                }
            }
            catch
            {
                // 修复失败不影响启动
            }
            return repaired;
        }

        public static string CreateDesktop(AppMeta app, string exePath, Log log)
        {
            // 单文件版：指向稳定的启动器，避免换版本后快捷方式指向已清理的载荷目录
            exePath = ResolveStableLauncher(exePath);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                throw new InvalidOperationException("找不到桌面目录");

            var fileName = AppExporter.SanitizeFileName(app.Name) + ".lnk";
            var lnkPath = Path.Combine(desktop, fileName);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("创建快捷方式失败：系统未提供 WScript.Shell");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) throw new InvalidOperationException("创建快捷方式失败");

                var shortcutType = shortcut.GetType();
                SetProperty(shortcutType, shortcut, "TargetPath", exePath);
                SetProperty(shortcutType, shortcut, "Arguments", "--open-app=" + app.Id);
                SetProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(exePath));
                SetProperty(shortcutType, shortcut, "IconLocation", exePath + ",0");
                SetProperty(shortcutType, shortcut, "Description", app.Name + " · AppForge 造物台");
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

                log.Info("已创建桌面快捷方式：" + lnkPath);
                return lnkPath;
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        private static void SetProperty(Type type, object target, string name, string value)
        {
            try
            {
                type.InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value ?? string.Empty });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("设置快捷方式属性失败（" + name + "）：" + ex.Message);
            }
        }
    }
}
