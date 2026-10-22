using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using AppForge.Shared;
using AppForge.Host.Diagnostics;

namespace AppForge.Host.Bootstrap
{
    /// <summary>
    /// WebView2 缺失时的安装引导（FR-0.5/FR-0.6）。
    /// 此时渲染引擎还不可用，只能用原生 WinForms 窗口呈现。
    /// </summary>
    public static class WebView2Installer
    {
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>引导用户安装；返回是否已成功检测到运行时。</summary>
        public static bool GuideInstall(DepStatus missing, Log log)
        {
            using (var form = new InstallForm(missing, log))
            {
                form.ShowDialog();
            }
            return !string.IsNullOrEmpty(StartupChecks.WebView2Version());
        }

        public static bool InstallFromFile(string installerPath, Log log)
        {
            try
            {
                log.Info("运行 WebView2 安装包：" + installerPath);
                var psi = new ProcessStartInfo(installerPath, "/silent /install")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(600000);
                }
                return !string.IsNullOrEmpty(StartupChecks.WebView2Version());
            }
            catch (Exception ex)
            {
                log.Error("WebView2 安装失败", ex);
                return false;
            }
        }

        public static bool DownloadBootstrapper(string targetPath, Log log)
        {
            try
            {
                log.Info("下载 WebView2 引导程序…");
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "AppForge/0.1");
                    client.DownloadFile(BootstrapperUrl, targetPath);
                }
                return File.Exists(targetPath) && new FileInfo(targetPath).Length > 1024;
            }
            catch (Exception ex)
            {
                log.Error("下载 WebView2 引导程序失败", ex);
                return false;
            }
        }

        private sealed class InstallForm : Form
        {
            private readonly DepStatus _missing;
            private readonly Log _log;
            private readonly Label _status;
            private readonly Button _online;
            private readonly Button _offline;
            private readonly Button _quit;
            private bool _busy;

            public InstallForm(DepStatus missing, Log log)
            {
                _missing = missing;
                _log = log;

                Text = "AppForge · 需要安装一个系统组件";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                ClientSize = new Size(560, 260);
                BackColor = Color.FromArgb(250, 250, 252);
                Font = new Font("Microsoft YaHei UI", 9.5f);

                var title = new Label
                {
                    Text = "缺少 " + _missing.DisplayName,
                    Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(28, 31, 35),
                    Location = new Point(24, 22),
                    AutoSize = true
                };
                Controls.Add(title);

                var body = new Label
                {
                    Text = "AppForge 的界面需要 Windows 的网页组件（WebView2，与 Edge 同源）。\r\n" +
                           "它在 Windows 11 上是系统自带的，Windows 10 多数机器也已预装；当前系统缺少它，只需安装一次。\r\n\r\n" +
                           "· 无需管理员权限（按当前用户安装）\r\n" +
                           "· 约 100MB，安装完成后会自动重启 AppForge",
                    ForeColor = Color.FromArgb(80, 86, 94),
                    Location = new Point(24, 62),
                    Size = new Size(512, 110)
                };
                Controls.Add(body);

                _status = new Label
                {
                    Text = "当前状态：" + (_missing.Version ?? "未安装"),
                    ForeColor = Color.FromArgb(107, 114, 128),
                    Location = new Point(24, 176),
                    Size = new Size(512, 22)
                };
                Controls.Add(_status);

                _online = new Button
                {
                    Text = "在线安装（推荐）",
                    Size = new Size(150, 34),
                    Location = new Point(24, 208),
                    BackColor = Color.FromArgb(37, 99, 235),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                _online.FlatAppearance.BorderSize = 0;
                _online.Click += OnlineClick;
                Controls.Add(_online);

                _offline = new Button
                {
                    Text = "选择离线安装包…",
                    Size = new Size(150, 34),
                    Location = new Point(184, 208),
                    FlatStyle = FlatStyle.System
                };
                _offline.Click += OfflineClick;
                Controls.Add(_offline);

                _quit = new Button
                {
                    Text = "退出",
                    Size = new Size(90, 34),
                    Location = new Point(446, 208),
                    FlatStyle = FlatStyle.System
                };
                _quit.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(_quit);
            }

            private void OnlineClick(object sender, EventArgs e)
            {
                if (_busy) return;
                if (string.IsNullOrEmpty(StartupChecks.WebView2Version()) == false)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                _busy = true;
                SetBusy("正在下载安装程序…");
                var target = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");

                Task.Run(delegate
                {
                    var downloaded = DownloadBootstrapper(target, _log);
                    if (!downloaded)
                    {
                        Ui(delegate
                        {
                            _busy = false;
                            SetBusy("下载失败，请检查网络后重试，或选择离线安装包");
                            _online.Enabled = true;
                            _offline.Enabled = true;
                            _quit.Enabled = true;
                        });
                        return;
                    }

                    Ui(delegate { SetBusy("正在静默安装（可能需要 1~3 分钟）…"); });
                    var ok = InstallFromFile(target, _log);

                    Ui(delegate
                    {
                        _busy = false;
                        _online.Enabled = true;
                        _offline.Enabled = true;
                        _quit.Enabled = true;
                        if (ok)
                        {
                            _status.Text = "安装成功：" + StartupChecks.WebView2Version();
                            DialogResult = DialogResult.OK;
                            Close();
                        }
                        else
                        {
                            SetBusy("安装未完成。可直接下载离线安装包后重试：\r\nhttps://developer.microsoft.com/microsoft-edge/webview2/");
                        }
                    });
                });
            }

            private void OfflineClick(object sender, EventArgs e)
            {
                if (_busy) return;
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择 WebView2 运行时安装包";
                    dlg.Filter = "安装程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    _busy = true;
                    SetBusy("正在安装，请稍候…");
                    var path = dlg.FileName;
                    Task.Run(delegate
                    {
                        var ok = InstallFromFile(path, _log);
                        Ui(delegate
                        {
                            _busy = false;
                            _online.Enabled = true;
                            _offline.Enabled = true;
                            _quit.Enabled = true;
                            if (ok)
                            {
                                _status.Text = "安装成功：" + StartupChecks.WebView2Version();
                                DialogResult = DialogResult.OK;
                                Close();
                            }
                            else
                            {
                                SetBusy("安装失败，请确认所选文件是 WebView2 Evergreen 安装包");
                            }
                        });
                    });
                }
            }

            private void Ui(Action action)
            {
                if (IsDisposed) return;
                try { BeginInvoke(action); }
                catch { /* 窗口已关闭 */ }
            }

            private void SetBusy(string text)
            {
                _status.Text = text;
                _online.Enabled = false;
                _offline.Enabled = false;
                _quit.Enabled = false;
            }
        }
    }
}
