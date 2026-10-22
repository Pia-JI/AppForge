/** 极简 DOM 工具：h / toast / 对话框 / 时间格式化（不引任何库）。 */

export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);

  if (attrs) {
    Object.keys(attrs).forEach((key) => {
      const value = attrs[key];
      if (value === null || value === undefined || value === false) return;

      if (key === 'class') el.className = value;
      else if (key === 'text') el.textContent = value;
      else if (key === 'html') el.innerHTML = value;
      else if (key === 'dataset') Object.assign(el.dataset, value);
      else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
      else if (key.slice(0, 2) === 'on' && typeof value === 'function') {
        el.addEventListener(key.slice(2).toLowerCase(), value);
      } else {
        el.setAttribute(key, value);
      }
    });
  }

  appendChildren(el, children);
  return el;
}

function appendChildren(el, children) {
  children.forEach((child) => {
    if (child === null || child === undefined || child === false) return;
    if (Array.isArray(child)) { appendChildren(el, child); return; }
    if (child instanceof Node) { el.appendChild(child); return; }
    el.appendChild(document.createTextNode(String(child)));
  });
}

export function clear(el) {
  while (el && el.firstChild) el.removeChild(el.firstChild);
}

export function toast(message, kind, duration) {
  const wrap = document.getElementById('toast');
  if (!wrap) return;

  const el = h('div', { class: 'toast' + (kind ? ' ' + kind : ''), text: message });
  wrap.appendChild(el);

  setTimeout(() => {
    el.style.transition = 'opacity .2s, transform .2s';
    el.style.opacity = '0';
    el.style.transform = 'translateY(6px)';
    setTimeout(() => el.remove(), 220);
  }, duration || 2600);
}

function openOverlay(dialog) {
  const overlay = h('div', { class: 'overlay' }, dialog);
  document.getElementById('modal-root').appendChild(overlay);
  const close = () => overlay.remove();
  overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
  return { overlay, close };
}

export function confirmDialog(options) {
  const opts = options || {};
  return new Promise((resolve) => {
    const okBtn = h('button', {
      class: 'btn ' + (opts.danger ? 'danger' : 'primary'),
      text: opts.okText || '确定'
    });
    const cancelBtn = h('button', { class: 'btn', text: '取消' });

    const dialog = h('div', { class: 'dialog' },
      h('h3', { text: opts.title || '请确认' }),
      opts.message ? h('p', { text: opts.message }) : null,
      h('div', { class: 'dialog-actions' }, cancelBtn, okBtn)
    );

    const { close } = openOverlay(dialog);
    const finish = (value) => { close(); resolve(value); };

    okBtn.addEventListener('click', () => finish(true));
    cancelBtn.addEventListener('click', () => finish(false));
    setTimeout(() => okBtn.focus(), 30);
  });
}

export function promptDialog(options) {
  const opts = options || {};
  return new Promise((resolve) => {
    const input = h('input', {
      class: 'input',
      type: 'text',
      value: opts.value || '',
      maxlength: opts.maxlength || 200
    });
    const okBtn = h('button', { class: 'btn primary', text: opts.okText || '确定' });
    const cancelBtn = h('button', { class: 'btn', text: '取消' });

    const dialog = h('div', { class: 'dialog' },
      h('h3', { text: opts.title || '输入' }),
      opts.label ? h('p', { text: opts.label }) : null,
      input,
      h('div', { class: 'dialog-actions' }, cancelBtn, okBtn)
    );

    const { close } = openOverlay(dialog);
    const finish = (value) => { close(); resolve(value); };

    okBtn.addEventListener('click', () => finish(input.value.trim()));
    cancelBtn.addEventListener('click', () => finish(null));
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') finish(input.value.trim());
      if (e.key === 'Escape') finish(null);
    });
    setTimeout(() => { input.focus(); input.select(); }, 30);
  });
}

export function showMenu(anchorEl, items) {
  const rect = anchorEl.getBoundingClientRect();
  const menu = h('div', { class: 'menu' });

  items.forEach((item) => {
    if (item === '-') { menu.appendChild(h('div', { class: 'sep' })); return; }
    menu.appendChild(h('button', {
      class: item.danger ? 'danger' : '',
      text: item.label,
      onClick: () => {
        menu.remove();
        document.removeEventListener('mousedown', onDocMouseDown);
        item.action();
      }
    }));
  });

  document.body.appendChild(menu);
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  let left = rect.right - mw;
  let top = rect.bottom + 6;
  if (left < 8) left = 8;
  if (top + mh > window.innerHeight - 8) top = rect.top - mh - 6;
  menu.style.left = left + 'px';
  menu.style.top = Math.max(8, top) + 'px';

  const onDocMouseDown = (e) => {
    if (!menu.contains(e.target)) {
      menu.remove();
      document.removeEventListener('mousedown', onDocMouseDown);
    }
  };
  setTimeout(() => document.addEventListener('mousedown', onDocMouseDown), 0);
}

export function relativeTime(ts) {
  if (!ts) return '从未打开';
  const diff = Date.now() - ts;
  if (diff < 60 * 1000) return '刚刚';
  if (diff < 60 * 60 * 1000) return Math.floor(diff / 60000) + ' 分钟前';
  if (diff < 24 * 60 * 60 * 1000) return Math.floor(diff / 3600000) + ' 小时前';
  if (diff < 7 * 24 * 60 * 60 * 1000) return Math.floor(diff / 86400000) + ' 天前';

  const d = new Date(ts);
  const pad = (n) => (n < 10 ? '0' + n : '' + n);
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
}

export function formatBytes(bytes) {
  if (!bytes || bytes < 0) return '0 B';
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / 1024 / 1024).toFixed(2) + ' MB';
}

export function debounce(fn, wait) {
  let timer = null;
  return function debounced() {
    const args = arguments;
    clearTimeout(timer);
    timer = setTimeout(() => fn.apply(null, args), wait);
  };
}
