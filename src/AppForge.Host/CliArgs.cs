using System;
using System.Collections.Generic;

namespace AppForge.Host
{
    /// <summary>命令行参数（M1：--check / --selftest / --bench / --open-app / --data-dir）。</summary>
    public sealed class CliArgs
    {
        public string DataDir { get; private set; }
        public string OpenApp { get; private set; }
        public string SetApiKey { get; private set; }
        public string Generate { get; private set; }
        /// <summary>--open-folder=<路径>：只测试"打开目录"链路（无界面，输出 JSON）。</summary>
        public string OpenFolder { get; private set; }
        /// <summary>--unblock：清除本程序上的"来自 Internet"标记（解决 SmartScreen 反复提示）。</summary>
        public bool Unblock { get; private set; }
        /// <summary>配合 --gen 使用：对指定应用做增量修改。</summary>
        public string App { get; private set; }
        public bool Bench { get; private set; }
        public bool Selftest { get; private set; }
        public bool Check { get; private set; }
        public bool CheckModel { get; private set; }
        public bool ListModels { get; private set; }
        public bool UiDiag { get; private set; }
        public bool Help { get; private set; }
        /// <summary>由开机自启项带起（目前行为与正常启动一致，仅用于日志区分）。</summary>
        public bool AutoStartLaunch { get; private set; }

        /// <summary>无窗口的 CLI 模式（不抢单实例锁，便于并行测试）。</summary>
        public bool CliMode
        {
            get { return Check || Selftest || Bench || Help || CheckModel || ListModels || UiDiag || SetApiKey != null || Generate != null || OpenFolder != null || Unblock; }
        }

        public static CliArgs Parse(string[] args)
        {
            var result = new CliArgs();
            if (args == null) return result;

            foreach (var raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var arg = raw.Trim();
                var eq = arg.IndexOf('=');
                var name = eq >= 0 ? arg.Substring(0, eq) : arg;
                var value = eq >= 0 ? arg.Substring(eq + 1).Trim('"', ' ') : null;

                switch (name.ToLowerInvariant())
                {
                    case "--check":
                        result.Check = true;
                        break;
                    case "--selftest":
                        result.Selftest = true;
                        break;
                    case "--check-model":
                        result.CheckModel = true;
                        break;
                    case "--ui-diag":
                        result.UiDiag = true;
                        break;
                    case "--list-models":
                        result.ListModels = true;
                        break;
                    case "--set-api-key":
                        result.SetApiKey = value;
                        break;
                    case "--gen":
                        result.Generate = value;
                        break;
                    case "--app":
                        result.App = value;
                        break;
                    case "--open-folder":
                        result.OpenFolder = value;
                        break;
                    case "--unblock":
                        result.Unblock = true;
                        break;
                    case "--autostart":
                        result.AutoStartLaunch = true;
                        break;
                    case "--bench":
                        result.Bench = true;
                        break;
                    case "--open-app":
                        result.OpenApp = value;
                        break;
                    case "--data-dir":
                        result.DataDir = value;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        result.Help = true;
                        break;
                }
            }
            return result;
        }

        public static string Usage()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "AppForge 造物台",
                "",
                "用法：AppForge.exe [选项]",
                "",
                "  --open-app=<appId>   启动后直接打开指定应用",
                "  --data-dir=<路径>    指定数据目录（默认 %LOCALAPPDATA%\\AppForge）",
                "  --check              仅做依赖自检并输出 JSON",
                "  --selftest           运行无界面自检（数据层/路径/清单/密钥）",
                "  --bench              测量启动各阶段耗时并输出 JSON",
                "  --set-api-key=<key>  写入 API Key（DPAPI 加密存储；仅自动化用，推荐在设置页填写）",
                "  --check-model        用已保存的配置真实调用一次模型并输出结果",
                "  --list-models        拉取网关可用模型列表",
                "  --ui-diag            界面自检：启动界面、截图、采集 JS 错误后退出（需桌面会话）",
                "  --gen=\"<需求>\"        无界面跑一次完整生成（真模型+自测+固化），事件以 JSONL 输出",
                "  --app=<appId>        与 --gen 同时使用时，表示对已有应用做增量修改",
                "  --open-folder=<路径> 只测试\"打开目录\"链路（外壳/explorer/rundll32/剪贴板），输出 JSON",
                "  --unblock            清除本程序的\"来自 Internet\"标记（不再被 SmartScreen 拦）",
                "  --help               显示本帮助"
            });
        }

        /// <summary>
        /// 用于日志的命令行脱敏：密钥类参数一律替换为 ***，避免明文落日志。
        /// </summary>
        public static string MaskSecrets(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            var masked = new List<string>(args.Length);
            foreach (var raw in args)
            {
                if (raw == null) continue;
                var eq = raw.IndexOf('=');
                var name = eq >= 0 ? raw.Substring(0, eq) : raw;
                if (name.Equals("--set-api-key", StringComparison.OrdinalIgnoreCase) && eq >= 0)
                    masked.Add(name + "=***");
                else
                    masked.Add(raw);
            }
            return string.Join(" ", masked);
        }
    }
}
