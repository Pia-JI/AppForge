using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppForge.Shared;
using AppForge.Host.Ai;
using AppForge.Host.Diagnostics;

namespace AppForge.Host.Web
{
    /// <summary>
    /// 宿主 ⇄ 前端的消息 RPC：
    ///   UI  → 宿主：{ id, method, params }
    ///   宿主 → UI：{ id, ok, data } 或 { id, ok:false, error:{code,message} }
    ///   事件推送：{ event, payload }
    /// 只允许调用方法表里登记过的方法，杜绝"任意反射调用"。
    /// </summary>
    public sealed class RpcDispatcher
    {
        private readonly Dictionary<string, Func<IDictionary<string, object>, Task<object>>> _handlers;
        private readonly Log _log;

        public RpcDispatcher(Log log)
        {
            _log = log;
            _handlers = new Dictionary<string, Func<IDictionary<string, object>, Task<object>>>(StringComparer.Ordinal);
        }

        public void Register(string method, Func<IDictionary<string, object>, Task<object>> handler)
        {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("method 为空");
            _handlers[method] = handler;
        }

        public void RegisterSync(string method, Func<IDictionary<string, object>, object> handler)
        {
            Register(method, parameters => Task.FromResult(handler(parameters)));
        }

        public bool IsRegistered(string method)
        {
            return !string.IsNullOrEmpty(method) && _handlers.ContainsKey(method);
        }

        public async Task<string> HandleAsync(string json)
        {
            string id = null;
            try
            {
                var request = JsonLite.ParseObject(json);
                id = JsonLite.Str(request, "id");
                var method = JsonLite.Str(request, "method");
                if (string.IsNullOrEmpty(method))
                    return ErrorEnvelope(id, ErrorCodes.BadRequest, "缺少 method");

                Func<IDictionary<string, object>, Task<object>> handler;
                if (!_handlers.TryGetValue(method, out handler))
                    return ErrorEnvelope(id, ErrorCodes.NotAllowed, "未登记的方法：" + method);

                var parameters = JsonLite.Obj(request, "params") ?? JsonLite.New();
                var data = await handler(parameters).ConfigureAwait(true);

                var response = JsonLite.New();
                response["id"] = id;
                response["ok"] = true;
                response["data"] = data ?? JsonLite.New();
                return JsonLite.Serialize(response);
            }
            catch (RpcException ex)
            {
                return ErrorEnvelope(id, ex.Code, ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                return ErrorEnvelope(id, ErrorCodes.NotFound, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return ErrorEnvelope(id, ErrorCodes.BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("RPC 处理失败", ex);
                return ErrorEnvelope(id, ErrorCodes.Internal, "内部错误：" + ex.Message);
            }
        }

        public static string ErrorEnvelope(string id, string code, string message)
        {
            var error = JsonLite.New();
            error["code"] = code;
            error["message"] = message ?? string.Empty;

            var response = JsonLite.New();
            response["id"] = id;
            response["ok"] = false;
            response["error"] = error;
            return JsonLite.Serialize(response);
        }

        public static string EventEnvelope(string name, object payload)
        {
            var envelope = JsonLite.New();
            envelope["event"] = name;
            envelope["payload"] = payload;
            return JsonLite.Serialize(envelope);
        }
    }
}
