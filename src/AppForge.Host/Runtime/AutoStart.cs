using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using AppForge.Shared;

namespace AppForge.Host.Runtime
{
    /// <summary>开机自启：写 HKCU\...\Run（用户级，不需要管理员），可随时关闭。</summary>
    public static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AppForge";

        public static string ExecutablePath
        {
            get
            {
                try
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        return process.MainModule != null ? process.MainModule.FileName : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按设置同步注册表；返回实际状态。</summary>
        public static bool Apply(bool enabled)
        {
            var exe = ExecutablePath;
            if (enabled && string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("无法定位程序路径，开机自启设置失败");

            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new InvalidOperationException("无法写入开机启动项（注册表不可写）");
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + exe + "\" --autostart", RegistryValueKind.String);
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            return IsEnabled();
        }
    }

    /// <summary>
    /// 单实例交接：第二次启动（如桌面快捷方式 --open-app=xxx）把请求写入文件，
    /// 已在运行的实例轮询取走后直接打开该应用。
    /// </summary>
    public static class OpenRequest
    {
        public static string FilePath(Workspace ws)
        {
            return Path.Combine(ws.DataDir, "open-request.json");
        }

        public static void Write(Workspace ws, string appId)
        {
            try
            {
                var payload = JsonLite.New();
                payload["appId"] = appId;
                payload["ts"] = TimeUtil.Now();
                JsonStore.WriteAtomic(FilePath(ws), JsonLite.Serialize(payload));
            }
            catch
            {
                // 交接失败不影响主流程
            }
        }

        public static string Take(Workspace ws)
        {
            var path = FilePath(ws);
            try
            {
                var json = JsonStore.ReadOrNull(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                JsonStore.TryDelete(path);
                var payload = JsonLite.ParseObject(json);
                return JsonLite.Str(payload, "appId");
            }
            catch
            {
                JsonStore.TryDelete(path);
                return null;
            }
        }
    }
}
