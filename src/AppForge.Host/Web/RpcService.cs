using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppForge.Shared;
using AppForge.Host.Ai;
using AppForge.Host.Bootstrap;
using AppForge.Host.Diagnostics;
using AppForge.Host.Gen;
using AppForge.Host.Runtime;

namespace AppForge.Host.Web
{
    /// <summary>
    /// 打开一个目录。返回 false 表示没能叫起资源管理器；copied 表示是否已把路径放进剪贴板
    /// （界面据此决定提示文案，不要谎报"已复制"）。
    /// </summary>
    public delegate bool OpenFolderHandler(string path, out bool copied);

    /// <summary>RPC 方法的具体实现（设置页就是靠这里读写配置与密钥）。</summary>
    public sealed class RpcService
    {
        private readonly Workspace _ws;
        private readonly Log _log;
        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly AppRepository _repo;
        private readonly VersionService _versions;
        private readonly TrashService _trash;
        private readonly OpenAiClient _ai;
        private readonly Func<string, IDictionary<string, object>> _openApp;
        private readonly OpenFolderHandler _openFolder;
        private readonly GenerationEngine _engine;
        private readonly Action<bool> _applyHotkey;

        public RpcDispatcher Dispatcher { get; private set; }

        public RpcService(
            Workspace ws,
            Log log,
            SettingsStore settings,
            SecretsStore secrets,
            AppRepository repo,
            OpenAiClient ai,
            Func<string, IDictionary<string, object>> openApp,
            OpenFolderHandler openFolder,
            GenerationEngine engine,
            Action<bool> applyHotkey)
        {
            _ws = ws;
            _log = log;
            _settings = settings;
            _secrets = secrets;
            _repo = repo;
            _versions = new VersionService(ws, repo);
            _trash = new TrashService(ws, repo);
            _ai = ai;
            _openApp = openApp;
            _openFolder = openFolder;
            _engine = engine;
            _applyHotkey = applyHotkey;

            Dispatcher = new RpcDispatcher(log);
            RegisterAll();
        }

        public static string Version
        {
            get
            {
                var v = typeof(RpcService).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        private void RegisterAll()
        {
            Dispatcher.RegisterSync(RpcMethods.Ping, Ping);
            Dispatcher.RegisterSync(RpcMethods.Info, Info);
            Dispatcher.RegisterSync(RpcMethods.AppsList, AppsList);
            Dispatcher.RegisterSync(RpcMethods.AppsRename, AppsRename);
            Dispatcher.RegisterSync(RpcMethods.AppsPin, AppsPin);
            Dispatcher.RegisterSync(RpcMethods.AppsDelete, AppsDelete);
            Dispatcher.RegisterSync(RpcMethods.LaunchOpen, LaunchOpen);
            Dispatcher.RegisterSync(RpcMethods.SettingsGet, SettingsGet);
            Dispatcher.RegisterSync(RpcMethods.SettingsSet, SettingsSet);
            Dispatcher.RegisterSync(RpcMethods.SettingsSetApiKey, SettingsSetApiKey);
            Dispatcher.Register(RpcMethods.SettingsListModels, SettingsListModels);
            Dispatcher.Register(RpcMethods.SettingsTestModel, SettingsTestModel);
            Dispatcher.RegisterSync(RpcMethods.DepStatusMethod, DepStatusAll);
            Dispatcher.RegisterSync(RpcMethods.OpenDataDir, OpenDataDir);
            Dispatcher.RegisterSync(RpcMethods.RevealApp, RevealApp);
            Dispatcher.RegisterSync(RpcMethods.FsList, FsList);
            Dispatcher.RegisterSync(RpcMethods.GenStart, GenStart);
            Dispatcher.RegisterSync(RpcMethods.GenModify, GenModify);
            Dispatcher.RegisterSync(RpcMethods.GenCancel, GenCancel);
            Dispatcher.RegisterSync(RpcMethods.AppsGet, AppsGet);
            Dispatcher.RegisterSync(RpcMethods.VersionsList, VersionsList);
            Dispatcher.RegisterSync(RpcMethods.VersionsRollback, VersionsRollback);
            Dispatcher.RegisterSync(RpcMethods.VersionsDiff, VersionsDiffHandler);
            Dispatcher.RegisterSync(RpcMethods.AppsExport, AppsExport);
            Dispatcher.RegisterSync(RpcMethods.AppsShortcut, AppsShortcut);
            Dispatcher.RegisterSync(RpcMethods.TrashList, TrashListHandler);
            Dispatcher.RegisterSync(RpcMethods.TrashRestore, TrashRestoreHandler);
            Dispatcher.RegisterSync(RpcMethods.DiagInfo, DiagInfoHandler);
            Dispatcher.RegisterSync(RpcMethods.DiagExport, DiagExportHandler);
            Dispatcher.RegisterSync(RpcMethods.SystemOpenLogs, OpenLogsHandler);
        }

        // ---------- 导出 / 快捷方式 / 回收站 / 诊断 ----------

        private object AppsExport(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var app = _repo.Get(id);
            var manifest = _repo.ReadManifest(app);
            var versionDir = _repo.CurrentVersionDir(app);
            var path = AppExporter.ExportZip(_ws, app, app.CurrentVersion, versionDir, manifest);
            _log.Info("导出应用 zip：" + path);

            var d = JsonLite.New();
            d["path"] = path;
            d["size"] = new FileInfo(path).Length;
            return d;
        }

        private object AppsShortcut(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var path = ShortcutCreator.CreateDesktop(_repo.Get(id), CurrentExePath(), _log);
            var d = JsonLite.New();
            d["path"] = path;
            return d;
        }

        private object TrashListHandler(IDictionary<string, object> p)
        {
            var items = new List<object>();
            foreach (var entry in _trash.List()) items.Add(entry.ToJson());
            var d = JsonLite.New();
            d["items"] = items;
            d["count"] = items.Count;
            return d;
        }

        private object TrashRestoreHandler(IDictionary<string, object> p)
        {
            var appId = JsonLite.Str(p, "appId");
            if (string.IsNullOrEmpty(appId)) appId = JsonLite.Str(p, "id");
            if (string.IsNullOrEmpty(appId)) throw new ArgumentException("缺少 appId");

            var app = _trash.Restore(appId);
            _log.Info("从回收站恢复应用：" + appId);

            var d = JsonLite.New();
            d["app"] = app.ToJson();
            return d;
        }

        private object VersionsDiffHandler(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var from = JsonLite.Int(p, "from");
            var to = JsonLite.Int(p, "to");
            if (from <= 0 || to <= 0) throw new ArgumentException("缺少 from / to 版本号");
            return VersionDiff.Compare(_ws, id, from, to, 60);
        }

        private object DiagInfoHandler(IDictionary<string, object> p)
        {
            return AppDiagnostics.BuildInfo(_ws, _repo, _versions, _trash, RpcService.Version, StartupChecks.RunAll(_ws));
        }

        private object DiagExportHandler(IDictionary<string, object> p)
        {
            var info = AppDiagnostics.BuildInfo(_ws, _repo, _versions, _trash, RpcService.Version, StartupChecks.RunAll(_ws));
            var path = AppDiagnostics.ExportZip(_ws, info);
            _log.Info("导出诊断包：" + path);
            var d = JsonLite.New();
            d["path"] = path;
            d["size"] = new FileInfo(path).Length;
            return d;
        }

        private object OpenLogsHandler(IDictionary<string, object> p)
        {
            return OpenFolderResult(_ws.LogsDir);
        }

        /// <summary>打开目录并把结果回给界面：ok=false 时界面提示"路径已复制"而不是静默失败。</summary>
        private IDictionary<string, object> OpenFolderResult(string path)
        {
            bool copied;
            var opened = _openFolder(path, out copied);
            var d = JsonLite.New();
            d["ok"] = opened;
            d["path"] = path ?? string.Empty;
            d["copied"] = copied;   // 打不开时路径是否已进剪贴板（界面的提示文案据此调整）
            return d;
        }

        private static string CurrentExePath()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return process.MainModule != null ? process.MainModule.FileName : null;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- 版本 ----------

        private object AppsGet(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var d = JsonLite.New();
            d["app"] = _repo.Get(id).ToJson();
            d["versions"] = VersionsToJson(id);
            return d;
        }

        private object VersionsList(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var d = JsonLite.New();
            d["appId"] = id;
            d["versions"] = VersionsToJson(id);
            return d;
        }

        private object VersionsRollback(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var seq = JsonLite.Int(p, "seq");
            if (seq <= 0) throw new ArgumentException("缺少版本号 seq");

            var app = _versions.Rollback(id, seq);
            _log.Info("回滚版本：" + id + " -> v" + seq);
            var d = JsonLite.New();
            d["app"] = app.ToJson();
            d["versions"] = VersionsToJson(id);
            return d;
        }

        private List<object> VersionsToJson(string appId)
        {
            var list = new List<object>();
            foreach (var version in _versions.List(appId)) list.Add(version.ToJson());
            return list;
        }

        // ---------- 生成 ----------

        private object GenStart(IDictionary<string, object> p)
        {
            var prompt = (JsonLite.Str(p, "prompt") ?? string.Empty).Trim();
            if (prompt.Length < 2) throw new ArgumentException("请先用一句话描述你要的应用");
            if (prompt.Length > 2000) throw new ArgumentException("需求描述过长（≤2000 字）");
            if (!_secrets.HasApiKey())
                throw new RpcException(ErrorCodes.BadRequest, "尚未配置 API Key：请到设置页粘贴并保存密钥");
            if (_engine == null)
                throw new RpcException(ErrorCodes.Internal, "生成引擎未就绪（界面环境异常）");
            if (_engine.IsRunning)
                throw new RpcException(ErrorCodes.Conflict, "已有生成任务在进行中，请等待完成");

            var runId = _engine.Start(prompt);
            _log.Info("开始生成 runId=" + runId);

            var d = JsonLite.New();
            d["runId"] = runId;
            return d;
        }

        private object GenCancel(IDictionary<string, object> p)
        {
            var runId = JsonLite.Str(p, "runId");
            if (!string.IsNullOrEmpty(runId) && _engine != null) _engine.Cancel(runId);
            return JsonLite.New();
        }

        private object GenModify(IDictionary<string, object> p)
        {
            var appId = RequireAppId(p);
            var prompt = (JsonLite.Str(p, "prompt") ?? string.Empty).Trim();
            if (prompt.Length < 2) throw new ArgumentException("请说明要改什么（例如：把字体调大、加一个按日期排序的按钮）");
            if (prompt.Length > 2000) throw new ArgumentException("修改要求过长（≤2000 字）");
            if (!_secrets.HasApiKey())
                throw new RpcException(ErrorCodes.BadRequest, "尚未配置 API Key：请到设置页粘贴并保存密钥");
            if (_engine == null)
                throw new RpcException(ErrorCodes.Internal, "生成引擎未就绪（界面环境异常）");
            if (_engine.IsRunning)
                throw new RpcException(ErrorCodes.Conflict, "已有生成任务在进行中，请等待完成");

            var runId = _engine.StartModify(appId, prompt);
            _log.Info("开始修改应用 appId=" + appId + " runId=" + runId);

            var d = JsonLite.New();
            d["runId"] = runId;
            d["appId"] = appId;
            return d;
        }

        // ---------- 基础 ----------

        private object Ping(IDictionary<string, object> p)
        {
            var d = JsonLite.New();
            d["pong"] = true;
            d["product"] = Defaults.ProductName;
            d["version"] = Version;
            return d;
        }

        private object Info(IDictionary<string, object> p)
        {
            var d = JsonLite.New();
            d["product"] = Defaults.ProductName;
            d["version"] = Version;
            d["dataDir"] = _ws.Root;
            d["portable"] = _ws.Portable;
            d["os"] = StartupChecks.OsDescription();
            d["webview2"] = StartupChecks.WebView2Version() ?? "未安装";
            d["dotnet"] = (StartupChecks.DotNetRelease() ?? 0).ToString();
            d["hasApiKey"] = _secrets.HasApiKey();
            return d;
        }

        // ---------- 应用库 ----------

        private object AppsList(IDictionary<string, object> p)
        {
            var search = JsonLite.Str(p, "search");
            var items = _repo.List(search).Select(a => (object)a.ToJson()).ToList();
            var d = JsonLite.New();
            d["apps"] = items;
            d["count"] = items.Count;
            return d;
        }

        private object AppsRename(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            _repo.Rename(id, JsonLite.Str(p, "name"));
            return WrapApp(id);
        }

        private object AppsPin(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            _repo.Pin(id, JsonLite.Bool(p, "pinned"));
            return WrapApp(id);
        }

        private object AppsDelete(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            _repo.Delete(id);
            var d = JsonLite.New();
            d["deleted"] = id;
            return d;
        }

        private object LaunchOpen(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            var metrics = _openApp(id);
            if (metrics == null) throw new RpcException(ErrorCodes.Internal, "应用启动失败");
            return metrics;
        }

        // ---------- 设置 / 密钥 ----------

        private object SettingsGet(IDictionary<string, object> p)
        {
            var d = JsonLite.New();
            d["settings"] = SettingsToJson(_settings.Current);
            d["hasApiKey"] = _secrets.HasApiKey();
            d["maskedKey"] = SecretsStore.Mask(_secrets.GetApiKey());
            d["deps"] = DepsToJson();
            return d;
        }

        private object SettingsSet(IDictionary<string, object> p)
        {
            var patch = JsonLite.Obj(p, "patch");
            if (patch == null) throw new ArgumentException("缺少 patch");

            var next = _settings.Current.ApplyPatch(patch);
            var errors = next.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));

            _settings.Save(next);
            _log.Info("设置已更新：baseUrl=" + next.BaseUrl + " model=" + next.Model);

            var warnings = new List<string>();
            if (patch.ContainsKey("autoStart"))
            {
                try
                {
                    var actual = AutoStart.Apply(next.AutoStart);
                    if (actual != next.AutoStart) warnings.Add("开机自启状态未能同步（注册表不可写）");
                }
                catch (Exception ex)
                {
                    warnings.Add("开机自启设置失败：" + ex.Message);
                }
            }

            if (patch.ContainsKey("hotkeyEnabled") && _applyHotkey != null)
            {
                try { _applyHotkey(next.HotkeyEnabled); }
                catch (Exception ex) { warnings.Add("全局快捷键设置失败：" + ex.Message); }
            }

            var d = JsonLite.New();
            d["settings"] = SettingsToJson(next);
            d["hasApiKey"] = _secrets.HasApiKey();
            d["maskedKey"] = SecretsStore.Mask(_secrets.GetApiKey());
            d["warnings"] = warnings;
            return d;
        }

        private object SettingsSetApiKey(IDictionary<string, object> p)
        {
            var key = JsonLite.Str(p, "apiKey");
            if (key == null) throw new ArgumentException("缺少 apiKey");
            key = key.Trim();

            if (key.Length > 0 && key.Length < 8)
                throw new ArgumentException("API Key 太短，请检查是否粘贴完整");

            _secrets.SetApiKey(key);
            _log.Info(key.Length == 0 ? "API Key 已清除" : "API Key 已更新（DPAPI 加密）");

            var d = JsonLite.New();
            d["hasApiKey"] = _secrets.HasApiKey();
            d["maskedKey"] = SecretsStore.Mask(_secrets.GetApiKey());
            return d;
        }

        private async Task<object> SettingsListModels(IDictionary<string, object> p)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds)))
            {
                var models = await _ai.ListModelsAsync(cts.Token).ConfigureAwait(true);
                var d = JsonLite.New();
                d["models"] = models.ToList();
                d["current"] = _settings.Current.Model;
                return d;
            }
        }

        private async Task<object> SettingsTestModel(IDictionary<string, object> p)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds + 5)))
            {
                var result = await _ai.TestModelAsync(cts.Token).ConfigureAwait(true);
                var d = JsonLite.New();
                d["ok"] = result.Ok;
                d["latencyMs"] = result.LatencyMs;
                d["model"] = result.Model;
                d["sample"] = result.Sample ?? string.Empty;
                d["error"] = result.Error ?? string.Empty;
                d["promptTokens"] = result.PromptTokens;
                d["completionTokens"] = result.CompletionTokens;
                return d;
            }
        }

        // ---------- 依赖 / 目录 ----------

        private object DepStatusAll(IDictionary<string, object> p)
        {
            var d = JsonLite.New();
            d["deps"] = DepsToJson();
            return d;
        }

        private object OpenDataDir(IDictionary<string, object> p)
        {
            return OpenFolderResult(_ws.Root);
        }

        private object RevealApp(IDictionary<string, object> p)
        {
            var id = RequireAppId(p);
            return OpenFolderResult(_ws.AppDir(id));
        }

        /// <summary>
        /// 列出一个目录的内容（只读，且只能在 AppForge 自己的数据目录内）。
        /// 用途：某些机器上 explorer.exe 根本起不来（0xC0000142），"打开目录"退化成在界面里直接看文件列表。
        /// </summary>
        private object FsList(IDictionary<string, object> p)
        {
            var path = JsonLite.Str(p, "path");
            if (string.IsNullOrWhiteSpace(path)) path = _ws.Root;

            var full = Path.GetFullPath(path);
            if (!PathGuard.IsWithin(_ws.Root, full))
                throw new RpcException(ErrorCodes.NotAllowed, "只能查看 AppForge 自己的数据目录");
            if (!Directory.Exists(full))
                throw new RpcException(ErrorCodes.NotFound, "目录不存在：" + full);

            var dir = new DirectoryInfo(full);
            var items = new List<object>();

            foreach (var sub in dir.GetDirectories())
            {
                if (items.Count >= 300) break;
                var d = JsonLite.New();
                d["name"] = sub.Name;
                d["dir"] = true;
                d["size"] = 0;
                d["mtime"] = sub.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
                items.Add(d);
            }

            foreach (var file in dir.GetFiles())
            {
                if (items.Count >= 300) break;
                var d = JsonLite.New();
                d["name"] = file.Name;
                d["dir"] = false;
                d["size"] = file.Length;
                d["mtime"] = file.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
                items.Add(d);
            }

            var result = JsonLite.New();
            result["path"] = full;
            result["parent"] = string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_ws.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.GetDirectoryName(full);
            result["items"] = items;
            result["count"] = items.Count;
            return result;
        }

        // ---------- 助手 ----------

        private string RequireAppId(IDictionary<string, object> p)
        {
            var id = JsonLite.Str(p, "id");
            if (string.IsNullOrEmpty(id)) id = JsonLite.Str(p, "appId");
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("缺少 id");
            var app = _repo.Get(id);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + id);
            return id;
        }

        private object WrapApp(string id)
        {
            var app = _repo.Get(id);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + id);
            var d = JsonLite.New();
            d["app"] = app.ToJson();
            return d;
        }

        private List<object> DepsToJson()
        {
            var list = new List<object>();
            foreach (var dep in StartupChecks.RunAll(_ws))
                list.Add(dep.ToJson());
            return list;
        }

        private static IDictionary<string, object> SettingsToJson(Settings s)
        {
            var d = JsonLite.New();
            d["baseUrl"] = s.BaseUrl;
            d["model"] = s.Model;
            d["temperature"] = s.Temperature;
            d["maxTokens"] = s.MaxTokens;
            d["timeoutSeconds"] = s.TimeoutSeconds;
            d["keepVersions"] = s.KeepVersions;
            d["firstRunDone"] = s.FirstRunDone;
            d["autoStart"] = s.AutoStart;
            d["hotkeyEnabled"] = s.HotkeyEnabled;
            d["autoStartActual"] = AutoStart.IsEnabled();

            var defaults = JsonLite.New();
            defaults["baseUrl"] = Defaults.BaseUrl;
            defaults["model"] = Defaults.Model;
            d["defaults"] = defaults;
            return d;
        }
    }
}
