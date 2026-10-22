/**
 * 宿主 ⇄ UI 的消息 RPC 封装。
 * 走 WebView2 原生 postMessage：{id, method, params} → {id, ok, data|error}
 * 没有桥（比如用浏览器直接打开页面）时给出明确错误，而不是静默失败。
 */

const bridge = (typeof window !== 'undefined' && window.chrome && window.chrome.webview) || null;
const pending = new Map();
const listeners = new Map();
let seq = 0;

export const bridgeAvailable = !!bridge;

if (bridge) {
  bridge.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg || typeof msg !== 'object') return;

    if (msg.event) {
      const handlers = listeners.get(msg.event) || [];
      handlers.forEach((fn) => {
        try { fn(msg.payload); } catch (err) { console.error('[rpc] 事件处理异常', msg.event, err); }
      });
      return;
    }

    if (!msg.id) return;
    const entry = pending.get(msg.id);
    if (!entry) return;

    pending.delete(msg.id);
    clearTimeout(entry.timer);

    if (msg.ok) {
      entry.resolve(msg.data == null ? {} : msg.data);
    } else {
      const error = new Error((msg.error && msg.error.message) || '未知错误');
      error.code = msg.error && msg.error.code;
      entry.reject(error);
    }
  });
}

export function call(method, params, timeoutMs) {
  if (!bridge) {
    return Promise.reject(new Error('宿主桥不可用：请通过 AppForge.exe 启动本界面'));
  }

  const id = 'r' + (++seq);
  const timeout = timeoutMs || 120000;

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error('请求超时：' + method));
    }, timeout);

    pending.set(id, { resolve, reject, timer });

    try {
      bridge.postMessage({ id, method, params: params || {} });
    } catch (err) {
      pending.delete(id);
      clearTimeout(timer);
      reject(err);
    }
  });
}

export function on(eventName, handler) {
  if (!listeners.has(eventName)) listeners.set(eventName, []);
  listeners.get(eventName).push(handler);
  return () => {
    const list = listeners.get(eventName) || [];
    const index = list.indexOf(handler);
    if (index >= 0) list.splice(index, 1);
  };
}
