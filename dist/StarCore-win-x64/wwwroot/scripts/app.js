/**
 * app.js —— 应用引导。
 *
 * 顺序很重要：
 *   1. 主题先落地（避免首帧闪一下默认配色）
 *   2. 挂窗口交互（标题栏拖拽、窗口按钮）—— 用户最早会碰的东西
 *   3. 注册页面，再去内核拉数据
 *   4. 进首屏，最后才撤掉启动图
 */

import { hasHost, invoke, on } from './bridge.js';
import { applyTheme, bindTheme, ACCENTS, inkOn } from './theme.js';
import { getState, loadAll, setState, subscribe, upsertTask, removeTask, getActiveAccount, getActiveInstance } from './store.js';
import {
  icon, iconButton, initTooltips, h, fill, button, fmtBytes, fmtSpeed, menu, toast,
  dialog, chip, progress, emptyState,
} from './ui.js';
import { registerSection, register, go, initRouteShortcuts } from './router.js';
import { RAIL, SECTIONS } from './nav.js';

import instanceModule from '../pages/instance.js';
import downloadModule from '../pages/download.js';
import resourcesModule from '../pages/resources.js';
import accountsModule from '../pages/accounts.js';
import announcementModule from '../pages/announcement.js';
import logsModule from '../pages/logs.js';
import settingsModule from '../pages/settings.js';

const MODULES = [instanceModule, downloadModule, resourcesModule, accountsModule, announcementModule, logsModule, settingsModule];

/* ===========================================================================
   启动
   ========================================================================== */

async function boot() {
  applyTheme(getState().settings);
  const refreshTheme = bindTheme(() => getState().settings);
  // 宿主在页面加载完成后会报告真实渲染后端，收到后立刻重算一次开销档
  document.addEventListener('sc:rendermode', () => refreshTheme(true));

  initTooltips();
  wireWindowControls();
  renderRailTail();
  renderTitlebarActions();
  wireHostEvents();
  initRouteShortcuts();

  registerModules();

  // 内核数据：失败也要让界面起来，用户至少能看到问题在哪
  try {
    await loadAll();
  } catch (e) {
    console.error('[app] 初始化数据失败', e);
    toast({ title: '初始化受限', desc: '部分数据未能读取，界面将以空状态呈现。', kind: 'warning', timeout: 7000 });
  }

  bindStatusbar();

  // 背景音乐：挂解锁监听 + 按设置同步一次；设置变化时也要跟着调
  attachMusicUnlock();
  syncBackgroundMusic();
  subscribe('settings', () => syncBackgroundMusic());

  await go('instances');

  // 撤启动图
  requestAnimationFrame(() => {
    const b = document.getElementById('boot');
    if (!b) return;
    b.classList.add('is-gone');
    setTimeout(() => b.remove(), 400);
  });

  // 首次运行引导：没有任何账户时直接把用户送到账户页
  if (!getState().accounts.length && getState().settings.showFirstRun !== false) {
    setTimeout(() => {
      toast({
        title: '还没有账户',
        desc: '登录微软账户，或创建一个离线账户即可开始。',
        kind: 'accent',
        timeout: 9000,
        action: { label: '去添加', onClick: () => go('accounts') },
      });
    }, 900);
  }
}

/* ===========================================================================
   窗口交互
   ========================================================================== */

function wireWindowControls() {
  const setIcon = (id, name) => {
    const el = document.getElementById(id);
    if (el) fill(el, icon(name, 'sm'));
  };
  setIcon('btnMin', 'minus');
  setIcon('btnMax', 'square');
  setIcon('btnClose', 'x');

  document.getElementById('btnMin')?.addEventListener('click', () => invoke('window.minimize').catch(() => {}));
  document.getElementById('btnMax')?.addEventListener('click', () => invoke('window.toggleMaximize').catch(() => {}));
  document.getElementById('btnClose')?.addEventListener('click', () => invoke('window.close').catch(() => {}));

  // 标题栏拖拽：交给宿主做原生的非客户区拖拽，比 JS 搬运窗口平滑得多
  const bar = document.getElementById('titlebar');
  bar?.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    if (e.target.closest('button, a, input, .no-drag, [data-tip], .brand__sub')) return;
    invoke('window.drag').catch(() => {});
  });

  // 双击标题栏最大化/还原
  bar?.addEventListener('dblclick', (e) => {
    if (e.target.closest('button, a, input, .no-drag')) return;
    invoke('window.toggleMaximize').catch(() => {});
  });

  // 最大化状态变化时，交给宿主通知；这里兜底样式与按钮图标
  on('window.state', (p) => {
    const max = !!p?.maximized;
    document.documentElement.dataset.maximized = max ? 'true' : 'false';
    // 和系统一致：最大化后按钮变成"还原"（两个叠放的方块）
    setIcon('btnMax', max ? 'copy' : 'square');
    document.getElementById('btnMax')?.setAttribute('title', max ? '还原' : '最大化');
    document.getElementById('btnMax')?.setAttribute('aria-label', max ? '还原' : '最大化');
  });
}

/* ===========================================================================
   图标栏底部：主题、通知、头像
   ========================================================================== */

function renderRailTail() {
  const rail = document.getElementById('rail');
  if (!rail) return;
  const tail = h('div', { 'data-tail': '', class: 'col', style: { alignItems: 'center', gap: 'var(--sp-2)', width: '100%' } });

  const themeBtn = iconButton('moon', { size: 'lg', tip: '切换深浅色', onClick: () => toggleTheme() });
  themeBtn.id = 'btnTheme';

  const aboutBtn = iconButton('info', { size: 'lg', tip: '关于 StarCore', onClick: () => go('settings', 'about') });
  aboutBtn.id = 'btnAbout';

  const avatarBtn = h('button', {
    class: 'rail-avatar', type: 'button', 'data-tip': '账户', 'aria-label': '账户',
    onClick: () => go('accounts'),
  });
  avatarBtn.id = 'railAvatar';

  tail.append(h('div', { class: 'rail__sep' }), themeBtn, aboutBtn, avatarBtn);
  rail.appendChild(tail);

  const paintAvatar = () => {
    const acc = getActiveAccount();
    const color = inkOn('#2196F3');
    fill(avatarBtn, acc
      ? h('div', { class: 'avatar avatar--md avatar--round', style: { background: 'var(--accent-soft)', color: 'var(--accent-text)' } },
          acc.skinFace
            ? h('img', { src: acc.skinFace, alt: acc.userName ?? '', style: { imageRendering: 'pixelated' } })
            : h('span', { text: (acc.userName ?? '?').slice(0, 1).toUpperCase() }))
      : h('div', { class: 'avatar avatar--md avatar--round', style: { background: 'var(--surface-3)', color: 'var(--text-3)' } }, icon('user', 'sm')));
    void color;
  };
  paintAvatar();
  subscribe('accounts', paintAvatar);
  subscribe('activeUuid', paintAvatar);

  const paintTheme = () => {
    const dark = document.documentElement.dataset.theme === 'dark';
    fill(themeBtn, icon(dark ? 'sun' : 'moon', 'sm'));
  };
  paintTheme();
  subscribe('settings', () => requestAnimationFrame(paintTheme));
  void ACCENTS;
}

function toggleTheme() {
  const cur = getState().settings.theme;
  const dark = document.documentElement.dataset.theme === 'dark';
  void cur;
  import('./store.js').then(({ patchSettings }) => patchSettings({ theme: dark ? 'light' : 'dark' }));
}

/* ===========================================================================
   标题栏右侧：任务指示 + 快捷动作
   ========================================================================== */

function renderTitlebarActions() {
  const host = document.getElementById('titlebarActions');
  if (!host) return;

  const tasksBtn = iconButton('loader-circle', { size: 'md', tip: '任务中心', onClick: () => showTaskCenter() });
  tasksBtn.id = 'btnTasks';
  tasksBtn.style.display = 'none';

  const searchBtn = iconButton('search', { size: 'md', tip: '快速搜索 (Ctrl+K)', onClick: () => globalSearch() });

  const noticeBtn = iconButton('bell', { size: 'md', tip: '公告与更新', onClick: () => go('announcement') });

  fill(host, searchBtn, noticeBtn, tasksBtn);

  const sync = () => {
    const running = getState().tasks.filter((t) => t.state === 'running' || t.state === 'queued');
    tasksBtn.style.display = running.length ? '' : 'none';
    const dot = running.length > 1 ? h('span', { class: 'rail-btn__badge', text: String(running.length) }) : null;
    if (dot) tasksBtn.appendChild(dot);
    else Array.from(tasksBtn.querySelectorAll('.rail-btn__badge')).forEach((n) => n.remove());
  };
  sync();
  subscribe('tasks', sync);
}

/* ===========================================================================
   任务中心

   之前的实现是一个菜单：只有标题和百分比，没有取消按钮，也看不到速度与剩余量。
   下载卡住时用户唯一的办法是关掉启动器 —— 这是最糟的"取消"方式，
   因为它会连带留下半成品文件（我们此前的 Java 残缺运行时事故就是这么来的）。

   现在换成正经面板：进行中的任务带进度条、速度、取消；已结束的可以逐条或批量清除。
   ========================================================================== */

const TASK_STATE_META = {
  queued:    { label: '排队中', variant: '', icon: 'clock' },
  running:   { label: '进行中', variant: 'accent', icon: 'loader-circle' },
  done:      { label: '已完成', variant: 'success', icon: 'circle-check' },
  completed: { label: '已完成', variant: 'success', icon: 'circle-check' },
  failed:    { label: '失败',   variant: 'danger', icon: 'circle-x' },
  cancelled: { label: '已取消', variant: '', icon: 'circle-x' },
};

function isActiveTask(t) {
  return t.state === 'running' || t.state === 'queued';
}

function showTaskCenter() {
  const body = h('div', { class: 'col', style: { gap: 'var(--sp-5)', maxHeight: '52vh', overflowY: 'auto' } });

  function paint() {
    const tasks = getState().tasks ?? [];

    if (!tasks.length) {
      fill(body, emptyState({
        icon: 'loader-circle',
        title: '没有任务',
        desc: '下载版本、安装模组、下载 Java 时，任务会出现在这里。',
      }));
      return;
    }

    const active = tasks.filter(isActiveTask);
    const finished = tasks.filter((t) => !isActiveTask(t));

    fill(body,
      ...(active.length
        ? [h('div', { class: 'panel__group', text: `进行中 · ${active.length}` }), ...active.map(taskRow)]
        : []),
      ...(finished.length
        ? [h('div', { class: 'panel__group', text: `已结束 · ${finished.length}` }), ...finished.map(taskRow)]
        : []),
    );
  }

  function taskRow(t) {
    const meta = TASK_STATE_META[t.state] ?? TASK_STATE_META.queued;
    const pct = Math.round(t.progress ?? 0);

    return h('div', { class: 'card', style: { padding: 'var(--sp-5) var(--sp-6)' } },
      h('div', { class: 'row-between', style: { gap: 'var(--sp-5)' } },
        h('div', { class: 'grow truncate', style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)' }, text: t.title ?? '任务' }),
        chip(meta.label, { variant: meta.variant || undefined, icon: meta.icon }),
      ),

      isActiveTask(t)
        ? h('div', { class: 'col', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-5)' } },
            progress(pct, { size: 'sm' }),
            h('div', { class: 'row-between', style: { gap: 'var(--sp-5)' } },
              h('div', { class: 'dim truncate', style: { fontSize: 'var(--fs-micro)' }, text: t.detail ?? `进度 ${pct}%` }),
              h('div', { class: 'row', style: { gap: 'var(--sp-5)', flexShrink: '0' } },
                t.speed ? h('span', { class: 'dim mono', style: { fontSize: 'var(--fs-micro)' }, text: fmtSpeed(t.speed) }) : null,
                t.totalBytes
                  ? h('span', { class: 'dim mono', style: { fontSize: 'var(--fs-micro)' }, text: `${fmtBytes(t.doneBytes ?? 0)} / ${fmtBytes(t.totalBytes)}` })
                  : null,
              ),
            ),
          )
        : (t.error || t.detail)
          ? h('div', {
              class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: 'var(--sp-4)', lineHeight: 'var(--lh-snug)', color: t.error ? 'var(--danger)' : '' },
              text: t.error ?? t.detail,
            })
          : null,

      h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-5)' } },
        // 取消按钮：这是这一版的重点。以前任务卡住只能关启动器，
        // 而强杀进程会留下半成品文件（此前的 Java 残缺运行时事故就是这么发生的）。
        isActiveTask(t) && t.cancellable !== false
          ? button('取消', {
              variant: 'danger-ghost', size: 'sm', icon: 'x',
              onClick: async (e) => {
                e.currentTarget.disabled = true;
                try {
                  await invoke('task.cancel', { id: t.id });
                  toast({ title: '已取消', desc: t.title, kind: 'neutral', timeout: 2000 });
                } catch (err) {
                  toast({ title: '取消失败', desc: err?.message ?? String(err), kind: 'danger' });
                  e.currentTarget.disabled = false;
                }
              },
            })
          : null,
        !isActiveTask(t)
          ? button('清除记录', {
              variant: 'ghost', size: 'sm', icon: 'trash-2',
              onClick: () => { removeTask(t.id); },
            })
          : null,
      ),
    );
  }

  paint();

  dialog({
    title: '任务中心',
    icon: 'loader-circle',
    width: 560,
    body,
    actions: [
      {
        label: '取消全部进行中', variant: 'ghost',
        onClick: async () => {
          const active = (getState().tasks ?? []).filter(isActiveTask);
          if (!active.length) return;
          for (const t of active) await invoke('task.cancel', { id: t.id }).catch(() => {});
          toast({ title: `已取消 ${active.length} 个任务`, kind: 'neutral' });
        },
      },
      {
        label: '清除已完成', variant: 'ghost',
        onClick: () => {
          for (const t of (getState().tasks ?? []).filter((x) => !isActiveTask(x))) removeTask(t.id);
        },
      },
      { label: '关闭', variant: 'primary' },
    ],
  });
}

async function globalSearch() {
  dialog({
    title: '快速搜索',
    icon: 'search',
    width: 520,
    body: h('div', {},
      h('div', { class: 'search' },
        icon('search', 'sm'),
        h('input', { class: 'input', placeholder: '搜索实例、版本、模组…', id: 'gsInput' }),
      ),
      h('div', { class: 'dim', style: { marginTop: '14px', fontSize: 'var(--fs-xs)' }, text: '输入关键词后回车即可跳转到对应结果页。' }),
    ),
    actions: [
      { label: '取消', variant: 'ghost', value: null },
      {
        label: '搜索', variant: 'primary', value: null, onClick: () => {
          const q = document.getElementById('gsInput')?.value?.trim();
          if (q) {
            import('./store.js').then(({ setState }) => setState({ quickQuery: q }));
            go('resources', 'mod');
          }
        },
      },
    ],
  });
}

/* ===========================================================================
   背景音乐

   两个必须处理的现实问题：
     1. 浏览器/WebView2 默认禁止自动播放。所以播放要等到用户第一次交互
        （点击或按键）之后才真正开始 —— 不这么做会静默失败，用户以为坏了。
     2. 音量默认压到 0.45。背景音乐是用来衬气氛的，不是主角；
        默认满音量的启动器是非常冒犯的设计。
   ========================================================================== */

let bgAudio = null;
let musicUnlocked = false;

function ensureAudio() {
  if (!bgAudio) {
    bgAudio = new Audio();
    bgAudio.loop = true;
    bgAudio.preload = 'auto';
  }
  return bgAudio;
}

function syncBackgroundMusic() {
  const st = getState().settings ?? {};
  const url = st.bgMusicUrl ?? '';
  const want = !!st.bgMusicEnabled && !!url;

  const audio = ensureAudio();
  audio.volume = Math.min(1, Math.max(0, Number(st.bgMusicVolume ?? 0.45)));

  if (!want) {
    audio.pause();
    return;
  }

  if (audio.dataset.src !== url) {
    audio.dataset.src = url;
    audio.src = url;
  }

  // 没被用户交互"解锁"之前不要去 play，否则必然被策略拒绝并抛一个没用的异常
  if (!musicUnlocked) return;

  audio.play().catch(() => { /* 自动播放被拒时静默降级，用户再点一下就会开始 */ });
}

function attachMusicUnlock() {
  const unlock = () => {
    if (musicUnlocked) return;
    musicUnlocked = true;
    syncBackgroundMusic();
    window.removeEventListener('pointerdown', unlock);
    window.removeEventListener('keydown', unlock);
  };
  window.addEventListener('pointerdown', unlock, { once: false });
  window.addEventListener('keydown', unlock, { once: false });
}

/* ===========================================================================
   状态条
   ========================================================================== */

function bindStatusbar() {
  const bar = document.getElementById('statusbar');
  if (!bar) return;

  const paint = () => {
    const s = getState();
    const acc = getActiveAccount();
    const inst = getActiveInstance();
    const running = s.tasks.filter((t) => t.state === 'running');
    const launch = s.launch;

    fill(bar,
      h('div', { class: 'statusbar__item' },
        icon(running.length ? 'loader-circle' : 'circle-check', 'xs'),
        h('span', { text: running.length ? `${running[0].title} · ${Math.round(running[0].progress ?? 0)}%` : '就绪' }),
      ),
      running.length ? h('div', { style: { width: '120px' } }, h('div', { class: 'progress progress--sm' }, h('div', { class: 'progress__bar', style: { '--pct': `${running[0].progress ?? 0}%` } }))) : null,
      h('div', { class: 'statusbar__spacer' }),
      h('div', { class: 'statusbar__item' }, icon('boxes', 'xs'), h('span', { text: inst ? inst.name : '未选择实例' })),
      h('div', { class: 'statusbar__item' }, icon('user', 'xs'), h('span', { text: acc ? acc.userName : '未登录' })),
      launch.state === 'running'
        ? h('div', { class: 'statusbar__item', style: { color: 'var(--success)' } }, icon('play', 'xs'), h('span', { text: `游戏中 · PID ${launch.pid ?? '—'}` }))
        : null,
      h('div', { class: 'statusbar__item', style: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-micro)' } },
        h('span', { text: hasHost ? `v${getState().app?.version ?? '1.0.0'}` : '预览模式' })),
    );
  };

  paint();
  for (const k of ['tasks', 'launch', 'accounts', 'activeUuid', 'instances', 'activeInstanceId', 'app']) subscribe(k, paint);
}

/* ===========================================================================
   宿主事件
   ========================================================================== */

function wireHostEvents() {
  on('task.update', (t) => {
    if (!t?.id) return;
    if (t.state === 'done' || t.state === 'failed' || t.state === 'cancelled') {
      upsertTask(t);
      if (t.state === 'done') toast({ title: t.title ?? '任务完成', desc: t.detail, kind: 'success' });
      if (t.state === 'failed') toast({ title: t.title ?? '任务失败', desc: t.error, kind: 'danger', timeout: 8000 });
      setTimeout(() => removeTask(t.id), t.state === 'done' ? 2600 : 12000);
    } else {
      upsertTask(t);
    }
  });

  on('launch.state', (p) => {
    setState({ launch: { ...getState().launch, ...p } });
    if (p?.state === 'running') toast({ title: '游戏已启动', desc: `PID ${p.pid}`, kind: 'success' });
    if (p?.state === 'crashed') toast({ title: '游戏异常退出', desc: `退出码 ${p.exitCode}`, kind: 'danger', timeout: 0, action: { label: '查看日志', onClick: () => go('logs') } });
  });

  on('accounts.changed', async () => {
    const { invoke } = await import('./bridge.js');
    const a = await invoke('account.list').catch(() => null);
    if (a) setState({ accounts: a.profiles ?? [], activeUuid: a.activeUuid ?? null });
  });

  on('instances.changed', async () => {
    const { invoke } = await import('./bridge.js');
    const i = await invoke('instance.list').catch(() => null);
    if (i) setState({ instances: i.instances ?? [], activeInstanceId: i.activeId ?? getState().activeInstanceId });
  });

  on('toast', (p) => toast(p ?? {}));

  on('navigate', (p) => { if (p?.section) go(p.section, p.sub ?? null); });
}

/* ===========================================================================
   页面注册
   ========================================================================== */

function registerModules() {
  for (const mod of MODULES) {
    if (mod.section) {
      registerSection(mod.section, { panel: mod.panel, onEnter: mod.onEnter });
    }
    for (const [key, def] of Object.entries(mod.pages ?? {})) {
      register(key, def);
    }
  }
  void SECTIONS;
  void RAIL;
  void button;
  void fmtBytes;
}

/* ===========================================================================
   暴露给宿主的调用入口
   宿主（C#）需要两件事：从快捷方式带参启动某个实例、以及按需跳页。
   只暴露必要的方法，避免把内部状态泄漏到全局。
   ========================================================================== */

globalThis.StarCore = {
  go: (section, sub) => go(section, sub ?? null),

  /** 由 --launch <id> 触发：切到该实例并直接启动。 */
  async launch(instanceId) {
    try {
      await invoke('instance.setActive', { id: instanceId }).catch(() => {});
      const r = await invoke('instance.list').catch(() => null);
      if (r) setState({ instances: r.instances ?? [], activeInstanceId: instanceId });
      await go('instances');
      await invoke('launch.start', { instanceId });
    } catch (e) {
      toast({ title: '启动失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
      go('logs');
    }
  },
};

/** 自检入口：由宿主的 --theme / --accent 参数调用，走正常的设置落盘路径。 */
globalThis.__scApplySettings = (patch) => {
  try { applySettingsPatch(patch); } catch (e) { console.warn('[app] 应用命令行设置失败', e); }
};

function applySettingsPatch(patch) {
  const next = { ...getState().settings, ...patch };
  setState({ settings: next });
}

/* 启动（DOM 已就绪，因为 script 是 module 且在 body 末尾） */
boot().catch((e) => {
  console.error('[app] 引导失败', e);
  const b = document.getElementById('boot');
  if (b) b.querySelector('.boot-name').textContent = '启动失败，请查看日志';
});
