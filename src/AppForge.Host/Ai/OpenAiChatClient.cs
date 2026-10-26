using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppForge.Shared;
using AppForge.Host.Diagnostics;

namespace AppForge.Host.Ai
{
    public sealed class ToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Arguments { get; set; }
    }

    public sealed class ChatTurn
    {
        public string Content { get; set; }
        public List<ToolCall> ToolCalls { get; set; }
        public string FinishReason { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }

        public ChatTurn()
        {
            ToolCalls = new List<ToolCall>();
        }
    }

    /// <summary>流式进度回调载荷（生成引擎转成 gen.event kind="delta"）。</summary>
    public sealed class ChatStreamProgress
    {
        /// <summary>"content"（正文）或 "tool"（工具参数）。</summary>
        public string Phase { get; set; }
        /// <summary>本轮已累计的字符数（正文 + 工具参数）。</summary>
        public long Chars { get; set; }
        /// <summary>当前正在生成的工具名（未知时为空串）。</summary>
        public string Tool { get; set; }
        /// <summary>最近一段增量文本（已截断，供界面滚动显示）。</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// 带 function calling 的对话客户端。
    /// 默认走流式：网关在「约 60s 没有任何字节」时会返回 504，非流式写大文件必然被掐断，
    /// 流式能持续收到字节、边生成边把进度推给界面。
    /// 流式不可用（上游不支持 SSE / SSE 解析失败）时自动回退一次非流式；
    /// 504/502/503/429 等上游瞬态错误各重试 1 次（退避 1.5s），仍失败才抛错。
    /// </summary>
    public sealed class OpenAiChatClient
    {
        private static readonly HttpClient Shared = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>进度回调最小间隔（避免刷爆 UI）。</summary>
        private const int ProgressIntervalMs = 150;
        /// <summary>进度里"最近片段"的最大长度。</summary>
        private const int ProgressTextLimit = 200;
        /// <summary>流式读超时：连续这么久没有任何数据行就判定连接假死（略高于网关 60s 超时）。</summary>
        private const int StallTimeoutMs = 75000;
        /// <summary>上游瞬态错误（504/502/503/429…）的重试次数。</summary>
        private const int MaxUpstreamRetries = 1;
        /// <summary>重试前的退避时间。</summary>
        private const int UpstreamRetryDelayMs = 1500;
        /// <summary>默认最大输出 tokens（推理模型一轮写一个大文件经常超过 8192）。</summary>
        private const int MaxTokensCap = 16000;
        /// <summary>上游不接受大 max_tokens 时回退到的旧默认值。</summary>
        private const int LegacyMaxTokensCap = 8192;

        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly Log _log;

        /// <summary>本进程内实际使用的 max_tokens 上限（被上游 400 拒绝后降到 8192）。</summary>
        private int _maxTokensCap = MaxTokensCap;

        /// <summary>
        /// stream_options 支持情况：0=默认不发送（本网关流式响应里自带 usage，实测无需它），
        /// 1=上游明确可用，-1=上游拒绝过（不再尝试）。
        /// </summary>
        private int _streamUsageSupport;

        public OpenAiChatClient(SettingsStore settings, SecretsStore secrets, Log log)
        {
            _settings = settings;
            _secrets = secrets;
            _log = log;
        }

        // ---------------- 非流式（含 504 重试） ----------------

        public async Task<ChatTurn> CompleteAsync(IList<object> messages, IList<object> tools, CancellationToken ct)
        {
            var retried = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await NonStreamingOnceAsync(messages, tools, ct).ConfigureAwait(true);
                }
                catch (UpstreamRetryableException ex)
                {
                    if (retried >= MaxUpstreamRetries) throw TooManyUpstreamFailures(ex, retried);

                    retried++;
                    _log.Warn("非流式请求命中上游瞬态错误（" + ex.Message + "），" + UpstreamRetryDelayMs + "ms 后重试第 " + retried + " 次");
                    await Task.Delay(UpstreamRetryDelayMs, ct).ConfigureAwait(true);
                }
            }
        }

        private async Task<ChatTurn> NonStreamingOnceAsync(IList<object> messages, IList<object> tools, CancellationToken ct)
        {
            var apiKey = RequireApiKey();
            var payload = BuildPayload(messages, tools, false);
            var url = OpenAiClient.NormalizeBaseUrl(_settings.Current.BaseUrl) + "/chat/completions";
            var body = JsonLite.Serialize(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await Shared.SendAsync(request, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    throw new UpstreamRetryableException("网络错误：" + ex.Message);
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (!response.IsSuccessStatusCode)
                    {
                        var status = (int)response.StatusCode;
                        _log.Warn("模型请求失败：" + status + " " + TextUtil.Ellipsis(text, 300));
                        if (IsRetryableStatus(status))
                            throw new UpstreamRetryableException("HTTP " + status + " " + DescribeStatus(status));
                        if (DowngradeMaxTokens(status, text))
                            throw new UpstreamRetryableException("上游不接受较大的 max_tokens，已降到 " + _maxTokensCap + " 重试");
                        // 404 在直连网关（frp/反代）里通常是"后端暂时不在"而不是配置错，重试一次代价很小
                        if (status == 404 || IsGatewayPage(text))
                            throw new UpstreamRetryableException("网关返回 HTTP " + status + "（" + GatewayHint(text) + "）");
                        throw new RpcException(ErrorCodes.Upstream,
                            "模型请求失败（HTTP " + status + "）：" + FriendlyBody(text, 300));
                    }

                    return ParseTurn(text);
                }
            }
        }

        // ---------------- 流式（重试 + 自动回退） ----------------

        /// <summary>
        /// 流式对话：累加 SSE 的 delta.content / delta.tool_calls 拼成 ChatTurn。
        /// - 上游瞬态错误（408/429/5xx，含网关 504）：重试 1 次；仍失败抛出可读错误（告知"已重试"）。
        /// - 流式专有问题（400/422 拒绝 stream_options、SSE 解析失败、没有任何 data）：降级/回退到非流式。
        /// </summary>
        public async Task<ChatTurn> CompleteStreamingAsync(
            IList<object> messages,
            IList<object> tools,
            Action<ChatStreamProgress> onProgress,
            CancellationToken ct)
        {
            var retried = 0;
            // 默认不发送 stream_options：实测本网关的流式响应末尾自带 usage；
            // 只有在确认上游需要时才发送（避免多一次被 400 拒绝的往返）。
            var includeUsage = _streamUsageSupport > 0;
            var downgraded = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var turn = await StreamingOnceAsync(messages, tools, includeUsage, onProgress, ct).ConfigureAwait(true);

                    // 上游流式没回 usage（统计不到 token）时，下一轮开始请求 stream_options；
                    // 若因此被 400 拒绝，StreamOptionRejectedException 分支会退回不带 stream_options 的请求。
                    if (_streamUsageSupport == 0 && !includeUsage && turn.PromptTokens == 0 && turn.CompletionTokens == 0)
                    {
                        _streamUsageSupport = 1;
                        _log.Info("流式响应未带 usage，后续请求将携带 stream_options 以统计 token");
                    }
                    return turn;
                }
                catch (UpstreamRetryableException ex)
                {
                    if (retried < MaxUpstreamRetries)
                    {
                        retried++;
                        _log.Warn("流式请求命中上游瞬态错误（" + ex.Message + "），" + UpstreamRetryDelayMs +
                                  "ms 后重试第 " + retried + " 次");
                        await Task.Delay(UpstreamRetryDelayMs, ct).ConfigureAwait(true);
                        continue;
                    }

                    // 重试后仍失败：不再用非流式重试（大请求同样会被网关掐断），直接给出可操作的错误
                    _log.Warn("流式请求重试 " + retried + " 次后仍失败：" + ex.Message);
                    throw TooManyUpstreamFailures(ex, retried);
                }
                catch (StreamOptionRejectedException ex)
                {
                    _streamUsageSupport = -1;
                    if (downgraded || includeUsage == false)
                    {
                        _log.Warn(ex.Message + "；降级后仍被拒绝，回退为非流式请求");
                        return await FallbackToNonStreamingAsync(messages, tools, ct, "流式参数不被上游接受").ConfigureAwait(true);
                    }

                    _log.Warn(ex.Message + "；改为不带 stream_options 的流式请求重试");
                    includeUsage = false;
                    downgraded = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RpcException ex)
                {
                    // 配置类错误（缺 Key 等）重试没有意义，直接抛出
                    if (string.Equals(ex.Code, ErrorCodes.BadRequest, StringComparison.Ordinal)) throw;
                    _log.Warn("流式请求失败（" + ex.Message + "），回退为非流式请求");
                    return await FallbackToNonStreamingAsync(messages, tools, ct, ex.Message).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _log.Warn("流式请求失败（" + ex.GetType().Name + "：" + ex.Message + "），回退为非流式请求");
                    return await FallbackToNonStreamingAsync(messages, tools, ct, ex.Message).ConfigureAwait(true);
                }
            }
        }

        /// <summary>非流式兜底：它自己也带 504 重试；兜底也失败时把"流式为何失败"带上，便于排障。</summary>
        private async Task<ChatTurn> FallbackToNonStreamingAsync(
            IList<object> messages, IList<object> tools, CancellationToken ct, string reason)
        {
            try
            {
                return await CompleteAsync(messages, tools, ct).ConfigureAwait(true);
            }
            catch (RpcException ex)
            {
                _log.Warn("非流式兜底同样失败：" + ex.Message + "（流式失败原因：" + reason + "）");
                throw;
            }
        }

        private async Task<ChatTurn> StreamingOnceAsync(
            IList<object> messages,
            IList<object> tools,
            bool includeUsage,
            Action<ChatStreamProgress> onProgress,
            CancellationToken ct)
        {
            var apiKey = RequireApiKey();
            var payload = BuildPayload(messages, tools, true);
            if (includeUsage)
            {
                var streamOptions = JsonLite.New();
                streamOptions["include_usage"] = true;
                payload["stream_options"] = streamOptions;
            }

            var url = OpenAiClient.NormalizeBaseUrl(_settings.Current.BaseUrl) + "/chat/completions";
            var body = JsonLite.Serialize(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    // ResponseHeadersRead：拿到响应头就开始逐块读，不等整个响应体（大文件写入的关键）
                    response = await Shared
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    throw new UpstreamRetryableException("网络错误：" + ex.Message);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorText = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                        var status = (int)response.StatusCode;
                        _log.Warn("流式请求失败：" + status + " " + TextUtil.Ellipsis(errorText, 300));

                        if (IsRetryableStatus(status))
                            throw new UpstreamRetryableException("HTTP " + status + " " + DescribeStatus(status) +
                                                                 "：" + FriendlyBody(errorText, 160));
                        if (DowngradeMaxTokens(status, errorText))
                            throw new UpstreamRetryableException("上游不接受较大的 max_tokens，已降到 " + _maxTokensCap + " 重试");
                        if (status == 404 || IsGatewayPage(errorText))
                            throw new UpstreamRetryableException("网关返回 HTTP " + status + "（" + GatewayHint(errorText) + "）");
                        if (includeUsage && (status == 400 || status == 422))
                            throw new StreamOptionRejectedException("上游不接受 stream_options（HTTP " + status + "）");

                        throw new RpcException(ErrorCodes.Upstream,
                            "模型请求失败（HTTP " + status + "）：" + FriendlyBody(errorText, 300));
                    }

                    return await ReadStreamAsync(response, onProgress, ct).ConfigureAwait(true);
                }
            }
        }

        private async Task<ChatTurn> ReadStreamAsync(
            HttpResponseMessage response,
            Action<ChatStreamProgress> onProgress,
            CancellationToken ct)
        {
            var turn = new ChatTurn();
            var content = new StringBuilder();
            var reasoningChars = 0;
            var calls = new SortedDictionary<int, StreamToolCall>();
            var sawData = false;
            var sawDone = false;
            var sawProgress = false;
            var lastProgressTick = 0;
            string lastToolName = string.Empty;
            var lastToolWasActive = false;
            var totalArgsChars = 0;

            // 假死看门狗：每次收到数据行就重置；超时则取消并释放流，让读操作立刻失败退出
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(true))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                watchdog.CancelAfter(StallTimeoutMs);
                using (watchdog.Token.Register(delegate
                {
                    try { stream.Dispose(); } catch { /* 忽略 */ }
                }))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        string line;
                        try
                        {
                            line = await reader.ReadLineAsync().ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            if (watchdog.IsCancellationRequested)
                                throw new UpstreamRetryableException(
                                    "流式响应 " + (StallTimeoutMs / 1000) + " 秒没有收到任何数据");
                            throw new IOException("读取流式响应失败：" + ex.Message, ex);
                        }

                        if (line == null) break;                 // 流结束（可能被网关中途掐断）
                        watchdog.CancelAfter(StallTimeoutMs);    // 收到数据即重置看门狗

                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed[0] == ':') continue;                 // 空行 / SSE 注释
                        if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;   // event:/id: 等忽略

                        var data = trimmed.Substring(5).Trim();
                        if (data.Length == 0) continue;
                        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                        {
                            sawDone = true;
                            break;
                        }

                        sawData = true;

                        IDictionary<string, object> chunk;
                        try
                        {
                            chunk = JsonLite.ParseObject(data);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidDataException("SSE 数据行不是合法 JSON：" + TextUtil.Ellipsis(data, 120), ex);
                        }

                        // usage（有的上游只在最后一块给，有的完全不给 → 按 0 计）
                        var usage = JsonLite.Obj(chunk, "usage");
                        if (usage != null)
                        {
                            var promptTokens = JsonLite.Long(usage, "prompt_tokens");
                            var completionTokens = JsonLite.Long(usage, "completion_tokens");
                            if (promptTokens > 0) turn.PromptTokens = promptTokens;
                            if (completionTokens > 0) turn.CompletionTokens = completionTokens;
                        }

                        var choices = JsonLite.List(chunk, "choices");
                        if (choices != null)
                        {
                            foreach (var item in choices)
                            {
                                var choice = item as IDictionary<string, object>;
                                if (choice == null) continue;

                                var finishReason = JsonLite.Str(choice, "finish_reason");
                                if (!string.IsNullOrEmpty(finishReason)) turn.FinishReason = finishReason;

                                var delta = JsonLite.Obj(choice, "delta");
                                if (delta == null) delta = JsonLite.Obj(choice, "message"); // 少数兼容实现用 message
                                if (delta == null) continue;

                                var piece = JsonLite.Str(delta, "content");
                                if (!string.IsNullOrEmpty(piece))
                                {
                                    content.Append(piece);
                                    lastToolWasActive = false;
                                }
                                else
                                {
                                    // 推理模型会先流 reasoning_content：只用于进度显示，不进正文
                                    var reasoning = JsonLite.Str(delta, "reasoning_content");
                                    if (!string.IsNullOrEmpty(reasoning)) reasoningChars += reasoning.Length;
                                }

                                var toolCalls = JsonLite.List(delta, "tool_calls");
                                if (toolCalls != null)
                                {
                                    foreach (var raw in toolCalls)
                                    {
                                        var d = raw as IDictionary<string, object>;
                                        if (d == null) continue;

                                        var index = JsonLite.Int(d, "index", 0);
                                        StreamToolCall acc;
                                        if (!calls.TryGetValue(index, out acc))
                                        {
                                            acc = new StreamToolCall();
                                            calls[index] = acc;
                                        }

                                        var id = JsonLite.Str(d, "id");
                                        if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(acc.Id)) acc.Id = id;

                                        var function = JsonLite.Obj(d, "function");
                                        if (function == null) continue;

                                        var name = JsonLite.Str(function, "name");
                                        if (!string.IsNullOrEmpty(name)) acc.Name = MergeName(acc.Name, name);

                                        var argsPiece = JsonLite.Str(function, "arguments");
                                        if (!string.IsNullOrEmpty(argsPiece))
                                        {
                                            acc.Arguments.Append(argsPiece);
                                            totalArgsChars += argsPiece.Length;
                                        }

                                        if (!string.IsNullOrEmpty(acc.Name)) lastToolName = acc.Name;
                                        lastToolWasActive = true;
                                    }
                                }
                            }
                        }

                        // 进度节流：≥150ms 推一次
                        var now = Environment.TickCount;
                        if (onProgress != null && (!sawProgress || unchecked(now - lastProgressTick) >= ProgressIntervalMs))
                        {
                            sawProgress = true;
                            lastProgressTick = now;

                            var tail = lastToolWasActive ? TailOfCurrentArgs(calls) : Tail(content.ToString());
                            var progress = new ChatStreamProgress
                            {
                                Phase = lastToolWasActive ? "tool" : "content",
                                Chars = content.Length + reasoningChars + totalArgsChars,
                                Tool = lastToolName ?? string.Empty,
                                Text = tail
                            };
                            try { onProgress(progress); }
                            catch (Exception ex) { _log.Warn("流式进度回调异常：" + ex.Message); }
                        }
                    }
                }
            }

            if (!sawData)
                throw new InvalidDataException("流式响应没有任何 data 行");

            // 被网关中途掐断的特征：没有 [DONE]、也没有 finish_reason → 按瞬态错误重试（实测 60s 处会 504）
            if (!sawDone && string.IsNullOrEmpty(turn.FinishReason))
                throw new UpstreamRetryableException("流式响应中途断开（未收到 [DONE] / finish_reason）");

            turn.Content = content.Length > 0 ? content.ToString() : null;

            foreach (var pair in calls)
            {
                var acc = pair.Value;
                var call = new ToolCall
                {
                    Id = string.IsNullOrEmpty(acc.Id) ? "call_stream_" + pair.Key : acc.Id,
                    Name = (acc.Name ?? string.Empty).Trim(),
                    Arguments = acc.Arguments.ToString()
                };
                if (string.IsNullOrEmpty(call.Arguments)) call.Arguments = "{}";
                if (string.IsNullOrEmpty(acc.Id))
                    _log.Warn("流式响应缺少 tool_call id（index=" + pair.Key + "），已补为 " + call.Id);
                turn.ToolCalls.Add(call);
            }

            if (turn.ToolCalls.Count == 0 && string.IsNullOrEmpty(turn.Content) && string.IsNullOrEmpty(turn.FinishReason))
                throw new InvalidDataException("流式响应没有正文也没有工具调用");

            return turn;
        }

        /// <summary>工具名可能分片到达：整名重复/逐步补全都要合并成同一个名字。</summary>
        private static string MergeName(string current, string piece)
        {
            if (string.IsNullOrEmpty(current)) return piece;
            if (string.Equals(current, piece, StringComparison.Ordinal)) return current;
            if (piece.StartsWith(current, StringComparison.Ordinal)) return piece;      // 逐步补全：write → write_file
            if (current.StartsWith(piece, StringComparison.Ordinal)) return current;    // 重复给了前缀片段
            return current + piece;                                                    // 真·分片
        }

        private static string TailOfCurrentArgs(SortedDictionary<int, StreamToolCall> calls)
        {
            StreamToolCall last = null;
            foreach (var pair in calls)
            {
                if (pair.Value.Arguments.Length > 0) last = pair.Value;
            }
            return last == null ? string.Empty : Tail(last.Arguments.ToString());
        }

        private static string Tail(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= ProgressTextLimit) return text;
            return text.Substring(text.Length - ProgressTextLimit);
        }

        // ---------------- 公共部分 ----------------

        private static bool IsRetryableStatus(int status)
        {
            return status == 408 || status == 409 || status == 425 || status == 429 ||
                   status == 500 || status == 502 || status == 503 || status == 504;
        }

        /// <summary>
        /// 上游不接受较大的 max_tokens（不同网关/模型上限不同）时，本进程内降级到 8192 再试一次，
        /// 避免"一次改默认值就让所有人直接报 HTTP 400"。返回 true 表示已降级、调用方应重试。
        /// </summary>
        private bool DowngradeMaxTokens(int status, string errorText)
        {
            if (status != 400 && status != 422) return false;
            if (_maxTokensCap <= LegacyMaxTokensCap) return false;

            var text = errorText ?? string.Empty;
            var hit = text.IndexOf("max_tokens", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      text.IndexOf("maximum context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      text.IndexOf("context length", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      text.IndexOf("too large", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      text.IndexOf("too long", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hit) return false;

            _maxTokensCap = LegacyMaxTokensCap;
            _log.Warn("上游拒绝 max_tokens=" + MaxTokensCap + "（HTTP " + status + "），本进程内降级到 " + _maxTokensCap);
            return true;
        }

        private static string DescribeStatus(int status)
        {
            if (status == 504) return "网关超时（上游长时间没有响应）";
            if (status == 502 || status == 503) return "上游服务暂时不可用";
            if (status == 429) return "请求过于频繁";
            if (status == 408) return "请求超时";
            if (status == 404) return "网关找不到该地址（服务可能暂时下线）";
            if (status == 401 || status == 403) return "API Key 无效或没有权限";
            return "上游服务异常";
        }

        /// <summary>
        /// 上游/网关偶尔返回整页 HTML（例如 frp 的 "Not Found" 页面、nginx 502 页）。
        /// 直接把 HTML 塞进界面提示既难看也没信息量，这里把标签去掉并压成一行。
        /// </summary>
        private static string FriendlyBody(string body, int limit)
        {
            if (string.IsNullOrWhiteSpace(body)) return "（响应为空）";

            var raw = body.Trim();
            var looksHtml = raw.StartsWith("<", StringComparison.Ordinal) ||
                            raw.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            raw.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!looksHtml) return TextUtil.Ellipsis(raw.Replace("\r", " ").Replace("\n", " "), limit);

            var title = ExtractTagText(raw, "title");
            var h1 = ExtractTagText(raw, "h1");
            var text = !string.IsNullOrEmpty(title) ? title : h1;
            if (string.IsNullOrEmpty(text))
            {
                var sb = new System.Text.StringBuilder();
                var inTag = false;
                foreach (var ch in raw)
                {
                    if (ch == '<') { inTag = true; continue; }
                    if (ch == '>') { inTag = false; sb.Append(' '); continue; }
                    if (!inTag) sb.Append(ch);
                }
                text = sb.ToString();
            }

            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            return string.IsNullOrEmpty(text) ? "网关返回了一个 HTML 错误页" : TextUtil.Ellipsis(text, limit);
        }

        private static string ExtractTagText(string html, string tag)
        {
            var open = html.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
            if (open < 0) return null;
            var start = html.IndexOf('>', open);
            var end = html.IndexOf("</" + tag + ">", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end < 0 || end <= start) return null;
            return html.Substring(start + 1, end - start - 1);
        }

        /// <summary>响应体是不是"网关/反代自己的错误页"（这类 404/503 通常是瞬时的，值得重试一次）。</summary>
        private static bool IsGatewayPage(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            if (body.IndexOf("frp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                body.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (body.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (body.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 body.IndexOf("bad gateway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 body.IndexOf("service unavailable", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        private static string GatewayHint(string body)
        {
            var text = FriendlyBody(body, 80);
            return string.IsNullOrEmpty(text) ? "网关错误页" : text;
        }

        private static RpcException TooManyUpstreamFailures(UpstreamRetryableException ex, int retried)
        {
            var retriedText = retried > 0 ? "，已重试 " + retried + " 次" : string.Empty;
            return new RpcException(ErrorCodes.Upstream,
                "上游超时/不可用（" + ex.Message + "）" + retriedText +
                "。建议稍后重试，或在设置里换一个模型/网关");
        }

        private string RequireApiKey()
        {
            var apiKey = _secrets.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new RpcException(ErrorCodes.BadRequest, "尚未配置 API Key：请到设置页粘贴并保存密钥");
            return apiKey;
        }

        private IDictionary<string, object> BuildPayload(IList<object> messages, IList<object> tools, bool stream)
        {
            var settings = _settings.Current;
            var payload = JsonLite.New();
            payload["model"] = settings.Model;
            payload["messages"] = messages;
            payload["temperature"] = settings.Temperature;
            payload["max_tokens"] = Math.Min(settings.MaxTokens, _maxTokensCap);
            payload["stream"] = stream;
            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = tools;
                payload["tool_choice"] = "auto";
            }
            return payload;
        }

        private static ChatTurn ParseTurn(string text)
        {
            var turn = new ChatTurn();
            var root = JsonLite.ParseObject(text);

            var usage = JsonLite.Obj(root, "usage");
            turn.PromptTokens = JsonLite.Long(usage, "prompt_tokens");
            turn.CompletionTokens = JsonLite.Long(usage, "completion_tokens");

            var choices = JsonLite.List(root, "choices");
            if (choices == null || choices.Count == 0) return turn;

            var first = choices[0] as IDictionary<string, object>;
            turn.FinishReason = JsonLite.Str(first, "finish_reason");

            var message = JsonLite.Obj(first, "message");
            if (message == null) return turn;

            turn.Content = JsonLite.Str(message, "content");

            var calls = JsonLite.List(message, "tool_calls");
            if (calls != null)
            {
                foreach (var item in calls)
                {
                    var call = item as IDictionary<string, object>;
                    if (call == null) continue;
                    var function = JsonLite.Obj(call, "function");
                    turn.ToolCalls.Add(new ToolCall
                    {
                        Id = JsonLite.Str(call, "id"),
                        Name = JsonLite.Str(function, "name"),
                        Arguments = JsonLite.Str(function, "arguments") ?? "{}"
                    });
                }
            }
            return turn;
        }

        /// <summary>流式 tool_calls 的累加器（按 index 保序）。</summary>
        private sealed class StreamToolCall
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder();
        }

        /// <summary>上游拒绝 stream_options（400/422）：内部信号，用于降级重试。</summary>
        private sealed class StreamOptionRejectedException : Exception
        {
            public StreamOptionRejectedException(string message) : base(message) { }
        }

        /// <summary>上游瞬态错误（504/502/503/429/网络抖动/流中途断开）：内部信号，用于重试。</summary>
        private sealed class UpstreamRetryableException : Exception
        {
            public UpstreamRetryableException(string message) : base(message) { }
        }
    }
}
