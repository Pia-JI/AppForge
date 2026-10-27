using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AppForge.Host.Ai;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Gen;
using AppForge.Host.Web;
using AppForge.Host.Runtime;
using AppForge.Host.Windows;
using AppForge.Shared;

namespace AppForge.Host
{
    /// <summary>无界面 CLI 模式：--check / --selftest / --bench。</summary>
    public static class CliRunner
    {
        // ---------------- --check ----------------

        public static int RunCheck(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-check.log"));
            var deps = StartupChecks.RunAll(ws);
            var ok = deps.All(d => string.Equals(d.State, "ok", StringComparison.Ordinal));

            var result = JsonLite.New();
            result["mode"] = "check";
            result["ok"] = ok;
            result["product"] = Defaults.ProductName;
            result["version"] = RpcService.Version;
            result["dataDir"] = ws.Root;
            result["portable"] = ws.Portable;
            // 数据目录候选位置的逐个探测结果（"路径 → ok" / "路径 → 被拒绝的原因"），
            // 用来定位"为什么我的数据目录不可写"到底卡在哪一层。
            result["dataProbe"] = Program.DataDirProbeLog.ToList();
            result["deps"] = deps.Select(d => (object)d.ToJson()).ToList();

            ConsoleBridge.WriteLine(JsonLite.Serialize(result));
            log.Info("--check 完成，ok=" + ok);
            return ok ? 0 : 2;
        }

        // ---------------- --set-api-key / --check-model / --list-models ----------------

        public static int RunSetApiKey(Workspace ws, Log log, string apiKey)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-api.log"));
            var secrets = new SecretsStore(ws);
            secrets.SetApiKey(apiKey);

            var result = JsonLite.New();
            result["mode"] = "set-api-key";
            result["ok"] = secrets.HasApiKey();
            result["maskedKey"] = SecretsStore.Mask(secrets.GetApiKey());
            result["secretsPath"] = ws.SecretsPath;
            result["encrypted"] = true;
            ConsoleBridge.WriteLine(JsonLite.Serialize(result));
            log.Info("API Key 写入（DPAPI）：" + result["maskedKey"]);
            return secrets.HasApiKey() ? 0 : 2;
        }

        public static int RunListModels(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-models.log"));
            var settings = new SettingsStore(ws);
            settings.Load();
            var secrets = new SecretsStore(ws);
            var client = new OpenAiClient(settings, secrets, log);

            var result = JsonLite.New();
            result["mode"] = "list-models";
            result["baseUrl"] = OpenAiClient.NormalizeBaseUrl(settings.Current.BaseUrl);
            try
            {
                var models = client.ListModelsAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                result["ok"] = true;
                result["count"] = models.Count;
                result["models"] = models.ToList();
                ConsoleBridge.WriteLine(JsonLite.Serialize(result));
                return 0;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
                ConsoleBridge.WriteLine(JsonLite.Serialize(result));
                return 5;
            }
        }

        public static int RunCheckModel(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-model-test.log"));
            var settings = new SettingsStore(ws);
            settings.Load();
            var secrets = new SecretsStore(ws);
            var client = new OpenAiClient(settings, secrets, log);

            var result = JsonLite.New();
            result["mode"] = "check-model";
            result["baseUrl"] = OpenAiClient.NormalizeBaseUrl(settings.Current.BaseUrl);
            result["model"] = settings.Current.Model;
            result["hasApiKey"] = secrets.HasApiKey();

            try
            {
                var test = client.TestModelAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                result["ok"] = test.Ok;
                result["latencyMs"] = test.LatencyMs;
                result["sample"] = test.Sample ?? string.Empty;
                result["error"] = test.Error ?? string.Empty;
                result["promptTokens"] = test.PromptTokens;
                result["completionTokens"] = test.CompletionTokens;
                ConsoleBridge.WriteLine(JsonLite.Serialize(result));
                return test.Ok ? 0 : 5;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
                ConsoleBridge.WriteLine(JsonLite.Serialize(result));
                return 5;
            }
        }

        // ---------------- --bench ----------------
        public static int RunBench(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-bench.log"));
            var phases = JsonLite.New();
            var stopwatch = Stopwatch.StartNew();

            var deps = StartupChecks.RunAll(ws);
            phases["checks"] = stopwatch.ElapsedMilliseconds;

            var settings = new SettingsStore(ws);
            settings.Load();
            var secrets = new SecretsStore(ws);
            var repo = new AppRepository(ws);
            repo.Load();
            SeedData.EnsureSeeded(ws, repo);   // 与 GUI 启动保持一致：保证有示例应用
            phases["dataLoad"] = stopwatch.ElapsedMilliseconds;

            var firstFrame = -1L;
            var splashMs = -1L;
            var gui = "ok";
            string error = null;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new ShellForm(ws, log, settings, secrets, repo, deps, null))
                {
                    form.BenchMode = true;
                    form.FirstFrameShown += delegate(long ms) { firstFrame = ms; };
                    form.InitFailed += delegate(string msg) { gui = "unavailable"; error = msg; };

                    var guard = new System.Windows.Forms.Timer { Interval = 20000 };
                    guard.Tick += delegate
                    {
                        guard.Stop();
                        gui = gui == "ok" && firstFrame < 0 ? "timeout" : gui;
                        try { form.Close(); } catch { /* 忽略 */ }
                    };
                    form.Shown += delegate { guard.Start(); };

                    Application.Run(form);
                    guard.Dispose();
                    splashMs = form.SplashShownMs;
                }
            }
            catch (Exception ex)
            {
                gui = "unavailable";
                error = ex.Message;
            }

            var result = JsonLite.New();
            result["mode"] = "bench";
            result["splashMs"] = splashMs;
            result["firstFrameMs"] = firstFrame;
            result["phases"] = phases;
            result["gui"] = gui;
            result["error"] = error ?? string.Empty;
            result["dataDir"] = ws.Root;
            result["budget"] = "启动画面 ≤800ms（断言）；首帧信息性，环境相关（软件渲染/高 DPI 会变慢）";

            ConsoleBridge.WriteLine(JsonLite.Serialize(result));
            log.Perf(ws.PerfLogPath, result);

            if (gui != "ok") return 3;
            if (splashMs < 0 || splashMs > 800) return 4;
            if (firstFrame < 0 || firstFrame > 8000) return 4;
            return 0;
        }

        // ---------------- --ui-diag ----------------

        public static int RunUiDiag(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-ui-diag.log"));

            var deps = StartupChecks.RunAll(ws);
            var settings = new SettingsStore(ws);
            settings.Load();
            var secrets = new SecretsStore(ws);
            var repo = new AppRepository(ws);
            repo.Load();
            SeedData.EnsureSeeded(ws, repo);

            string json = null;
            var gui = "ok";
            string error = null;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new ShellForm(ws, log, settings, secrets, repo, deps, null))
                {
                    form.DiagMode = true;
                    form.DiagOutDir = Path.Combine(ws.LogsDir, "ui-diag");
                    form.DiagFinished += delegate(string result)
                    {
                        json = result;
                        try { form.BeginInvoke(new Action(form.Close)); } catch { /* 忽略 */ }
                    };
                    form.InitFailed += delegate(string msg)
                    {
                        gui = "unavailable";
                        error = msg;
                        try { form.BeginInvoke(new Action(form.Close)); } catch { /* 忽略 */ }
                    };

                    var guard = new System.Windows.Forms.Timer { Interval = 60000 };
                    guard.Tick += delegate { guard.Stop(); try { form.Close(); } catch { /* 忽略 */ } };
                    form.Shown += delegate { guard.Start(); };

                    Application.Run(form);
                    guard.Dispose();
                }
            }
            catch (Exception ex)
            {
                gui = "unavailable";
                error = ex.Message;
            }

            if (json == null)
            {
                var d = JsonLite.New();
                d["mode"] = "ui-diag";
                d["ok"] = false;
                d["gui"] = gui;
                d["error"] = error ?? "未能生成界面自检结果";
                ConsoleBridge.WriteLine(JsonLite.Serialize(d));
                return 6;
            }
            return 0;
        }

        // ---------------- --selftest ----------------

        public static int RunSelftest(Workspace ws, Log log)
        {
            ConsoleBridge.SetTee(Path.Combine(ws.LogsDir, "cli-selftest.log"));
            var runner = new AssertRunner();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                SelftestCore(runner, ws, log);
            }
            catch (Exception ex)
            {
                runner.Fail("selftest 崩溃", ex.Message);
            }

            stopwatch.Stop();

            var result = JsonLite.New();
            result["mode"] = "selftest";
            result["pass"] = runner.Passed;
            result["fail"] = runner.Failed;
            result["durationMs"] = stopwatch.ElapsedMilliseconds;
            result["failures"] = runner.Failures;
            ConsoleBridge.WriteLine(JsonLite.Serialize(result));

            if (runner.Failed > 0)
            {
                ConsoleBridge.WriteLine("");
                ConsoleBridge.WriteLine("失败项：");
                foreach (var f in runner.Failures) ConsoleBridge.WriteLine("  ✗ " + f);
                return 1;
            }
            return 0;
        }

        private static void SelftestCore(AssertRunner r, Workspace realWs, Log log)
        {
            // 1. 依赖自检
            var deps = StartupChecks.RunAll(realWs);
            r.Check("依赖自检返回 4 项", deps.Count == 4);
            r.Check("OS 检查有结果", deps.Any(d => d.Name == "os"));
            ConsoleBridge.WriteLine("  依赖状态：" + string.Join(" | ",
                deps.Select(d => d.DisplayName + "=" + d.State + "(" + d.Version + ")")));

            // 1.1 Shell UI 是否已内嵌（单文件交付的前提）
            var resources = typeof(EmbeddedAssets).Assembly.GetManifestResourceNames();
            r.Check("Shell UI 已内嵌进 exe",
                resources.Any(n => n.Replace('\\', '/').EndsWith("ui/index.html", StringComparison.OrdinalIgnoreCase)));
            r.Check("示例应用已内嵌进 dll",
                typeof(SeedData).Assembly.GetManifestResourceNames()
                    .Any(n => n.Replace('\\', '/').EndsWith("seed/todo/index.html", StringComparison.OrdinalIgnoreCase)));

            // 独立工作区，避免污染真实数据。优先放在已验证可写的真实数据目录下：
            // 受限机器上 %TEMP% 可能被安全软件/组策略拦截，不能把它当成必然可写。
            var tempRoot = CreateSelftestRoot(realWs);
            var previousNoShell = Environment.GetEnvironmentVariable("APPFORGE_NO_SHELL_OPEN");
            Environment.SetEnvironmentVariable("APPFORGE_NO_SHELL_OPEN", "1");
            try
            {
                var ws = new Workspace(tempRoot, true);
                ws.EnsureLayout();

                // 1.2 白屏判定（不启动 WebView2）：小页面不应被误杀，真正空白/图形页面要能区分
                r.Check("白屏判定不误杀小页面", !AppSelfTester.LooksBlank(35, 4, 0));
                r.Check("白屏判定识别空白页", AppSelfTester.LooksBlank(35, 0, 0));
                r.Check("白屏判定识别图形页", !AppSelfTester.LooksBlank(50, 0, 1));

                // 1.3 生成完成门槛：没有脚本/缺被引用文件时不能把 done 当成功
                var completeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "index.html", "<!doctype html><html><body data-app-root><button id=\"ok\">OK</button><script>document.getElementById('ok').onclick=function(){document.title='ok';};</script></body></html>" }
                };
                r.Check("入口完整时允许收尾", GenerationEngine.EntrySelfContained(completeFiles, "index.html"));

                var missingRefFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "index.html", "<!doctype html><html><body data-app-root><script src=\"app.js\"></script></body></html>" }
                };
                r.Check("入口缺被引用文件时拒绝收尾", !GenerationEngine.EntrySelfContained(missingRefFiles, "index.html"));

                var staticShellFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "index.html", "<!doctype html><html><body data-app-root><div>只有静态文字</div></body></html>" }
                };
                r.Check("入口缺脚本时拒绝收尾", !GenerationEngine.EntrySelfContained(staticShellFiles, "index.html"));

                bool toolboxComplex;
                var toolboxBudget = GenerationEngine.ComputeRoundBudget("做一个嵌入式工具箱，包含多个小工具", false, out toolboxComplex);
                r.Check("工具箱需求使用复杂轮次预算", toolboxComplex && toolboxBudget >= 40);
                r.Check("应用图标已加载", AppIcon.Large != null && AppIcon.Large.Width >= 16);
                r.Check("生成应用 emoji 图标已加载", AppIcon.ForApp("🧰").Width >= 16);
                using (var colorIcon = ColorEmojiIcon.Render("🧰"))
                    r.Check("彩色 emoji 图标可渲染", colorIcon != null && colorIcon.Width >= 16);
                r.Check("不同 emoji 图标可区分", AppIcon.ForApp("✅") != AppIcon.ForApp("🧰"));

                // 2. 设置读写
                var settingsStore = new SettingsStore(ws);
                settingsStore.Load();
                r.Check("设置默认值可用", settingsStore.Current.BaseUrl == Defaults.BaseUrl);
                settingsStore.Current.Model = "gpt-4o-mini";
                settingsStore.Save(settingsStore.Current);

                var reloaded = new SettingsStore(ws);
                reloaded.Load();
                r.Check("设置持久化 roundtrip", reloaded.Current.Model == "gpt-4o-mini");

                var patched = reloaded.Current.ApplyPatch(NewPatch("temperature", 0.8));
                r.Check("设置 patch 生效", Math.Abs(patched.Temperature - 0.8) < 0.0001);
                r.Check("非法温度被校验拦截", patched.ApplyPatch(NewPatch("temperature", 9.0)).Validate().Count > 0);

                // 3. 密钥（DPAPI）
                var secrets = new SecretsStore(ws);
                r.Check("初始无密钥", !secrets.HasApiKey());
                var sampleKey = "sk-selftest-0123456789abcdef";
                secrets.SetApiKey(sampleKey);
                r.Check("密钥加密后读回一致", secrets.GetApiKey() == sampleKey);
                r.Check("密钥脱敏展示", SecretsStore.Mask(sampleKey).Contains("••••"));
                r.Check("密钥不落明文", !File.ReadAllText(ws.SecretsPath).Contains(sampleKey));
                secrets.SetApiKey(string.Empty);
                r.Check("密钥可清除", !secrets.HasApiKey());

                // 4. 路径守卫
                var inside = PathGuard.ResolveWithin(ws.DataDir, "apps/abc/index.html");
                r.Check("合法相对路径可解析", inside.StartsWith(Path.GetFullPath(ws.DataDir), StringComparison.OrdinalIgnoreCase));
                r.Check("拒绝 .. 越界", Throws<PathGuardException>(delegate { PathGuard.ResolveWithin(ws.DataDir, "../secrets.dat"); }));
                r.Check("拒绝盘符路径", Throws<PathGuardException>(delegate { PathGuard.ResolveWithin(ws.DataDir, "C:\\Windows\\win.ini"); }));
                r.Check("拒绝根路径", Throws<PathGuardException>(delegate { PathGuard.ResolveWithin(ws.DataDir, "/etc/passwd"); }));

                // 5. Manifest 校验
                var manifest = new Manifest { Id = "aptest0001", Name = "测试应用" };
                r.Check("合法 manifest 通过结构校验", manifest.ValidateShape().Count == 0);
                r.Check("offline=false 被拒绝", new Manifest { Id = "aptest0002", Name = "x", Offline = false }.ValidateShape().Count > 0);
                r.Check("非法 type 被拒绝", new Manifest { Id = "aptest0003", Name = "x", Type = "exe" }.ValidateShape().Count > 0);
                r.Check("entry 非 html 被拒绝", new Manifest { Id = "aptest0004", Name = "x", Entry = "a.js" }.ValidateShape().Count > 0);

                var versionDir = ws.VersionDir("aptest0001", 1);
                Directory.CreateDirectory(versionDir);
                File.WriteAllText(Path.Combine(versionDir, "index.html"), "<html><body data-app-root>ok</body></html>");
                r.Check("manifest 磁盘校验通过", manifest.ValidateOnDisk(versionDir).Count == 0);
                File.WriteAllText(Path.Combine(versionDir, "manifest.json"), manifest.ToJson());
                r.Check("entry 缺失被检出", new Manifest { Id = "aptest0005", Name = "x", Entry = "missing.html" }.ValidateOnDisk(versionDir).Count > 0);

                // 6. 应用库 CRUD
                var repo = new AppRepository(ws);
                repo.Load();
                r.Check("初始应用库为空", repo.Count == 0);

                var app = new AppMeta
                {
                    Id = "aptest0001",
                    Name = "测试应用",
                    Description = "自检用",
                    Type = AppTypes.StaticWeb,
                    Icon = "🧪",
                    CurrentVersion = 1,
                    Status = "ready",
                    CreatedAt = TimeUtil.Now(),
                    UpdatedAt = TimeUtil.Now()
                };
                repo.Upsert(app);
                r.Check("新增应用成功", repo.Count == 1 && repo.Get("aptest0001") != null);

                repo.Rename("aptest0001", "改名后的应用");
                r.Check("重命名生效", repo.Get("aptest0001").Name == "改名后的应用");

                repo.Pin("aptest0001", true);
                r.Check("置顶生效", repo.Get("aptest0001").Pinned);

                repo.TouchOpen("aptest0001");
                r.Check("打开计数与时间", repo.Get("aptest0001").LaunchCount == 1 && repo.Get("aptest0001").LastOpenedAt > 0);

                var searchHit = repo.List("改名");
                r.Check("搜索命中", searchHit.Count == 1);
                r.Check("搜索不命中", repo.List("不存在的东西").Count == 0);

                var loadedManifest = repo.ReadManifest(repo.Get("aptest0001"));
                r.Check("可读回 manifest", loadedManifest != null && loadedManifest.Entry == "index.html");

                // 7. 索引自愈
                File.Delete(ws.IndexPath);
                var repo2 = new AppRepository(ws);
                repo2.Load();
                r.Check("索引丢失后可重建", repo2.Count == 1);

                // 8. 删除 → 回收站
                repo2.Delete("aptest0001");
                r.Check("删除后应用库为空", repo2.Count == 0);
                r.Check("目录进入回收站", Directory.Exists(ws.TrashDir) && Directory.GetDirectories(ws.TrashDir).Length == 1);

                // 9. 示例种子
                var seedWs = new Workspace(Path.Combine(tempRoot, "seed"), true);
                seedWs.EnsureLayout();
                var seedRepo = new AppRepository(seedWs);
                seedRepo.Load();
                var seeded = SeedData.EnsureSeeded(seedWs, seedRepo);
                r.Check("示例应用写入成功", seeded && seedRepo.Count == 3);
                foreach (var seed in seedRepo.List(null))
                {
                    var m = seedRepo.ReadManifest(seed);
                    r.Check("示例 manifest 有效：" + seed.Name,
                        m != null && m.ValidateOnDisk(seedRepo.CurrentVersionDir(seed)).Count == 0);
                }

                // 10. RPC 分发
                var rpc = new RpcService(
                    seedWs, log, new SettingsStore(seedWs), new SecretsStore(seedWs), seedRepo,
                    new OpenAiClient(new SettingsStore(seedWs), new SecretsStore(seedWs), log),
                    delegate(string id) { return JsonLite.New(); },
                    delegate(string path, out bool copied, out IList<string> stages)
                    {
                        copied = false;
                        stages = new List<string>();
                        return false;
                    },
                    null,
                    null);
                rpc.Dispatcher.RegisterSync("selftest.echo", delegate(IDictionary<string, object> p) { return p; });

                var ping = rpc.Dispatcher.HandleAsync("{\"id\":\"1\",\"method\":\"ui.ping\"}").GetAwaiter().GetResult();
                var pingObj = JsonLite.ParseObject(ping);
                r.Check("RPC ping 成功", JsonLite.Bool(pingObj, "ok") && JsonLite.Bool(JsonLite.Obj(pingObj, "data"), "pong"));

                var evil = rpc.Dispatcher.HandleAsync("{\"id\":\"2\",\"method\":\"evil.exec\"}").GetAwaiter().GetResult();
                var evilObj = JsonLite.ParseObject(evil);
                r.Check("RPC 拒绝未登记方法", !JsonLite.Bool(evilObj, "ok") &&
                    JsonLite.Str(JsonLite.Obj(evilObj, "error"), "code") == ErrorCodes.NotAllowed);

                var settingsRpc = rpc.Dispatcher.HandleAsync("{\"id\":\"3\",\"method\":\"settings.get\"}").GetAwaiter().GetResult();
                var settingsObj = JsonLite.ParseObject(settingsRpc);
                r.Check("RPC settings.get 返回配置", JsonLite.Bool(settingsObj, "ok") &&
                    JsonLite.Obj(JsonLite.Obj(settingsObj, "data"), "settings") != null);

                var appsRpc = rpc.Dispatcher.HandleAsync("{\"id\":\"4\",\"method\":\"apps.list\",\"params\":{\"search\":\"\"}}").GetAwaiter().GetResult();
                var appsObj = JsonLite.ParseObject(appsRpc);
                var appsList = JsonLite.Obj(appsObj, "data");
                r.Check("RPC apps.list 返回 3 个示例", JsonLite.Bool(appsObj, "ok") && JsonLite.Int(appsList, "count") == 3);

                // 生成入口：没有 API Key 时必须立即拒绝（而不是启动任务后静默失败）
                var genRpc = rpc.Dispatcher.HandleAsync(
                    "{\"id\":\"5\",\"method\":\"gen.start\",\"params\":{\"prompt\":\"做一个待办清单\"}}").GetAwaiter().GetResult();
                var genObj = JsonLite.ParseObject(genRpc);
                r.Check("RPC gen.start 无 Key 时被拒绝", !JsonLite.Bool(genObj, "ok") &&
                    JsonLite.Str(JsonLite.Obj(genObj, "error"), "code") == ErrorCodes.BadRequest);

                var genEmpty = rpc.Dispatcher.HandleAsync(
                    "{\"id\":\"6\",\"method\":\"gen.start\",\"params\":{\"prompt\":\"\"}}").GetAwaiter().GetResult();
                r.Check("RPC gen.start 空需求被拒绝", !JsonLite.Bool(JsonLite.ParseObject(genEmpty), "ok"));

                // 打开目录：宿主打不开资源管理器时，必须如实回 ok=false（界面据此提示并给路径），
                // 不能"以为成功"——之前正因为如此，用户只能看到一个 explorer.exe 崩溃弹窗。
                var openRpc = rpc.Dispatcher.HandleAsync("{\"id\":\"7\",\"method\":\"app.openDataDir\"}").GetAwaiter().GetResult();
                var openObj = JsonLite.ParseObject(openRpc);
                var openData = JsonLite.Obj(openObj, "data");
                r.Check("RPC app.openDataDir 如实回报打开结果", JsonLite.Bool(openObj, "ok") &&
                    openData != null && !JsonLite.Bool(openData, "ok") &&
                    !string.IsNullOrEmpty(JsonLite.Str(openData, "path")));

                // fs.list：打不开资源管理器时的应用内替代方案，必须能列出数据目录内容且不许越界
                var listRpc = rpc.Dispatcher.HandleAsync("{\"id\":\"8\",\"method\":\"fs.list\",\"params\":{}}").GetAwaiter().GetResult();
                var listObj = JsonLite.ParseObject(listRpc);
                var listData = JsonLite.Obj(listObj, "data");
                r.Check("RPC fs.list 列出数据目录", JsonLite.Bool(listObj, "ok") &&
                    listData != null && !string.IsNullOrEmpty(JsonLite.Str(listData, "path")));

                var listEvil = rpc.Dispatcher.HandleAsync(
                    "{\"id\":\"9\",\"method\":\"fs.list\",\"params\":{\"path\":\"C:\\\\Windows\"}}").GetAwaiter().GetResult();
                r.Check("RPC fs.list 拒绝越界路径", !JsonLite.Bool(JsonLite.ParseObject(listEvil), "ok"));

                // 12. 版本服务：列表 / 回滚 / 清理
                var versionRpc = rpc.Dispatcher.HandleAsync(
                    "{\"id\":\"7\",\"method\":\"versions.list\",\"params\":{\"appId\":\"apdemo0001\"}}").GetAwaiter().GetResult();
                var versionObj = JsonLite.ParseObject(versionRpc);
                var versionData = JsonLite.Obj(versionObj, "data");
                var versionItems = versionData != null ? JsonLite.List(versionData, "versions") : null;
                r.Check("RPC versions.list 返回示例版本", JsonLite.Bool(versionObj, "ok") && versionItems != null && versionItems.Count == 1);

                var appsGetRpc = rpc.Dispatcher.HandleAsync(
                    "{\"id\":\"8\",\"method\":\"apps.get\",\"params\":{\"appId\":\"apdemo0001\"}}").GetAwaiter().GetResult();
                var appsGetObj = JsonLite.ParseObject(appsGetRpc);
                r.Check("RPC apps.get 返回应用与版本", JsonLite.Bool(appsGetObj, "ok") &&
                    JsonLite.Obj(JsonLite.Obj(appsGetObj, "data"), "app") != null);

                var vws = new Workspace(Path.Combine(tempRoot, "versions"), true);
                vws.EnsureLayout();
                var vrepo = new AppRepository(vws);
                vrepo.Load();
                var vapp = new AppMeta
                {
                    Id = "apver00001",
                    Name = "版本测试",
                    Type = AppTypes.StaticWeb,
                    CurrentVersion = 2,
                    Status = "ready",
                    CreatedAt = TimeUtil.Now(),
                    UpdatedAt = TimeUtil.Now()
                };
                for (var seq = 1; seq <= 2; seq++)
                {
                    var dir = vws.VersionDir(vapp.Id, seq);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body data-app-root>v" + seq + "</body></html>");
                    File.WriteAllText(Path.Combine(dir, "manifest.json"),
                        new Manifest { Id = vapp.Id, Name = "版本测试" }.ToJson());
                }
                vrepo.Upsert(vapp);

                var versions = new VersionService(vws, vrepo);
                r.Check("版本服务列出 2 个版本", versions.List(vapp.Id).Count == 2);
                r.Check("下一个版本号是 3", versions.NextSeq(vapp.Id) == 3);

                var diff = VersionDiff.Compare(vws, vapp.Id, 1, 2, 10);
                r.Check("版本 diff 检出 1 个修改文件", JsonLite.Int(diff, "fileCount") == 1);
                r.Check("版本 diff 行数统计正确",
                    JsonLite.Int(diff, "addedLines") == 1 && JsonLite.Int(diff, "removedLines") == 1);

                // 13. 回收站：删除 → 列出 → 恢复
                var trashApp = new AppMeta
                {
                    Id = "aptrash001",
                    Name = "回收站测试",
                    Type = AppTypes.StaticWeb,
                    CurrentVersion = 1,
                    Status = "ready",
                    CreatedAt = TimeUtil.Now(),
                    UpdatedAt = TimeUtil.Now()
                };
                var trashVersionDir = vws.VersionDir(trashApp.Id, 1);
                Directory.CreateDirectory(trashVersionDir);
                File.WriteAllText(Path.Combine(trashVersionDir, "index.html"), "<html><body data-app-root>t</body></html>");
                File.WriteAllText(Path.Combine(trashVersionDir, "manifest.json"),
                    new Manifest { Id = trashApp.Id, Name = trashApp.Name }.ToJson());
                vrepo.Upsert(trashApp);
                vrepo.Delete(trashApp.Id);

                var trash = new TrashService(vws, vrepo);
                r.Check("删除后进入回收站", trash.List().Any(e => e.AppId == trashApp.Id));
                var restored = trash.Restore(trashApp.Id);
                r.Check("从回收站恢复应用", restored != null && vrepo.Get(trashApp.Id) != null);
                r.Check("恢复后版本目录存在", Directory.Exists(vws.VersionDir(trashApp.Id, 1)));

                // 14. 导出 zip / 诊断包
                var exportApp = vrepo.Get(trashApp.Id);
                var exportZip = AppExporter.ExportZip(vws, exportApp, 1, vws.VersionDir(exportApp.Id, 1),
                    vrepo.ReadManifest(exportApp));
                r.Check("导出 zip 成功且非空", File.Exists(exportZip) && new FileInfo(exportZip).Length > 200);

                var diagInfo = AppDiagnostics.BuildInfo(vws, vrepo, versions, trash, "selftest", deps);
                var diagZip = AppDiagnostics.ExportZip(vws, diagInfo);
                r.Check("导出诊断包成功", File.Exists(diagZip) && new FileInfo(diagZip).Length > 100);
                r.Check("诊断信息包含应用数量", JsonLite.Int(diagInfo, "appCount") >= 1);

                var rolledBack = versions.Rollback(vapp.Id, 1);
                r.Check("回滚后当前版本为 v0001", rolledBack.CurrentVersion == 1);

                // 切回 v0002 后再按 keep=1 清理：应删掉更旧的 v0001，保留当前 v0002
                versions.Rollback(vapp.Id, 2);
                var pruned = versions.Prune(vapp.Id, 1);
                var afterPrune = versions.List(vapp.Id);
                r.Check("保留 1 个版本时清掉更旧的版本",
                    pruned.Count == 1 && pruned[0] == 1 && afterPrune.Count == 1 && afterPrune[0].Seq == 2);
                r.Check("当前版本永不被清理", afterPrune.Count == 1 && afterPrune[0].Seq == 2);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPFORGE_NO_SHELL_OPEN", previousNoShell);
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
                catch { /* 忽略 */ }
            }
        }

        private static string CreateSelftestRoot(Workspace realWs)
        {
            var stamp = "selftest-" + Guid.NewGuid().ToString("N");
            var candidates = new List<string>
            {
                Path.Combine(realWs.Root, "." + stamp)
            };
            try { candidates.Add(Path.Combine(Path.GetTempPath(), "appforge-" + stamp)); }
            catch { /* 忽略 */ }

            var errors = new System.Text.StringBuilder();
            foreach (var root in candidates)
            {
                try
                {
                    Directory.CreateDirectory(root);
                    var probe = Path.Combine(root, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return root;
                }
                catch (Exception ex)
                {
                    errors.AppendLine("  · " + root + " → " + ex.Message);
                }
            }

            throw new IOException("找不到可写的自检临时目录：\r\n" + errors);
        }

        private static IDictionary<string, object> NewPatch(string key, object value)
        {
            var patch = JsonLite.New();
            patch[key] = value;
            return patch;
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
            catch { return false; }
        }

        private sealed class AssertRunner
        {
            public int Passed { get; private set; }
            public int Failed { get; private set; }
            public List<string> Failures { get; private set; }

            public AssertRunner()
            {
                Failures = new List<string>();
            }

            public void Check(string name, bool condition, string detail = null)
            {
                if (condition)
                {
                    Passed++;
                    ConsoleBridge.WriteLine("PASS  " + name);
                }
                else
                {
                    Fail(name, detail ?? "断言为假");
                }
            }

            public void Fail(string name, string detail)
            {
                Failed++;
                Failures.Add(name + "（" + detail + "）");
                ConsoleBridge.WriteLine("FAIL  " + name + "  ← " + detail);
            }
        }
    }
}
