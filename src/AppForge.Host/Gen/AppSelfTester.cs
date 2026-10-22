using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AppForge.Shared;
using AppForge.Host.Diagnostics;
using AppForge.Host.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AppForge.Host.Gen
{
    public sealed class SelfTestResult
    {
        public bool Pass { get; set; }
        public string Errors { get; set; }
        public int BodyLength { get; set; }
        public bool RootFound { get; set; }
        public string Title { get; set; }
        public string Failure { get; set; }

        public SelfTestResult()
        {
            Errors = string.Empty;
            Failure = string.Empty;
        }
    }

    /// <summary>
    /// 生成物自测：把草稿目录挂到独立的虚拟主机，在隐藏窗口里真跑一遍。
    /// 采集：导航失败 / 页面 JS 报错 / 未捕获 Promise / 白屏 / data-app-root 缺失。
    /// </summary>
    public sealed class AppSelfTester
    {
        private const string ErrorCollector =
            "(function(){window.__afTestErrors=[];" +
            "window.addEventListener('error',function(e){try{window.__afTestErrors.push(String(e.message)+' @'+(e.lineno||0));}catch(_){}});" +
            "window.addEventListener('unhandledrejection',function(e){try{window.__afTestErrors.push('unhandledrejection: '+String(e.reason));}catch(_){}});" +
            "var ce=console.error;console.error=function(){try{window.__afTestErrors.push(Array.prototype.join.call(arguments,' '));}catch(_){};return ce.apply(console,arguments);};})();";

        private readonly Log _log;

        public AppSelfTester(Log log)
        {
            _log = log;
        }

        public async Task<SelfTestResult> TestAsync(CoreWebView2Environment environment, string draftDir, string entry, string host, int timeoutMs)
        {
            var result = new SelfTestResult();
            if (environment == null)
            {
                result.Failure = "WebView2 环境不可用（当前不支持的运行环境）";
                return result;
            }

            var form = new Form
            {
                Text = "AppForge 自测",
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),   // 屏幕外，用户不可见
                Size = new Size(1000, 760),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            var webView = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(webView);

            var completed = new TaskCompletionSource<bool>();
            try
            {
                form.Show();
                await webView.EnsureCoreWebView2Async(environment);
                var core = webView.CoreWebView2;

                VirtualHosts.MapDraft(core, host, draftDir);
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsWebMessageEnabled = false;

                await core.AddScriptToExecuteOnDocumentCreatedAsync(ErrorCollector);

                core.NavigationCompleted += delegate(object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess)
                    {
                        result.Failure = "页面加载失败：" + e.WebErrorStatus;
                    }
                    completed.TrySetResult(e.IsSuccess);
                };

                core.Navigate("https://" + host + "/" + entry);

                var finished = await Task.WhenAny(completed.Task, Task.Delay(timeoutMs));
                if (finished != completed.Task)
                {
                    result.Failure = "自测超时（" + timeoutMs + "ms 未完成加载）";
                    return result;
                }

                // 给页面初始化逻辑一点时间（定时器/异步渲染）
                await Task.Delay(700);

                var probe = await core.ExecuteScriptAsync(
                    "(function(){var errs=window.__afTestErrors||[];" +
                    "return JSON.stringify({errors:errs.slice(0,12),bodyLen:document.body?document.body.innerHTML.length:0," +
                    "root:!!document.querySelector('[data-app-root]'),title:document.title||''});})()");

                var text = Unquote(probe);
                var obj = JsonLite.ParseObject(text);

                result.BodyLength = JsonLite.Int(obj, "bodyLen");
                result.RootFound = JsonLite.Bool(obj, "root");
                result.Title = JsonLite.Str(obj, "title");
                var errors = JsonLite.List(obj, "errors");
                var errorList = new List<string>();
                if (errors != null)
                {
                    foreach (var item in errors)
                    {
                        var line = item == null ? string.Empty : item.ToString();
                        if (!string.IsNullOrWhiteSpace(line)) errorList.Add("- " + line);
                    }
                }
                result.Errors = string.Join("\n", errorList);

                if (string.IsNullOrEmpty(result.Failure) && errorList.Count > 0)
                    result.Failure = "页面有 JS 错误";

                if (string.IsNullOrEmpty(result.Failure) && result.BodyLength < 200)
                    result.Failure = "页面内容过少（疑似白屏）";

                if (string.IsNullOrEmpty(result.Failure) && !result.RootFound)
                    result.Failure = "缺少 data-app-root 标记（无法判定渲染成功）";

                result.Pass = string.IsNullOrEmpty(result.Failure);
                return result;
            }
            catch (Exception ex)
            {
                result.Failure = "自测异常：" + ex.Message;
                _log.Error("自测异常", ex);
                return result;
            }
            finally
            {
                try { form.Hide(); } catch { /* 忽略 */ }
                try { webView.Dispose(); } catch { /* 忽略 */ }
                try { form.Dispose(); } catch { /* 忽略 */ }
            }
        }

        /// <summary>ExecuteScriptAsync 的结果是 JSON 编码的字符串，这里还原成普通文本。</summary>
        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                try { return JsonLite.Deserialize<string>(trimmed) ?? string.Empty; }
                catch { return trimmed; }
            }
            return trimmed;
        }
    }
}
