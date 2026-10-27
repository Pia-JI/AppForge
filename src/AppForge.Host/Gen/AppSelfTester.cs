using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
        /// <summary>页面可见文字长度（已去掉空白），用于区分“HTML 很长但实际白屏”。</summary>
        public int VisibleTextLength { get; set; }
        /// <summary>页面上是否有实际占位的 canvas/svg/img/video/iframe。</summary>
        public int VisualElementCount { get; set; }
        public bool RootFound { get; set; }
        public string Title { get; set; }
        public string Failure { get; set; }
        /// <summary>true 表示失败原因是运行环境（WebView2 起不来），不是应用代码有问题。</summary>
        public bool EnvironmentFailure { get; set; }
        /// <summary>true 表示这是"静态检查"结果（环境降级路径）。</summary>
        public bool StaticCheck { get; set; }

        public SelfTestResult()
        {
            Errors = string.Empty;
            Failure = string.Empty;
        }
    }

    /// <summary>
    /// 生成物自测：把草稿目录挂到独立的虚拟主机，在隐藏窗口里真跑一遍。
    /// 采集：导航失败 / 页面 JS 报错 / 未捕获 Promise / 白屏 / data-app-root 缺失。
    ///
    /// 提速要点：隐藏窗口 + WebView2 只建一次（<see cref="SelfTestSession"/>），
    /// 修复循环里的多次自测复用同一个会话；加载完成后不再固定等待，改成轮询页面是否就绪。
    /// </summary>
    public sealed class AppSelfTester : IDisposable
    {
        /// <summary>页面加载完成后最多再等多久让初始化逻辑跑完（轮询，满足即走）。</summary>
        public const int ReadyWaitMs = 2000;
        /// <summary>就绪轮询间隔。</summary>
        public const int ReadyPollMs = 100;
        /// <summary>建窗（EnsureCoreWebView2Async）超时：WebView2 起不来时不能把整次生成拖死。</summary>
        public const int SessionCreateTimeoutMs = 15000;

        internal const string ErrorCollector =
            "(function(){window.__afTestErrors=[];" +
            "window.addEventListener('error',function(e){try{window.__afTestErrors.push(String(e.message)+' @'+(e.lineno||0));}catch(_){}});" +
            "window.addEventListener('unhandledrejection',function(e){try{window.__afTestErrors.push('unhandledrejection: '+String(e.reason));}catch(_){}});" +
            "var ce=console.error;console.error=function(){try{window.__afTestErrors.push(Array.prototype.join.call(arguments,' '));}catch(_){};return ce.apply(console,arguments);};})();";

        private readonly Log _log;
        private SelfTestSession _session;

        public AppSelfTester(Log log)
        {
            _log = log;
        }

        /// <summary>
        /// 取得（必要时创建）可复用的自测会话：同一个草稿目录 + 同一个 host 就复用隐藏窗口与 WebView2。
        /// 创建失败不抛异常，返回一个"不可用"的会话，让自测按失败处理（原因写进 Failure）。
        /// </summary>
        public async Task<SelfTestSession> EnsureSessionAsync(CoreWebView2Environment environment, string host, string folder)
        {
            var current = _session;
            if (current != null && !current.IsDisposed && current.Matches(host, folder)) return current;

            ReleaseSession();

            if (environment == null)
            {
                _log.Warn("自测跳过：WebView2 环境不可用");
                _session = SelfTestSession.CreateUnavailable(_log, host, folder, "WebView2 环境不可用（当前不支持的运行环境）");
                return _session;
            }

            _session = await SelfTestSession.CreateAsync(_log, environment, host, folder);
            return _session;
        }

        /// <summary>释放可复用的自测会话（本次生成结束/异常时调用）。</summary>
        public void ReleaseSession()
        {
            var current = _session;
            _session = null;
            if (current != null) current.Dispose();
        }

        /// <summary>兼容旧调用：一次性会话，测完立即释放。</summary>
        public async Task<SelfTestResult> TestAsync(
            CoreWebView2Environment environment, string draftDir, string entry, string host, int timeoutMs)
        {
            var session = await EnsureSessionAsync(environment, host, draftDir);
            try
            {
                return await session.TestAsync(entry, timeoutMs);
            }
            finally
            {
                ReleaseSession();
            }
        }

        public void Dispose()
        {
            ReleaseSession();
        }

        /// <summary>
        /// 判断异常是否属于"运行环境问题"（WebView2/COM 起不来）而不是"应用代码问题"。
        /// 环境问题不该进自动修复环——模型重写代码也修不好，只会白烧时间与 token。
        /// </summary>
        public static bool IsEnvironmentException(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Runtime.InteropServices.COMException) return true;
                if (current is ObjectDisposedException) return true;
                var message = current.Message ?? string.Empty;
                if (message.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (message.IndexOf("灾难性故障", StringComparison.Ordinal) >= 0) return true;
                if (message.IndexOf("0x8000FFFF", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (message.IndexOf("E_UNEXPECTED", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 自测环境不可用时的降级校验：不跑页面，只做磁盘上的静态检查。
        /// 目的：宁可给出"静态校验通过（未真跑）"的可用应用，也不要因为环境问题让用户一次都生成不出来。
        /// </summary>
        public static SelfTestResult StaticCheck(string draftDir, string entry, IDictionary<string, string> files)
        {
            var result = new SelfTestResult { StaticCheck = true };
            var path = string.IsNullOrEmpty(entry) ? "index.html" : entry;

            string content = null;
            if (files != null && files.TryGetValue(path, out content) && content == null) content = null;
            if (content == null)
            {
                try
                {
                    var full = System.IO.Path.Combine(draftDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (System.IO.File.Exists(full)) content = System.IO.File.ReadAllText(full);
                }
                catch
                {
                    content = null;
                }
            }

            if (content == null)
            {
                result.Failure = "静态校验失败：入口文件 " + path + " 不存在";
                return result;
            }
            if (content.Trim().Length < 120)
            {
                result.Failure = "静态校验失败：入口文件内容过少（" + content.Trim().Length + " 字符）";
                return result;
            }

            var visible = StripTags(content);
            result.BodyLength = visible.Length;
            result.RootFound = content.IndexOf("data-app-root", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!result.RootFound) result.Failure = "静态校验：缺少 data-app-root 标记";
            else if (visible.Length < 20) result.Failure = "静态校验：页面几乎没有可见文字";

            result.Pass = string.IsNullOrEmpty(result.Failure);
            if (result.Pass)
            {
                result.Title = "静态校验通过";
                result.Errors = string.Empty;
            }
            return result;
        }

        /// <summary>粗略去掉标签与脚本样式，估算可见文字长度（只为判断"不是空壳"）。</summary>
        private static string StripTags(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;

            var sb = new System.Text.StringBuilder(html.Length);
            var inTag = false;
            for (var i = 0; i < html.Length; i++)
            {
                var ch = html[i];
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (inTag) continue;
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>白屏判定：不能再只靠 innerHTML 长度，小应用可能只有几十个字符。</summary>
        internal static bool LooksBlank(int bodyLength, int visibleTextLength, int visualElementCount)
        {
            return bodyLength < 80 && visibleTextLength == 0 && visualElementCount == 0;
        }
    }

    /// <summary>
    /// 可复用的自测会话：隐藏 Form + WebView2 + 虚拟主机映射，只在首次创建时付一次代价，
    /// 后续每次 TestAsync 只是导航 + 采集（省掉每次新建窗口的 0.3~1s）。
    /// </summary>
    public sealed class SelfTestSession : IDisposable
    {
        private readonly Log _log;
        private string _unavailable;   // 非空表示会话不可用（建窗失败/环境缺失）

        private Form _form;
        private WebView2 _webView;
        private CoreWebView2 _core;

        private bool _disposed;

        public string Host { get; private set; }
        public string Folder { get; private set; }

        public bool IsDisposed
        {
            get { return _disposed; }
        }

        private SelfTestSession(Log log, string host, string folder)
        {
            _log = log;
            Host = host;
            Folder = folder;
        }

        public bool Matches(string host, string folder)
        {
            return string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase);
        }

        internal static SelfTestSession CreateUnavailable(Log log, string host, string folder, string reason)
        {
            var session = new SelfTestSession(log, host, folder);
            session._unavailable = reason;
            return session;
        }

        internal static async Task<SelfTestSession> CreateAsync(
            Log log, CoreWebView2Environment environment, string host, string folder)
        {
            var session = new SelfTestSession(log, host, folder);
            var stopwatch = Stopwatch.StartNew();

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

            session._form = form;
            session._webView = webView;

            try
            {
                form.Show();

                // 建窗必须有超时：WebView2 初始化卡住（用户数据目录被占/浏览器进程残留）时
                // 不能把整次生成永久挂起，否则界面上只能看到计时器一直涨、取消也没反应。
                var ensure = webView.EnsureCoreWebView2Async(environment);
                var guard = await Task.WhenAny(ensure, Task.Delay(AppSelfTester.SessionCreateTimeoutMs));
                if (guard != ensure)
                    throw new TimeoutException("WebView2 初始化超过 " + AppSelfTester.SessionCreateTimeoutMs + "ms");
                await ensure;   // 让真实异常照常抛出

                var core = webView.CoreWebView2;
                session._core = core;

                VirtualHosts.MapDraft(core, host, folder);
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsWebMessageEnabled = false;

                // 错误采集脚本注册一次即可：每次新文档创建时都会自动执行
                await core.AddScriptToExecuteOnDocumentCreatedAsync(AppSelfTester.ErrorCollector);

                stopwatch.Stop();
                log.Info("自测会话已就绪：" + host + " → " + folder + "（建窗耗时 " + stopwatch.ElapsedMilliseconds + "ms）");
                return session;
            }
            catch (Exception ex)
            {
                log.Error("创建自测会话失败", ex);
                session.Dispose();
                return CreateUnavailable(log, host, folder, "自测会话创建失败：" + ex.Message);
            }
        }

        /// <summary>跑一次自测：导航 → 等就绪（自适应轮询）→ 采集错误/白屏/data-app-root。</summary>
        public async Task<SelfTestResult> TestAsync(string entry, int timeoutMs)
        {
            var result = new SelfTestResult();
            if (!string.IsNullOrEmpty(_unavailable))
            {
                result.Failure = _unavailable;
                result.EnvironmentFailure = true;
                return result;
            }
            if (_disposed || _core == null)
            {
                result.Failure = "自测会话已释放";
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = delegate(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                try { _core.NavigationCompleted -= handler; } catch { /* 忽略 */ }
                if (!e.IsSuccess) result.Failure = "页面加载失败：" + e.WebErrorStatus;
                completed.TrySetResult(e.IsSuccess);
            };
            _core.NavigationCompleted += handler;

            try
            {
                _core.Navigate(BuildUrl(entry));

                var finished = await Task.WhenAny(completed.Task, Task.Delay(timeoutMs));
                if (finished != completed.Task)
                {
                    result.Failure = "自测超时（" + timeoutMs + "ms 未完成加载）";
                    try { _core.Stop(); } catch { /* 忽略 */ }
                    stopwatch.Stop();
                    _log.Warn("自测超时：" + entry + "，耗时 " + stopwatch.ElapsedMilliseconds + "ms");
                    return result;
                }

                if (!completed.Task.Result)   // 导航失败，Failure 已在事件里写好
                {
                    stopwatch.Stop();
                    _log.Info("自测耗时 " + stopwatch.ElapsedMilliseconds + "ms（导航失败）");
                    return result;
                }

                await WaitForReadyAsync();
                await CollectAsync(result);
            }
            catch (Exception ex)
            {
                result.Failure = "自测异常：" + ex.Message;
                result.EnvironmentFailure = AppSelfTester.IsEnvironmentException(ex);
                _log.Error("自测异常", ex);
            }
            finally
            {
                stopwatch.Stop();
                _log.Info("自测耗时 " + stopwatch.ElapsedMilliseconds + "ms（" + entry +
                          "，pass=" + result.Pass + (string.IsNullOrEmpty(result.Failure) ? "" : "，" + result.Failure) + "）");
                try { _core.NavigationCompleted -= handler; } catch { /* 忽略 */ }
            }

            return result;
        }

        private async Task WaitForReadyAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < AppSelfTester.ReadyWaitMs)
            {
                var ready = false;
                try
                {
                    var probe = await _core.ExecuteScriptAsync(
                        "(function(){try{return !!(document.readyState==='complete'&&document.body&&document.body.innerHTML.length>50);}catch(_){return false;}})()");
                    ready = string.Equals((probe ?? string.Empty).Trim().Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _log.Warn("轮询页面就绪状态失败：" + ex.Message);
                    return;
                }

                if (ready) return;
                await Task.Delay(AppSelfTester.ReadyPollMs);
            }
            _log.Info("页面在 " + AppSelfTester.ReadyWaitMs + "ms 内未报告就绪，直接采集结果");
        }

        private async Task CollectAsync(SelfTestResult result)
        {
            var probe = await _core.ExecuteScriptAsync(
                "(function(){var errs=window.__afTestErrors||[];" +
                "var b=document.body,text=b?(b.innerText||b.textContent||''):'';" +
                "var visuals=0,nodes=document.querySelectorAll('canvas,svg,img,video,iframe');" +
                "for(var i=0;i<nodes.length;i++){var el=nodes[i],r=el.getBoundingClientRect(),s=getComputedStyle(el);" +
                "if(r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'&&s.opacity!=='0')visuals++;}" +
                "return JSON.stringify({errors:errs.slice(0,12),bodyLen:b?b.innerHTML.length:0,textLen:text.replace(/\\s+/g,'').length,visuals:visuals," +
                "root:!!document.querySelector('[data-app-root]'),title:document.title||''});})()");

            var text = Unquote(probe);
            var obj = JsonLite.ParseObject(text);

            result.BodyLength = JsonLite.Int(obj, "bodyLen");
            result.VisibleTextLength = JsonLite.Int(obj, "textLen");
            result.VisualElementCount = JsonLite.Int(obj, "visuals");
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

            // 小应用可以只有几十个字符，不能按固定 200 字符误判白屏。
            // 只有“DOM 极小 + 没有可见文字 + 没有可见图形”三者同时成立，才认为是白屏。
            if (string.IsNullOrEmpty(result.Failure) &&
                AppSelfTester.LooksBlank(result.BodyLength, result.VisibleTextLength, result.VisualElementCount))
                result.Failure = "页面内容过少（疑似白屏）";

            if (string.IsNullOrEmpty(result.Failure) && !result.RootFound)
                result.Failure = "缺少 data-app-root 标记（无法判定渲染成功）";

            result.Pass = string.IsNullOrEmpty(result.Failure);
        }

        /// <summary>每次导航带一个时间戳参数，避免 WebView2 复用会话时命中上一次的缓存。</summary>
        private string BuildUrl(string entry)
        {
            var path = string.IsNullOrEmpty(entry) ? "index.html" : entry.Trim().TrimStart('/');
            var separator = path.IndexOf('?') >= 0 ? "&" : "?";
            return "https://" + Host + "/" + path + separator + "__af=" +
                   DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_form != null && _form.IsHandleCreated && _form.InvokeRequired)
                {
                    _form.Invoke(new Action(DisposeCore));   // 必须在创建窗口的 UI 线程上释放
                }
                else
                {
                    DisposeCore();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("释放自测窗口失败：" + ex.Message);
                DisposeCore();
            }
        }

        private void DisposeCore()
        {
            var form = _form;
            var webView = _webView;
            _form = null;
            _webView = null;
            _core = null;

            try { if (form != null) form.Hide(); } catch { /* 忽略 */ }
            try { if (webView != null) webView.Dispose(); } catch { /* 忽略 */ }
            try { if (form != null) form.Dispose(); } catch { /* 忽略 */ }
        }
    }
}
