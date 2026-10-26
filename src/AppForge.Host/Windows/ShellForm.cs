﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AppForge.Shared;
using AppForge.Host.Ai;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Gen;
using AppForge.Host.Runtime;
using AppForge.Host.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 主窗口：WinForms 只做窗口壳，全部像素由 WebView2 渲染 Shell UI。
    /// 启动顺序：建窗 → 共享 WebView2 环境 → 映射 shell 虚拟主机 → 导航 → 就绪打点。
    /// </summary>
    public sealed class ShellForm : Form
    {
        private readonly Workspace _ws;
        private readonly Log _log;
        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly AppRepository _repo;
        private readonly IList<DepStatus> _deps;
        private readonly string _startAppId;
        private readonly EmbeddedAssets _assets;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private WebView2 _webView;
        private Label _splash;
        private CoreWebView2Environment _environment;
        private RpcService _rpc;
        private GenerationEngine _engine;
        private NotifyIcon _tray;
        private Label _fallback;
        private bool _quitting;
        private bool _ready;
        private long _firstFrameMs = -1;
        private long _envReadyMs = -1;
        private long _navRequestedMs = -1;
        private long _navStartingMs = -1;
        private long _splashShownMs = -1;

        // ---- 全局快捷键（Ctrl+Alt+Space）----
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0xAF01;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkSpace = 0x20;
        private bool _hotkeyRegistered;
        private Timer _openRequestTimer;
        private bool? _pendingHotkey;

        /// <summary>注册/注销全局快捷键（Ctrl+Alt+Space 唤出或隐藏造物台）。</summary>
        public void ApplyHotkey(bool enabled)
        {
            if (!IsHandleCreated)
            {
                _pendingHotkey = enabled;
                return;
            }

            try
            {
                if (_hotkeyRegistered)
                {
                    UnregisterHotKey(Handle, HotkeyId);
                    _hotkeyRegistered = false;
                }
                if (enabled)
                    _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt, VkSpace);
                _log.Info("全局快捷键 " + (enabled ? (_hotkeyRegistered ? "已注册 Ctrl+Alt+Space" : "注册失败（可能被其他程序占用）") : "已关闭"));
            }
            catch (Exception ex)
            {
                _log.Warn("注册全局快捷键失败：" + ex.Message);
            }
        }

        private void ToggleVisibility()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleVisibility();
                return;
            }

            // 第二次启动（双击 exe / 桌面快捷方式）通过 WM_COPYDATA 传来"唤出 / 打开某应用"
            if (m.Msg == SingleInstance.WmCopyData)
            {
                string command;
                if (SingleInstance.TryReadCommand(m.LParam, out command))
                {
                    _log.Info("收到激活请求：" + (string.IsNullOrEmpty(command) ? "activate" : command));
                    BeginInvoke(new Action(delegate
                    {
                        RestoreFromTray();
                        if (!string.IsNullOrEmpty(command) && command != SingleInstance.ActivateCommand)
                        {
                            try { OpenAppInternal(command); }
                            catch (Exception ex) { _log.Warn("激活后打开应用失败：" + ex.Message); }
                        }
                    }));
                    m.Result = new IntPtr(1);
                    return;
                }
            }

            base.WndProc(ref m);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>原生启动画面可见的时刻（用户感知的"点了有反应"）。</summary>
        public long SplashShownMs { get { return _splashShownMs; } }

        public bool BenchMode { get; set; }
        public event Action<long> FirstFrameShown;
        public event Action<string> InitFailed;

        /// <summary>界面自检模式：截图 + 采集 JS 错误，用于无人工介入地验证渲染。</summary>
        public bool DiagMode { get; set; }
        public string DiagOutDir { get; set; }
        public event Action<string> DiagFinished;

        public long FirstFrameMs { get { return _firstFrameMs; } }

        public ShellForm(
            Workspace workspace,
            Log log,
            SettingsStore settings,
            SecretsStore secrets,
            AppRepository repo,
            IList<DepStatus> deps,
            string startAppId)
        {
            _ws = workspace;
            _log = log;
            _settings = settings;
            _secrets = secrets;
            _repo = repo;
            _deps = deps;
            _startAppId = startAppId;
            _assets = new EmbeddedAssets(AppDomain.CurrentDomain.BaseDirectory);

            Text = Defaults.ProductName;
            BackColor = Color.FromArgb(22, 24, 28);
            Icon = SystemIcons.Application;
            AutoScaleMode = AutoScaleMode.None; // 尺寸由 DpiHelper 手动换算，避免 WinForms 双重缩放
            StartPosition = FormStartPosition.Manual;

            var s = _settings.Current;
            var logicalWidth = s.WindowWidth > 0 ? s.WindowWidth : 1180;
            var logicalHeight = s.WindowHeight > 0 ? s.WindowHeight : 780;

            MinimumSize = new Size(DpiHelper.Scaled(880), DpiHelper.Scaled(600));
            ClientSize = ClampToWorkingArea(new Size(DpiHelper.Scaled(logicalWidth), DpiHelper.Scaled(logicalHeight)));

            var savedBounds = new Rectangle(new Point(s.WindowX, s.WindowY), Size);
            if (s.WindowX != int.MinValue && s.WindowY != int.MinValue && IsOnScreen(savedBounds))
            {
                Location = new Point(s.WindowX, s.WindowY);
            }
            else
            {
                var area = WorkingArea;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }

            BuildUi();
        }

        private static Rectangle WorkingArea
        {
            get
            {
                var screen = Screen.PrimaryScreen;
                return screen != null ? screen.WorkingArea : new Rectangle(0, 0, 1920, 1080);
            }
        }

        private static Size ClampToWorkingArea(Size preferred)
        {
            var area = WorkingArea;
            var maxWidth = Math.Max(DpiHelper.Scaled(640), area.Width - DpiHelper.Scaled(40));
            var maxHeight = Math.Max(DpiHelper.Scaled(480), area.Height - DpiHelper.Scaled(40));
            return new Size(Math.Min(preferred.Width, maxWidth), Math.Min(preferred.Height, maxHeight));
        }

        private static bool IsOnScreen(Rectangle bounds)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            }
            return false;
        }

        private void BuildUi()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            // 原生启动画面：WebView2 冷启动期间先给用户一个画面（约 100ms 内可见），避免"点了没反应"
            _splash = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(22, 24, 28),
                ForeColor = Color.FromArgb(190, 198, 210),
                Font = new Font("Microsoft YaHei UI", 11f),
                Text = Defaults.ProductName + "\r\n\r\n正在启动界面…"
            };
            Controls.Add(_splash);
            _splash.BringToFront();

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = Defaults.ProductName,
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开造物台", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Quit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreFromTray(); };

            Load += async delegate { await InitializeAsync(); };
            FormClosing += OnFormClosing;
            Shown += delegate
            {
                if (_splashShownMs < 0) _splashShownMs = _clock.ElapsedMilliseconds;
            };

            // 全局快捷键（句柄未创建时先挂起，创建后再注册）
            HandleCreated += delegate
            {
                if (_pendingHotkey.HasValue)
                {
                    var pending = _pendingHotkey.Value;
                    _pendingHotkey = null;
                    ApplyHotkey(pending);
                }
            };
            ApplyHotkey(_settings.Current.HotkeyEnabled);

            // 单实例交接：桌面快捷方式带 --open-app=xxx 启动时，由已运行实例响应
            _openRequestTimer = new Timer { Interval = 1200 };
            _openRequestTimer.Tick += delegate
            {
                var requestedAppId = OpenRequest.Take(_ws);
                if (requestedAppId == null) return; // 没有请求

                try
                {
                    _log.Info("收到文件交接请求：" + (string.IsNullOrEmpty(requestedAppId) ? "activate" : requestedAppId));
                    RestoreFromTray();
                    if (!string.IsNullOrEmpty(requestedAppId) && requestedAppId != SingleInstance.ActivateCommand)
                        OpenAppInternal(requestedAppId);
                }
                catch (Exception ex)
                {
                    _log.Warn("打开交接请求失败：" + ex.Message);
                }
            };
            _openRequestTimer.Start();
        }

        private async Task InitializeAsync()
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions("--disable-features=msWebOOUI,msPdfOOUI");
                _environment = await CoreWebView2Environment.CreateAsync(null, _ws.WebViewDir, options);
                await _webView.EnsureCoreWebView2Async(_environment);
                _envReadyMs = _clock.ElapsedMilliseconds;

                var core = _webView.CoreWebView2;
                var shellFolder = _assets.EnsureFolder(_ws);
                VirtualHosts.MapShell(core, shellFolder);

                core.Settings.AreDevToolsEnabled = true;   // Shell 保留（便于排错）
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;

                _engine = new GenerationEngine(_ws, _log, _settings, _secrets, _repo, _environment, PushEvent);

                _rpc = new RpcService(
                    _ws, _log, _settings, _secrets, _repo,
                    new OpenAiClient(_settings, _secrets, _log),
                    OpenAppInternal,
                    OpenFolderInternal,
                    _engine,
                    ApplyHotkey);

                core.WebMessageReceived += OnWebMessage;
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;
                core.NavigationStarting += delegate
                {
                    if (_navStartingMs < 0) _navStartingMs = _clock.ElapsedMilliseconds;
                };

                _navRequestedMs = _clock.ElapsedMilliseconds;
                core.Navigate(Hosts.ShellUrl("index.html"));
            }
            catch (Exception ex)
            {
                _log.Error("Shell 初始化失败", ex);
                var message = ex.Message;
                if (InitFailed != null) InitFailed(message);
                ShowFallback(message);
            }
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var core = _webView != null ? _webView.CoreWebView2 : null;
            if (core == null || _rpc == null) return;

            // 关键：UI 用 postMessage(object) 发送时，必须读 WebMessageAsJson；
            // 用 TryGetWebMessageAsString 会抛异常（之前所有 RPC 都卡在这里，界面因此空白）。
            string raw;
            try
            {
                raw = e.WebMessageAsJson;
            }
            catch
            {
                try { raw = e.TryGetWebMessageAsString(); }
                catch { return; }
            }

            if (string.IsNullOrWhiteSpace(raw)) return;

            // 如果 JS 发的是字符串（而非对象），取到的是带引号的 JSON 字符串，需要还原一次
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                try { trimmed = JsonLite.Deserialize<string>(trimmed); }
                catch { /* 保持原样 */ }
            }

            // UI 事件（如 JS 报错上报）：记录日志，不进 RPC 分发
            var probe = JsonLite.ParseObject(trimmed);
            var eventName = JsonLite.Str(probe, "event");
            if (!string.IsNullOrEmpty(eventName))
            {
                var payload = JsonLite.Obj(probe, "payload");
                _log.Error("UI 事件 " + eventName + "：" + JsonLite.Serialize(payload), null);
                return;
            }

            try
            {
                // 在 UI 线程上等待：await 期间消息循环照常运转，长请求不会冻界面；
                // 处理完成后回到 UI 线程再 PostWebMessageAsJson（WebView2 要求 UI 线程）。
                var response = await _rpc.Dispatcher.HandleAsync(trimmed);
                if (!IsDisposed && core != null) core.PostWebMessageAsJson(response);
            }
            catch (Exception ex)
            {
                _log.Error("消息处理失败", ex);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowFallback("页面加载失败：" + e.WebErrorStatus);
                return;
            }

            _ready = true;
            _firstFrameMs = _clock.ElapsedMilliseconds;

            if (_splash != null)
            {
                _splash.Visible = false;
                _splash.Dispose();
                _splash = null;
            }

            _log.Info("Shell 首帧就绪：" + _firstFrameMs + "ms" +
                      "（启动画面 " + _splashShownMs + "ms，环境 " + _envReadyMs + "ms，导航请求 " + _navRequestedMs + "ms，导航开始 " + _navStartingMs + "ms）");
            _log.Perf(_ws.PerfLogPath, new Dictionary<string, object>
            {
                { "type", "shell-ready" },
                { "ts", TimeUtil.Now() },
                { "splashMs", _splashShownMs },
                { "firstFrameMs", _firstFrameMs },
                { "envReadyMs", _envReadyMs },
                { "navRequestedMs", _navRequestedMs },
                { "navStartingMs", _navStartingMs },
                { "devMode", _assets.IsDevMode }
            });

            if (FirstFrameShown != null) FirstFrameShown(_firstFrameMs);

            if (!string.IsNullOrEmpty(_startAppId))
            {
                var id = _startAppId;
                BeginInvoke(new Action(delegate { OpenAppInternal(id); }));
            }

            if (BenchMode) BeginInvoke(new Action(Close));
            if (DiagMode) BeginInvoke(new Action(delegate { var ignored = RunDiagAsync(); }));
        }

        // ---------------- 界面自检（截图 + JS 错误采集） ----------------

        private async System.Threading.Tasks.Task RunDiagAsync()
        {
            var result = JsonLite.New();
            try
            {
                var dir = string.IsNullOrEmpty(DiagOutDir) ? _ws.LogsDir : DiagOutDir;
                Directory.CreateDirectory(dir);
                result["outDir"] = dir;
                result["dpi"] = "scale=" + DpiHelper.Scale.ToString("0.##") +
                                " deviceDpi=" + DeviceDpi +
                                " client=" + ClientSize.Width + "x" + ClientSize.Height +
                                " screen=" + WorkingArea.Width + "x" + WorkingArea.Height;

                await Task.Delay(1500);
                result["launcherState"] = await EvalAsync(
                    "(function(){var v=document.getElementById('view')||{};return JSON.stringify({viewHtmlLen:(v.innerHTML||'').length," +
                    "cards:document.querySelectorAll('.card').length,empty:document.querySelectorAll('.empty').length," +
                    "status:(document.getElementById('status-left')||{}).textContent,innerWidth:window.innerWidth," +
                    "innerHeight:window.innerHeight,devicePixelRatio:window.devicePixelRatio," +
                    "errors:(window.__afErrors||[]).slice(0,10)});})()");
                await CaptureAsync(Path.Combine(dir, "01-launcher.png"));

                // 页面侧耗时（判断慢在资源加载还是渲染）
                result["pagePerf"] = await EvalAsync(
                    "(function(){var t=performance.timing;var r=performance.getEntriesByType('resource')||[];" +
                    "return JSON.stringify({dcl:t.domContentLoadedEventEnd-t.navigationStart," +
                    "load:t.loadEventEnd-t.navigationStart,resCount:r.length," +
                    "resTotal:Math.round(r.reduce(function(a,x){return a+x.duration;},0))," +
                    "slowest:r.slice().sort(function(a,b){return b.duration-a.duration;}).slice(0,5)" +
                    ".map(function(x){return [x.name.split('/').pop(),Math.round(x.duration)];})});})()");

                await EvalAsync("(function(){location.hash='#/settings';return 'ok';})()");
                await Task.Delay(1500);
                result["settingsState"] = await EvalAsync(
                    "(function(){var v=document.getElementById('view')||{};return JSON.stringify({viewHtmlLen:(v.innerHTML||'').length," +
                    "panels:document.querySelectorAll('.panel').length,inputs:document.querySelectorAll('input').length," +
                    "buttons:document.querySelectorAll('button').length,errors:(window.__afErrors||[]).slice(0,10)});})()");
                await CaptureAsync(Path.Combine(dir, "02-settings.png"));

                // 模型下拉框可用性验证：写入缓存 → 重渲染设置页 → 模拟选择 → 断言手填框同步
                await EvalAsync(
                    "(function(){try{localStorage.setItem('af.models'," +
                    "JSON.stringify(['gpt-4o-mini','deepseek-v4.1-flash','glm-5.3-flash','kimi-k2.7']));}catch(e){}" +
                    "location.hash='#/launcher';return 1;})()");
                await Task.Delay(500);
                await EvalAsync("(function(){location.hash='#/settings';return 1;})()");
                await Task.Delay(1500);
                result["modelSelect"] = await EvalAsync(
                    "(function(){var s=document.getElementById('set-model-select');var i=document.getElementById('set-model');" +
                    "if(!s||!i)return 'no-select';var count=s.options.length;var target='glm-5.3-flash';" +
                    "s.value=target;s.dispatchEvent(new Event('change'));" +
                    "return JSON.stringify({optionCount:count,selectValue:s.value,inputValue:i.value," +
                    "ok:i.value===target,disabled:s.disabled});})()");
                await CaptureAsync(Path.Combine(dir, "05-settings-model-dropdown.png"));

                // 生成工作台：输入框必须可写、步骤必须渲染
                await EvalAsync("(function(){location.hash='#/builder';return 1;})()");
                await Task.Delay(1200);
                result["builderState"] = await EvalAsync(
                    "(function(){var t=document.querySelector('.builder-input');" +
                    "return JSON.stringify({textarea:!!t,disabled:t?t.disabled:null," +
                    "buttons:document.querySelectorAll('.builder-wrap button').length," +
                    "steps:document.querySelectorAll('.step').length," +
                    "errors:(window.__afErrors||[]).slice(0,6)});})()");
                await CaptureAsync(Path.Combine(dir, "06-builder.png"));

                // 目标应用：优先最新 AI 生成的应用，否则用示例
                var newestForDiag = _repo.List(null).FirstOrDefault(a => !a.Id.StartsWith("apdemo", StringComparison.OrdinalIgnoreCase))
                                    ?? _repo.List(null).FirstOrDefault();
                var diagAppId = newestForDiag != null ? newestForDiag.Id : "apdemo0001";
                result["diagTargetApp"] = diagAppId;

                // 修改工作台（#/builder?app=<id>）
                await EvalAsync("(function(){location.hash='#/builder?app=" + diagAppId + "';return 1;})()");
                await Task.Delay(1600);
                result["modifyBuilder"] = await EvalAsync(
                    "(function(){var h2=document.querySelector('.page-head h2');var t=document.querySelector('.builder-input');" +
                    "return JSON.stringify({header:h2?h2.textContent:'',textarea:!!t,disabled:t?t.disabled:null," +
                    "errors:(window.__afErrors||[]).slice(0,5)});})()");
                await CaptureAsync(Path.Combine(dir, "07-builder-modify.png"));

                // 版本列表（异步 RPC → 暂存到 window 再读）
                await EvalAsync(
                    "(function(){window.__afVer='pending';" +
                    "window.__af.call('versions.list',{appId:'" + diagAppId + "'})" +
                    ".then(function(r){window.__afVer=JSON.stringify((r.versions||[]).map(function(v){return v.seq;}));})" +
                    ".catch(function(e){window.__afVer='err:'+e.message;});return 1;})()");
                await Task.Delay(1200);
                result["versions"] = await EvalAsync("window.__afVer");

                // 缩小窗口，验证内容是否跟随缩放
                var before = await EvalAsync("JSON.stringify({w:window.innerWidth,h:window.innerHeight})");
                BeginInvoke(new Action(delegate { ClientSize = new Size(900, 620); }));
                await Task.Delay(1000);
                var after = await EvalAsync("JSON.stringify({w:window.innerWidth,h:window.innerHeight})");
                result["resizeCheck"] = "缩窗前=" + before + " 缩窗后=" + after;
                await CaptureAsync(Path.Combine(dir, "03-settings-small.png"));

                // 打开一个应用窗口，验证应用实例与内容
                OpenAppInternal(diagAppId);
                await Task.Delay(2000);
                var appForm = Application.OpenForms.OfType<AppForm>().FirstOrDefault();
                if (appForm == null)
                {
                    result["appWindow"] = "未创建";
                }
                else
                {
                    var d = JsonLite.New();
                    d["formClientSize"] = appForm.ClientSize.Width + "x" + appForm.ClientSize.Height;
                    d["webViewSize"] = appForm.View.Width + "x" + appForm.View.Height;
                    d["webViewVisible"] = appForm.View.Visible;

                    var core = appForm.View.CoreWebView2;
                    d["hasCore"] = core != null;
                    if (core != null)
                    {
                        d["url"] = core.Source;
                        d["title"] = await SafeEvalAsync(core, "document.title");
                        d["bodyHtmlLen"] = await SafeEvalAsync(core, "document.body ? document.body.innerHTML.length : -1");
                        d["rootFound"] = await SafeEvalAsync(core, "document.querySelector('[data-app-root]') !== null");
                        await CaptureCoreAsync(core, Path.Combine(dir, "04-app.png"));
                    }
                    result["appWindow"] = d;
                }

                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
                _log.Error("界面自检失败", ex);
            }

            var json = JsonLite.Serialize(result);
            ConsoleBridge.WriteLine(json);
            if (DiagFinished != null) DiagFinished(json);
            else BeginInvoke(new Action(Close));
        }

        private async System.Threading.Tasks.Task<string> EvalAsync(string script)
        {
            try { return await _webView.CoreWebView2.ExecuteScriptAsync(script); }
            catch (Exception ex) { return "\"eval-error: " + ex.Message.Replace("\"", "'") + "\""; }
        }

        private static async System.Threading.Tasks.Task<string> SafeEvalAsync(CoreWebView2 core, string script)
        {
            try { return await core.ExecuteScriptAsync(script); }
            catch { return "eval-error"; }
        }

        private System.Threading.Tasks.Task CaptureAsync(string path)
        {
            return CaptureCoreAsync(_webView.CoreWebView2, path);
        }

        private static async System.Threading.Tasks.Task CaptureCoreAsync(CoreWebView2 core, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _log.Error("WebView2 进程失败：" + e.ProcessFailedKind, null);
        }

        /// <summary>启动一个应用窗口（RPC launch.open 的实现）。</summary>
        private IDictionary<string, object> OpenAppInternal(string appId)
        {
            var app = _repo.Get(appId);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + appId);

            var manifest = _repo.ReadManifest(app);
            if (manifest == null) throw new InvalidOperationException("应用清单缺失或损坏：" + appId);

            var versionDir = _repo.CurrentVersionDir(app);
            var stopwatch = Stopwatch.StartNew();

            var form = new AppForm(app, manifest, versionDir, _environment, _log);
            form.Ready += delegate
            {
                var ms = stopwatch.ElapsedMilliseconds;
                _log.Info("应用已打开：" + appId + " 用时 " + ms + "ms");
                _log.Perf(_ws.PerfLogPath, new Dictionary<string, object>
                {
                    { "type", "app-launch" },
                    { "ts", TimeUtil.Now() },
                    { "appId", appId },
                    { "durationMs", ms }
                });
            };
            form.Show(this);

            _repo.TouchOpen(appId);
            _settings.Current.LastOpenAppId = appId;
            try { _settings.Save(_settings.Current); } catch { /* 忽略 */ }

            var metrics = JsonLite.New();
            metrics["appId"] = appId;
            metrics["name"] = app.Name;
            metrics["version"] = app.CurrentVersion;
            metrics["entry"] = manifest.Entry;
            metrics["warm"] = _ready;
            return metrics;
        }

        /// <summary>
        /// 打开目录：交给 FolderOpener（外壳调用 → explorer.exe → rundll32 → 复制路径）。
        /// 打不开时会把路径放进剪贴板并返回 false，由界面提示用户，不再弹系统级错误框。
        /// </summary>
        private bool OpenFolderInternal(string path, out bool copied)
        {
            return FolderOpener.Open(path, out copied, _log.Warn);
        }

        private void ShowFallback(string message)
        {
            if (IsDisposed) return;
            if (_fallback == null)
            {
                _fallback = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(220, 224, 230),
                    BackColor = Color.FromArgb(22, 24, 28),
                    Font = new Font("Microsoft YaHei UI", 13f)
                };
                Controls.Add(_fallback);
                _fallback.BringToFront();
            }

            _fallback.Text =
                "界面引擎（WebView2）初始化失败\r\n\r\n" +
                TextUtil.Ellipsis(message, 200) + "\r\n\r\n" +
                "依赖状态：\r\n" + string.Join("\r\n", _deps.Select(d => "· " + d.DisplayName + "：" + d.State + "（" + d.Version + "）")) +
                "\r\n\r\n若在受限环境（如无桌面会话的沙箱）中运行，属预期现象；请在真实桌面会话启动。" +
                "\r\n日志：" + System.IO.Path.Combine(_ws.LogsDir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

            if (InitFailed != null && BenchMode) BeginInvoke(new Action(Close));
        }

        /// <summary>向 Shell UI 推送事件（生成进度等）。</summary>
        private void PushEvent(string name, object payload)
        {
            try
            {
                var core = _webView != null ? _webView.CoreWebView2 : null;
                if (core == null) return;
                core.PostWebMessageAsJson(RpcDispatcher.EventEnvelope(name, payload));
            }
            catch (Exception ex)
            {
                _log.Warn("推送事件失败：" + ex.Message);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void Quit()
        {
            _quitting = true;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _log.Info("窗口关闭请求：reason=" + e.CloseReason + " quitting=" + _quitting +
                      " bench=" + BenchMode + " diag=" + DiagMode);

            if (!_quitting && !BenchMode && !DiagMode && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    if (_tray != null)
                        _tray.ShowBalloonTip(1500, Defaults.ProductName, "已最小化到托盘，双击图标可恢复", ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    _log.Warn("托盘提示失败：" + ex.Message);
                }
                _log.Info("已隐藏到托盘（进程继续运行）");
                return;
            }

            if (_engine != null) _engine.CancelAll();

            if (_openRequestTimer != null)
            {
                _openRequestTimer.Stop();
                _openRequestTimer.Dispose();
                _openRequestTimer = null;
            }

            if (_hotkeyRegistered)
            {
                try { UnregisterHotKey(Handle, HotkeyId); } catch { /* 忽略 */ }
                _hotkeyRegistered = false;
            }

            try
            {
                // 自检/基准模式不写回窗口尺寸，避免污染用户设置
                if (WindowState == FormWindowState.Normal && !DiagMode && !BenchMode)
                {
                    // 存逻辑像素，保证换 DPI 后尺寸不漂移
                    _settings.Current.WindowWidth = DpiHelper.Unscaled(ClientSize.Width);
                    _settings.Current.WindowHeight = DpiHelper.Unscaled(ClientSize.Height);
                    _settings.Current.WindowX = Location.X;
                    _settings.Current.WindowY = Location.Y;
                    _settings.Save(_settings.Current);
                }
            }
            catch { /* 忽略 */ }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
