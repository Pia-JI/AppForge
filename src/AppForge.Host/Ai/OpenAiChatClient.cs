using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 带 function calling 的对话客户端（非流式；生成引擎按轮次拿完整 tool_calls，
    /// 比流式拼装 tool 参数更稳，进度通过事件推给界面）。
    /// </summary>
    public sealed class OpenAiChatClient
    {
        private static readonly HttpClient Shared = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly SettingsStore _settings;
        private readonly SecretsStore _secrets;
        private readonly Log _log;

        public OpenAiChatClient(SettingsStore settings, SecretsStore secrets, Log log)
        {
            _settings = settings;
            _secrets = secrets;
            _log = log;
        }

        public async Task<ChatTurn> CompleteAsync(IList<object> messages, IList<object> tools, CancellationToken ct)
        {
            var settings = _settings.Current;
            var apiKey = _secrets.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new RpcException(ErrorCodes.BadRequest, "尚未配置 API Key：请到设置页粘贴并保存密钥");

            var payload = JsonLite.New();
            payload["model"] = settings.Model;
            payload["messages"] = messages;
            payload["temperature"] = settings.Temperature;
            payload["max_tokens"] = Math.Min(settings.MaxTokens, 16000);
            payload["stream"] = false;
            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = tools;
                payload["tool_choice"] = "auto";
            }

            var url = OpenAiClient.NormalizeBaseUrl(settings.BaseUrl) + "/chat/completions";
            var body = JsonLite.Serialize(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

                using (var response = await Shared.SendAsync(request, ct).ConfigureAwait(true))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (!response.IsSuccessStatusCode)
                    {
                        _log.Warn("模型请求失败：" + (int)response.StatusCode + " " + TextUtil.Ellipsis(text, 300));
                        throw new RpcException(ErrorCodes.Upstream,
                            "模型请求失败（HTTP " + (int)response.StatusCode + "）：" + TextUtil.Ellipsis(text, 300));
                    }

                    return ParseTurn(text);
                }
            }
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
    }
}
