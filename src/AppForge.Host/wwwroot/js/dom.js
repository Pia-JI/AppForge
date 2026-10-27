/** 极简 DOM 工具：h / toast / 对话框 / 时间格式化（不引任何库）。 */

import { call } from './rpc.js';

/**
 * 打开目录类 RPC 的统一处理。
 * 宿主返回 ok=false 表示"系统没有接受打开请求"（多见于安全软件拦截，或精简系统
 * 限制了资源管理器调用；此时宿主已把路径放进了剪贴板），
 * 这里给一条明确提示并降级成应用内文件列表，而不是静默失败。
 */
export async function openFolder(method, params) {
  try {
    const res = await call(method, params);
    if (res && res.ok === false) {
      // 系统没接受打开请求（安全软件拦截 / 管理员权限下 shell 拒绝 / 无交互外壳）→ 界面里直接看文件列表
      await folderDialog(res.path || '', res.copied, res.elevated, res.stages);
      return false;
    }
    return true;
  } catch (err) {
    toast(err.message, 'bad');
    return false;
  }
}

/**
 * 应用内文件列表（只读）。
 * 用途：系统调不起资源管理器时，"打开目录"退化成在 AppForge 里直接浏览数据目录，
 * 用户至少能看到应用文件、导出/复制路径，而不是只收到一个系统错误框。
 * stages 是宿主返回的逐级诊断（HRESULT / Win32 码 / 退出码），折叠展示、可一键复制给开发者。
 */
export async function folderDialog(startPath, alreadyCopied, elevated, stages) {
  let current = startPath;

  const pathLine = h('div', { class: 'folder-path', text: '加载中…' });
  const listEl = h('div', { class: 'folder-list' });
  const upBtn = h('button', { class: 'btn ghost', text: '↑ 上一级' });
  const copyBtn = h('button', { class: 'btn', text: '复制路径' });
  const closeBtn = h('button', { class: 'btn primary', text: '关闭' });

  const hintText = elevated
    ? 'AppForge 目前以「管理员身份」运行。Windows 不允许管理员进程把打开文件夹的请求交给普通权限的资源管理器，所以这里改成在应用内显示。' +
      (alreadyCopied ? '路径已复制到剪贴板。' : '') +
      '想正常用系统资源管理器打开：关掉 AppForge，用普通权限重新启动（不要选「以管理员身份运行」）。'
    : (alreadyCopied
      ? '已尝试 6 种方式（ShellExecute / COM 导航 / shell32 / explorer / explorer /e / rundll32），系统都没有真的打开窗口——常见原因是联想电脑管家、火绒、360 等安全软件拦住了资源管理器调用。路径已复制到剪贴板，按 Win+E 打开资源管理器后粘贴到地址栏即可。'
      : '已尝试 6 种方式（ShellExecute / COM 导航 / shell32 / explorer / explorer /e / rundll32），系统都没有真的打开窗口——常见原因是联想电脑管家、火绒、360 等安全软件拦住了资源管理器调用。');

  // 逐级诊断：给用户一个"能复制给开发者"的入口，也便于自己看是哪一级被拦
  const diagBox = h('div', { class: 'folder-diag' });
  if (stages && stages.length) {
    const pre = h('pre', { class: 'folder-diag-pre', text: stages.join('\n') });
    const copyDiag = h('button', { class: 'btn ghost sm', text: '复制诊断信息' });
    copyDiag.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(stages.join('\n'));
        toast('诊断信息已复制', 'good');
      } catch (err) {
        toast('复制失败：' + err.message, 'bad');
      }
    });
    const details = h('details', {},
      h('summary', { text: '逐级诊断（' + stages.length + ' 行，点开可复制给开发者）' }),
      pre
    );
    diagBox.appendChild(details);
    diagBox.appendChild(copyDiag);
  }

  const dialog = h('div', { class: 'dialog dialog-wide' },
    h('h3', { text: '📁 文件列表' }),
    h('p', { class: 'hint', text: hintText }),
    pathLine,
    h('div', { class: 'folder-actions' }, upBtn, copyBtn),
    listEl,
    diagBox,
    h('div', { class: 'dialog-actions' }, closeBtn)
  );

  const { close } = openOverlay(dialog);
  closeBtn.addEventListener('click', close);

  copyBtn.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(current || '');
      toast('路径已复制', 'good');
    } catch (err) {
      toast('复制失败：' + err.message, 'bad');
    }
  });

  async function load(target) {
    listEl.textContent = '';
    listEl.appendChild(h('p', { class: 'hint', text: '读取中…' }));
    let data;
    try {
      data = await call('fs.list', { path: target });
    } catch (err) {
      listEl.textContent = '';
      listEl.appendChild(h('p', { class: 'hint', text: '打不开这个目录：' + err.message }));
      return;
    }

    current = data.path;
    pathLine.textContent = data.path;
    upBtn.disabled = !data.parent;
    listEl.textContent = '';

    const items = data.items || [];
    if (!items.length) {
      listEl.appendChild(h('p', { class: 'hint', text: '（空目录）' }));
      return;
    }

    items.forEach((item) => {
      const row = h('button', {
        class: 'folder-row',
        onClick: () => {
          if (item.dir) { load(current.replace(/[\\/]+$/, '') + '\\' + item.name); return; }
          navigator.clipboard.writeText(current.replace(/[\\/]+$/, '') + '\\' + item.name)
            .then(() => toast('文件路径已复制', 'good'))
            .catch(() => { });
        }
      },
        h('span', { class: 'folder-name', text: (item.dir ? '📁 ' : '📄 ') + item.name }),
        h('span', { class: 'folder-meta', text: item.dir ? '文件夹' : formatBytes(item.size) })
      );
      listEl.appendChild(row);
    });
  }

  upBtn.addEventListener('click', async () => {
    try {
      const data = await call('fs.list', { path: current });
      if (data.parent) load(data.parent);
    } catch (err) {
      toast(err.message, 'bad');
    }
  });

  await load(current);
}

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
