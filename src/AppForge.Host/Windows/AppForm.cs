using System;
using System.Drawing;
using System.Windows.Forms;
using AppForge.Shared;
using AppForge.Host.Diagnostics;
using AppForge.Host.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 单个应用的运行窗口：
    ///   · 独立虚拟主机（独立 origin）⇒ 存储隔离
    ///   · 不注册 WebMessageReceived ⇒ 应用拿不到任何宿主能力
    ///   · 关闭即销毁，不留后台进程
    /// </summary>
    public sealed class AppForm : Form
    {
        private readonly AppMeta _app;
        private readonly Manifest _manifest;
        private readonly string _versionDir;
        private readonly CoreWebView2Environment _environment;
        private readonly Log _log;
        private WebView2 _webView;
        private bool _navigated;

        public event Action Ready;

        /// <summary>供界面自检/调试读取的 WebView2 控件。</summary>
        public WebView2 View
        {
            get { return _webView; }
        }

        public AppForm(AppMeta app, Manifest manifest, string versionDir, CoreWebView2Environment environment, Log log)
        {
            _app = app;
            _manifest = manifest;
            _versionDir = versionDir;
            _environment = environment;
            _log = log;

            Text = app.Name;
            // 标题栏与任务栏使用该应用自己的 emoji 图标，和启动台/应用页保持一致。
            Icon = AppIcon.ForApp(string.IsNullOrWhiteSpace(manifest.Icon) ? app.Icon : manifest.Icon);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 26, 30);
            AutoScaleMode = AutoScaleMode.None; // 尺寸按 DPI 手动换算（manifest 用 CSS 逻辑像素）

            var width = manifest.Window != null ? manifest.Window.Width : 960;
            var height = manifest.Window != null ? manifest.Window.Height : 720;
            var minWidth = manifest.Window != null ? manifest.Window.MinWidth : 480;
            var minHeight = manifest.Window != null ? manifest.Window.MinHeight : 360;

            ClientSize = new Size(
                Math.Max(DpiHelper.Scaled(width), DpiHelper.Scaled(minWidth)),
                Math.Max(DpiHelper.Scaled(height), DpiHelper.Scaled(minHeight)));
            MinimumSize = new Size(DpiHelper.Scaled(minWidth), DpiHelper.Scaled(minHeight));
            if (manifest.Window != null && !manifest.Window.Resizable)
            {
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
            }

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            Shown += async delegate { await InitializeAsync(); };
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            if (_navigated) return;
            _navigated = true;

            try
            {
                await _webView.EnsureCoreWebView2Async(_environment);
                var core = _webView.CoreWebView2;

                VirtualHosts.MapApp(core, _app.Id, _versionDir);

                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false; // 应用与宿主零通道

                core.NavigationCompleted += delegate(object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess)
                    {
                        ShowError("页面加载失败：" + e.WebErrorStatus);
                        return;
                    }
                    if (Ready != null) Ready();
                };

                core.ProcessFailed += delegate(object s, CoreWebView2ProcessFailedEventArgs e)
                {
                    _log.Error("应用进程失败：" + _app.Id + " / " + e.ProcessFailedKind, null);
                    ShowError("应用进程异常退出：" + e.ProcessFailedKind);
                };

                core.Navigate(Hosts.AppUrl(_app.Id, _manifest.Entry));
            }
            catch (Exception ex)
            {
                _log.Error("应用窗口初始化失败", ex);
                ShowError("应用窗口初始化失败：" + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            if (IsDisposed) return;
            var label = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(30, 32, 38),
                ForeColor = Color.FromArgb(230, 120, 120),
                Font = new Font("Microsoft YaHei UI", 11f),
                Text = message + "\r\n\r\n（可在造物台对该应用说「重新生成」来修复）"
            };
            Controls.Add(label);
            label.BringToFront();
        }
    }
}
