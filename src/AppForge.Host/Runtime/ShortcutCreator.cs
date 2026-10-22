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
    /// </summary>
    public static class ShortcutCreator
    {
        public static string CreateDesktop(AppMeta app, string exePath, Log log)
        {
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
