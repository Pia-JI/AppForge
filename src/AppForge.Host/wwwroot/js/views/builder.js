import { call, on } from '../rpc.js';
import { h, clear, toast } from '../dom.js';
import { state, refreshApps, render as renderCurrentView } from '../app.js';

/**
 * 生成工作台：
 *   新建：输入需求 → gen.start
 *   修改：从桌面进入（#/builder?app=<id>）→ gen.modify，基于现有代码做增量修改
 * 进度通过 gen.event 实时推送。
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
  steps: [],
  logs: [],
  error: null,
  result: null,
  tokensIn: 0,
  tokensOut: 0,
  files: 0,
  mode: 'create'
};

let target = null; // 修改模式：{ app, versions }
let refs = null;

on('gen.event', (event) => { handleEvent(event); });

function reset() {
  gen.runId = null;
  gen.running = false;
  gen.steps = STEP_DEFS.map((s) => ({ key: s.key, label: s.label, status: 'waiting', message: '' }));
  gen.logs = [];
  gen.error = null;
  gen.result = null;
  gen.tokensIn = 0;
  gen.tokensOut = 0;
  gen.files = 0;
  gen.mode = target && target.app ? 'modify' : 'create';
}

reset();

function stepOf(key) {
  return gen.steps.find((s) => s.key === key);
}

function handleEvent(event) {
  if (!event) return;
  if (!gen.runId && event.runId) gen.runId = event.runId;
  if (gen.runId && event.runId && event.runId !== gen.runId) return;

  if (event.kind === 'step') {
    const step = stepOf(event.step);
    if (step) {
      step.status = event.status || 'running';
      step.message = event.message || step.message;
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
    gen.result = {
      appId: event.appId,
      name: event.name,
      fileCount: event.fileCount,
      seq: event.seq,
      mode: event.mode || gen.mode,
      noChange: !!event.noChange,
      tokensIn: event.tokensIn,
      tokensOut: event.tokensOut
    };
    gen.tokensIn = event.tokensIn || 0;
    gen.tokensOut = event.tokensOut || 0;

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
  }

  if (!gen.steps.length) reset();
  if (!gen.running && !gen.result && !gen.error) reset();

  const isModify = !!(target && target.app);
  const appName = isModify ? target.app.name : '';
  const currentSeq = isModify ? target.app.currentVersion : 0;

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
  textarea.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') start();
  });

  const startBtn = h('button', { class: 'btn primary', text: isModify ? '开始修改' : '开始生成' });
  startBtn.addEventListener('click', () => start());

  const cancelBtn = h('button', { class: 'btn', text: '取消' });
  cancelBtn.addEventListener('click', async () => {
    if (!gen.runId) return;
    try { await call('gen.cancel', { runId: gen.runId }); } catch (e) { /* 忽略 */ }
  });

  const actions = h('div', { class: 'actions' },
    startBtn,
    cancelBtn,
    h('span', { class: 'grow' }),
    h('span', { class: 'hint', text: 'Ctrl + Enter 提交 · ' + (isModify ? '修改通常 20~90 秒' : '生成通常 30~120 秒') })
  );

  const stepsEl = h('div', { class: 'steps' });
  const logsEl = h('div', { class: 'gen-logs' });
  const statusEl = h('div', { class: 'gen-status' });
  const resultEl = h('div');

  wrap.appendChild(textarea);
  wrap.appendChild(actions);
  wrap.appendChild(statusEl);
  wrap.appendChild(stepsEl);
  wrap.appendChild(logsEl);
  wrap.appendChild(resultEl);

  refs = { textarea, startBtn, cancelBtn, stepsEl, logsEl, statusEl, resultEl, isModify };
  paint();
  if (!gen.running) setTimeout(() => textarea.focus(), 50);

  async function start() {
    if (gen.running) return;
    const prompt = (textarea.value || '').trim();
    if (!prompt) {
      toast(isModify ? '先说要改什么' : '先用一句话描述你要的应用', 'bad');
      textarea.focus();
      return;
    }

    reset();
    gen.mode = isModify ? 'modify' : 'create';
    gen.running = true;
    paint();

    try {
      const res = isModify
        ? await call('gen.modify', { appId: target.app.id, prompt: prompt }, 30000)
        : await call('gen.start', { prompt: prompt }, 30000);
      gen.runId = res.runId;
      paint();
    } catch (err) {
      gen.running = false;
      gen.error = err.message;
      paint();
      toast(err.message, 'bad', 6000);
    }
  }
}

function paint() {
  if (!refs || state.view !== 'builder') return;
  const { startBtn, cancelBtn, stepsEl, logsEl, statusEl, resultEl, textarea, isModify } = refs;

  startBtn.disabled = gen.running;
  startBtn.textContent = gen.running ? '处理中…' : (isModify ? '开始修改' : '开始生成');
  cancelBtn.style.display = gen.running ? '' : 'none';
  textarea.disabled = gen.running;

  if (gen.running) {
    statusEl.className = 'gen-status running';
    statusEl.textContent = (isModify ? '正在修改…' : '正在生成…') + '（模型思考 + 自动自测，请稍候）';
  } else if (gen.error) {
    statusEl.className = 'gen-status bad';
    statusEl.textContent = '失败：' + gen.error;
  } else if (gen.result) {
    statusEl.className = 'gen-status ok';
    statusEl.textContent = gen.result.noChange ? '没有改动' : '完成，已保存';
  } else {
    statusEl.className = 'gen-status';
    statusEl.textContent = '';
  }

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

  clear(logsEl);
  gen.logs.slice(-40).forEach((line) => {
    logsEl.appendChild(h('div', { class: 'gen-log-line', text: '· ' + line.message }));
  });
  logsEl.style.display = gen.logs.length ? 'block' : 'none';
  if (gen.logs.length) logsEl.scrollTop = logsEl.scrollHeight;

  clear(resultEl);
  if (gen.result) {
    const seqText = gen.result.seq ? 'v' + String(gen.result.seq).padStart(4, '0') : '';
    resultEl.appendChild(h('div', { class: 'panel', style: { marginTop: '16px' } },
      h('h3', {}, (gen.result.noChange ? 'ℹ️ ' : '✅ ') + (gen.result.name || '应用') +
        (gen.result.noChange ? ' 无改动' : ' 已' + (gen.result.mode === 'modify' ? '更新' : '生成') + (seqText ? '到 ' + seqText : ''))),
      h('p', { class: 'hint', text: '文件 ' + (gen.result.fileCount || gen.files) + ' 个 · tokens 输入 ' + gen.tokensIn + ' / 输出 ' + gen.tokensOut }),
      h('div', { class: 'actions' },
        h('button', {
          class: 'btn primary', text: '打开应用',
          onClick: () => call('launch.open', { id: gen.result.appId }).catch((e) => toast(e.message, 'bad'))
        }),
        h('button', { class: 'btn', text: '查看版本历史', onClick: () => { location.hash = '#/launcher'; toast('在应用卡片右上角 ⋯ 里可以回滚版本', 'ok', 4200); } }),
        h('button', {
          class: 'btn ghost', text: gen.result.mode === 'modify' ? '继续修改' : '再做一个',
          onClick: () => {
            reset();
            if (refs) refs.textarea.value = '';
            paint();
            if (refs) refs.textarea.focus();
          }
        })
      )
    ));
  } else if (gen.error) {
    resultEl.appendChild(h('div', { class: 'panel', style: { marginTop: '16px' } },
      h('h3', {}, '失败'),
      h('p', { class: 'hint', text: gen.error }),
      h('div', { class: 'actions' },
        h('button', {
          class: 'btn', text: '重试',
          onClick: () => {
            const prompt = (refs.textarea.value || '').trim();
            reset();
            gen.running = true;
            paint();
            const promise = refs.isModify && target && target.app
              ? call('gen.modify', { appId: target.app.id, prompt: prompt }, 30000)
              : call('gen.start', { prompt: prompt }, 30000);
            promise.then((res) => { gen.runId = res.runId; paint(); })
              .catch((err) => { gen.running = false; gen.error = err.message; paint(); });
          }
        }),
        h('button', { class: 'btn ghost', text: '打开数据目录', onClick: () => call('app.openDataDir').catch(() => { }) })
      )
    ));
  }
}
