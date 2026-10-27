using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using AppForge.Shared;

namespace AppForge.Host.Bootstrap
{
    /// <summary>
    /// 启动依赖自检（FR-0.4）：OS / .NET Framework / WebView2 / 数据目录。
    /// 正常机器全程 &lt; 10ms，且不联网。
    /// </summary>
    public static class StartupChecks
    {
        public const int MinWindowsBuild = 19045;   // Windows 10 22H2
        public const int MinDotNet48Release = 528040;

        public static IList<DepStatus> RunAll(Workspace ws)
        {
            var list = new List<DepStatus>();
            list.Add(CheckOs());
            list.Add(CheckDotNet());
            list.Add(CheckWebView2());
            list.Add(CheckDataDir(ws));
            return list;
        }

        public static DepStatus CheckOs()
        {
            var build = OsBuild();
            var status = new DepStatus
            {
                Name = "os",
                DisplayName = "Windows 系统版本",
                Version = OsDescription(),
                Required = true
            };

            if (build <= 0)
            {
                status.State = "missing";
                status.Message = "无法识别系统版本";
            }
            else if (build < MinWindowsBuild)
            {
                status.State = "unsupported";
                status.Message = "需要 Windows 10 22H2（19045）或 Windows 11";
            }
            else
            {
                status.State = "ok";
                status.Message = "系统版本受支持";
            }
            return status;
        }

        public static DepStatus CheckDotNet()
        {
            var release = DotNetRelease();
            var status = new DepStatus
            {
                Name = "dotnet48",
                DisplayName = ".NET Framework 4.8",
                Version = release.HasValue ? "Release " + release.Value : "未检测到",
                Required = true
            };

            if (!release.HasValue || release.Value < MinDotNet48Release)
            {
                status.State = "unsupported";
                status.Message = "需要 .NET Framework 4.8（Windows 10 1903+ / Windows 11 自带）";
            }
            else
            {
                status.State = "ok";
                status.Message = "系统自带，无需安装";
            }
            return status;
        }

        public static DepStatus CheckWebView2()
        {
            var version = WebView2Version();
            var status = new DepStatus
            {
                Name = "webview2",
                DisplayName = "Microsoft Edge WebView2 运行时",
                Version = string.IsNullOrEmpty(version) ? "未安装" : version,
                Required = true,
                State = string.IsNullOrEmpty(version) ? "missing" : "ok",
                Message = string.IsNullOrEmpty(version)
                    ? "当前系统缺少 WebView2 运行时，可一键静默安装（无需管理员）"
                    : "系统已有，无需安装"
            };
            return status;
        }

        public static DepStatus CheckDataDir(Workspace ws)
        {
            var status = new DepStatus
            {
                Name = "data",
                DisplayName = "数据目录",
                Version = ws.Root,
                Required = true
            };
            try
            {
                // 与 Program.EnsureWritableWorkspace 用同一套判据（真写一个文件），
                // 避免"启动时选了这个目录、检查时又说不可写"的两套标准。
                ws.ProbeWritable();
                status.State = "ok";
                status.Message = ws.Portable ? "便携模式" : "可写";
            }
            catch (Exception ex)
            {
                status.State = "missing";
                status.Message = "目录不可写：" + ex.Message +
                                 "（常见原因：火绒/360 的文件保护、Defender 受控文件夹访问把本程序挡在写文件之前）";
            }
            return status;
        }

        // ---------- 底层探测 ----------

        public static int OsBuild()
        {
            try
            {
                var raw = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "CurrentBuildNumber", null) as string;
                int build;
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out build))
                    return build;

                return Environment.OSVersion.Version.Build;
            }
            catch
            {
                return 0;
            }
        }

        public static string OsDescription()
        {
            var build = OsBuild();
            var name = build >= 22000 ? "Windows 11" : build >= 10240 ? "Windows 10" : "Windows";
            try
            {
                var ubr = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "UBR", null);
                return name + "（Build " + build + (ubr != null ? "." + ubr : string.Empty) + "）";
            }
            catch
            {
                return name + "（Build " + build + "）";
            }
        }

        public static int? DotNetRelease()
        {
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                    "Release", null);
                if (v == null) return null;
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        public static string WebView2Version()
        {
            // 演练用：模拟"系统缺少 WebView2"，验证首启安装引导与降级路径
            if (string.Equals(Environment.GetEnvironmentVariable("APPFORGE_SIMULATE_NO_WEBVIEW2"), "1",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
            catch
            {
                return null;
            }
        }
    }
}
