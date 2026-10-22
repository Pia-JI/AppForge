using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppForge.Host.Ai;
using AppForge.Host.Diagnostics;
using AppForge.Shared;
using Microsoft.Web.WebView2.Core;

namespace AppForge.Host.Gen
{
    public sealed class GenOutcome
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public string Entry { get; set; }
        public int FileCount { get; set; }
        public int Seq { get; set; }
        public string Mode { get; set; }
        public bool NoChange { get; set; }
        public long TokensIn { get; set; }
        public long TokensOut { get; set; }
    }

    /// <summary>
    /// 生成引擎：
    ///   新建：plan → code → selftest → repair(≤3) → finalize(v0001)
    ///   修改：读取当前版本 → 同一个闭环 → finalize(vNNNN+1)，未产生改动则不入库
    /// 只写草稿目录，自测通过才进正式应用库。
    /// </summary>
    public sealed class GenerationEngine
    {
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".html", ".htm", ".js", ".mjs", ".css", ".json", ".svg", ".txt", ".md", ".webmanifest"
        };

        private const int MaxCodeRounds = 12;
        private const int MaxRepairAttempts = 3;
        private const int MaxCodeRoundsPerRepair = 6;
        private const int MaxToolFileChars = 400000;
        private const int ModifyPerFileLimit = 40000;
        private const int ModifyTotalLimit = 240000;

        private readonly Workspace _ws;
        private readonly Log _log;
        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly AppRepository _repo;
        private readonly VersionService _versions;
        private readonly CoreWebView2Environment _environment;
        private readonly Action<string, object> _push;
        private readonly OpenAiChatClient _chat;
        private readonly AppSelfTester _tester;
        private readonly Dictionary<string, CancellationTokenSource> _runs;

        public GenerationEngine(
            Workspace workspace,
            Log log,
            SettingsStore settings,
            SecretsStore secrets,
            AppRepository repo,
            CoreWebView2Environment environment,
            Action<string, object> push)
        {
            _ws = workspace;
            _log = log;
            _settings = settings;
            _secrets = secrets;
            _repo = repo;
            _versions = new VersionService(workspace, repo);
            _environment = environment;
            _push = push;
            _chat = new OpenAiChatClient(settings, secrets, log);
            _tester = new AppSelfTester(log);
            _runs = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        }

        public bool IsRunning
        {
            get { lock (_runs) return _runs.Count > 0; }
        }

        /// <summary>GUI：新建应用。</summary>
        public string Start(string prompt)
        {
            return StartInternal("create", null, prompt);
        }

        /// <summary>GUI：修改已有应用。</summary>
        public string StartModify(string appId, string prompt)
        {
            return StartInternal("modify", appId, prompt);
        }

        private string StartInternal(string mode, string appId, string prompt)
        {
            var runId = AppId.NewRun();
            var cts = new CancellationTokenSource();
            lock (_runs) _runs[runId] = cts;

            Action<string, string, string, string, IDictionary<string, object>> emit =
                delegate(string kind, string step, string status, string message, IDictionary<string, object> extra)
                {
                    PushEvent(runId, kind, step, status, message, extra);
                };

            var task = RunCoreAsync(runId, mode, appId, prompt, emit, cts.Token);
            task.ContinueWith(delegate { lock (_runs) _runs.Remove(runId); }, TaskScheduler.Default);
            return runId;
        }

        public void Cancel(string runId)
        {
            lock (_runs)
            {
                CancellationTokenSource cts;
                if (_runs.TryGetValue(runId, out cts))
                {
                    try { cts.Cancel(); } catch { /* 忽略 */ }
                }
            }
        }

        public void CancelAll()
        {
            lock (_runs)
            {
                foreach (var cts in _runs.Values)
                {
                    try { cts.Cancel(); } catch { /* 忽略 */ }
                }
                _runs.Clear();
            }
        }

        /// <summary>CLI：新建。</summary>
        public Task<GenOutcome> RunOnceAsync(string prompt, Action<string, string, string, string, IDictionary<string, object>> emit, CancellationToken ct)
        {
            return RunCoreAsync(AppId.NewRun(), "create", null, prompt, emit, ct);
        }

        /// <summary>CLI：修改。</summary>
        public Task<GenOutcome> RunOnceModifyAsync(string appId, string prompt, Action<string, string, string, string, IDictionary<string, object>> emit, CancellationToken ct)
        {
            return RunCoreAsync(AppId.NewRun(), "modify", appId, prompt, emit, ct);
        }

        // ---------------- 主流程 ----------------

        private async Task<GenOutcome> RunCoreAsync(
            string runId,
            string mode,
            string targetAppId,
            string prompt,
            Action<string, string, string, string, IDictionary<string, object>> emit,
            CancellationToken ct)
        {
            var isModify = string.Equals(mode, "modify", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(targetAppId);
            var tokens = new TokenCounter();
            var draftDir = Path.Combine(_ws.DraftsDir, runId);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var saved = false;
            AppMeta existing = null;
            var appId = isModify ? targetAppId : AppId.New();

            try
            {
                AppMeta meta;
                if (isModify)
                {
                    existing = _repo.Get(appId);
                    if (existing == null) throw new RpcException(ErrorCodes.NotFound, "要修改的应用不存在");
                    meta = CloneMeta(existing);
                }
                else
                {
                    meta = new AppMeta
                    {
                        Id = appId,
                        Type = AppTypes.StaticWeb,
                        Status = "draft",
                        Name = DeriveName(prompt),
                        Description = TextUtil.Ellipsis(prompt, 40),
                        Icon = "🧩"
                    };
                }

                Directory.CreateDirectory(draftDir);
                _log.Info((isModify ? "修改开始" : "生成开始") + " runId=" + runId + " app=" + appId +
                          " prompt=" + TextUtil.Ellipsis(prompt, 80));

                emit("step", "plan", "running", isModify ? "读取现有应用…" : "理解需求并规划…", null);

                var messages = new List<object>();
                messages.Add(Msg("system", Prompts.SystemPrompt()));
                var entry = "index.html";

                if (isModify)
                {
                    var currentDir = _ws.VersionDir(appId, existing.CurrentVersion);
                    if (!Directory.Exists(currentDir))
                        throw new RpcException(ErrorCodes.NotFound, "当前版本目录缺失：v" + existing.CurrentVersion);

                    CopyDirectory(currentDir, draftDir);
                    LoadTextFiles(draftDir, files);

                    var draftManifest = ReadDraftManifest(draftDir);
                    if (draftManifest != null && !string.IsNullOrWhiteSpace(draftManifest.Entry)) entry = draftManifest.Entry;

                    var dump = Prompts.BuildFilesDump(files, ModifyPerFileLimit, ModifyTotalLimit);
                    messages.Add(Msg("user", Prompts.ModifyPrompt(prompt, appId, existing.Name, dump)));
                    emit("step", "plan", "ok", "已读取现有应用（" + files.Count + " 个文件，v" + existing.CurrentVersion + "）", null);
                }
                else
                {
                    messages.Add(Msg("user", Prompts.UserPrompt(prompt, appId)));
                    emit("step", "plan", "ok", "需求已理解，开始写文件", null);
                }

                var tools = Prompts.BuildTools();
                emit("step", "code", "running", isModify ? "按需求修改代码…" : "生成代码中…", null);
                var written = await CodegenAsync(runId, messages, tools, draftDir, files, meta, tokens, emit, ct, MaxCodeRounds);

                if (!isModify && files.Count == 0)
                    throw new RpcException(ErrorCodes.Upstream, "模型没有产出任何文件（可能是模型不支持 function calling，请在设置里换 gpt-4o-mini）");

                entry = PickEntry(files) ?? entry;
                if (!files.Keys.Any(k => k.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
                    throw new RpcException(ErrorCodes.Upstream, "没有生成 .html 入口文件");

                if (isModify && written == 0)
                {
                    emit("step", "code", "ok", "本次没有产生文件改动", null);
                    var noChange = new GenOutcome
                    {
                        AppId = appId,
                        Name = existing.Name,
                        Entry = entry,
                        FileCount = files.Count,
                        Seq = existing.CurrentVersion,
                        Mode = "modify",
                        NoChange = true,
                        TokensIn = tokens.In,
                        TokensOut = tokens.Out
                    };
                    var extraNoChange = JsonLite.New();
                    extraNoChange["appId"] = appId;
                    extraNoChange["name"] = existing.Name;
                    extraNoChange["seq"] = existing.CurrentVersion;
                    extraNoChange["mode"] = "modify";
                    extraNoChange["noChange"] = true;
                    emit("done", null, null, "没有需要修改的内容，已保持 v" + existing.CurrentVersion, extraNoChange);
                    return noChange;
                }

                emit("step", "code", "ok",
                    (isModify ? "已修改 " : "已生成 ") + files.Count + " 个文件（" + TextUtil.Ellipsis(string.Join("、", files.Keys), 80) + "）", null);

                // 自测 + 自动修复
                SelfTestResult test = null;
                for (var attempt = 0; attempt < MaxRepairAttempts; attempt++)
                {
                    emit("step", "selftest", "running", attempt == 0 ? "加载页面并检查…" : "重新自测…", null);
                    test = await _tester.TestAsync(_environment, draftDir, entry, Hosts.ForDraft(runId), 15000);

                    if (test.Pass)
                    {
                        emit("step", "selftest", "ok", "自测通过（页面内容 " + test.BodyLength + " 字符，无 JS 错误）", null);
                        break;
                    }

                    emit("step", "selftest", "fail",
                        test.Failure + (string.IsNullOrEmpty(test.Errors) ? "" : "｜" + TextUtil.Ellipsis(test.Errors, 200)), null);

                    if (attempt == MaxRepairAttempts - 1) break;

                    emit("step", "repair", "running", "根据错误自动修复（第 " + (attempt + 1) + " 次）…", null);
                    messages.Add(Msg("user", Prompts.RepairPrompt(appId, ErrorDetails(test), FilesSummary(files))));
                    await CodegenAsync(runId, messages, tools, draftDir, files, meta, tokens, emit, ct, MaxCodeRoundsPerRepair);
                    emit("step", "repair", "ok", "修复完成，重新自测", null);

                    entry = PickEntry(files) ?? entry;
                }

                if (test == null || !test.Pass)
                {
                    var detail = test == null ? "未执行自测" : (test.Failure + "\n" + ErrorDetails(test));
                    throw new RpcException(ErrorCodes.Upstream, "生成失败：应用未通过自测。\n" + TextUtil.Ellipsis(detail, 600));
                }

                // 固化
                emit("step", "finalize", "running", isModify ? "保存为新版本…" : "保存为应用版本…", null);

                var manifest = ReadDraftManifest(draftDir) ?? new Manifest();
                manifest.Id = appId;
                manifest.Name = meta.Name;
                manifest.Description = meta.Description;
                manifest.Icon = string.IsNullOrEmpty(meta.Icon) ? "🧩" : meta.Icon;
                manifest.Type = AppTypes.StaticWeb;
                manifest.Entry = entry;
                manifest.Offline = true;
                if (manifest.Window == null)
                    manifest.Window = new Manifest.WindowSpec { Width = 980, Height = 760, MinWidth = 420, MinHeight = 380, Resizable = true };
                File.WriteAllText(Path.Combine(draftDir, "manifest.json"), manifest.ToJson(), new UTF8Encoding(false));

                var manifestErrors = manifest.ValidateOnDisk(draftDir);
                if (manifestErrors.Count > 0)
                    throw new RpcException(ErrorCodes.Internal, "应用清单校验失败：" + string.Join("；", manifestErrors));

                var seq = isModify ? _versions.NextSeq(appId) : 1;
                var versionDir = _ws.VersionDir(appId, seq);
                var parent = Path.GetDirectoryName(versionDir);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
                Directory.Move(draftDir, versionDir);

                var now = TimeUtil.Now();
                meta.CurrentVersion = seq;
                meta.Status = "ready";
                meta.UpdatedAt = now;
                if (meta.CreatedAt == 0) meta.CreatedAt = now;
                meta.Bytes = JsonStore.DirectoryBytes(versionDir);
                _repo.Upsert(meta);
                saved = true;

                _versions.WriteMeta(versionDir, new VersionMeta
                {
                    Seq = seq,
                    Kind = isModify ? "modify" : "create",
                    Note = (isModify ? "AI 修改：" : "AI 生成：") + TextUtil.Ellipsis(prompt, 60),
                    Prompt = prompt,
                    Model = _settings.Current.Model,
                    CreatedAt = now,
                    Entry = entry,
                    Bytes = meta.Bytes
                });

                var pruned = _versions.Prune(appId, Math.Max(1, _settings.Current.KeepVersions));
                var prunedText = pruned.Count > 0 ? "，清理旧版本 " + string.Join("/", pruned.Select(p => "v" + p.ToString("0000"))) : string.Empty;

                emit("step", "finalize", "ok", "已保存为 v" + seq.ToString("0000") + "（" + FormatSize(meta.Bytes) + "）" + prunedText, null);

                var outcome = new GenOutcome
                {
                    AppId = appId,
                    Name = meta.Name,
                    Entry = entry,
                    FileCount = files.Count,
                    Seq = seq,
                    Mode = isModify ? "modify" : "create",
                    TokensIn = tokens.In,
                    TokensOut = tokens.Out
                };

                var doneExtra = JsonLite.New();
                doneExtra["appId"] = appId;
                doneExtra["name"] = meta.Name;
                doneExtra["entry"] = entry;
                doneExtra["seq"] = seq;
                doneExtra["mode"] = outcome.Mode;
                doneExtra["fileCount"] = files.Count;
                doneExtra["tokensIn"] = tokens.In;
                doneExtra["tokensOut"] = tokens.Out;
                emit("done", null, null, isModify ? "修改完成" : "生成完成", doneExtra);

                _log.Info((isModify ? "修改成功" : "生成成功") + " appId=" + appId + " v" + seq +
                          " 文件数=" + files.Count + " tokens(in/out)=" + tokens.In + "/" + tokens.Out);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                emit("error", null, null, "已取消", null);
                _log.Info((isModify ? "修改" : "生成") + "已取消 runId=" + runId);
                throw;
            }
            catch (Exception ex)
            {
                emit("error", null, null, ex.Message, null);
                _log.Error((isModify ? "修改失败" : "生成失败") + " runId=" + runId, ex);
                throw;
            }
            finally
            {
                if (!saved)
                {
                    try { if (Directory.Exists(draftDir)) Directory.Delete(draftDir, true); }
                    catch { /* 忽略 */ }
                }
            }
        }

        // ---------------- 代码生成循环 ----------------

        private async Task<int> CodegenAsync(
            string runId,
            IList<object> messages,
            IList<object> tools,
            string draftDir,
            IDictionary<string, string> files,
            AppMeta meta,
            TokenCounter tokens,
            Action<string, string, string, string, IDictionary<string, object>> emit,
            CancellationToken ct,
            int maxRounds)
        {
            var done = false;
            var written = 0;

            for (var round = 0; round < maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                emit("log", null, null, "模型思考中…（第 " + (round + 1) + " 轮）", null);

                var turn = await _chat.CompleteAsync(messages, tools, ct);
                tokens.In += turn.PromptTokens;
                tokens.Out += turn.CompletionTokens;
                messages.Add(AssistantMsg(turn));

                if (turn.ToolCalls.Count == 0)
                {
                    if (files.Count > 0) { done = true; break; }
                    messages.Add(Msg("user", "请使用工具完成工作：先 set_app_info，再 write_file 写完整文件，最后调用 done。"));
                    continue;
                }

                foreach (var call in turn.ToolCalls)
                {
                    var before = files.Count;
                    var output = ExecuteTool(call, draftDir, files, meta, emit);
                    if (string.Equals(call.Name, "write_file", StringComparison.OrdinalIgnoreCase) &&
                        output.StartsWith("已写入", StringComparison.Ordinal)) written++;
                    messages.Add(ToolMsg(call.Id, output));
                    if (string.Equals(call.Name, "done", StringComparison.OrdinalIgnoreCase)) done = true;
                }

                if (done) break;
            }

            if (!done && files.Count > 0)
                emit("log", null, null, "模型未显式调用 done，按已有文件继续", null);

            return written;
        }

        // ---------------- 工具执行 ----------------

        private string ExecuteTool(
            ToolCall call,
            string draftDir,
            IDictionary<string, string> files,
            AppMeta meta,
            Action<string, string, string, string, IDictionary<string, object>> emit)
        {
            try
            {
                var name = (call.Name ?? string.Empty).Trim();
                var args = ParseArgs(call.Arguments);

                switch (name.ToLowerInvariant())
                {
                    case "set_app_info":
                    {
                        var appName = (JsonLite.Str(args, "name") ?? string.Empty).Trim();
                        var description = (JsonLite.Str(args, "description") ?? string.Empty).Trim();
                        var icon = (JsonLite.Str(args, "icon") ?? string.Empty).Trim();
                        if (appName.Length > 0) meta.Name = TextUtil.Ellipsis(appName, 16);
                        if (description.Length > 0) meta.Description = TextUtil.Ellipsis(description, 60);
                        if (icon.Length > 0) meta.Icon = TextUtil.Ellipsis(icon, 4);
                        emit("log", null, null, "应用信息：" + meta.Name + " " + meta.Icon, null);
                        return "已设置：" + meta.Name;
                    }

                    case "write_file":
                    {
                        var path = NormalizePath(JsonLite.Str(args, "path"));
                        var content = JsonLite.Str(args, "content") ?? string.Empty;
                        if (path.Length == 0) return "失败：path 不能为空";
                        if (content.Length > MaxToolFileChars) return "失败：单文件过大（>" + MaxToolFileChars + " 字符）";

                        var ext = Path.GetExtension(path);
                        if (!AllowedExtensions.Contains(ext)) return "失败：不支持的文件类型 " + ext;

                        var full = PathGuard.ResolveWithin(draftDir, path);
                        var dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(full, content, new UTF8Encoding(false));
                        files[path] = content;

                        emit("log", null, null, "写入 " + path + "（" + content.Length + " 字符）", null);
                        return "已写入 " + path;
                    }

                    case "read_file":
                    {
                        var path = NormalizePath(JsonLite.Str(args, "path"));
                        string content;
                        if (files.TryGetValue(path, out content)) return TextUtil.Ellipsis(content, 20000);
                        var full = PathGuard.ResolveWithin(draftDir, path);
                        return File.Exists(full) ? TextUtil.Ellipsis(File.ReadAllText(full), 20000) : "失败：文件不存在";
                    }

                    case "list_files":
                        return files.Count == 0 ? "（空）" : string.Join("\n", files.Keys);

                    case "done":
                        return "完成";

                    default:
                        return "失败：未知工具 " + name;
                }
            }
            catch (PathGuardException ex)
            {
                return "失败：路径越界（" + ex.Message + "）";
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }
        }

        // ---------------- 文件工具 ----------------

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dest = Path.Combine(target, rel);
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(file, dest, true);
            }
        }

        private static void LoadTextFiles(string dir, IDictionary<string, string> files)
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                var name = Path.GetFileName(rel);
                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "version.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!AllowedExtensions.Contains(Path.GetExtension(rel))) continue;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxToolFileChars) continue;
                    files[rel] = File.ReadAllText(file);
                }
                catch
                {
                    // 跳过读不了的文件
                }
            }
        }

        private static Manifest ReadDraftManifest(string dir)
        {
            try
            {
                var json = JsonStore.ReadOrNull(Path.Combine(dir, "manifest.json"));
                return string.IsNullOrWhiteSpace(json) ? null : Manifest.FromJson(json);
            }
            catch
            {
                return null;
            }
        }

        private static AppMeta CloneMeta(AppMeta source)
        {
            return new AppMeta
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                Type = source.Type,
                Icon = source.Icon,
                CurrentVersion = source.CurrentVersion,
                Status = source.Status,
                Pinned = source.Pinned,
                LaunchCount = source.LaunchCount,
                LastOpenedAt = source.LastOpenedAt,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt,
                Bytes = source.Bytes
            };
        }

        // ---------------- 事件与消息 ----------------

        private void PushEvent(string runId, string kind, string step, string status, string message, IDictionary<string, object> extra)
        {
            var payload = JsonLite.New();
            payload["runId"] = runId;
            payload["kind"] = kind;
            if (step != null) payload["step"] = step;
            if (status != null) payload["status"] = status;
            if (message != null) payload["message"] = message;
            if (extra != null)
            {
                foreach (var kv in extra) payload[kv.Key] = kv.Value;
            }

            try
            {
                if (_push != null) _push("gen.event", payload);
            }
            catch (Exception ex)
            {
                _log.Warn("推送生成事件失败：" + ex.Message);
            }
        }

        private static IDictionary<string, object> Msg(string role, string content)
        {
            var d = JsonLite.New();
            d["role"] = role;
            d["content"] = content;
            return d;
        }

        private static IDictionary<string, object> AssistantMsg(ChatTurn turn)
        {
            var d = JsonLite.New();
            d["role"] = "assistant";
            d["content"] = turn.Content ?? string.Empty;
            if (turn.ToolCalls.Count > 0)
            {
                var calls = new List<object>();
                foreach (var call in turn.ToolCalls)
                {
                    var fn = JsonLite.New();
                    fn["name"] = call.Name;
                    fn["arguments"] = call.Arguments;
                    var item = JsonLite.New();
                    item["id"] = call.Id;
                    item["type"] = "function";
                    item["function"] = fn;
                    calls.Add(item);
                }
                d["tool_calls"] = calls;
            }
            return d;
        }

        private static IDictionary<string, object> ToolMsg(string toolCallId, string content)
        {
            var d = JsonLite.New();
            d["role"] = "tool";
            d["tool_call_id"] = toolCallId;
            d["content"] = content ?? string.Empty;
            return d;
        }

        private static IDictionary<string, object> ParseArgs(string json)
        {
            try { return JsonLite.ParseObject(json); }
            catch { return JsonLite.New(); }
        }

        private static string NormalizePath(string path)
        {
            var p = (path ?? string.Empty).Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            return p.TrimStart('/');
        }

        private static string PickEntry(IDictionary<string, string> files)
        {
            var index = files.Keys.FirstOrDefault(k => k.Equals("index.html", StringComparison.OrdinalIgnoreCase));
            if (index != null) return index;
            return files.Keys.FirstOrDefault(k => k.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        }

        private static string FilesSummary(IDictionary<string, string> files)
        {
            var sb = new StringBuilder();
            foreach (var kv in files) sb.AppendLine("- " + kv.Key + "（" + kv.Value.Length + " 字符）");
            return sb.ToString();
        }

        private static string ErrorDetails(SelfTestResult test)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(test.Failure)) sb.AppendLine(test.Failure);
            if (!string.IsNullOrEmpty(test.Errors)) sb.AppendLine("页面报错：").AppendLine(test.Errors);
            if (test.BodyLength > 0) sb.AppendLine("body 内容长度：" + test.BodyLength);
            sb.AppendLine("是否找到 data-app-root：" + (test.RootFound ? "是" : "否"));
            return sb.ToString();
        }

        private static string DeriveName(string prompt)
        {
            var text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0) return "新应用";
            return TextUtil.Ellipsis(text, 12);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        private sealed class TokenCounter
        {
            public long In;
            public long Out;
        }
    }
}
