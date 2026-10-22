import { call } from '../rpc.js';
import { h, clear, toast, relativeTime, formatBytes, showMenu, confirmDialog, promptDialog } from '../dom.js';
import { state, refreshApps, render as renderCurrentView } from '../app.js';

export async function render(container) {
  const apps = state.apps || [];

  container.appendChild(h('div', { class: 'launcher-head' },
    h('h2', { text: state.search ? '搜索结果' : '我的应用' }),
    h('span', { class: 'count', text: apps.length + ' 个' })
  ));

  if (state.loading) {
    container.appendChild(skeletonGrid());
    return;
  }

  if (apps.length === 0) {
    container.appendChild(emptyState());
    return;
  }

  const grid = h('div', { class: 'grid' });
  apps.forEach((app) => grid.appendChild(card(app)));
  container.appendChild(grid);
}

function skeletonGrid() {
  const grid = h('div', { class: 'grid' });
  for (let i = 0; i < 6; i++) {
    grid.appendChild(h('div', { class: 'card skeleton' },
      h('div', { class: 'sk-box' }),
      h('div', { class: 'sk-line', style: { width: '62%' } }),
      h('div', { class: 'sk-line', style: { width: '88%' } })
    ));
  }
  return grid;
}

function emptyState() {
  const text = state.search
    ? '没有匹配的应用，换个关键词试试'
    : '还没有应用。说出你的需求，AI 会现场写一个给你。';

  const box = h('div', { class: 'empty' },
    h('div', { class: 'big', text: state.search ? '🔍' : '🧰' }),
    h('h3', { text: state.search ? '没有搜索结果' : '造第一个应用' }),
    h('p', { text: text })
  );

  if (!state.search) {
    box.appendChild(h('button', {
      class: 'btn primary',
      text: '＋ 新建应用',
      onClick: () => { location.hash = '#/builder'; }
    }));
  }
  return box;
}

function card(app) {
  const node = h('div', {
    class: 'card',
    tabindex: '0',
    onClick: (event) => {
      if (event.target.closest('.more')) return;
      openApp(app);
    },
    onKeydown: (event) => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openApp(app); }
    }
  },
    h('div', { class: 'icon', text: app.icon || '🧩' }),
    h('div', { class: 'name' },
      app.name,
      app.pinned ? h('span', { class: 'pin', text: '📌' }) : null
    ),
    h('div', { class: 'desc', text: app.description || '（无描述）' }),
    h('div', { class: 'meta' },
      h('span', { text: 'v' + app.currentVersion }),
      h('span', { text: '·' }),
      h('span', { text: relativeTime(app.lastOpenedAt) }),
      app.bytes ? h('span', { text: '·' }) : null,
      app.bytes ? h('span', { text: formatBytes(app.bytes) }) : null
    ),
    h('button', {
      class: 'more',
      title: '更多操作',
      text: '⋯',
      onClick: (event) => {
        event.stopPropagation();
        showMenu(event.currentTarget, [
          { label: '📂 打开', action: () => openApp(app) },
          { label: '🤖 用 AI 修改', action: () => { location.hash = '#/builder?app=' + encodeURIComponent(app.id); } },
          { label: '🕘 版本历史（v' + app.currentVersion + '）', action: () => showVersions(app) },
          '-',
          { label: '📦 导出 zip', action: () => exportApp(app) },
          { label: '🔗 创建桌面快捷方式', action: () => createShortcut(app) },
          '-',
          { label: app.pinned ? '📌 取消置顶' : '📌 置顶', action: () => togglePin(app) },
          { label: '✏️ 重命名', action: () => renameApp(app) },
          { label: '🗂 打开目录', action: () => revealApp(app) },
          '-',
          { label: '🗑 删除应用', danger: true, action: () => deleteApp(app) }
        ]);
      }
    })
  );
  return node;
}

async function openApp(app) {
  try {
    await call('launch.open', { id: app.id });
    toast('已打开「' + app.name + '」', 'ok');
    await refreshApps();
    await renderCurrentView();
  } catch (err) {
    toast(err.message, 'bad', 5000);
  }
}

async function togglePin(app) {
  try {
    await call('apps.pin', { id: app.id, pinned: !app.pinned });
    await refreshApps();
    await renderCurrentView();
  } catch (err) {
    toast(err.message, 'bad');
  }
}

async function renameApp(app) {
  const name = await promptDialog({
    title: '重命名应用',
    label: '最多 40 个字',
    value: app.name,
    maxlength: 40
  });
  if (name === null) return;
  if (!name) { toast('名称不能为空', 'bad'); return; }

  try {
    await call('apps.rename', { id: app.id, name: name });
    await refreshApps();
    await renderCurrentView();
    toast('已重命名', 'ok');
  } catch (err) {
    toast(err.message, 'bad');
  }
}

async function revealApp(app) {
  try {
    await call('fs.reveal', { id: app.id });
  } catch (err) {
    toast(err.message, 'bad');
  }
}

async function deleteApp(app) {
  const confirmed = await confirmDialog({
    title: '删除「' + app.name + '」？',
    message: '应用会移入回收站（7 天后清理），之后可在设置页的「回收站」里恢复。',
    okText: '删除',
    danger: true
  });
  if (!confirmed) return;

  try {
    await call('apps.delete', { id: app.id });
    await refreshApps();
    await renderCurrentView();
    toast('已删除（可在设置页回收站恢复）', 'ok');
  } catch (err) {
    toast(err.message, 'bad');
  }
}

async function exportApp(app) {
  try {
    const res = await call('apps.export', { id: app.id }, 60000);
    toast('已导出：' + res.path, 'ok', 7000);
  } catch (err) {
    toast('导出失败：' + err.message, 'bad', 6000);
  }
}

async function createShortcut(app) {
  try {
    const res = await call('apps.shortcut', { id: app.id }, 30000);
    toast('已创建桌面快捷方式：' + res.path, 'ok', 7000);
  } catch (err) {
    toast('创建快捷方式失败：' + err.message, 'bad', 6000);
  }
}

// ---------------- 版本历史 ----------------

function formatDate(ts) {
  if (!ts) return '未知时间';
  const d = new Date(ts);
  const pad = (n) => (n < 10 ? '0' + n : '' + n);
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
    ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
}

async function showVersions(app) {
  let data;
  try {
    data = await call('versions.list', { appId: app.id });
  } catch (err) {
    toast('读取版本失败：' + err.message, 'bad');
    return;
  }

  const versions = (data && data.versions) || [];
  const rows = h('div', { class: 'version-list' });

  function build() {
    clear(rows);
    if (!versions.length) {
      rows.appendChild(h('div', { class: 'hint', text: '还没有版本记录' }));
      return;
    }

    versions.forEach((v) => {
      const isCurrent = v.seq === app.currentVersion;
      rows.appendChild(h('div', { class: 'version-row' + (isCurrent ? ' current' : '') },
        h('div', { class: 'version-main' },
          h('div', { class: 'version-title' },
            h('span', { class: 'badge ' + (isCurrent ? 'ok' : 'dim'), text: 'v' + String(v.seq).padStart(4, '0') }),
            h('span', { class: 'version-note', text: v.note || (v.kind === 'modify' ? 'AI 修改' : 'AI 生成') }),
            isCurrent ? h('span', { class: 'badge dim', text: '当前版本' }) : null
          ),
          h('div', { class: 'version-meta', text: formatDate(v.createdAt) + ' · ' + formatBytes(v.bytes) + (v.model ? ' · ' + v.model : '') })
        ),
        h('div', { class: 'version-actions' },
          isCurrent
            ? h('button', {
                class: 'btn sm', text: '打开',
                onClick: () => { overlay.remove(); openApp(app); }
              })
            : h('div', { class: 'inline' },
                h('button', {
                  class: 'btn sm', text: '对比',
                  onClick: () => showDiff(app, v.seq, app.currentVersion)
                }),
                h('button', { class: 'btn sm primary', text: '回滚', onClick: () => rollback(v) })
              )
        )
      ));
    });
  }

  async function rollback(version) {
    const label = 'v' + String(version.seq).padStart(4, '0');
    const confirmed = await confirmDialog({
      title: '回滚到 ' + label + '？',
      message: '当前版本会保留，之后还可以再切回来。回滚后打开应用即为该版本内容。',
      okText: '回滚'
    });
    if (!confirmed) return;

    try {
      const res = await call('versions.rollback', { appId: app.id, seq: version.seq });
      app.currentVersion = res.app.currentVersion;
      versions.length = 0;
      (res.versions || []).forEach((item) => versions.push(item));
      build();
      await refreshApps();
      toast('已回滚到 ' + label, 'ok');
    } catch (err) {
      toast(err.message, 'bad');
    }
  }

  const overlay = h('div', { class: 'overlay' },
    h('div', { class: 'dialog version-dialog' },
      h('h3', { text: '版本历史 · ' + app.name }),
      h('p', { text: '每次 AI 生成或修改跑通后都会保存一个版本，可随时回滚。' }),
      rows,
      h('div', { class: 'dialog-actions' },
        h('button', { class: 'btn', text: '关闭', onClick: () => overlay.remove() })
      )
    )
  );

  document.getElementById('modal-root').appendChild(overlay);
  overlay.addEventListener('click', (e) => { if (e.target === overlay) overlay.remove(); });
  build();
}

// ---------------- 版本对比 ----------------

async function showDiff(app, fromSeq, toSeq) {
  let data;
  try {
    data = await call('versions.diff', { appId: app.id, from: fromSeq, to: toSeq }, 60000);
  } catch (err) {
    toast('对比失败：' + err.message, 'bad', 6000);
    return;
  }

  const files = data.files || [];
  const list = h('div', { class: 'diff-list' });

  if (!files.length) {
    list.appendChild(h('div', { class: 'hint', text: '两个版本内容完全相同' }));
  }

  files.forEach((file) => {
    const isAdded = file.status === 'added';
    const isRemoved = file.status === 'removed';
    const badgeCls = isAdded ? 'ok' : isRemoved ? 'bad' : 'warn';
    const badgeText = isAdded ? '新增' : isRemoved ? '删除' : '修改';

    list.appendChild(h('div', { class: 'diff-file' },
      h('div', { class: 'diff-head' },
        h('span', { class: 'badge ' + badgeCls, text: badgeText }),
        h('span', { class: 'diff-path', text: file.path }),
        h('span', { class: 'diff-stat', text: '+' + file.added + ' / -' + file.removed })
      ),
      file.preview ? h('pre', { class: 'diff-preview', text: file.preview }) : null,
      file.note ? h('div', { class: 'hint', text: file.note }) : null
    ));
  });

  const overlay = h('div', { class: 'overlay' },
    h('div', { class: 'dialog version-dialog' },
      h('h3', { text: '版本对比 · ' + app.name }),
      h('p', {
        text: 'v' + String(fromSeq).padStart(4, '0') + ' → v' + String(toSeq).padStart(4, '0') +
          ' · ' + files.length + ' 个文件变化 · 共 +' + (data.addedLines || 0) + ' / -' + (data.removedLines || 0) + ' 行'
      }),
      list,
      h('div', { class: 'dialog-actions' },
        h('button', { class: 'btn', text: '关闭', onClick: () => overlay.remove() })
      )
    )
  );

  document.getElementById('modal-root').appendChild(overlay);
  overlay.addEventListener('click', (e) => { if (e.target === overlay) overlay.remove(); });
}
