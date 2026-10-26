import { call, on } from '../rpc.js';
import { h, clear, toast, openFolder } from '../dom.js';
import { state, refreshApps, render as renderCurrentView } from '../app.js';

/**
 * 生成工作台：
 *   新建：输入需求 → gen.start
 *   修改：从桌面进入（#/builder?app=<id>）→ gen.modify，基于现有代码做增量修改
 * 进度通过 gen.event 实时推送（step / log / delta / done / error）。
 *
 * 状态复位约定（issue：复杂应用被强制结束后再点"新建应用"，界面还停在上一次的失败屏）：
 *   1) 进入页面时，只要没有正在运行的任务，就完整复位（runId / steps / logs / error / result 全清）；
 *      有任务在跑时保留进度，切走再回来还能看到进度。
 *   2) 已经停在 #/builder 上再点顶栏"新建应用"，hash 不变 → 不会触发 hashchange，
 *      由 app.js 调 resetBuilder() 兜底做一轮全新界面。
 *   3) 被放弃（取消 / 复位 / 重新开始）的 runId 记入 ignoredRuns，迟到的旧事件一律丢弃，
 *      避免上一个任务的收尾事件污染新一轮界面。
 */

const STEP_DEFS = [
  { key: 'plan', label: '理解需求 · 规划' },
  { key: 'code', label: '生成代码' },
  { key: 'selftest', label: '运行自测' },
  { key: 'repair', label: '自动修复' },
  { key: 'finalize', label: '保存版本' }
];

const gen = {
  runId: null,
  running: false,
  starting: false,      // gen.start / gen.modify 的 RPC 还在路上（runId 未知）
  cancelled: false,
  steps: [],
  logs: [],
  error: null,
  result: null,
  tokensIn: 0,
  tokensOut: 0,
  files: 0,
  mode: 'create',
  startTs: 0,           // 本次运行的起始时间戳，用于计时
  roundText: '',        // 例如「第 3/26 轮」（来自 step message）
  live: null            // 最近一次 delta：{ phase, chars, tool, text }
};

/**
 * 启动令牌：每次 start() 取一个；resetState()/取消会让令牌失效。
 * 这样"点了开始又立刻取消/复位"时，迟到的 gen.start 响应不会把已放弃的 run 认回来。
 */
let startToken = 0;

/** 上一次提交的描述：失败/取消后重进页面回填，成功后清空（只回填输入框，不影响"界面是新的一轮"） */
const lastPrompt = { create: '', modify: '' };

/** 已放弃的 runId（环形保留少量），用于丢弃迟到的旧事件 */
const ignoredRuns = [];

/** 计时/节流参数 */
const TICK_MS = 200;      // 计时器刷新间隔
const LIVE_MIN_MS = 150;  // delta 轻量渲染的最小间隔

let tickTimer = null;
let liveTimer = null;
let lastLiveAt = 0;

// 与后端工具名对齐（未知工具名统一退回"处理"，不把英文名抛给用户）
const TOOL_LABELS = {
  write_file: '写文件',
  read_file: '读文件',
  list_files: '浏览文件',
  set_app_info: '写应用信息',
  done: '收尾',
  finish: '收尾'
};

let target = null; // 修改模式：{ app, versions }
let refs = null;

on('gen.event', (event) => { handleEvent(event); });

// ---------------- 定时器（唯一入口，避免泄漏/叠加） ----------------

function clearTick() {
  if (tickTimer !== null) { clearInterval(tickTimer); tickTimer = null; }
}

function clearLive() {
  if (liveTimer !== null) { clearTimeout(liveTimer); liveTimer = null; }
}

function stopTimers() {
  clearTick();
  clearLive();
}

function ensureTick() {
  if (tickTimer !== null || !gen.running) return;
  tickTimer = setInterval(() => {
    // 任务已结束 / 已离开工作台 → 自愈停表，保证没有后台空转的定时器
    if (!gen.running || state.view !== 'builder' || !refs) { clearTick(); return; }
    paintStatus(); // 只改状态行文字，不重建 DOM
  }, TICK_MS);
}

/** delta 轻量渲染节流：≥150ms 一次，且同一时刻最多挂一个 setTimeout */
function scheduleLivePaint() {
  if (liveTimer !== null) return;
  if (state.view !== 'builder' || !gen.running) return; // 不在工作台就不排渲染
  const wait = lastLiveAt + LIVE_MIN_MS - Date.now();
  liveTimer = setTimeout(() => {
    liveTimer = null;
    lastLiveAt = Date.now();
    if (!gen.running || state.view !== 'builder' || !refs) return;
    paintStatus();
  }, wait > 0 ? wait : 0);
}

// ---------------- 展示格式化 ----------------

function elapsedText() {
  if (!gen.startTs) return '';
  const seconds = (Date.now() - gen.startTs) / 1000;
  return (seconds < 0 ? 0 : seconds).toFixed(1) + ' 秒';
}

function formatChars(chars) {
  const n = Number(chars) || 0;
  if (n < 1000) return String(n);
  if (n < 10000) return (n / 1000).toFixed(1) + 'k';
  if (n < 1000000) return Math.round(n / 1000) + 'k';
  return (n / 1000000).toFixed(1) + 'M';
}

function toolLabel(name) {
  if (!name) return '';
  const key = String(name).toLowerCase();
  return TOOL_LABELS[key] || '';
}

function fileNameIn(text) {
  if (!text) return '';
  const matched = /([A-Za-z0-9_.-]+\.(?:html?|css|js|mjs|json|md|svg|txt|webmanifest))\b/i.exec(String(text));
  return matched ? matched[1] : '';
}

function snippetOf(text) {
  if (!text) return '';
  const flat = String(text).replace(/\s+/g, ' ').trim();
  if (!flat) return '';
  return flat.length > 80 ? '…' + flat.slice(-79) : flat;
}

/** 运行中的"实时"一行（旧后端没有 delta 时返回等待态，不报错） */
function liveTextOf() {
  const live = gen.live;
  if (!live) return { main: '等待模型响应…', snippet: '' };

  const file = fileNameIn(live.text);
  const label = toolLabel(live.tool);
  let main;

  if (live.phase === 'tool' && label === '写文件' && file) main = '正在写 ' + file;
  else if (live.phase === 'tool') main = '正在' + (label || '处理') + '…';
  else if (file) main = '正在写 ' + file;
  else if (live.tool) main = '正在' + (label || '处理') + '…';
  else main = '正在思考…';

  if (live.chars > 0) main += ' · 已收到 ' + formatChars(live.chars) + ' 字';
  return { main: main, snippet: snippetOf(live.text) };
}

// ---------------- 状态 ----------------

function ignoreRun(runId) {
  if (!runId) return;
  if (ignoredRuns.indexOf(runId) >= 0) return;
  ignoredRuns.push(runId);
  if (ignoredRuns.length > 24) ignoredRuns.shift();
}

function isIgnoredRun(runId) {
  return !!runId && ignoredRuns.indexOf(runId) >= 0;
}

function modeKey() {
  return gen.mode === 'modify' ? 'modify' : 'create';
}

/** 完整复位（不动 lastPrompt） */
function resetState() {
  ignoreRun(gen.runId);
  stopTimers();
  startToken++; // 让还在路上的 gen.start 响应作废

  gen.runId = null;
  gen.running = false;
  gen.starting = false;
  gen.cancelled = false;
  gen.cancelRequested = false; // 残留的取消标记会静默杀掉下一轮开始（取消后立刻重开的场景）
  gen.steps = STEP_DEFS.map((s) => ({ key: s.key, label: s.label, status: 'waiting', message: '' }));
  gen.logs = [];
  gen.error = null;
  gen.result = null;
  gen.tokensIn = 0;
  gen.tokensOut = 0;
  gen.files = 0;
  gen.startTs = 0;
  gen.roundText = '';
  gen.live = null;
  gen.mode = target && target.app ? 'modify' : 'create';
}

/** 公开复位：给 app.js 在"已经停在 #/builder"时手动开一轮全新界面 */
export function resetBuilder() {
  // 有任务在跑时不能复位：后端同一时间只允许一个任务，
  // 复位会让界面变空、但任务还在跑，用户再点开始只会收到"已有任务在进行中"。
  if (gen.running || gen.starting) {
    toast('当前还有生成任务在进行中，先点「取消」再开新的', 'bad', 3600);
    return false;
  }
  resetState();
  if (refs) {
    refs.textarea.disabled = false;
    refs.textarea.value = lastPrompt[modeKey()] || '';
  }
  paint();
  return true;
}

/** 离开工作台：只停计时器，保留进度（切回来还能看到） */
export function pauseBuilder() {
  stopTimers();
}

/** 只读快照：给 --ui-diag / E2E 断言内部状态（复位是否干净、有没有遗留定时器） */
export function debugSnapshot() {
  return {
    runId: gen.runId,
    running: gen.running,
    starting: gen.starting,
    cancelled: gen.cancelled,
    mode: gen.mode,
    error: gen.error,
    result: gen.result ? Object.assign({}, gen.result) : null,
    logs: gen.logs.length,
    tokensIn: gen.tokensIn,
    tokensOut: gen.tokensOut,
    files: gen.files,
    roundText: gen.roundText,
    live: gen.live ? Object.assign({}, gen.live) : null,
    steps: gen.steps.map((step) => ({ key: step.key, status: step.status, message: step.message })),
    timers: { tick: tickTimer !== null, live: liveTimer !== null }
  };
}

resetState();

function stepOf(key) {
  return gen.steps.find((s) => s.key === key);
}

function handleEvent(event) {
  if (!event) return;
  const runId = event.runId || null;

  // 已放弃的 run（取消/复位前的旧任务）迟到事件一律丢弃
  if (isIgnoredRun(runId)) return;
  if (gen.runId && runId && runId !== gen.runId) return;
  // 新任务的事件可能比 gen.start 的 RPC 响应先到：只在"正在启动"阶段认领 runId；
  // 既不是当前 run 也不在启动中 → 属于更早的、已无人认领的 run，直接丢弃。
  if (!gen.runId && runId) {
    if (!gen.starting) return;
    gen.runId = runId;
  }

  if (event.kind === 'delta') {
    if (!gen.running) return;
    gen.live = {
      phase: event.phase === 'tool' ? 'tool' : 'content',
      chars: Number(event.chars) > 0 ? Number(event.chars) : 0,
      tool: event.tool || '',
      text: event.text || ''
    };
    scheduleLivePaint(); // 轻量刷新，绝不触发全量 paint()
    return;
  }

  if (event.kind === 'step') {
    const step = stepOf(event.step);
    if (step) {
      step.status = event.status || 'running';
      step.message = event.message || step.message;
      // 新阶段开始：上一阶段的"实时"行已经过期
      if (event.status === 'running') gen.live = null;
    }
    if (event.message) {
      const round = /第\s*(\d+)\s*\/\s*(\d+)\s*轮/.exec(event.message);
      if (round) gen.roundText = '第 ' + round[1] + '/' + round[2] + ' 轮';
    }
    if (event.step === 'code' && event.status === 'ok' && event.message) {
      const matched = /(\d+)\s*个文件/.exec(event.message);
      if (matched) gen.files = Number(matched[1]);
    }
  } else if (event.kind === 'log') {
    gen.logs.push({ time: Date.now(), message: event.message || '' });
    if (gen.logs.length > 200) gen.logs.splice(0, gen.logs.length - 200);
  } else if (event.kind === 'done') {
    gen.running = false;
    gen.starting = false;
    gen.cancelRequested = false;
    gen.cancelled = false;
    gen.live = null;
    gen.roundText = '';
    stopTimers();

    gen.result = {
      appId: event.appId,
      name: event.name,
      fileCount: event.fileCount,
      seq: event.seq,
      mode: event.mode || gen.mode,
      noChange: !!event.noChange,
      degraded: !!event.degraded,
      incomplete: !!event.incomplete,
      tokensIn: event.tokensIn,
      tokensOut: event.tokensOut
    };
    gen.tokensIn = event.tokensIn || 0;
    gen.tokensOut = event.tokensOut || 0;
    // 成功了就不用再回填描述（下次进来是真正空白的一轮）
    lastPrompt[modeKey()] = '';

    if (gen.result.noChange) {
      toast('没有需要修改的内容，已保持 v' + String(event.seq || '').padStart(4, '0'), 'ok', 4000);
    } else {
      toast((gen.result.mode === 'modify' ? '修改完成：' : '生成完成：') + (event.name || '应用'), 'ok', 3600);
    }

    refreshCurrent();
    if (event.appId) {
      call('launch.open', { id: event.appId })
        .then(() => refreshApps())
        .catch((err) => toast('已保存，但打开失败：' + err.message, 'bad', 5000));
    }
  } else if (event.kind === 'error') {
    gen.running = false;
    gen.starting = false;
    gen.cancelRequested = false;
    gen.cancelled = false;
    gen.live = null;
    gen.roundText = '';
    stopTimers();

    gen.error = event.message || '失败';
    toast(gen.error, 'bad', 6000);
    refreshCurrent();
  }

  if (state.view === 'builder') paint();
}

async function refreshCurrent() {
  try {
    await refreshApps();
    if (state.view === 'launcher') await renderCurrentView();
  } catch (e) { /* 忽略 */ }
}

export async function render(container) {
  const appId = state.routeQuery && state.routeQuery.app;

  // 修改模式：先取回应用信息
  target = null;
  if (appId) {
    try {
      target = await call('apps.get', { appId: appId });
    } catch (err) {
      toast('无法修改该应用：' + err.message, 'bad', 5000);
      location.hash = '#/launcher';
      return;
    }
    if (!container.isConnected) return; // 取数据期间用户已经切走
  }

  const isModify = !!(target && target.app);
  const appName = isModify ? target.app.name : '';
  const currentSeq = isModify ? target.app.currentVersion : 0;

  // 进入页面：没有运行中的任务就完整复位（有任务在跑则保留进度）
  if (!gen.running) resetState();

  // 失败/取消后重进：把上次的描述回填到输入框（只回填，界面仍是全新一轮）
  const promptKey = isModify ? 'modify' : 'create';
  const refill = lastPrompt[promptKey] || '';

  const wrap = h('div', { class: 'builder-wrap' });
  container.appendChild(wrap);

  wrap.appendChild(h('div', { class: 'page-head' },
    h('div', {},
      h('h2', { text: isModify ? '用 AI 修改：' + appName : '新建应用' }),
      h('p', {
        text: isModify
          ? '当前版本 v' + String(currentSeq).padStart(4, '0') + ' · 说一句要改什么，AI 只改相关文件，自测通过后存为新版本'
          : '描述你要的软件，AI 会现场写代码、自动运行自测，通过后保存到桌面'
      })
    ),
    h('button', {
      class: 'btn', text: '← 返回桌面',
      onClick: () => { location.hash = '#/launcher'; }
    })
  ));

  const hasKey = !!(state.settingsData && state.settingsData.hasApiKey);
  if (!hasKey) {
    wrap.appendChild(h('div', { class: 'notice' },
      '还没有配置 API Key，生成功能需要它。',
      h('button', {
        class: 'btn sm', style: { marginLeft: '10px' }, text: '去设置 →',
        onClick: () => { location.hash = '#/settings'; }
      })
    ));
  }

  const textarea = h('textarea', {
    class: 'builder-input',
    placeholder: isModify
      ? '例如：把字体调大一点、加一个按日期排序的按钮、支持删除后撤销…'
      : '例如：做一个待办清单，能勾选、能删除、能按优先级排序，数据存在本机，界面简洁一点…',
    maxlength: '2000'
  });
  if (refill) textarea.value = refill;
  textarea.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') start();
  });

  const startBtn = h('button', { class: 'btn primary', text: isModify ? '开始修改' : '开始生成' });
  startBtn.addEventListener('click', () => start());

  const cancelBtn = h('button', { class: 'btn', text: '取消' });
  cancelBtn.addEventListener('click', () => cancelRun());

  const actions = h('div', { class: 'actions' },
    startBtn,
    cancelBtn,
    h('span', { class: 'grow' }),
    h('span', { class: 'hint', text: 'Ctrl + Enter 提交 · ' + (isModify ? '修改一般 20~90 秒' : '生成一般 30~150 秒，复杂一点会久一些（能看到实时进度）') })
  );

  // 状态行 = 主文案 + 计时/轮次（分开一个 span，数字用等宽，避免每 200ms 抖动）
  const statusMainEl = h('span', { class: 'gen-main' });
  const statusTimerEl = h('span', { class: 'gen-timer' });
  const statusEl = h('div', { class: 'gen-status' }, statusMainEl, statusTimerEl);

  // 运行中的"实时"行（只有收到 delta 才出现；旧后端没有 delta 时保持隐藏）
  const liveMainEl = h('span', { class: 'gen-live-main' });
  const liveTextEl = h('span', { class: 'gen-live-text' });
  const liveEl = h('div', { class: 'gen-live' }, liveMainEl, liveTextEl);
  liveEl.style.display = 'none';

  const stepsEl = h('div', { class: 'steps' });
  const logsEl = h('div', { class: 'gen-logs' });
  const resultEl = h('div');

  wrap.appendChild(textarea);
  if (refill) {
    wrap.appendChild(h('div', {
      class: 'gen-refill',
      text: '已保留上次的描述，可直接「' + (isModify ? '开始修改' : '开始生成') + '」重试，或改一改再提交'
    }));
  }
  wrap.appendChild(actions);
  wrap.appendChild(statusEl);
  wrap.appendChild(liveEl);
  wrap.appendChild(stepsEl);
  wrap.appendChild(logsEl);
  wrap.appendChild(resultEl);

  refs = {
    textarea, startBtn, cancelBtn, stepsEl, logsEl, statusEl,
    statusMainEl, statusTimerEl, liveEl, liveMainEl, liveTextEl,
    resultEl, isModify, start
  };

  if (gen.running) ensureTick();
  paint();
  if (!gen.running) {
    setTimeout(() => {
      if (refs && refs.textarea === textarea && textarea.isConnected) textarea.focus();
    }, 50);
  }

  async function start() {
    if (gen.running) return;
    const prompt = (textarea.value || '').trim();
    if (!prompt) {
      toast(isModify ? '先说要改什么' : '先用一句话描述你要的应用', 'bad');
      textarea.focus();
      return;
    }

    resetState();
    gen.mode = isModify ? 'modify' : 'create';
    gen.running = true;
    gen.starting = true;
    gen.startTs = Date.now();
    gen.live = null;
    lastPrompt[isModify ? 'modify' : 'create'] = prompt;

    ensureTick();
    paint();

    try {
      const res = isModify
        ? await call('gen.modify', { appId: target.app.id, prompt: prompt }, 30000)
        : await call('gen.start', { prompt: prompt }, 30000);
      gen.starting = false;

      const runId = (res && res.runId) || null;
      if (gen.cancelRequested) {
        // 启动期间用户已经点了取消：runId 一回来就立刻取消，并丢弃它的后续事件
        gen.cancelRequested = false;
        if (runId) {
          ignoreRun(runId);
          call('gen.cancel', { runId: runId }).catch(() => { /* 忽略 */ });
        }
        return;
      }

      if (runId) gen.runId = runId;
      paint();
    } catch (err) {
      gen.starting = false;
      if (gen.cancelRequested) { gen.cancelRequested = false; return; }
      gen.running = false;
      gen.cancelled = false;
      gen.live = null;
      gen.roundText = '';
      stopTimers();
      gen.error = err.message;
      paint();
      toast(err.message, 'bad', 6000);
    }
  }
}

/** 取消：立刻回到可再操作状态，不等后端事件；旧 run 的迟到事件全部丢弃 */
function cancelRun() {
  if (!gen.running) return;

  gen.running = false;
  gen.cancelled = true;
  gen.error = null;
  gen.live = null;
  gen.roundText = '';
  stopTimers();
  // 正在跑的步骤回到等待，避免取消后还显示"进行中"
  gen.steps.forEach((step) => { if (step.status === 'running') step.status = 'waiting'; });

  const runId = gen.runId;
  if (runId) {
    ignoreRun(runId);
    gen.runId = null;
    call('gen.cancel', { runId: runId }).catch(() => { /* 后端已结束也无妨 */ });
  } else {
    gen.cancelRequested = true; // runId 还没回来，等 RPC 返回后补取消
  }

  paint();
  toast('已取消本次生成', 'ok', 2600);
}

/** 换个说法：保留原来的描述（选中全选，方便直接改写） */
function reword() {
  if (!refs) return;
  const textarea = refs.textarea;
  textarea.disabled = false;
  if (!textarea.value) textarea.value = lastPrompt[modeKey()] || '';
  textarea.focus();
  try { textarea.setSelectionRange(0, textarea.value.length); } catch (e) { /* 忽略 */ }
  toast('改一改描述，再点「' + (refs.isModify ? '开始修改' : '开始生成') + '」', 'ok', 3200);
}

// ---------------- 渲染 ----------------

function paint() {
  if (!refs || state.view !== 'builder') return;
  const { startBtn, cancelBtn, textarea, isModify } = refs;

  startBtn.disabled = gen.running;
  startBtn.textContent = gen.running ? '处理中…' : (isModify ? '开始修改' : '开始生成');
  cancelBtn.style.display = gen.running ? '' : 'none';
  cancelBtn.disabled = !!gen.cancelRequested;
  textarea.disabled = gen.running;

  paintStatus();
  paintSteps();
  paintLogs();
  paintResult();
}

/** 轻量刷新：只更新状态行 + 实时行（计时器与 delta 走这里） */
function paintStatus() {
  if (!refs || state.view !== 'builder') return;
  const { statusEl, statusMainEl, statusTimerEl, liveEl, liveMainEl, liveTextEl } = refs;

  let cls = '';
  let main = '';
  let timer = '';

  if (gen.running) {
    cls = 'running';
    main = gen.mode === 'modify' ? '正在修改…' : '正在生成…';
    const elapsed = elapsedText();
    timer = ' · ' + elapsed + (gen.roundText ? ' · ' + gen.roundText : '');
  } else if (gen.error) {
    cls = 'bad';
    main = '失败：' + gen.error;
  } else if (gen.cancelled) {
    main = '已取消，可以重新开始';
  } else if (gen.result) {
    cls = 'ok';
    main = gen.result.noChange ? '没有改动' : '完成，已保存';
  }

  statusEl.className = 'gen-status' + (cls ? ' ' + cls : '');
  statusMainEl.textContent = main;
  statusTimerEl.textContent = timer;
  statusTimerEl.style.display = timer ? '' : 'none';

  // 实时行只在运行中显示；旧后端不发 delta 时这里一直是隐藏的
  const live = gen.running ? liveTextOf() : null;
  if (live && live.main) {
    liveEl.style.display = '';
    liveMainEl.textContent = live.main;
    liveTextEl.textContent = live.snippet;
    liveTextEl.style.display = live.snippet ? '' : 'none';
  } else {
    liveEl.style.display = 'none';
    liveMainEl.textContent = '';
    liveTextEl.textContent = '';
  }
}

function paintSteps() {
  const stepsEl = refs.stepsEl;
  clear(stepsEl);
  gen.steps.forEach((step) => {
    const badge = step.status === 'ok' ? '✓' : step.status === 'fail' ? '✕' : step.status === 'running' ? '…' : '·';
    const cls = step.status === 'ok' ? 'ok' : step.status === 'fail' ? 'fail' : step.status === 'running' ? 'running' : '';
    stepsEl.appendChild(h('div', { class: 'step ' + cls, title: step.message || '' },
      h('span', { class: 'dot ' + cls, text: badge }),
      h('span', { text: step.label }),
      step.message ? h('span', { class: 'step-msg', text: (step.message.length > 42 ? step.message.slice(0, 42) + '…' : step.message) }) : null
    ));
  });
}

function paintLogs() {
  const logsEl = refs.logsEl;
  clear(logsEl);
  gen.logs.slice(-40).forEach((line) => {
    logsEl.appendChild(h('div', { class: 'gen-log-line', text: '· ' + line.message }));
  });
  logsEl.style.display = gen.logs.length ? 'block' : 'none';
  if (gen.logs.length) logsEl.scrollTop = logsEl.scrollHeight;
}

function paintResult() {
  const resultEl = refs.resultEl;
  clear(resultEl);

  if (gen.result) {
    const seqText = gen.result.seq ? 'v' + String(gen.result.seq).padStart(4, '0') : '';
    resultEl.appendChild(h('div', { class: 'panel', style: { marginTop: '16px' } },
      h('h3', {}, (gen.result.noChange ? 'ℹ️ ' : '✅ ') + (gen.result.name || '应用') +
        (gen.result.noChange ? ' 无改动' : ' 已' + (gen.result.mode === 'modify' ? '更新' : '生成') + (seqText ? '到 ' + seqText : ''))),
      h('p', { class: 'hint', text: '文件 ' + (gen.result.fileCount || gen.files) + ' 个 · tokens 输入 ' + gen.tokensIn + ' / 输出 ' + gen.tokensOut }),
      gen.result.degraded
        ? h('p', { class: 'hint', text: '⚠️ 自测运行环境不可用，本次只做了静态校验（入口文件存在、结构完整），没能真正跑一遍页面。建议先打开确认。' })
        : null,
      gen.result.incomplete
        ? h('p', { class: 'hint', text: '⚠️ 生成在轮次上限处收尾，可能还有没写完的部分。可以点「继续修改」补一句你要的细节。' })
        : null,
      h('div', { class: 'actions' },
        h('button', {
          class: 'btn primary', text: '打开应用',
          onClick: () => call('launch.open', { id: gen.result.appId }).catch((e) => toast(e.message, 'bad'))
        }),
        h('button', { class: 'btn', text: '查看版本历史', onClick: () => { location.hash = '#/launcher'; toast('在应用卡片右上角 ⋯ 里可以回滚版本', 'ok', 4200); } }),
        h('button', {
          class: 'btn ghost', text: gen.result.mode === 'modify' ? '继续修改' : '再做一个',
          onClick: () => {
            resetState();
            if (refs) {
              refs.textarea.value = '';
              refs.textarea.disabled = false;
              paint();
              refs.textarea.focus();
            }
          }
        })
      )
    ));
  } else if (gen.error) {
    resultEl.appendChild(h('div', { class: 'panel', style: { marginTop: '16px' } },
      h('h3', {}, '❌ 失败'),
      h('p', { class: 'hint', text: gen.error }),
      h('div', { class: 'actions' },
        h('button', { class: 'btn primary', text: '重新生成', onClick: () => { if (refs) refs.start(); } }),
        h('button', { class: 'btn', text: '换个说法', onClick: () => reword() }),
        h('button', { class: 'btn ghost', text: '打开数据目录', onClick: () => openFolder('app.openDataDir') })
      ),
      h('p', { class: 'hint', text: '描述还在上面的输入框里，重试会沿用同一个需求。' })
    ));
  } else if (gen.cancelled) {
    resultEl.appendChild(h('div', { class: 'panel', style: { marginTop: '16px' } },
      h('h3', {}, '⏹ 已取消'),
      h('p', { class: 'hint', text: '这次生成已经停掉，后端迟到的消息不会再生效。描述还在上面的输入框里，可以直接重新开始。' }),
      h('div', { class: 'actions' },
        h('button', { class: 'btn primary', text: '重新生成', onClick: () => { if (refs) refs.start(); } }),
        h('button', { class: 'btn', text: '换个说法', onClick: () => reword() })
      )
    ));
  }
}
