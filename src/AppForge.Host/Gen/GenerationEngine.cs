using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        private const int MaxRepairAttempts = 3;
        private const int MaxCodeRoundsPerRepair = 6;
        private const int MaxToolFileChars = 400000;
        private const int ModifyPerFileLimit = 40000;
        private const int ModifyTotalLimit = 240000;

        // ---- 轮次预算（只用于防止无限烧钱，不再作为“未写完也收尾”的开关）----
        private const int BaseCodeRounds = 24;        // 新建应用的轮次基数
        private const int ModifyBaseRounds = 16;      // 修改应用的轮次基数
        private const int ComplexExtraRounds = 16;    // 复杂需求加成
        private const int MaxCodeRoundsCap = 160;     // 安全上限；达到时拒绝保存半成品
        private const int RoundExtensionSize = 16;    // 未完成时自动续轮，每档 16 轮
        private const int LongPromptChars = 150;      // 需求超过这个字数算复杂需求

        // ---- 收尾与上下文控制 ----
        private const int SingleWriteLineHint = 400;  // 引导模型单次写入的行数上限
        private const int MaxIdleRounds = 4;          // 复杂需求下入口已完整、连续无变化的最大观察轮数
        private const int ReadFileResultLimit = 4000; // 回给模型的单次读取上限（上下文瘦身）
        private const int SelfTestTimeoutMs = 12000;  // 自测导航超时

        /// <summary>出现这些词就认为需求偏复杂，多给一些轮次。</summary>
        private static readonly string[] ComplexKeywords =
        {
            "多", "图表", "统计", "报表", "导入", "导出", "日历", "看板",
            "订单", "库存", "多页面", "权限", "搜索", "筛选", "设置",
            "工具箱", "助手", "聊天", "画板", "编辑器", "计算器", "游戏",
            "播放器", "记账", "提醒", "任务", "清单", "转换", "批量"
        };

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
                bool complex;
                var budget = ComputeRoundBudget(prompt, isModify, out complex);
                _log.Info("轮次预算=" + budget + "（" + (isModify ? "修改" : "新建") + "，需求 " + Length(prompt) +
                          " 字，复杂需求=" + (complex ? "是" : "否") + "）");

                emit("step", "code", "running", isModify ? "按需求修改代码…" : "生成代码中…", null);
                var codegen = await CodegenAsync(runId, messages, tools, draftDir, files, meta, tokens, emit, ct,
                    budget, "code", isModify ? "修改代码中" : "生成代码中", !complex);

                if (!isModify && files.Count == 0)
                    throw new RpcException(ErrorCodes.Upstream,
                        "模型在 " + budget + " 轮内没有产出任何文件。可以试试：① 把需求拆成更小的应用；" +
                        "② 在设置里换成支持 function calling 的模型（如 gpt-4o-mini）；③ 检查 Base URL / API Key 是否可用");

                entry = PickEntry(files) ?? entry;
                if (!files.Keys.Any(k => k.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
                    throw new RpcException(ErrorCodes.Upstream, "没有生成 .html 入口文件");

                if (isModify && codegen.Written == 0)
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

                if (codegen.BudgetExhausted)
                {
                    // 安全上限只防无限烧钱，绝不把“轮次到顶”当成完成。
                    // 模型没有明确收尾时直接拒绝保存，避免再出现“能打开但功能缺一半”的应用。
                    _log.Warn("达到安全轮次上限且模型未收尾：budget=" + codegen.RoundsUsed +
                              "，文件=" + files.Count + "，拒绝保存");
                    throw new RpcException(ErrorCodes.Upstream,
                        "模型在 " + codegen.RoundsUsed + " 轮内仍未明确完成本次生成（没有调用 done），" +
                        "本次未保存半成品。请点「继续修改」分步补功能，或把需求拆小后重试；" +
                        "也可以在设置里换用更稳定的工具调用模型。");
                }
                else
                {
                    emit("step", "code", "ok",
                        (isModify ? "已修改 " : "已生成 ") + files.Count + " 个文件（" + TextUtil.Ellipsis(string.Join("、", files.Keys), 80) + "）", null);
                }

                // 自测 + 自动修复（隐藏窗口复用：整个闭环只建一次 WebView2）
                SelfTestResult test = null;
                SelfTestSession session = null;
                var degraded = false;
                var realFailureSeen = false;   // 已经确认过"代码真有问题"（不是环境问题）
                var envRetried = false;        // 环境重试只做一次，且不占用修复次数
                try
                {
                    for (var attempt = 0; attempt < MaxRepairAttempts; attempt++)
                    {
                        emit("step", "selftest", "running", attempt == 0 ? "加载页面并检查…" : "重新自测…", null);

                        test = null;
                        try
                        {
                            session = await _tester.EnsureSessionAsync(_environment, Hosts.ForDraft(runId), draftDir);
                            test = await session.TestAsync(entry, SelfTestTimeoutMs);
                        }
                        catch (Exception ex)
                        {
                            // 会话/轮询本身炸了：按环境问题处理，不让异常直接终结整次生成
                            _log.Error("自测执行异常", ex);
                            test = new SelfTestResult
                            {
                                Failure = "自测异常：" + ex.Message,
                                EnvironmentFailure = AppSelfTester.IsEnvironmentException(ex)
                            };
                        }

                        if (test.Pass)
                        {
                            emit("step", "selftest", "ok", "自测通过（页面内容 " + test.BodyLength + " 字符，无 JS 错误）", null);
                            break;
                        }

                        // 环境问题（WebView2 起不来）：重试一次，仍不行就降级为静态校验——
                        // 模型重写代码修不好环境，进修复环只会白烧 1~2 分钟。
                        if (test.EnvironmentFailure)
                        {
                            if (!envRetried)
                            {
                                envRetried = true;
                                _log.Warn("自测环境不可用，重试一次：" + test.Failure);
                                emit("step", "selftest", "running", "自测运行环境不可用，重试…", null);
                                _tester.ReleaseSession();
                                attempt--;   // 环境重试不占用修复次数
                                continue;
                            }

                            if (realFailureSeen)
                            {
                                // 已知代码有问题 + 现在环境不可用：不许用静态校验给它发通行证
                                _log.Warn("环境不可用，且此前真实自测已确认代码有问题，不做静态降级");
                                test = new SelfTestResult
                                {
                                    Failure = "运行环境不可用（" + test.Failure + "），且此前真实自测已发现代码问题，无法确认修复结果"
                                };
                                break;
                            }

                            var staticResult = AppSelfTester.StaticCheck(draftDir, entry, files);
                            staticResult.EnvironmentFailure = true;
                            _log.Warn("自测环境不可用，降级为静态校验：pass=" + staticResult.Pass +
                                      "（" + (staticResult.Failure ?? string.Empty) + "）");
                            emit("step", "selftest", staticResult.Pass ? "ok" : "fail",
                                staticResult.Pass
                                    ? "自测降级：运行环境不可用，已改用静态校验（入口文件存在、含 data-app-root，未真跑页面）。建议重启后再打开应用确认"
                                    : "自测降级后仍未通过：" + staticResult.Failure,
                                null);
                            test = staticResult;
                            degraded = true;
                            break;
                        }

                        realFailureSeen = true;
                        emit("step", "selftest", "fail",
                            test.Failure + (string.IsNullOrEmpty(test.Errors) ? "" : "｜" + TextUtil.Ellipsis(test.Errors, 200)), null);

                        if (attempt == MaxRepairAttempts - 1) break;

                        emit("step", "repair", "running", "根据错误自动修复（第 " + (attempt + 1) + " 次）…", null);
                        messages.Add(Msg("user", Prompts.RepairPrompt(appId, ErrorDetails(test), FilesSummary(files))));
                        await CodegenAsync(runId, messages, tools, draftDir, files, meta, tokens, emit, ct,
                            MaxCodeRoundsPerRepair, "repair", "修复代码中", false);
                        emit("step", "repair", "ok", "修复完成，重新自测", null);

                        entry = PickEntry(files) ?? entry;
                    }
                }
                finally
                {
                    _tester.ReleaseSession();   // 本次生成结束/异常都释放隐藏窗口
                }

                if (test == null || !test.Pass)
                {
                    var detail = test == null ? "未执行自测" : (test.Failure + "\n" + ErrorDetails(test));
                    var prefix = test != null && test.StaticCheck
                        ? "生成失败：运行环境不可用，静态校验也没通过。\n"
                        : "生成失败：应用未通过自测。\n";
                    throw new RpcException(ErrorCodes.Upstream, prefix + TextUtil.Ellipsis(detail, 600));
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

                var noteSuffix = string.Empty;
                if (degraded) noteSuffix += "（自测环境不可用，已静态校验）";

                _versions.WriteMeta(versionDir, new VersionMeta
                {
                    Seq = seq,
                    Kind = isModify ? "modify" : "create",
                    Note = (isModify ? "AI 修改：" : "AI 生成：") + TextUtil.Ellipsis(prompt, 60) + noteSuffix,
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
                if (degraded) doneExtra["degraded"] = true;
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

        private async Task<CodegenResult> CodegenAsync(
            string runId,
            IList<object> messages,
            IList<object> tools,
            string draftDir,
            IDictionary<string, string> files,
            AppMeta meta,
            TokenCounter tokens,
            Action<string, string, string, string, IDictionary<string, object>> emit,
            CancellationToken ct,
            int maxRounds,
            string stepKey,
            string phaseLabel,
            bool allowEarlyFinish)
        {
            var result = new CodegenResult();
            var idleRounds = 0;      // 连续"没有内容变化"的轮数（只读、无效写入或原样重写都算）
            var scriptNudged = false; // 是否已提示过"还没有任何脚本"
            var missingRefNudged = 0; // 已提示过几次"入口引用了还没写的文件"（防止刷屏）
            var doneRejected = 0;     // 模型过早调用 done、但入口还不完整时，要求继续补齐
            var extensionCount = 0;   // 基础预算用完后自动续轮的次数
            var auditRequested = false; // 模型表示结束/文件已稳定后，先做一次需求完整性核对

            for (var round = 0; round < maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                var roundNo = round + 1;
                var auditJustRequested = false; // 本轮刚发出核对请求，不能在同一轮直接收尾

                emit("step", stepKey, "running", phaseLabel + "…（第 " + roundNo + "/" + maxRounds + " 轮）", null);

                var roundWatch = Stopwatch.StartNew();
                var deltaThrottle = new DeltaThrottle(150);
                ChatTurn turn;
                try
                {
                    turn = await _chat.CompleteStreamingAsync(messages, tools, delegate(ChatStreamProgress progress)
                    {
                        EmitDelta(emit, progress, deltaThrottle);
                    }, ct);
                }
                catch (Exception)
                {
                    roundWatch.Stop();
                    throw;
                }
                roundWatch.Stop();
                result.RoundsUsed = roundNo;

                tokens.In += turn.PromptTokens;
                tokens.Out += turn.CompletionTokens;

                var truncated = string.Equals(turn.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
                var assistant = AssistantMsg(turn);
                messages.Add(assistant);

                _log.Info("第 " + roundNo + " 轮：耗时 " + roundWatch.ElapsedMilliseconds + "ms，toolCalls=" + turn.ToolCalls.Count +
                          "，tokens in/out=" + turn.PromptTokens + "/" + turn.CompletionTokens +
                          (truncated ? "，finish_reason=length（输出被截断）" : ""));

                if (turn.ToolCalls.Count == 0)
                {
                    if (files.Count > 0)
                    {
                        var textEntry = PickEntry(files) ?? "index.html";
                        var textSelfContained = EntrySelfContained(files, textEntry);
                        var textSaysIncomplete = TextSaysIncomplete(turn.Content);

                        if (textSelfContained && !textSaysIncomplete)
                        {
                            if (!auditRequested)
                            {
                                auditRequested = true;
                                auditJustRequested = true;
                                messages.Add(Msg("user", Prompts.CompletionAuditPrompt(textEntry)));
                                emit("log", null, null, "模型用文字表示结束，先要求逐条核对需求再收尾", null);
                                _log.Info("第 " + roundNo + " 轮：模型未调用工具，先触发完成前需求核对");
                            }
                            else
                            {
                                result.Done = true;
                                emit("log", null, null, "入口完整、需求核对后模型确认完成，收尾", null);
                                _log.Info("第 " + roundNo + " 轮：完成前核对通过，代码生成结束");
                                break;
                            }
                        }
                        else
                        {
                            idleRounds++;
                            messages.Add(Msg("user", Prompts.ContinueIncompletePrompt(textEntry, MissingLocalRefs(files, textEntry))));
                            emit("log", null, null, "模型只回复了文字，但应用仍未完成，已要求继续补齐", null);
                            _log.Info("第 " + roundNo + " 轮：模型未调用工具且应用未完成，已要求继续");
                        }
                    }
                    else
                    {
                        messages.Add(Msg("user", "请使用工具完成工作：先 set_app_info，再 write_file 写完整文件，最后调用 done。"));
                    }
                    emit("log", null, null, "第 " + roundNo + " 轮完成（" + Seconds(roundWatch) + "s）", null);
                    continue;
                }

                var needSegmentHint = false;
                var suggestedPath = string.Empty;
                var contentChanged = false;   // 本轮写进去的内容是否与写之前不同（"真的往前走了"的唯一判据）

                foreach (var call in turn.ToolCalls)
                {
                    var name = (call.Name ?? string.Empty).Trim();
                    var isWrite = string.Equals(name, "write_file", StringComparison.OrdinalIgnoreCase);
                    string output;

                    // 只有"本次响应被截断 + 参数不是合法 JSON"才说明这一次写入不可用。
                    // 同一轮里先写完整、后写被截断时，前面那份合法写入必须照常落盘（否则白丢）。
                    if (truncated && isWrite && !IsValidArguments(call.Arguments))
                    {
                        // 截断轮的 write_file 参数不完整：不落盘，直接引导分段写法（避免反复整篇重写）
                        var path = TryExtractPath(call.Arguments);
                        output = "失败：本次响应被输出长度截断（finish_reason=length），" +
                                 (string.IsNullOrEmpty(path) ? "文件" : path) + " 的内容不完整，未写入。" +
                                 "请把大文件拆成多个文件（每个 ≤" + SingleWriteLineHint + " 行），或先写骨架再用 append:true 分段追加。";
                        needSegmentHint = true;
                        if (string.IsNullOrEmpty(suggestedPath)) suggestedPath = path;
                        SetToolArguments(assistant, call.Id, SlimFailedArgs(path, "本次响应被截断，内容不完整，未写入"));
                        _log.Warn("第 " + roundNo + " 轮输出被截断：path=" + (string.IsNullOrEmpty(path) ? "(未知)" : path) + "，已跳过写入");
                        emit("log", null, null, "响应被输出长度截断，已提示模型改用分段写入", null);
                    }
                    else if (truncated && !IsValidArguments(call.Arguments))
                    {
                        output = "失败：本次响应被输出长度截断，工具参数不完整，未执行。请重新调用该工具。";
                        SetToolArguments(assistant, call.Id, "{}");
                        _log.Warn("第 " + roundNo + " 轮输出被截断：工具 " + name + " 参数不完整，已跳过执行");
                    }
                    else
                    {
                        var writePath = isWrite ? NormalizePath(TryGetString(call.Arguments, "path")) : string.Empty;
                        var existedBefore = isWrite && !string.IsNullOrEmpty(writePath) && files.ContainsKey(writePath);

                        // 写之前先留一份旧内容：写完逐字节比对，才能区分"真改了"和"又把同一份内容重写了一遍"。
                        string contentBefore = null;
                        if (existedBefore) files.TryGetValue(writePath, out contentBefore);

                        var outcome = ExecuteTool(call, draftDir, files, meta, emit);
                        output = outcome.Text;

                        if (outcome.WroteFile)
                        {
                            result.Written++;
                            string contentAfter = null;
                            files.TryGetValue(string.IsNullOrEmpty(outcome.Path) ? writePath : outcome.Path, out contentAfter);
                            if (!string.Equals(contentBefore, contentAfter, StringComparison.Ordinal)) contentChanged = true;

                            // 上下文瘦身：历史里不再保留整份文件内容，避免后面每轮输入 token 线性膨胀
                            SetToolArguments(assistant, call.Id, SlimWriteArgs(outcome));
                        }
                        else if (isWrite)
                        {
                            SetToolArguments(assistant, call.Id, SlimFailedArgs(outcome.Path, TextUtil.Ellipsis(outcome.Text, 120)));
                        }
                    }

                    messages.Add(ToolMsg(call.Id, output));
                    if (string.Equals(name, "done", StringComparison.OrdinalIgnoreCase)) result.Done = true;
                }

                if (contentChanged) auditRequested = false;

                if (needSegmentHint)
                {
                    messages.Add(Msg("user", Prompts.SegmentWritePrompt(suggestedPath, SingleWriteLineHint)));
                }

                var entryName = PickEntry(files) ?? "index.html";
                var roundTextIncomplete = TextSaysIncomplete(turn.Content);

                // 模型自己还在文字里说“还缺 / 未实现”，即使顺手调用了 done 也不能信。
                if (result.Done && roundTextIncomplete)
                {
                    result.Done = false;
                    doneRejected++;
                    messages.Add(Msg("user", Prompts.ContinueIncompletePrompt(
                        entryName, MissingLocalRefs(files, entryName))));
                    emit("log", null, null, "模型调用了 done，但说明里仍有未完成项，已拒绝收尾并要求继续", null);
                    _log.Info("第 " + roundNo + " 轮：done 被拒绝（第 " + doneRejected + " 次），模型文字仍表示未完成");
                }

                // done 只是"申请收尾"，不是免检通行证：入口缺脚本/缺被引用文件时拒绝收尾。
                if (result.Done && !EntrySelfContained(files, entryName))
                {
                    result.Done = false;
                    doneRejected++;
                    var missingForDone = MissingLocalRefs(files, entryName);
                    messages.Add(Msg("user", Prompts.DoneRejectedPrompt(entryName, missingForDone)));
                    emit("log", null, null, "模型调用了 done，但入口仍不完整，已拒绝收尾并要求继续", null);
                    _log.Info("第 " + roundNo + " 轮：done 被拒绝（第 " + doneRejected + " 次），入口不完整");
                }

                // done 不是免检通行证：哪怕入口结构完整，也要先让模型逐条核对需求，
                // 避免出现"manifest 宣称 7 个工具、实际只实现 4 个"这种能打开但不能用的半成品。
                if (result.Done && EntrySelfContained(files, entryName) && !auditRequested)
                {
                    result.Done = false;
                    auditRequested = true;
                    auditJustRequested = true;
                    messages.Add(Msg("user", Prompts.CompletionAuditPrompt(entryName)));
                    emit("log", null, null, "模型调用了 done，收尾前先逐条核对需求是否全部实现", null);
                    _log.Info("第 " + roundNo + " 轮：done 前触发完成前需求核对");
                }

                // 入口引用了还没写出来的 js/css：应用一定跑不起来。
                // 这属于"模型还在补文件"，绝不能当成原地打转提前收尾，先明确催一次补齐。
                if (!result.Done && files.Count > 0)
                {
                    var missingRefs = MissingLocalRefs(files, entryName);
                    if (missingRefs.Count > 0 && missingRefNudged < 2)
                    {
                        missingRefNudged++;
                        messages.Add(Msg("user", Prompts.MissingFilesPrompt(entryName, missingRefs)));
                        emit("log", null, null, "入口引用了还没写的文件：" + string.Join("、", missingRefs) + "，已提示模型补齐", null);
                        _log.Info("第 " + roundNo + " 轮：入口缺被引用文件 " + string.Join("、", missingRefs) + "，已提示补齐");
                    }
                }

                // 只有静态界面、没有交互逻辑时不收尾：直接点明"还缺 JS"，让模型补完（只提示一次）
                if (!result.Done && !scriptNudged && files.Count > 0 && !HasAnyScript(files))
                {
                    scriptNudged = true;
                    messages.Add(Msg("user",
                        "注意：目前已写入的文件里还没有任何脚本（<script>）。这个应用需要可交互并保存数据，" +
                        "请把交互逻辑补上（可以写进 index.html 的 <script>，或写成 app.js 并在 index.html 里用 " +
                        "<script src=\"app.js\"></script> 引用），补齐后调用 done。"));
                    emit("log", null, null, "检测到还没有交互脚本，已提示模型补齐 JS", null);
                }

                emit("log", null, null, "第 " + roundNo + " 轮完成（" + Seconds(roundWatch) + "s）", null);
                if (result.Done) break;

                // 收尾判断只看"内容有没有真的变化"：
                //   · 有任何变化（哪怕还是重写同一个文件）都算在推进，必须让模型继续；
                //   · 只读、无效写入、原样重写都计入 idle；
                //   · 入口还引用着没写出来的文件时永不收尾（那是"正在补文件"，不是打转）。
                // 不再保留"连续两轮重复写就停"的独立分支：它会在复杂应用补功能的阶段误伤，
                // 而且与下面的 MaxIdleRounds 观察窗口重复。
                idleRounds = contentChanged ? 0 : idleRounds + 1;

                // 入口完整且连续无变化后，也不能立刻收尾：先让模型逐条核对用户需求。
                // 简单需求观察 1 轮，复杂需求观察 MaxIdleRounds 轮；只有核对过才允许结束。
                var stableComplete = !roundTextIncomplete &&
                                     EntrySelfContained(files, entryName) &&
                                     ((allowEarlyFinish && idleRounds >= 1) ||
                                      (!allowEarlyFinish && idleRounds >= MaxIdleRounds));
                if (!result.Done && !auditJustRequested && stableComplete)
                {
                    if (!auditRequested)
                    {
                        auditRequested = true;
                        messages.Add(Msg("user", Prompts.CompletionAuditPrompt(entryName)));
                        emit("log", null, null, "入口已完整、文件已稳定，先核对一次需求完整性再收尾", null);
                        _log.Info("第 " + roundNo + " 轮：文件稳定，触发完成前需求核对");
                    }
                    else
                    {
                        result.Done = true;
                        result.EarlyFinish = true;
                        emit("log", null, null, "入口完整、需求核对通过，收尾", null);
                        _log.Info("第 " + roundNo + " 轮：完成前核对通过，提前结束代码生成");
                        break;
                    }
                }

                // 基础预算到时还不完整：自动续轮，绝不再用"最后一轮，赶紧精简收尾"逼模型截断功能。
                if (round + 1 >= maxRounds && files.Count > 0 && maxRounds < MaxCodeRoundsCap)
                {
                    var previousCap = maxRounds;
                    maxRounds = Math.Min(MaxCodeRoundsCap, maxRounds + RoundExtensionSize);
                    extensionCount++;
                    messages.Add(Msg("user", Prompts.ExtendBudgetPrompt(
                        extensionCount, maxRounds - round - 1, entryName, MissingLocalRefs(files, entryName))));
                    emit("log", null, null,
                        "应用尚未完成，轮次预算由 " + previousCap + " 自动延长到 " + maxRounds +
                        "（不简化、不强收尾）", null);
                    _log.Info("第 " + roundNo + " 轮：入口尚未完成，轮次预算 " + previousCap + " → " + maxRounds);
                }
            }

            if (!result.Done && files.Count > 0)
            {
                result.BudgetExhausted = true;
                var finalEntry = PickEntry(files) ?? "index.html";
                result.EntryComplete = EntrySelfContained(files, finalEntry);
                emit("log", null, null,
                    "达到最长轮次（" + maxRounds + "），" +
                    (result.EntryComplete ? "入口结构完整，进入自测" : "入口仍不完整，将拒绝保存半成品"), null);
            }

            return result;
        }

        /// <summary>把流式进度转成 gen.event（kind="delta"），节流 ≥150ms。</summary>
        private void EmitDelta(
            Action<string, string, string, string, IDictionary<string, object>> emit,
            ChatStreamProgress progress,
            DeltaThrottle throttle)
        {
            if (progress == null || !throttle.ShouldEmit()) return;

            var extra = JsonLite.New();
            extra["phase"] = string.IsNullOrEmpty(progress.Phase) ? "content" : progress.Phase;
            extra["chars"] = progress.Chars;
            extra["tool"] = progress.Tool ?? string.Empty;
            extra["text"] = TextUtil.Ellipsis(progress.Text, 200);
            emit("delta", null, null, null, extra);
        }

        private static string Seconds(Stopwatch watch)
        {
            return (watch.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static int Length(string text)
        {
            return string.IsNullOrEmpty(text) ? 0 : text.Length;
        }

        /// <summary>
        /// 轮次预算：新建 24 轮 / 修改 16 轮；需求超过 150 字或含复杂关键词再加 16 轮。
        /// 未完成时每档自动续 16 轮；160 轮只是防无限烧钱的安全上限，不是完成判据。
        /// </summary>
        internal static int ComputeRoundBudget(string prompt, bool isModify, out bool complex)
        {
            var budget = isModify ? ModifyBaseRounds : BaseCodeRounds;
            var text = prompt ?? string.Empty;

            complex = text.Length > LongPromptChars;
            if (!complex)
            {
                foreach (var keyword in ComplexKeywords)
                {
                    if (text.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                    {
                        complex = true;
                        break;
                    }
                }
            }

            if (complex) budget += ComplexExtraRounds;
            if (budget > MaxCodeRoundsCap) budget = MaxCodeRoundsCap;
            return budget;
        }

        private sealed class CodegenResult
        {
            public int Written;
            public bool Done;
            public int RoundsUsed;
            public bool BudgetExhausted;
            /// <summary>达到硬上限时入口是否完整；不完整时上层必须拒绝保存。</summary>
            public bool EntryComplete;
            /// <summary>入口文件已完整且自包含，主动提前收尾（省掉一次多余的整篇重写）。</summary>
            public bool EarlyFinish;
        }

        /// <summary>≥intervalMs 才允许放行一次（Environment.TickCount 差值，含回绕处理）。</summary>
        private sealed class DeltaThrottle
        {
            private readonly int _intervalMs;
            private int _lastTick;
            private bool _emitted;

            public DeltaThrottle(int intervalMs)
            {
                _intervalMs = intervalMs;
            }

            public bool ShouldEmit()
            {
                var now = Environment.TickCount;
                if (_emitted && unchecked(now - _lastTick) < _intervalMs) return false;
                _emitted = true;
                _lastTick = now;
                return true;
            }
        }

        // ---------------- 工具执行 ----------------

        private ToolOutcome ExecuteTool(
            ToolCall call,
            string draftDir,
            IDictionary<string, string> files,
            AppMeta meta,
            Action<string, string, string, string, IDictionary<string, object>> emit)
        {
            var name = (call.Name ?? string.Empty).Trim();
            try
            {
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
                        return Reply("已设置：" + meta.Name);
                    }

                    case "write_file":
                    {
                        var path = NormalizePath(JsonLite.Str(args, "path"));
                        var content = JsonLite.Str(args, "content") ?? string.Empty;
                        var append = JsonLite.Bool(args, "append");
                        if (path.Length == 0) return FailOutcome(string.Empty, "path 不能为空");
                        if (content.Length > MaxToolFileChars) return FailOutcome(path, "单文件过大（>" + MaxToolFileChars + " 字符）");

                        var ext = Path.GetExtension(path);
                        if (!AllowedExtensions.Contains(ext)) return FailOutcome(path, "不支持的文件类型 " + ext);

                        var full = PathGuard.ResolveWithin(draftDir, path);
                        var dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        // append=true：追加到已有内容末尾（内存里的最新内容优先，其次磁盘）；文件不存在等同新建
                        var total = content;
                        var appended = false;
                        if (append)
                        {
                            string existing = null;
                            if (files.TryGetValue(path, out existing) && existing != null) appended = true;
                            else if (File.Exists(full))
                            {
                                existing = File.ReadAllText(full);
                                appended = true;
                            }

                            if (appended && existing != null)
                            {
                                if (existing.Length + content.Length > MaxToolFileChars)
                                    return FailOutcome(path, "追加后超过单文件上限（" + MaxToolFileChars + " 字符），请拆成多个文件");
                                total = existing + content;
                            }
                        }

                        File.WriteAllText(full, total, new UTF8Encoding(false));
                        files[path] = total;

                        emit("log", null, null,
                            (appended ? "追加 " : "写入 ") + path + "（" + content.Length + " 字符，累计 " + total.Length + " 字符）", null);

                        return new ToolOutcome
                        {
                            Text = "已写入 " + path + "（" + (appended ? "追加" : "本段") + " " + FormatChars(content.Length) +
                                   "，累计 " + FormatChars(total.Length) + "）",
                            WroteFile = true,
                            Path = path,
                            AddedChars = content.Length,
                            TotalChars = total.Length,
                            Append = appended
                        };
                    }

                    case "read_file":
                    {
                        var path = NormalizePath(JsonLite.Str(args, "path"));
                        string content = null;
                        if (!files.TryGetValue(path, out content) || content == null)
                        {
                            var full = PathGuard.ResolveWithin(draftDir, path);
                            if (!File.Exists(full)) return FailOutcome(path, "文件不存在");
                            content = File.ReadAllText(full);
                        }
                        // 上下文瘦身：回给模型的内容封顶，避免一次读取把历史撑爆
                        return Reply(LimitContent(content, ReadFileResultLimit));
                    }

                    case "list_files":
                        return Reply(files.Count == 0 ? "（空）" : string.Join("\n", files.Keys));

                    case "done":
                        return Reply("完成");

                    default:
                        return FailOutcome(string.Empty, "未知工具 " + name);
                }
            }
            catch (PathGuardException ex)
            {
                _log.Warn("工具路径越界 " + name + "：" + ex.Message);
                return FailOutcome(string.Empty, "路径越界（" + ex.Message + "）");
            }
            catch (Exception ex)
            {
                _log.Warn("工具执行异常 " + name + "：" + ex.Message);
                return FailOutcome(string.Empty, ex.Message);
            }
        }

        private static ToolOutcome Reply(string text)
        {
            return new ToolOutcome { Text = text };
        }

        private static ToolOutcome FailOutcome(string path, string message)
        {
            return new ToolOutcome { Text = "失败：" + message, Path = path };
        }

        /// <summary>按字符上限截断（保留换行，并在末尾注明已截断）。</summary>
        private static string LimitContent(string content, int limit)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            if (content.Length <= limit) return content;
            return content.Substring(0, limit) +
                   "\n（已截断，仅显示前 " + limit + " 字符，共 " + content.Length + " 字符；需要完整重写时请直接 write_file 覆盖）";
        }

        private static string FormatChars(int chars)
        {
            if (chars < 1024) return chars + " 字符";
            return (chars / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "KB";
        }

        /// <summary>工具执行结果：文本回给模型，同时记录是否真的落盘（用于上下文瘦身）。</summary>
        private sealed class ToolOutcome
        {
            public string Text;
            public string Path;
            public bool WroteFile;
            public bool Append;
            public int AddedChars;
            public int TotalChars;
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

        /// <summary>从 tool_call 的 arguments 里取一个字符串字段（参数非法时返回空串，不抛）。</summary>
        private static string TryGetString(string arguments, string key)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return string.Empty;
            try
            {
                var value = JsonLite.Str(ParseArgs(arguments), key);
                return value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>草稿里是否已经有任何脚本（内联 &lt;script&gt; 或 .js 文件）。</summary>
        private static bool HasAnyScript(IDictionary<string, string> files)
        {
            if (files == null) return false;
            foreach (var kv in files)
            {
                if ((kv.Key ?? string.Empty).EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return true;
                var content = kv.Value ?? string.Empty;
                if (content.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 模型在文字里明确说“还缺/未完成”时，不能把这段文字当成 done。
        /// 只匹配明确的未完成措辞，避免把“已完成，没有缺少”误判成未完成。
        /// </summary>
        private static bool TextSaysIncomplete(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var markers = new[]
            {
                "未完成", "尚未完成", "还没完成", "仍未完成",
                "还缺", "仍缺", "尚未实现", "未实现", "待实现", "待补充",
                "没有实现", "缺少功能", "占位实现", "TODO:", "FIXME",
                "not implemented", "still missing", "pending implementation"
            };
            foreach (var marker in markers)
            {
                var index = 0;
                while ((index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    // “没有未完成的功能”“不存在未实现项”这类否定句不算未完成；
                    // 只看标记词前面一小段，避免把整句话里的“没有”错误地套到别的功能上。
                    var prefixStart = Math.Max(0, index - 8);
                    var prefix = text.Substring(prefixStart, index - prefixStart);
                    var negated = prefix.IndexOf("没有", StringComparison.Ordinal) >= 0 ||
                                  prefix.IndexOf("并无", StringComparison.Ordinal) >= 0 ||
                                  prefix.IndexOf("不存在", StringComparison.Ordinal) >= 0 ||
                                  prefix.IndexOf("未发现", StringComparison.Ordinal) >= 0 ||
                                  prefix.IndexOf("不缺少", StringComparison.Ordinal) >= 0 ||
                                  prefix.EndsWith("无", StringComparison.Ordinal);
                    if (!negated) return true;
                    index += marker.Length;
                }
            }
            return false;
        }

        /// <summary>
        /// 入口文件是否"完整且自包含"：完整（含脚本）+ 它引用的本地 js/css 文件都已经写好。
        /// 用来在模型永远不会主动调 done 的情况下提前收尾，又不至于把多文件应用截成半成品。
        /// </summary>
        internal static bool EntrySelfContained(IDictionary<string, string> files, string entry)
        {
            if (!EntryLooksComplete(files, entry)) return false;
            return MissingLocalRefs(files, entry).Count == 0;
        }

        /// <summary>
        /// 入口文件里引用了、但当前还没写出来的本地 js/css。
        /// 非空说明应用现在打开会缺资源（白屏 / 交互失效），此时绝不能提前收尾。
        /// </summary>
        private static List<string> MissingLocalRefs(IDictionary<string, string> files, string entry)
        {
            var missing = new List<string>();
            if (files == null || files.Count == 0) return missing;

            var path = string.IsNullOrEmpty(entry) ? "index.html" : entry;
            string content;
            if (!files.TryGetValue(path, out content) || string.IsNullOrEmpty(content)) return missing;

            foreach (var reference in ExtractLocalReferences(content))
            {
                if (!files.ContainsKey(reference) && !missing.Contains(reference)) missing.Add(reference);
            }
            return missing;
        }

        /// <summary>抽出 HTML 里引用的本地资源路径（相对路径；忽略 http(s)/data:/# 等）。</summary>
        private static IEnumerable<string> ExtractLocalReferences(string html)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(html)) return result;

            var pattern = new Regex("(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            foreach (Match match in pattern.Matches(html))
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length == 0) continue;
                if (value.StartsWith("#", StringComparison.Ordinal)) continue;
                if (value.IndexOf("//", StringComparison.Ordinal) == 0) continue;
                if (value.IndexOf(":", StringComparison.Ordinal) > 0 &&
                    value.IndexOf(":", StringComparison.Ordinal) < 8) continue;   // http: data: mailto: 等
                if (value.StartsWith("/", StringComparison.Ordinal))
                {
                    // 虚拟主机下的绝对路径同样映射到草稿根目录
                    value = value.TrimStart('/');
                }
                if (!value.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                    !value.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(NormalizePath(value));
            }
            return result;
        }

        /// <summary>
        /// 入口文件是否已经"真的写完"：闭合的 HTML + data-app-root + 至少一段脚本。
        /// 只靠闭合标签判断会把"只有静态骨架、还没写交互逻辑"的版本误判成完成（实测踩过）。
        /// </summary>
        private static bool EntryLooksComplete(IDictionary<string, string> files, string entry)
        {
            if (files == null || files.Count == 0) return false;
            var path = string.IsNullOrEmpty(entry) ? "index.html" : entry;
            string content;
            if (!files.TryGetValue(path, out content) || string.IsNullOrEmpty(content)) return false;

            if (content.IndexOf("</html>", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
            {
                if (content.IndexOf("data-app-root", StringComparison.OrdinalIgnoreCase) < 0) return false;
                // 交互逻辑：内联 <script>、引用的 .js 文件、或内联事件属性，至少要有一个
                if (content.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0 &&
                    content.IndexOf("onclick=", StringComparison.OrdinalIgnoreCase) < 0 &&
                    content.IndexOf("onchange=", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        /// <summary>判断 tool_call 的 arguments 是否是合法 JSON 对象（截断轮的参数往往不合法）。</summary>
        private static bool IsValidArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return false;
            try
            {
                JsonLite.ParseObject(arguments);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 上下文瘦身：把已落盘的 write_file 参数换成摘要（tool_call id 与顺序保持不变，
        /// 满足 OpenAI 兼容要求；后续轮次输入 token 不再随文件内容线性膨胀）。
        /// </summary>
        private static string SlimWriteArgs(ToolOutcome outcome)
        {
            var d = JsonLite.New();
            d["path"] = outcome.Path;
            d["content"] = "<此文件已成功写入磁盘（共 " + outcome.TotalChars + " 字符），历史里不再重复其内容；不要重写它>";
            if (outcome.Append) d["append"] = true;
            return JsonLite.Serialize(d);
        }

        private static string SlimFailedArgs(string path, string reason)
        {
            var d = JsonLite.New();
            d["path"] = path ?? string.Empty;
            d["content"] = "<未写入：" + TextUtil.Ellipsis(reason, 160) + ">";
            return JsonLite.Serialize(d);
        }

        /// <summary>改写历史里某条 assistant tool_call 的 arguments（找不到就静默跳过）。</summary>
        private static void SetToolArguments(IDictionary<string, object> assistantMessage, string toolCallId, string arguments)
        {
            var calls = JsonLite.List(assistantMessage, "tool_calls");
            if (calls == null) return;

            foreach (var item in calls)
            {
                var call = item as IDictionary<string, object>;
                if (call == null) continue;
                var id = JsonLite.Str(call, "id");
                if (!string.Equals(id, toolCallId, StringComparison.Ordinal)) continue;
                var function = JsonLite.Obj(call, "function");
                if (function == null) return;
                function["arguments"] = arguments;
                return;
            }
        }

        /// <summary>从可能被截断的 JSON 参数里尽力取出 path（截断后 JSON 通常无法解析，走文本兜底）。</summary>
        private string TryExtractPath(string rawArguments)
        {
            if (string.IsNullOrEmpty(rawArguments)) return string.Empty;

            try
            {
                var path = JsonLite.Str(JsonLite.ParseObject(rawArguments), "path");
                if (!string.IsNullOrEmpty(path)) return NormalizePath(path);
            }
            catch (Exception ex)
            {
                _log.Warn("截断参数不是合法 JSON，改用文本兜底提取 path：" + ex.GetType().Name);
            }

            const string key = "\"path\"";
            var at = rawArguments.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return string.Empty;
            var colon = rawArguments.IndexOf(':', at + key.Length);
            if (colon < 0) return string.Empty;
            var start = rawArguments.IndexOf('"', colon + 1);
            if (start < 0) return string.Empty;

            var sb = new StringBuilder();
            for (var i = start + 1; i < rawArguments.Length; i++)
            {
                var ch = rawArguments[i];
                if (ch == '\\' && i + 1 < rawArguments.Length)
                {
                    sb.Append(rawArguments[i + 1]);
                    i++;
                    continue;
                }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return NormalizePath(sb.ToString());
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
