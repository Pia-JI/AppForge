using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AfSpike
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 1) 依赖自检：WebView2 运行时版本（首启检查用的同一 API）
            string ver = null;
            try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex) { Console.WriteLine("SELFTEST webview2=missing err=" + ex.GetType().Name); }
            Console.WriteLine("SELFTEST webview2_version=" + (ver ?? "null"));
            Console.WriteLine("SELFTEST net_version=" + Environment.Version);
            Console.WriteLine("SELFTEST os=" + Environment.OSVersion.Version);
            Console.WriteLine("SELFTEST app_base=" + AppDomain.CurrentDomain.BaseDirectory);

            if (ver == null) return 2;
            if (args.Length == 0 || args[0] != "--gui") return 0;

            // 2) 冒烟：创建 WebView2 并导航一段内联 HTML，打印结果后自动退出
            var form = new Form { Text = "af-spike", Width = 640, Height = 480 };
            var wv = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(wv);
            var udf = Path.Combine(Path.GetTempPath(), "af-wv2-" + Guid.NewGuid().ToString("N"));
            form.Shown += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, udf);
                    await wv.EnsureCoreWebView2Async(env);
                    Console.WriteLine("SELFTEST env_ok=" + env.BrowserVersionString);
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
                    wv.CoreWebView2.NavigationCompleted += (a, b) =>
                        tcs.TrySetResult("navigated success=" + b.IsSuccess + " status=" + b.HttpStatusCode);
                    wv.NavigateToString("<html><body><h1 data-app-root>ok</h1><script>document.title='af-ok'</script></body></html>");
                    var r = await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(8000));
                    Console.WriteLine("SELFTEST " + (r == tcs.Task ? tcs.Task.Result : "navigate_timeout"));
                    var js = await wv.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-app-root]') !== null");
                    Console.WriteLine("SELFTEST assert_root=" + js);
                }
                catch (Exception ex) { Console.WriteLine("SELFTEST gui_err=" + ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
            try { Directory.Delete(udf, true); } catch { }
            return 0;
        }
    }
}
