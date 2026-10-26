using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Runtime;
using AppForge.Host.Windows;
using AppForge.Shared;

namespace AppForge.Host
{
    internal static class Program
    {
        /// <summary>
        /// 确保数据目录可写：默认位置 → exe 同目录 data → %TEMP%\AppForge\data，逐个尝试。
        /// （受限环境/受控文件夹访问下 %LOCALAPPDATA% 可能不可写，不能让程序因此起不来）
        /// </summary>
        private static string EnsureWritableWorkspace(ref Workspace workspace, string exeDir, ref bool fallbackUsed)
        {
            var first = workspace;
            var candidates = new List<Workspace>
            {
                first,
                new Workspace(System.IO.Path.Combine(exeDir, "data"), true),
                new Workspace(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppForge", "data"), true)
            };

            var errors = new System.Text.StringBuilder();
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.EnsureLayout();
                    if (!string.Equals(candidate.Root, first.Root, StringComparison.OrdinalIgnoreCase)) fallbackUsed = true;
                    workspace = candidate;
                    return null;
                }
                catch (Exception ex)
                {
                    errors.AppendLine("  · " + candidate.Root + " → " + ex.Message);
                }
            }

            return "数据目录不可写，已尝试所有候选位置：\r\n" + errors +
                   "\r\n可尝试用 --data-dir=<可写目录> 启动，或检查安全软件的「受控文件夹访问」设置。";
        }

        private const string MutexName = "Local\\AppForge.SingleInstance.v1";

        /// <summary>
        /// --open-folder=&lt;路径&gt;：只验证"打开目录"链路（外壳 → explorer → rundll32 → 剪贴板）。
        /// 用于在没有桌面会话的机器上排查 0xC0000142 这类环境问题。
        /// </summary>
        /// <summary>--unblock：把 exe 及同目录文件的"来自 Internet"标记清掉（SmartScreen 提示治理）。</summary>
        private static int RunUnblock(Diagnostics.Log log, int alreadyCleared)
        {
            var count = alreadyCleared + MotwCleaner.SelfUnblock();
            log.Info("--unblock 清除标记：" + count + " 个文件");
            ConsoleBridge.WriteLine("{\"mode\":\"unblock\",\"ok\":true,\"cleared\":" + count + "}");
            return 0;
        }

        private static int RunOpenFolder(string path, Diagnostics.Log log)
        {
            bool copied;
            var opened = Windows.FolderOpener.Open(path, out copied, log.Warn);
            var json = "{\"mode\":\"open-folder\",\"ok\":" + (opened ? "true" : "false") +
                       ",\"copied\":" + (copied ? "true" : "false") +
                       ",\"path\":\"" + (path ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}";
            ConsoleBridge.WriteLine(json);
            return opened ? 0 : 2;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            var cli = CliArgs.Parse(args);
            if (cli.CliMode) ConsoleBridge.Ensure();

            // 所有模式都关掉系统级硬错误弹窗（子进程 0xC0000142 之类只留退出码，不再弹框）
            ProcessErrorMode.Apply();

            // 自我解除"来自 Internet"标记：带该标记的未签名程序每次启动都会被 SmartScreen 拦一下
            var unblocked = MotwCleaner.SelfUnblock();

            if (cli.Help)
            {
                ConsoleBridge.WriteLine(CliArgs.Usage());
                return 0;
            }

            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var workspace = Workspace.Resolve(cli.DataDir, exeDir);

            Mutex mutex = null;
            var isFirst = true;
            if (!cli.CliMode)
            {
                mutex = new Mutex(true, MutexName, out isFirst);
                if (!isFirst)
                {
                    // 第二次启动（双击 exe / 桌面快捷方式）：先尝试 WM_COPYDATA 激活已有窗口
                    // （窗口隐藏到托盘时 MainWindowHandle 为 0，所以用 EnumWindows 找窗口）
                    var activated = false;
                    try { activated = SingleInstance.ActivateExisting(cli.OpenApp); }
                    catch { /* 忽略 */ }

                    if (!activated)
                    {
                        // 兜底：写交接文件，由已运行实例的定时器取走
                        try
                        {
                            workspace.EnsureLayout();
                            OpenRequest.Write(workspace, cli.OpenApp);
                        }
                        catch { /* 交接失败不阻塞 */ }
                    }

                    try
                    {
                        var bootLog = new Log(workspace.LogsDir);
                        bootLog.Info("检测到已有实例：activated=" + activated +
                                     " openApp=" + (cli.OpenApp ?? "-"));
                    }
                    catch { /* 忽略 */ }

                    return 0;
                }
            }

            try
            {
                // 数据目录可能不可写（只读配置/受管电脑/受限沙箱）→ 回退到 exe 同目录
                var fallbackUsed = false;
                var layoutError = EnsureWritableWorkspace(ref workspace, exeDir, ref fallbackUsed);
                if (layoutError != null)
                {
                    if (cli.CliMode)
                    {
                        ConsoleBridge.WriteLine("{\"mode\":\"startup\",\"ok\":false,\"error\":\"" +
                            layoutError.Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"}");
                    }
                    else
                    {
                        MessageBox.Show(layoutError + "\r\n\r\n可尝试用 --data-dir=<可写目录> 启动。",
                            Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return 3;
                }

                var log = new Log(workspace.LogsDir);
                if (fallbackUsed) log.Warn("默认数据目录不可写，已回退到：" + workspace.Root);
                if (unblocked > 0) log.Info("已清除 " + unblocked + " 个文件的「来自 Internet」标记（SmartScreen 不再提示）");
                log.Info("AppForge 启动，数据目录：" + workspace.Root + "，参数：" + CliArgs.MaskSecrets(args));

                if (cli.Check) return CliRunner.RunCheck(workspace, log);
                if (cli.Selftest) return CliRunner.RunSelftest(workspace, log);
                if (cli.Bench) return CliRunner.RunBench(workspace, log);
                if (cli.SetApiKey != null) return CliRunner.RunSetApiKey(workspace, log, cli.SetApiKey);
                if (cli.ListModels) return CliRunner.RunListModels(workspace, log);
                if (cli.CheckModel) return CliRunner.RunCheckModel(workspace, log);
                if (cli.UiDiag) return CliRunner.RunUiDiag(workspace, log);
                if (cli.Generate != null) return CliGen.Run(workspace, log, cli.Generate, cli.App);
                if (cli.OpenFolder != null) return RunOpenFolder(cli.OpenFolder, log);
                if (cli.Unblock) return RunUnblock(log, unblocked);

                // ---- 依赖自检（FR-0.4）----
                var deps = StartupChecks.RunAll(workspace);
                var blocking = deps.FirstOrDefault(d => d.Required && !string.Equals(d.State, "ok", StringComparison.Ordinal));
                if (blocking != null)
                {
                    log.Warn("依赖缺失：" + blocking.Name + " / " + blocking.Message);
                    if (string.Equals(blocking.Name, "webview2", StringComparison.Ordinal))
                    {
                        if (!WebView2Installer.GuideInstall(blocking, log))
                        {
                            MessageBox.Show(
                                "缺少 WebView2 运行时，程序无法显示界面。\r\n\r\n" +
                                "可访问 https://developer.microsoft.com/microsoft-edge/webview2/ 手动安装后重试。",
                                Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return 2;
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            blocking.DisplayName + " 检查未通过：\r\n" + blocking.Message,
                            Defaults.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 2;
                    }
                    deps = StartupChecks.RunAll(workspace);
                }

                var settingsStore = new SettingsStore(workspace);
                settingsStore.Load();
                var secrets = new SecretsStore(workspace);
                var repo = new AppRepository(workspace);
                repo.Load();
                SeedData.EnsureSeeded(workspace, repo);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 关掉系统级硬错误弹窗（如子进程 explorer.exe 起不来时的 0xc0000142 提示框），
                // 失败改由我们自己的日志 + 界面提示呈现。
                ProcessErrorMode.Apply();
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    log.Error("UI 线程未捕获异常", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    log.Error("未捕获异常", e.ExceptionObject as Exception);
                };

                using (var form = new ShellForm(workspace, log, settingsStore, secrets, repo, deps, cli.OpenApp))
                {
                    Application.Run(form);
                }
                return 0;
            }
            finally
            {
                if (mutex != null)
                {
                    try { if (isFirst) mutex.ReleaseMutex(); } catch { /* 忽略 */ }
                    mutex.Dispose();
                }
            }
        }
    }
}
