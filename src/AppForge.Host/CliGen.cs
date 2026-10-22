using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AppForge.Host.Ai;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Gen;
using AppForge.Shared;
using Microsoft.Web.WebView2.Core;

namespace AppForge.Host
{
    /// <summary>
    /// `--gen="&lt;需求&gt;"`：无界面跑完整生成闭环（真模型 + 真自测 + 固化），事件以 JSONL 打印。
    /// 用途：自动化验收 / 排障，不需要人工点界面。
    /// </summary>
    public static class CliGen
    {
        public static int Run(Workspace ws, Log log, string prompt, string appId)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-gen.log"));

            if (string.IsNullOrWhiteSpace(prompt))
            {
                Emit(new Dictionary<string, object> { { "mode", "gen" }, { "ok", false }, { "error", "缺少需求描述" } });
                return 2;
            }

            if (!new SecretsStore(ws).HasApiKey())
            {
                Emit(new Dictionary<string, object> { { "mode", "gen" }, { "ok", false }, { "error", "尚未配置 API Key" } });
                return 2;
            }

            var exitCode = 1;
            var host = new GenHostForm();

            host.Shown += async delegate
            {
                try
                {
                    var deps = StartupChecks.RunAll(ws);
                    var blocking = deps.FirstOrDefault(d => d.Required && !string.Equals(d.State, "ok", StringComparison.Ordinal));
                    if (blocking != null)
                    {
                        Emit(new Dictionary<string, object>
                        {
                            { "mode", "gen" }, { "ok", false },
                            { "error", "依赖检查未通过：" + blocking.DisplayName + " " + blocking.Message }
                        });
                        return;
                    }

                    var environment = await CoreWebView2Environment.CreateAsync(null, ws.WebViewDir, null);
                    var settings = new SettingsStore(ws);
                    settings.Load();
                    var secrets = new SecretsStore(ws);
                    var repo = new AppRepository(ws);
                    repo.Load();
                    SeedData.EnsureSeeded(ws, repo);

                    Action<string, string, string, string, IDictionary<string, object>> emit =
                        delegate(string kind, string step, string status, string message, IDictionary<string, object> extra)
                        {
                            var d = new Dictionary<string, object> { { "mode", "gen" }, { "kind", kind } };
                            if (step != null) d["step"] = step;
                            if (status != null) d["status"] = status;
                            if (message != null) d["message"] = message;
                            if (extra != null)
                            {
                                foreach (var kv in extra) d[kv.Key] = kv.Value;
                            }
                            Emit(d);
                        };

                    var engine = new GenerationEngine(ws, log, settings, secrets, repo, environment,
                        delegate(string name, object payload) { EmitPayload(payload); });

                    var outcome = string.IsNullOrEmpty(appId)
                        ? await engine.RunOnceAsync(prompt, emit, CancellationToken.None)
                        : await engine.RunOnceModifyAsync(appId, prompt, emit, CancellationToken.None);

                    var done = new Dictionary<string, object>
                    {
                        { "mode", "gen" }, { "ok", true },
                        { "appId", outcome.AppId }, { "name", outcome.Name },
                        { "entry", outcome.Entry }, { "fileCount", outcome.FileCount },
                        { "seq", outcome.Seq }, { "op", outcome.Mode },
                        { "noChange", outcome.NoChange },
                        { "tokensIn", outcome.TokensIn }, { "tokensOut", outcome.TokensOut }
                    };
                    Emit(done);
                    exitCode = 0;
                }
                catch (Exception ex)
                {
                    Emit(new Dictionary<string, object> { { "mode", "gen" }, { "ok", false }, { "error", ex.Message } });
                    log.Error("CLI 生成失败", ex);
                    exitCode = 1;
                }
                finally
                {
                    try { host.Close(); } catch { /* 忽略 */ }
                }
            };

            Application.Run(host);
            return exitCode;
        }

        private static void Emit(IDictionary<string, object> payload)
        {
            ConsoleBridge.WriteLine(JsonLite.Serialize(payload));
        }

        private static void EmitPayload(object payload)
        {
            if (payload != null) ConsoleBridge.WriteLine(JsonLite.Serialize(payload));
        }

        /// <summary>只用于挂消息循环的隐藏窗口（自测需要 WebView2 消息泵）。</summary>
        private sealed class GenHostForm : Form
        {
            public GenHostForm()
            {
                Text = "AppForge CLI";
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-4000, -4000);
                Size = new Size(1, 1);
                FormBorderStyle = FormBorderStyle.None;
            }
        }
    }
}
