using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppForge.Shared;
using AppForge.Host.Diagnostics;

namespace AppForge.Host.Ai
{
    /// <summary>
    /// OpenAI 兼容协议的客户端（M1 只做：列模型 + 连通性自检）。
    /// 流式对话与工具调用在 M2 的 GenerationEngine 中扩展。
    /// 只用 BCL：HttpClient + JavaScriptSerializer。
    /// </summary>
    public sealed class OpenAiClient
    {
        // 超时统一由 CancellationToken 控制（HttpClient 的 Timeout 一旦发过请求就不能再改）
        private static readonly HttpClient Shared = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly Log _log;

        public OpenAiClient(SettingsStore settings, SecretsStore secrets, Log log)
        {
            _settings = settings;
            _secrets = secrets;
            _log = log;
        }

        public static string NormalizeBaseUrl(string baseUrl)
        {
            var raw = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (raw.Length == 0) return Defaults.BaseUrl;

            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) return raw;
            if (uri.AbsolutePath.Trim('/').Length == 0) raw = raw + "/v1";
            return raw;
        }

        public async Task<IList<string>> ListModelsAsync(CancellationToken ct)
        {
            var settings = _settings.Current;
            var apiKey = _secrets.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new RpcException(ErrorCodes.BadRequest, "尚未配置 API Key，请先在设置中填写");

            var client = CreateClient(settings);
            var url = NormalizeBaseUrl(settings.BaseUrl) + "/models";
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new RpcException(ErrorCodes.Upstream, DescribeHttpError(response, body));

                    var list = new List<string>();
                    var obj = JsonLite.ParseObject(body);
                    var data = JsonLite.List(obj, "data");
                    if (data != null)
                    {
                        foreach (var item in data)
                        {
                            var d = item as IDictionary<string, object>;
                            var id = JsonLite.Str(d, "id");
                            if (!string.IsNullOrEmpty(id)) list.Add(id);
                        }
                    }
                    list.Sort(StringComparer.OrdinalIgnoreCase);
                    return list;
                }
            }
        }

        public async Task<TestModelResult> TestModelAsync(CancellationToken ct)
        {
            var settings = _settings.Current;
            var result = new TestModelResult { Model = settings.Model };
            var apiKey = _secrets.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                result.Error = "尚未配置 API Key";
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var client = CreateClient(settings);
                var url = NormalizeBaseUrl(settings.BaseUrl) + "/chat/completions";

                var payload = JsonLite.New();
                payload["model"] = settings.Model;
                payload["stream"] = false;
                payload["temperature"] = settings.Temperature;
                payload["max_tokens"] = Math.Min(settings.MaxTokens, 512);
                var messages = new List<object>();
                var user = JsonLite.New();
                user["role"] = "user";
                user["content"] = "只回复两个字：可用";
                messages.Add(user);
                payload["messages"] = messages;

                var body = JsonLite.Serialize(payload);
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        stopwatch.Stop();
                        result.LatencyMs = stopwatch.ElapsedMilliseconds;

                        if (!response.IsSuccessStatusCode)
                        {
                            result.Error = DescribeHttpError(response, text);
                            return result;
                        }

                        var obj = JsonLite.ParseObject(text);
                        var choices = JsonLite.List(obj, "choices");
                        if (choices != null && choices.Count > 0)
                        {
                            var first = choices[0] as IDictionary<string, object>;
                            var message = JsonLite.Obj(first, "message");
                            var content = JsonLite.Str(message, "content");
                            if (string.IsNullOrEmpty(content)) content = JsonLite.Str(message, "reasoning_content");
                            result.Sample = TextUtil.Ellipsis(content, 120);
                        }

                        var usage = JsonLite.Obj(obj, "usage");
                        result.PromptTokens = JsonLite.Long(usage, "prompt_tokens");
                        result.CompletionTokens = JsonLite.Long(usage, "completion_tokens");
                        result.Ok = true;
                        return result;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
                result.Error = "请求超时（" + settings.TimeoutSeconds + "s）";
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
                result.Error = ex.Message;
                _log.Warn("模型连通性测试失败：" + ex.Message);
                return result;
            }
        }

        private static HttpClient CreateClient(Settings settings)
        {
            // 复用静态 HttpClient（连接池复用，更快）；单次超时用 CancellationToken 控制
            return Shared;
        }

        private static string DescribeHttpError(HttpResponseMessage response, string body)
        {
            var status = (int)response.StatusCode;
            var hint = status == 401 || status == 403
                ? "API Key 无效或没有权限"
                : status == 404
                    ? "地址或模型不存在（检查 Base URL 是否包含 /v1、模型名是否正确）"
                    : status == 429
                        ? "请求过于频繁，请稍后重试"
                        : "上游返回错误";

            var detail = TextUtil.Ellipsis(body, 300);
            return hint + "（HTTP " + status + "）：" + detail;
        }
    }

    /// <summary>RPC 层可识别的业务异常（会被转成结构化错误信封）。</summary>
    public sealed class RpcException : Exception
    {
        public string Code { get; private set; }

        public RpcException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
