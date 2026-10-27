import { call } from '../rpc.js';
import { h, clear, toast, openFolder, folderDialog, formatBytes } from '../dom.js';
import { state } from '../app.js';

/** 设置页：模型地址 / API Key / 模型 / 生成参数 / 依赖状态 / 数据目录 / 关于 */
export async function render(container) {
  const data = state.settingsData || await call('settings.get');
  const settings = data.settings;
  state.settingsData = data;
  state.deps = data.deps || [];

  container.appendChild(h('div', { class: 'page-head' },
    h('div', {},
      h('h2', { text: '设置' }),
      h('p', { text: '所有数据保存在本机；API Key 使用 Windows DPAPI 加密存储，不会明文落盘' })
    ),
    h('button', { class: 'btn', text: '← 返回桌面', onClick: () => { location.hash = '#/launcher'; } })
  ));

  let trash = { items: [], count: 0 };
  let diag = {};
  try { trash = await call('trash.list'); } catch (e) { /* 忽略 */ }
  try { diag = await call('diag.info'); } catch (e) { /* 忽略 */ }

  container.appendChild(modelPanel(settings, data));
  container.appendChild(generatePanel(settings));
  container.appendChild(systemPanel(settings));
  container.appendChild(dependencyPanel());
  container.appendChild(trashPanel(trash));
  container.appendChild(diagPanel(diag));
  container.appendChild(dataPanel());
  container.appendChild(aboutPanel());
}

// ---------------- 模型连接 ----------------

function modelPanel(settings, data) {
  const baseUrl = h('input', {
    class: 'input', id: 'set-base-url', type: 'text',
    value: settings.baseUrl, spellcheck: 'false', placeholder: 'https://api.example.com/v1'
  });

  const keyInput = h('input', {
    class: 'input', id: 'set-api-key', type: 'password',
    placeholder: data.hasApiKey ? '已保存：' + data.maskedKey + '（输入新值可替换）' : '粘贴你的 API Key（sk-…）',
    autocomplete: 'off', spellcheck: 'false'
  });

  const keyState = h('span', {
    class: 'badge ' + (data.hasApiKey ? 'ok' : 'warn'),
    text: data.hasApiKey ? '已配置 ' + data.maskedKey : '未配置'
  });

  const toggleKey = h('button', {
    class: 'btn', text: '👁', title: '显示 / 隐藏',
    onClick: () => {
      keyInput.type = keyInput.type === 'password' ? 'text' : 'password';
    }
  });

  const saveKeyBtn = h('button', {
    class: 'btn', text: '保存密钥',
    onClick: async () => {
      const value = keyInput.value.trim();
      if (!value) { toast('请先粘贴 API Key', 'bad'); return; }

      saveKeyBtn.disabled = true;
      try {
        const res = await call('settings.setApiKey', { apiKey: value });
        data.hasApiKey = res.hasApiKey;
        data.maskedKey = res.maskedKey;
        keyInput.value = '';
        keyInput.placeholder = '已保存：' + res.maskedKey + '（输入新值可替换）';
        keyState.className = 'badge ok';
        keyState.textContent = '已配置 ' + res.maskedKey;
        state.settingsData = data;
        toast('API Key 已加密保存（仅本机可解密）', 'ok');
      } catch (err) {
        toast(err.message, 'bad', 5000);
      } finally {
        saveKeyBtn.disabled = false;
      }
    }
  });

  const clearKeyBtn = h('button', {
    class: 'btn ghost', text: '清除',
    onClick: async () => {
      try {
        const res = await call('settings.setApiKey', { apiKey: '' });
        data.hasApiKey = res.hasApiKey;
        data.maskedKey = res.maskedKey;
        keyState.className = 'badge warn';
        keyState.textContent = '未配置';
        keyInput.placeholder = '粘贴你的 API Key（sk-…）';
        state.settingsData = data;
        toast('已清除 API Key', 'ok');
      } catch (err) {
        toast(err.message, 'bad');
      }
    }
  });

  // 模型：原生 <select>（点开即出列表，Chromium 里比 datalist 可靠）+ 可手填输入框
  const modelInput = h('input', {
    class: 'input', id: 'set-model', type: 'text',
    value: settings.model, spellcheck: 'false', placeholder: 'gpt-4o-mini'
  });
  const modelSelect = h('select', { class: 'input', id: 'set-model-select', title: '从已拉取的模型中选择' });
  const MODEL_CACHE_KEY = 'af.models';

  function readModelCache() {
    try {
      const list = JSON.parse(localStorage.getItem(MODEL_CACHE_KEY) || '[]');
      return Array.isArray(list) ? list.filter((x) => typeof x === 'string' && x) : [];
    } catch (e) {
      return [];
    }
  }

  function writeModelCache(models) {
    try { localStorage.setItem(MODEL_CACHE_KEY, JSON.stringify(models || [])); } catch (e) { }
  }

  function fillModelSelect(models) {
    clear(modelSelect);
    if (!models || !models.length) {
      modelSelect.appendChild(h('option', { value: '', text: '（点右侧「拉取模型列表」）' }));
      modelSelect.disabled = true;
      return;
    }

    modelSelect.disabled = false;
    models.forEach((id) => modelSelect.appendChild(h('option', { value: id, text: id })));

    const current = (modelInput.value || '').trim();
    if (current && models.indexOf(current) < 0) {
      modelSelect.appendChild(h('option', { value: current, text: current + '（自定义）' }));
    }
    modelSelect.value = current || models[0];
  }

  fillModelSelect(readModelCache());

  modelSelect.addEventListener('change', () => {
    if (modelSelect.value) modelInput.value = modelSelect.value;
  });

  modelInput.addEventListener('input', () => {
    if (modelSelect.disabled || modelSelect.options.length === 0) return;
    const current = (modelInput.value || '').trim();
    const matched = Array.prototype.some.call(modelSelect.options, (option) => option.value === current);
    modelSelect.value = matched ? current : '';
  });

  const fetchModelsBtn = h('button', { class: 'btn', text: '拉取模型列表' });

  async function refreshModels(showToast) {
    fetchModelsBtn.disabled = true;
    fetchModelsBtn.textContent = '拉取中…';
    try {
      const res = await call('settings.listModels', {}, 60000);
      const models = res.models || [];
      writeModelCache(models);
      fillModelSelect(models);
      if (showToast) toast('已拉取 ' + models.length + ' 个模型，可点下拉框选择', 'ok', 3200);
      return models;
    } catch (err) {
      if (showToast) toast(err.message, 'bad', 6000);
      fillModelSelect(readModelCache());
      return null;
    } finally {
      fetchModelsBtn.disabled = false;
      fetchModelsBtn.textContent = '拉取模型列表';
    }
  }

  fetchModelsBtn.addEventListener('click', () => refreshModels(true));

  // 首次进入且无缓存、又已配置 Key：后台自动拉一次，让下拉框直接可用
  if (!readModelCache().length && data.hasApiKey) {
    setTimeout(() => { refreshModels(false); }, 300);
  }

  const temperature = h('input', {
    class: 'input', id: 'set-temperature', type: 'number',
    min: '0', max: '2', step: '0.1', value: settings.temperature
  });
  const maxTokens = h('input', {
    class: 'input', id: 'set-max-tokens', type: 'number',
    min: '256', max: '128000', step: '256', value: settings.maxTokens
  });
  const timeout = h('input', {
    class: 'input', id: 'set-timeout', type: 'number',
    min: '10', max: '600', step: '10', value: settings.timeoutSeconds
  });

  const result = h('div', { class: 'result', id: 'test-result', style: { display: 'none' } });

  const saveBtn = h('button', { class: 'btn primary', text: '保存设置' });
  const testBtn = h('button', { class: 'btn', text: '测试连接' });

  const collect = () => ({
    baseUrl: baseUrl.value.trim(),
    model: modelInput.value.trim(),
    temperature: Number(temperature.value),
    maxTokens: Number(maxTokens.value),
    timeoutSeconds: Number(timeout.value)
  });

  saveBtn.addEventListener('click', async () => {
    saveBtn.disabled = true;
    try {
      const res = await call('settings.set', { patch: collect() });
      data.settings = res.settings;
      state.settingsData = data;
      toast('设置已保存', 'ok');
    } catch (err) {
      toast(err.message, 'bad', 6000);
    } finally {
      saveBtn.disabled = false;
    }
  });

  testBtn.addEventListener('click', async () => {
    testBtn.disabled = true;
    testBtn.textContent = '测试中…';
    result.style.display = 'block';
    result.className = 'result';
    result.textContent = '正在连接模型…';

    try {
      // 先保存当前表单，避免"测的不是刚改的配置"
      const saved = await call('settings.set', { patch: collect() });
      data.settings = saved.settings;
      state.settingsData = data;

      const res = await call('settings.testModel', {}, 240000);
      if (res.ok) {
        result.className = 'result ok';
        result.textContent =
          '连接成功 · ' + res.latencyMs + 'ms · 模型 ' + res.model + '\n' +
          '回复：' + (res.sample || '(空)') + '\n' +
          'tokens：输入 ' + res.promptTokens + ' / 输出 ' + res.completionTokens;
        toast('模型连接正常', 'ok');
      } else {
        result.className = 'result bad';
        result.textContent = '连接失败：' + (res.error || '未知错误');
      }
    } catch (err) {
      result.className = 'result bad';
      result.textContent = '连接失败：' + err.message;
    } finally {
      testBtn.disabled = false;
      testBtn.textContent = '测试连接';
    }
  });

  const actions = h('div', { class: 'actions' }, testBtn, saveBtn, h('span', { class: 'grow' }),
    h('span', { class: 'hint', text: '测试会先保存当前表单再发起请求' }));

  return h('section', { class: 'panel' },
    h('h3', {}, '🔑 模型连接'),
    h('p', { class: 'hint', text: '默认已填好你的网关地址；API Key 只在保存时写入本机加密文件，界面上永远不回显明文。' }),

    h('div', { class: 'field' },
      h('label', { text: 'API 地址（Base URL）' }),
      baseUrl,
      h('div', { class: 'sub', text: '需包含版本路径，例如 https://api.example.com/v1；程序会自动补 /chat/completions 与 /models' })
    ),

    h('div', { class: 'field' },
      h('label', { text: 'API Key' }),
      h('div', { class: 'inline' }, keyInput, toggleKey, saveKeyBtn, clearKeyBtn, keyState),
      h('div', { class: 'sub', text: '加密方式：Windows DPAPI（仅当前用户可解密）；保存在数据目录的 secrets.dat' })
    ),

    h('div', { class: 'field' },
      h('label', { text: '模型' }),
      h('div', { class: 'inline', style: { flexWrap: 'nowrap' } },
        h('div', { style: { flex: '0 0 240px' } }, modelSelect),
        modelInput,
        fetchModelsBtn
      ),
      h('div', { class: 'sub', text: '下拉框来自网关的 /models 接口（已缓存到本机，随时可点开选择）；选择后点「保存设置」生效，也可直接手动输入模型名。推荐 gpt-4o-mini（支持 function calling，适合生成软件）。' })
    ),

    h('div', { class: 'row' },
      h('div', { class: 'field' }, h('label', { text: '温度（0~2）' }), temperature),
      h('div', { class: 'field' }, h('label', { text: '最大输出 tokens' }), maxTokens),
      h('div', { class: 'field' }, h('label', { text: '请求超时（秒）' }), timeout)
    ),

    actions,
    result
  );
}

// ---------------- 生成与版本 ----------------

function generatePanel(settings) {
  const keepVersions = h('input', {
    class: 'input', id: 'set-keep-versions', type: 'number',
    min: '1', max: '100', step: '1', value: settings.keepVersions
  });

  const saveBtn = h('button', {
    class: 'btn', text: '保存',
    onClick: async () => {
      saveBtn.disabled = true;
      try {
        const res = await call('settings.set', { patch: { keepVersions: Number(keepVersions.value) } });
        settings.keepVersions = res.settings.keepVersions;
        toast('已保存', 'ok');
      } catch (err) {
        toast(err.message, 'bad');
      } finally {
        saveBtn.disabled = false;
      }
    }
  });

  return h('section', { class: 'panel' },
    h('h3', {}, '🧱 生成与版本'),
    h('p', { class: 'hint', text: '每个应用每次"跑通"都会自动打一个版本快照，可随时回滚。' }),
    h('div', { class: 'row' },
      h('div', { class: 'field' },
        h('label', { text: '每个应用保留的版本数' }),
        h('div', { class: 'inline' }, keepVersions, saveBtn),
        h('div', { class: 'sub', text: '超出的旧版本自动清理（标记保留的除外）' })
      )
    )
  );
}

// ---------------- 依赖状态 ----------------

function dependencyPanel() {
  const list = h('div', { class: 'dep-list' });

  const paint = (deps) => {
    clear(list);
    deps.forEach((dep) => {
      const ok = dep.state === 'ok';
      list.appendChild(h('div', { class: 'dep-item' },
        h('div', {},
          h('div', { class: 'dep-name', text: dep.displayName }),
          h('div', { class: 'dep-ver', text: dep.version || '' })
        ),
        h('div', { class: 'inline' },
          h('span', { class: 'badge ' + (ok ? 'ok' : 'bad'), text: ok ? '正常' : (dep.state === 'missing' ? '缺失' : '不支持') }),
          ok ? null : h('span', { class: 'dep-ver', text: dep.message || '' })
        )
      ));
    });
  };

  paint(state.deps || []);

  const refresh = h('button', {
    class: 'btn', text: '重新检测',
    onClick: async () => {
      refresh.disabled = true;
      try {
        const res = await call('dep.status');
        state.deps = res.deps || [];
        paint(state.deps);
        toast('检测完成', 'ok');
      } catch (err) {
        toast(err.message, 'bad');
      } finally {
        refresh.disabled = false;
      }
    }
  });

  return h('section', { class: 'panel' },
    h('h3', {}, '🧩 依赖状态'),
    h('p', { class: 'hint', text: '本软件只用 Windows 自带组件。唯一例外是 WebView2 运行时：Win11 自带，Win10 多数随 Edge 预装；缺失时首次启动会自动静默安装。' }),
    list,
    h('div', { class: 'actions' }, refresh)
  );
}

// ---------------- 数据目录 ----------------

function dataPanel() {
  const info = state.info || {};
  const dirInput = h('input', { class: 'input', type: 'text', readonly: 'readonly', value: info.dataDir || '' });

  return h('section', { class: 'panel' },
    h('h3', {}, '🗂 数据目录'),
    h('p', { class: 'hint', text: '应用源码、版本快照、对话记录都在这里；可直接复制/备份/迁移。' }),
    h('div', { class: 'field' }, dirInput),
    h('div', { class: 'actions' },
      h('button', { class: 'btn', text: '打开数据目录', onClick: () => openFolder('app.openDataDir') }),
      h('button', { class: 'btn ghost', text: '在应用内浏览', onClick: () => folderDialog(info.dataDir || '') })
    )
  );
}

// ---------------- 关于 ----------------

function aboutPanel() {
  const info = state.info || {};
  const line = (label, value) => h('div', { class: 'about-line' }, h('b', { text: label }), h('span', { text: value || '-' }));

  return h('section', { class: 'panel' },
    h('h3', {}, 'ℹ️ 关于'),
    line('产品', info.product),
    line('版本', info.version),
    line('操作系统', info.os),
    line('WebView2', info.webview2),
    line('.NET 运行时', 'Windows 自带（Release ' + (info.dotnet || '-') + '）'),
    line('账户体系', '无 · 纯本地单用户，不联网同步'),
    h('p', { class: 'hint', text: '快捷键：Ctrl+Alt+Space 唤出 / 隐藏造物台；应用运行时按 Esc 可回到桌面（后续版本）。' })
  );
}

// ---------------- 系统集成 ----------------

function systemPanel(settings) {
  const autoStart = h('input', { type: 'checkbox', id: 'set-auto-start' });
  autoStart.checked = !!settings.autoStart;
  const hotkey = h('input', { type: 'checkbox', id: 'set-hotkey' });
  hotkey.checked = settings.hotkeyEnabled !== false;

  const save = h('button', { class: 'btn primary', text: '保存系统设置' });
  save.addEventListener('click', async () => {
    save.disabled = true;
    try {
      const res = await call('settings.set', { patch: { autoStart: autoStart.checked, hotkeyEnabled: hotkey.checked } });
      const warnings = res.warnings || [];
      if (warnings.length) toast(warnings.join('；'), 'bad', 7000);
      else toast('系统设置已保存', 'ok');

      settings.autoStart = autoStart.checked;
      settings.hotkeyEnabled = hotkey.checked;
      if (state.settingsData && state.settingsData.settings) {
        state.settingsData.settings.autoStart = settings.autoStart;
        state.settingsData.settings.hotkeyEnabled = settings.hotkeyEnabled;
      }
    } catch (err) {
      toast(err.message, 'bad', 6000);
    } finally {
      save.disabled = false;
    }
  });

  return h('section', { class: 'panel' },
    h('h3', {}, '🖥 系统集成'),
    h('p', { class: 'hint', text: '都写入当前用户级设置，不需要管理员权限，可随时关闭。' }),
    checkRow(autoStart, '开机自动启动', '开机后自动打开 AppForge（写入当前用户注册表 Run 项）'),
    checkRow(hotkey, '全局快捷键 Ctrl + Alt + Space', '任何时候唤出 / 隐藏造物台；被其他程序占用时会提示失败'),
    h('div', { class: 'actions' }, save)
  );
}

function checkRow(input, title, desc) {
  return h('label', { class: 'check-row' },
    input,
    h('div', {},
      h('div', { class: 'check-title', text: title }),
      h('div', { class: 'sub', text: desc })
    )
  );
}

// ---------------- 回收站 ----------------

function formatDate(ts) {
  if (!ts) return '未知时间';
  const d = new Date(ts);
  const pad = (n) => (n < 10 ? '0' + n : '' + n);
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
    ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
}

function trashPanel(trash) {
  const items = (trash && trash.items) || [];
  const list = h('div', { class: 'dep-list' });

  if (!items.length) {
    list.appendChild(h('div', { class: 'hint', text: '回收站是空的（删除的应用会先放这里，7 天后清理）' }));
  } else {
    items.forEach((item) => {
      const restoreBtn = h('button', { class: 'btn sm primary', text: '恢复' });
      restoreBtn.addEventListener('click', async () => {
        restoreBtn.disabled = true;
        try {
          await call('trash.restore', { appId: item.appId });
          toast('已恢复「' + (item.name || item.appId) + '」', 'ok');
          await refreshApps();
          await renderCurrentView();
        } catch (err) {
          toast('恢复失败：' + err.message, 'bad', 6000);
          restoreBtn.disabled = false;
        }
      });

      list.appendChild(h('div', { class: 'dep-item' },
        h('div', {},
          h('div', { class: 'dep-name', text: item.name || item.appId }),
          h('div', { class: 'dep-ver', text: formatBytes(item.bytes) + ' · 删除于 ' + formatDate(item.deletedAt) })
        ),
        restoreBtn
      ));
    });
  }

  return h('section', { class: 'panel' },
    h('h3', {}, '🧹 回收站' + (items.length ? '（' + items.length + '）' : '')),
    h('p', { class: 'hint', text: '删除的应用先进入回收站，可在这里恢复；超过 7 天自动清理。' }),
    list
  );
}

// ---------------- 诊断 ----------------

function diagPanel(diag) {
  const info = diag || {};
  const exportBtn = h('button', { class: 'btn primary', text: '导出诊断包' });
  exportBtn.addEventListener('click', async () => {
    exportBtn.disabled = true;
    try {
      const res = await call('diag.export', {}, 60000);
      toast('诊断包已导出：' + res.path, 'ok', 8000);
    } catch (err) {
      toast('导出失败：' + err.message, 'bad', 6000);
    } finally {
      exportBtn.disabled = false;
    }
  });

  return h('section', { class: 'panel' },
    h('h3', {}, '🩺 诊断'),
    h('p', { class: 'hint', text: '遇到问题时导出诊断包（含日志 / 配置 / 依赖状态 / 应用清单），便于定位；其中不含 API Key 明文。' }),
    h('div', { class: 'about-line' }, h('b', { text: '应用 / 版本' }), h('span', { text: (info.appCount || 0) + ' 个应用 · ' + (info.versionCount || 0) + ' 个版本' })),
    h('div', { class: 'about-line' }, h('b', { text: '回收站' }), h('span', { text: (info.trashCount || 0) + ' 项' })),
    h('div', { class: 'about-line' }, h('b', { text: '开机自启' }), h('span', { text: info.autoStart ? '已开启' : '未开启' })),
    h('div', { class: 'actions' },
      exportBtn,
      h('button', { class: 'btn', text: '打开日志目录', onClick: () => openFolder('system.openLogs') }),
      h('button', { class: 'btn ghost', text: '在应用内浏览', onClick: () => folderDialog((state.info && state.info.logsDir) || '') })
    )
  );
}
