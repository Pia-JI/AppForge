/**
 * 界面错误上报：把 JS 异常/未处理 Promise 拒绝转发给宿主日志。
 * 目的：用户那边界面出问题时，不用开 DevTools 也能在 logs\app-*.log 里看到原因。
 * 必须最先加载（index.html 中位于 app.js 之前）。
 */
(function () {
  window.__afErrors = window.__afErrors || [];

  function report(kind, message, extra) {
    var item = { kind: kind, message: String(message || ''), extra: extra || '' };
    window.__afErrors.push(item);
    console.error('[af] ' + kind + '：' + item.message, extra || '');
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({
          event: 'ui.error',
          payload: item
        }));
      }
    } catch (e) {
      /* 上报失败不影响页面 */
    }
  }

  window.addEventListener('error', function (e) {
    report('error', (e && e.message) || 'unknown error', {
      source: e && e.filename,
      line: e && e.lineno,
      column: e && e.colno,
      stack: e && e.error && e.error.stack ? String(e.error.stack).slice(0, 800) : ''
    });
  });

  window.addEventListener('unhandledrejection', function (e) {
    var reason = e && e.reason;
    report('unhandledrejection', (reason && reason.message) || reason || 'unknown rejection');
  });

  // 同时记录 console.error（不改变原有行为）
  var originalError = console.error;
  console.error = function () {
    try {
      var parts = Array.prototype.map.call(arguments, function (a) {
        if (a instanceof Error) return a.message + '\n' + (a.stack || '');
        if (typeof a === 'object') { try { return JSON.stringify(a); } catch (e) { return String(a); } }
        return String(a);
      });
      window.__afErrors.push({ kind: 'console.error', message: parts.join(' ') });
    } catch (e) { /* 忽略 */ }
    return originalError.apply(console, arguments);
  };

  window.__afReport = report;
})();
