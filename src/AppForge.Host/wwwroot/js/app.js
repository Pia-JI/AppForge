import { call, bridgeAvailable } from './rpc.js';
import { h, clear, toast, debounce } from './dom.js';
import * as Launcher from './views/launcher.js';
import * as Settings from './views/settings.js';
import * as Builder from './views/builder.js';

/** 全局状态（视图共享；不引状态库，够用且零依赖） */
export const state = {
  view: 'launcher',
  search: '',
  routeQuery: {},
  apps: [],
  info: null,
  settingsData: null,
  deps: [],
  loading: true,
  connected: false
};

const viewEl = document.getElementById('view');
const searchEl = document.getElementById('search');
const statusLeft = document.getElementById('status-left');
const statusRight = document.getElementById('status-right');

const views = { launcher: Launcher, settings: Settings, builder: Builder };

export async function refreshApps() {
  const res = await call('apps.list', { search: state.search });
  state.apps = res.apps || [];
}

export async function render() {
  const view = views[state.view] || views.launcher;

  searchEl.style.display = state.view === 'launcher' ? '' : 'none';

  clear(viewEl);
  const container = h('div', { class: 'view-body' });
  viewEl.appendChild(container);

  try {
    await view.render(container);
  } catch (err) {
    console.error(err);
    clear(container);
    container.appendChild(h('div', { class: 'notice', text: '加载失败：' + err.message }));
  }
}

function parseQuery(queryPart) {
  const out = {};
  if (!queryPart) return out;
  queryPart.split('&').forEach((pair) => {
    if (!pair) return;
    const index = pair.indexOf('=');
    const key = index >= 0 ? pair.slice(0, index) : pair;
    const value = index >= 0 ? pair.slice(index + 1) : '';
    try {
      out[decodeURIComponent(key)] = decodeURIComponent(value);
    } catch (e) {
      out[key] = value;
    }
  });
  return out;
}

function applyHash() {
  const raw = (location.hash || '').replace(/^#\/?/, '');
  const parts = raw.split('?');
  const name = (parts[0] || 'launcher').trim() || 'launcher';
  state.view = views[name] ? name : 'launcher';
  state.routeQuery = parseQuery(parts[1] || '');
  render();
}

function paintStatus() {
  if (!bridgeAvailable) {
    statusLeft.textContent = '未连接到宿主进程';
    return;
  }
  const info = state.info;
  const version = info ? ' · v' + info.version : '';
  statusLeft.textContent = (state.connected ? '已连接宿主' : '连接中…') + version +
    (info && info.portable ? ' · 便携模式' : '');

  const badDeps = (state.deps || []).filter((d) => d.state !== 'ok');
  const parts = [];
  parts.push(state.apps.length + ' 个应用');
  if (badDeps.length) parts.push('⚠ ' + badDeps.length + ' 项依赖异常');
  statusRight.textContent = parts.join('  ·  ');
}

async function boot() {
  if (!bridgeAvailable) {
    clear(viewEl);
    viewEl.appendChild(h('div', { class: 'empty' },
      h('div', { class: 'big', text: '🔌' }),
      h('h3', { text: '宿主桥不可用' }),
      h('p', { text: '当前页面不是在 AppForge.exe 中打开的。请双击 AppForge.exe 使用完整功能；直接用浏览器打开时只能看到静态界面。' })
    ));
    paintStatus();
    return;
  }

  location.hash = location.hash || '#/launcher';
  window.addEventListener('hashchange', applyHash);

  document.getElementById('btn-new').addEventListener('click', () => {
    location.hash = '#/builder';
  });
  document.getElementById('btn-settings').addEventListener('click', () => {
    location.hash = state.view === 'settings' ? '#/launcher' : '#/settings';
  });

  searchEl.addEventListener('input', debounce(async () => {
    state.search = searchEl.value.trim();
    try {
      await refreshApps();
      await render();
      paintStatus();
    } catch (err) {
      toast(err.message, 'bad');
    }
  }, 120));

  try {
    const pong = await call('ui.ping');
    state.connected = true;
    state.info = await call('app.info');
    const settings = await call('settings.get');
    state.settingsData = settings;
    state.deps = settings.deps || [];
    await refreshApps();
    state.loading = false;
    paintStatus();
    await render();
  } catch (err) {
    state.loading = false;
    statusLeft.textContent = '宿主通信失败';
    clear(viewEl);
    viewEl.appendChild(h('div', { class: 'notice', text: '与宿主通信失败：' + err.message }));
    console.error(err);
  }
}

boot();

// 调试/自检钩子：供 --ui-diag 自动化验证界面交互（不影响正常使用）
if (typeof window !== 'undefined') {
  window.__af = {
    call: call,
    state: state,
    refreshApps: refreshApps,
    render: render
  };
}
