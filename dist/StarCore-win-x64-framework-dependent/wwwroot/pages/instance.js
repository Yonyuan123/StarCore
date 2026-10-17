/**
 * instance.js —— 「实例」分区。
 *
 * 这是全应用的主场：启动游戏发生在这里。
 * 页面组成：
 *   instances              Hero 启动页（有实例时）/ 安装引导（无实例时）
 *   instances:list         实例网格：新建、导入、复制、删除
 *   instances:overview     实例概览与启动参数
 *   instances:worlds/mods/resourcepacks/shaders/backups  内容管理（共用一套渲染器）
 *   instances:export       导出整合包
 *   instances:advanced     高级：JVM 参数、分辨率、窗口行为
 *
 * 数据全部来自内核，无任何内置演示数据；实例为空时进入引导流程。
 */

import { invoke } from '../scripts/bridge.js';
import { getState, setState, subscribe, getActiveInstance, patchSettings } from '../scripts/store.js';
import {
  h, fill, icon, iconButton, button, emptyState, chip, loaderIcon, loaderLogo, blockCover,
  toast, dialog, confirm, menu, fmtBytes, fmtDateTime, fmtAgo, fmtDuration,
  selectControl as customSelect,
} from '../scripts/ui.js';
import { go } from '../scripts/router.js';
import { BACKGROUNDS, backgroundFile } from '../scripts/theme.js';

const SECTION = 'instances';

/* ===========================================================================
   二级面板：实例列表 + 新建/导入
   ========================================================================== */

function renderPanel(el, hh, ctx) {
  const body = h('div', { class: 'panel__body' });
  const head = h('div', { class: 'panel__head' });
  const foot = h('div', { class: 'panel__foot' });
  fill(el, head, body, foot);

  const paintHead = () => {
    const n = getState().instances.length;
    fill(head,
      h('div', { class: 'panel__title' }, icon('boxes', 'md'), h('span', { text: '实例' }), h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)', fontWeight: '400' }, text: n ? `${n}` : '' })),
      h('div', { class: 'search' },
        icon('search', 'sm'),
        h('input', { class: 'input', placeholder: '筛选实例…', onInput: (e) => paintList(e.target.value.trim()) }),
      ),
    );
  };

  let filter = '';
  const paintList = (q) => {
    filter = q ?? filter;
    const list = getState().instances;
    const shown = filter ? list.filter((i) => (i.name ?? '').toLowerCase().includes(filter.toLowerCase())) : list;
    const active = getState().activeInstanceId;

    if (!list.length) {
      fill(body,
        emptyState({
          icon: 'package-open',
          title: '还没有实例',
          desc: '实例是一个独立的游戏环境，拥有自己的版本、模组与存档。',
          actions: [button('新建实例', { variant: 'primary', size: 'sm', icon: 'plus', onClick: () => go(SECTION, 'list') })],
        }),
      );
      return;
    }
    if (!shown.length) {
      fill(body, h('div', { class: 'dim', style: { padding: '20px 10px', fontSize: 'var(--fs-sm)', textAlign: 'center' }, text: '没有匹配的实例' }));
      return;
    }

    fill(body, ...shown.map((inst) => h('div', {
      class: 'inst-row',
      role: 'button',
      tabindex: '0',
      'aria-selected': inst.id === active ? 'true' : 'false',
      onClick: async () => {
        await invoke('instance.setActive', { id: inst.id }).catch(() => {});
        setState({ activeInstanceId: inst.id });
        go(SECTION);
      },
      onDblclick: () => { setState({ activeInstanceId: inst.id }); go(SECTION, 'overview'); },
    },
      h('div', { class: 'inst-row__cover' },
        coverImg(inst.cover) ?? loaderLogo(inst.loader, 'sm')),
      h('div', { class: 'grow' },
        h('div', { class: 'inst-row__name truncate', text: inst.name ?? '未命名实例' }),
        h('div', { class: 'inst-row__meta truncate', text: instanceMeta(inst) }),
      ),
      inst.id === active ? icon('check', 'sm') : null,
    )));
  };

  const paintFoot = () => {
    fill(foot,
      button('新建实例', { variant: 'soft', size: 'md', icon: 'plus', block: true, onClick: () => go(SECTION, 'list') }),
    );
  };

  paintHead();
  paintList('');
  paintFoot();

  const off = subscribe('instances', () => { paintHead(); paintList(filter); });
  const off2 = subscribe('activeInstanceId', () => paintList(filter));
  return () => { off(); off2(); };
}

function instanceMeta(inst) {
  const parts = [];
  if (inst.versionId) parts.push(inst.versionId);
  if (inst.loader && inst.loader !== 'vanilla') parts.push(loaderLabel(inst.loader));
  if (inst.modCount) parts.push(`${inst.modCount} 个模组`);
  return parts.join(' · ') || '未配置版本';
}

function loaderLabel(id) {
  return { vanilla: '原版', forge: 'Forge', neoforge: 'NeoForge', fabric: 'Fabric', quilt: 'Quilt', optifine: 'OptiFine', liteloader: 'LiteLoader' }[id] ?? id;
}

/**
 * 账户类型的中文标签。
 *
 * 这是一次真实的崩溃源：Hero 区调用了 `accountKind(acc)`，但这个函数从来没被定义过，
 * 于是整个实例页在渲染阶段抛 ReferenceError，Router 把它兜成一个错误页 ——
 * 用户看到的就是「启动 加载失败 / accountKind is not defined」。
 * 字段名以 accounts.js 为准（那里读的是 `profileType`）。
 */
function accountKind(acc) {
  const t = String(acc?.profileType ?? '').toLowerCase();
  if (t === 'microsoft') return '微软账户';
  if (t === 'offline') return '离线账户';
  if (t === 'thirdparty' || t === 'authlib' || t === 'yggdrasil') return '第三方登录';
  return acc?.profileType ? String(acc.profileType) : '账户';
}

/** 版本封面：Mojang 官方方块贴图，像素风，放大时要保持硬边。 */
function coverImg(rel, cls, alt = '') {
  const src = blockCover(rel);
  if (!src) return null;
  return h('img', {
    class: cls, src, alt, loading: 'lazy', decoding: 'async',
    style: { imageRendering: 'pixelated' },
  });
}

/** 压在 Hero 背景图上的幽灵按钮：文字用图片上的前景色，保证在任何底图上都清晰。 */
function ghostOnHero(label, ic, onClick) {
  const b = button(label, { variant: 'ghost', size: 'sm', icon: ic, onClick });
  b.style.color = 'var(--on-hero-dim)';
  b.addEventListener('mouseenter', () => { b.style.color = 'var(--on-hero)'; b.style.background = 'rgba(255,255,255,0.14)'; });
  b.addEventListener('mouseleave', () => { b.style.color = 'var(--on-hero-dim)'; b.style.background = ''; });
  return b;
}

/* ===========================================================================
   Hero 启动页
   ========================================================================== */

function mountHero(el) {
  const bg = h('div', { class: 'hero' });
  const stage = h('div', { class: 'hero-stage' });
  const dock = h('div', { class: 'hero-dock' });
  const bgBtn = h('div', { class: 'hero-bg-btn' });

  fill(el,
    bg,
    h('div', { class: 'atmosphere', 'aria-hidden': 'true' }),
    h('div', { style: { position: 'relative', zIndex: '1', flex: '1 1 auto', display: 'flex', flexDirection: 'column', minHeight: '0', width: '100%' } }, stage, dock),
    bgBtn,
  );

  let paintHero = () => {};
  let paintDock = () => {};

  /* —— 背景 ——
     优先级：自定义视频 > 自定义图片 > 内置预设。
     视频排最前是因为它是"最重"的选择 —— 用户特意放了个视频当背景，
     结果被一张图片盖住会显得很莫名其妙。 */
  const applyBackground = () => {
    const st = getState().settings;
    const layers = () => [h('div', { class: 'hero__scrim' }), h('div', { class: 'hero__tint' }), h('div', { class: 'hero__grain' })];

    // 自定义视频
    if (st.bgVideoUrl) {
      const video = h('video', {
        class: 'hero__video is-current',
        src: st.bgVideoUrl,
        autoplay: true, loop: true, muted: true, playsinline: true,
        // 动态背景是装饰，静音循环播放；onerror 兜底避免留下黑块
        onError: (e) => {
          e.target.style.display = 'none';
          toast({ title: '背景视频无法播放', desc: '可能是不受支持的编码格式。', kind: 'warning' });
        },
      });
      bg.replaceChildren(video, ...layers());
      return;
    }

    // 自定义图片 或 内置预设
    const src = st.bgImageUrl || (() => {
      const file = backgroundFile(st.background);
      return file ? `assets/img/${file}` : null;
    })();

    if (!src) {
      bg.replaceChildren(h('div', { class: 'hero__scrim', style: { background: 'var(--bg)' } }));
      return;
    }

    const cur = bg.querySelector('img.is-current');
    const img = h('img', { class: 'hero__img', src, alt: '', decoding: 'async' });
    bg.replaceChildren(
      cur ? cur : null,
      img,
      ...layers(),
    );
    requestAnimationFrame(() => {
      img.classList.add('is-current');
      setTimeout(() => bg.querySelectorAll('img:not(.is-current), video').forEach((n) => n.remove()), 500);
    });
  };

  bgBtn.appendChild(button('更换背景', {
    variant: 'ghost', size: 'sm', icon: 'image',
    className: 'no-drag',
    onClick: (e) => openBackgroundMenu(e.currentTarget),
  }));
  bgBtn.querySelector('button').style.color = 'var(--on-hero)';

  function openBackgroundMenu(anchor) {
    menu(anchor, BACKGROUNDS.map((b) => ({
      label: b.label,
      hint: getState().settings.background === b.id ? '当前' : '',
      onClick: async () => { await patchSettings({ background: b.id }); applyBackground(); },
    })));
  }

  // —— 主区 ——
  paintHero = () => {
    const inst = getActiveInstance();
    const acc = getState().accounts.find((a) => a.uuid === getState().activeUuid) ?? null;
    const launch = getState().launch;

    if (!getState().instances.length) {
      fill(stage, introPanel());
      return;
    }

    const state = launch.state === 'running' ? 'running' : launch.state === 'preparing' ? 'preparing' : 'idle';
    const label = state === 'running' ? '结束游戏' : state === 'preparing' ? '正在启动…' : '启动游戏';
    const ic = state === 'running' ? 'square' : state === 'preparing' ? 'loader-circle' : 'play';

    const launchBtn = h('button', { class: 'btn-launch', type: 'button', 'data-state': state, onClick: () => onLaunch(state) },
      icon(ic, 'lg'),
      h('span', { text: label }),
    );

    fill(stage,
      h('div', { class: 'launch-stack' },
        h('div', { class: 'launch-avatar' },
          acc?.skinFace
            ? h('img', { src: acc.skinFace, alt: acc.userName ?? '' })
            : icon(acc ? 'user' : 'user-plus', '2xl'),
        ),
        h('div', {},
          h('div', { class: 'launch-name hero-shadow-text', text: acc ? (acc.userName ?? '玩家') : '未登录' }),
          h('div', { class: 'launch-sub hero-shadow-text', text: acc ? accountKind(acc) : '点击此处登录账户' }),
        ),
        launchBtn,
        h('button', { class: 'launch-version', type: 'button', onClick: (e) => openVersionMenu(e.currentTarget) },
          icon('layers', 'xs'),
          h('span', { class: 'mono', text: inst?.versionId ?? '未选择版本' }),
          inst?.loader && inst.loader !== 'vanilla' ? chip(loaderLabel(inst.loader), { variant: 'accent' }) : null,
          icon('chevron-down', 'xs'),
        ),
        h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-2)' } },
          ghostOnHero('游戏设置', 'settings', () => go(SECTION, 'overview')),
          ghostOnHero('高级设置', 'sliders-horizontal', () => go(SECTION, 'advanced')),
        ),
      ),
    );

    // 未登录时把整个头像区做成可点
    const avatarBox = stage.querySelector('.launch-avatar');
    if (!acc) {
      avatarBox.style.cursor = 'pointer';
      avatarBox.addEventListener('click', () => go('accounts'));
      stage.querySelector('.launch-name').style.cursor = 'pointer';
      stage.querySelector('.launch-name').addEventListener('click', () => go('accounts'));
    }
  };

  // —— 底部：当前实例卡 + 统计 ——
  paintDock = () => {
    const inst = getActiveInstance();
    if (!inst) { fill(dock); return; }

    const playtime = inst.totalPlayTimeMs ?? 0;
    const last = inst.lastPlayedAt;

    fill(dock,
      h('div', { class: 'instance-card-hero', role: 'button', tabindex: '0', onClick: () => go(SECTION, 'overview') },
        h('div', { class: 'instance-card-hero__cover' },
          coverImg(inst.cover) ?? icon('box', 'md')),
        h('div', { class: 'grow', style: { minWidth: '0' } },
          h('div', { class: 'instance-card-hero__title truncate', text: inst.name ?? '未命名实例' }),
          h('div', { class: 'instance-card-hero__sub truncate', text: instanceMeta(inst) }),
        ),
        icon('chevron-right', 'sm'),
      ),
      h('div', { class: 'hero-stats' },
        heroStat(fmtDuration(playtime), '累计游玩'),
        heroStat(inst.modCount ? String(inst.modCount) : '0', '已装模组'),
        heroStat(last ? fmtAgo(last) : '尚未启动', '上次游玩'),
      ),
    );
  };

  function heroStat(value, label) {
    return h('div', {},
      h('div', { class: 'hero-stat__value', text: value }),
      h('div', { class: 'hero-stat__label', text: label }),
    );
  }

  /**
   * 启动按钮下方那个选择器。
   *
   * 它的语义是「我要启动哪一个」—— 所以列的是**已经安装好的实例**，
   * 而不是全部游戏版本。之前列的是版本清单，那等于让你在这里"安装"，
   * 既和旁边的大按钮职责重叠，也容易让人以为选中就会开始下载。
   * 想装新版本请去「下载」页，安装会自动建好实例。
   */
  function openVersionMenu(anchor) {
    const all = getState().instances;
    const activeId = getState().activeInstanceId;

    // 只列**已安装**的实例：这个选择器的语义是"我要启动哪一个"，
    // 把没装任何东西的实例混进来，用户选中后点启动必然报"安装不完整"。
    const ready = all.filter((i) => i.installed);
    const skipped = all.length - ready.length;

    const items = ready.length
      ? ready.map((inst) => ({
          label: inst.name,
          // 角标显示版本 + 加载器，一眼能区分同版本的多个实例
          hint: [inst.versionId ?? '未配置版本', inst.loader && inst.loader !== 'vanilla' ? loaderLabel(inst.loader) : '']
            .filter(Boolean).join(' · '),
          // 用实例的方块封面（草方块=正式版、命令方块=快照…），与列表页保持一致
          iconNode: coverImg(inst.cover) ?? icon('boxes', 'sm'),
          active: inst.id === activeId,
          onClick: async () => {
            if (inst.id === activeId) return;
            await invoke('instance.setActive', { id: inst.id }).catch(() => {});
            setState({ activeInstanceId: inst.id });
          },
        }))
      : [{ label: '还没有装好的实例', hint: '请先到「下载」页安装一个版本', icon: 'info', disabled: true }];

    menu(anchor, [
      ...items,
      { separator: true },
      skipped > 0
        ? { label: `未安装的实例（${skipped} 个）…`, icon: 'folder', onClick: () => go(SECTION, 'list') }
        : null,
      { label: '新建实例…', icon: 'plus', onClick: () => go(SECTION, 'list') },
      { label: '安装新版本…', icon: 'download', onClick: () => go('download') },
    ].filter(Boolean));
  }

  async function onLaunch(state) {
    const inst = getActiveInstance();
    if (!inst) { toast({ title: '请先创建一个实例', kind: 'warning' }); go(SECTION, 'list'); return; }

    if (state === 'running') {
      const ok = await confirm({ title: '结束游戏', message: '强制结束当前运行中的游戏进程？未保存的进度可能丢失。', confirmLabel: '结束', danger: true });
      if (!ok) return;
      await invoke('launch.kill', { instanceId: inst.id }).catch(() => {});
      return;
    }
    if (state === 'preparing') return;

    if (!getState().accounts.length) {
      toast({ title: '需要先添加账户', desc: '登录微软账户或创建离线账户后才能启动。', kind: 'warning' });
      go('accounts');
      return;
    }
    if (!inst.versionId) {
      toast({ title: '该实例还没有版本', desc: '请先为它选择一个游戏版本。', kind: 'warning' });
      go('download');
      return;
    }

    try {
      setState({ launch: { ...getState().launch, state: 'preparing', instanceId: inst.id } });
      await invoke('launch.start', { instanceId: inst.id });
    } catch (e) {
      setState({ launch: { state: 'idle', instanceId: null, pid: null, startedAt: null, exitCode: null } });
      toast({ title: '启动失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
      go('logs');
    }
  }

  // —— 无实例时的引导 ——
  function introPanel() {
    return h('div', { class: 'welcome__inner stagger' },
      h('div', { class: 'welcome__badge' }, icon('sparkles', 'xs'), h('span', { text: '开始使用 StarCore' })),
      h('div', { class: 'welcome__title', text: '创建第一个游戏实例' }),
      h('div', { class: 'welcome__desc', text: '实例是独立的游戏环境：它拥有自己的版本、模组、资源包与存档。你可以为不同整合包各建一个实例，互不干扰。' }),
      h('div', { class: 'welcome__actions' },
        button('新建实例', { variant: 'primary', size: 'lg', icon: 'plus', onClick: () => go(SECTION, 'list') }),
        button('导入整合包', { variant: 'secondary', size: 'lg', icon: 'folder-open', onClick: () => importModpack() }),
        button('浏览版本', { variant: 'ghost', size: 'lg', icon: 'download', onClick: () => go('download') }),
      ),
      h('div', { class: 'steps' },
        stepCard('1', '选择游戏版本', '从正式版、快照或旧版本中挑选一个作为基础。'),
        stepCard('2', '按需安装加载器', 'Forge / NeoForge / Fabric / Quilt，装模组前先装它。'),
        stepCard('3', '添加账户并启动', '微软正版账户或离线账户，然后点启动即可。'),
      ),
    );
  }

  function stepCard(n, title, desc) {
    return h('div', { class: 'step-card' },
      h('div', { class: 'step-card__num', text: n }),
      h('div', { class: 'step-card__title', text: title }),
      h('div', { class: 'step-card__desc', text: desc }),
    );
  }

  async function importModpack() {
    try {
      const r = await invoke('system.pickFile', { title: '选择整合包文件', filter: '整合包 (*.zip;*.mrpack)|*.zip;*.mrpack' });
      if (!r?.path) return;
      await invoke('instance.importModpack', { path: r.path });
      const list = await invoke('instance.list').catch(() => null);
      if (list) setState({ instances: list.instances ?? [] });
      toast({ title: '整合包导入完成', kind: 'success' });
    } catch (e) {
      toast({ title: '导入失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  applyBackground();
  paintHero();
  paintDock();

  const offs = [
    subscribe('settings', () => { applyBackground(); }),
    subscribe('instances', paintHero),
    subscribe('activeInstanceId', () => { paintHero(); paintDock(); }),
    subscribe('accounts', paintHero),
    subscribe('activeUuid', paintHero),
    subscribe('launch', paintHero),
  ];

  return { destroy: () => offs.forEach((f) => f()) };
}

/* ===========================================================================
   实例网格
   ========================================================================== */

function mountList(el) {
  const scroll = h('div', { class: 'page__scroll' });
  const head = h('div', { class: 'page__header' });
  fill(el, head, scroll);

  async function doCreate() {
    const nameInput = h('input', { class: 'input', placeholder: '例如：1.20.1 生存', maxlength: '48' });
    const loaderSel = customSelect({
      value: 'vanilla',
      ariaLabel: '加载器',
      options: ['vanilla', 'fabric', 'forge', 'neoforge', 'quilt'].map((l) => ({ value: l, label: loaderLabel(l) })),
    });

    // 版本清单有近千条，全部铺进下拉会很难用；只列正式版里最近的若干条，
    // 其余交给「下载」页完整挑选。
    const all = getState().versions;
    const releases = all.filter((v) => v.type === 'release').slice(0, 40);
    const versionSel = customSelect({
      value: releases[0]?.id ?? '',
      ariaLabel: '游戏版本',
      placeholder: releases.length ? '请选择游戏版本' : '版本清单尚未载入，可稍后再选',
      options: [
        ...releases.map((v) => ({ value: v.id, label: v.id })),
        ...(all.length > releases.length
          ? [{ value: '', label: '—— 更多版本请到「下载」页选择 ——', disabled: true }]
          : []),
      ],
    });

    const res = await dialog({
      title: '新建实例', icon: 'plus', width: 480,
      body: h('div', { class: 'col', style: { gap: 'var(--sp-6)' } },
        h('div', { class: 'field' }, h('label', { class: 'field__label', text: '实例名称' }), nameInput,
          h('div', { class: 'field__hint', text: '仅用于在启动器中区分，可以随时改名。' })),
        h('div', { class: 'row', style: { gap: 'var(--sp-6)' } },
          h('div', { class: 'field grow' }, h('label', { class: 'field__label', text: '游戏版本' }), versionSel),
          h('div', { class: 'field grow' }, h('label', { class: 'field__label', text: '加载器' }), loaderSel),
        ),
        h('div', { class: 'field__hint', text: '创建后会在游戏目录下建立一个独立文件夹，版本与模组互不影响。' }),
      ),
      actions: [
        { label: '取消', variant: 'ghost', value: null },
        { label: '创建', variant: 'primary', value: 'create' },
      ],
    });
    if (res !== 'create') return;

    try {
      const created = await invoke('instance.create', {
        name: nameInput.value.trim() || '新实例',
        versionId: versionSel.value || null,
        loader: loaderSel.value,
      });
      const list = await invoke('instance.list').catch(() => null);
      if (list) setState({ instances: list.instances ?? [], activeInstanceId: created?.id ?? getState().activeInstanceId });
      toast({ title: '实例已创建', desc: created?.name, kind: 'success' });
      go(SECTION, 'overview');
    } catch (e) {
      toast({ title: '创建失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  function instCard(inst, active) {
    const cover = inst.cover;
    return h('div', {
      class: 'inst-card', 'data-active': active ? 'true' : 'false', role: 'button', tabindex: '0',
      onClick: async () => {
        await invoke('instance.setActive', { id: inst.id }).catch(() => {});
        setState({ activeInstanceId: inst.id });
        go(SECTION);
      },
    },
      h('div', { class: 'inst-card__cover' },
        coverImg(cover, null),
        h('div', { class: 'inst-card__badge' },
          loaderLogo(inst.loader, 'xs'),
          h('span', { text: loaderLabel(inst.loader ?? 'vanilla') })),
      ),
      h('div', { class: 'inst-card__body' },
        h('div', { class: 'row-between', style: { gap: 'var(--sp-4)' } },
          h('div', { class: 'inst-card__title truncate', text: inst.name ?? '未命名实例' }),
          iconButton('more-horizontal', {
            size: 'sm', tip: '更多操作',
            onClick: (e) => { e.stopPropagation(); instMenu(e.currentTarget, inst); },
          }),
        ),
        h('div', { class: 'inst-card__meta' },
          h('span', { text: inst.versionId ?? '未选择版本' }),
          h('span', { class: 'dot' }),
          h('span', { text: inst.lastPlayedAt ? fmtAgo(inst.lastPlayedAt) : '尚未启动' }),
          h('span', { class: 'dot' }),
          h('span', { text: fmtBytes(inst.sizeBytes ?? 0) }),
        ),
      ),
    );
  }

  function instMenu(anchor, inst) {
    menu(anchor, [
      { label: '打开实例文件夹', icon: 'folder-open', onClick: () => invoke('system.openInstanceFolder', { id: inst.id }).catch(() => {}) },
      { label: '重命名', icon: 'pencil', onClick: () => renameInstance(inst) },
      { label: '创建快捷方式', icon: 'link', onClick: () => invoke('system.createShortcut', { id: inst.id }).catch((e) => toast({ title: '创建快捷方式失败', desc: String(e?.message ?? e), kind: 'danger' })) },
      { label: '复制实例', icon: 'copy', onClick: () => duplicate(inst) },
      { separator: true },
      { label: '导出为整合包', icon: 'upload', onClick: () => go(SECTION, 'export') },
      { separator: true },
      { label: '删除实例', icon: 'trash-2', danger: true, onClick: () => remove(inst) },
    ]);
  }

  async function renameInstance(inst) {
    const input = h('input', { class: 'input', value: inst.name ?? '' });
    const r = await dialog({
      title: '重命名实例', icon: 'pencil', width: 420,
      body: h('div', { class: 'field' }, h('label', { class: 'field__label', text: '实例名称' }), input),
      actions: [{ label: '取消', variant: 'ghost', value: null }, { label: '保存', variant: 'primary', value: 'ok' }],
    });
    if (r !== 'ok') return;
    await invoke('instance.update', { id: inst.id, patch: { name: input.value.trim() || inst.name } }).catch(() => {});
    await refresh();
  }

  async function duplicate(inst) {
    const input = h('input', { class: 'input', value: `${inst.name} 副本` });
    const r = await dialog({
      title: '复制实例', icon: 'copy', width: 440,
      body: h('div', { class: 'col', style: { gap: 'var(--sp-5)' } },
        h('div', { class: 'field' }, h('label', { class: 'field__label', text: '新实例名称' }), input),
        h('div', { class: 'field__hint', text: '会完整复制版本、模组与配置；存档不会复制，以免占用大量空间。' }),
      ),
      actions: [{ label: '取消', variant: 'ghost', value: null }, { label: '复制', variant: 'primary', value: 'ok' }],
    });
    if (r !== 'ok') return;
    try {
      await invoke('instance.duplicate', { id: inst.id, name: input.value.trim() });
      toast({ title: '实例已复制', kind: 'success' });
      await refresh();
    } catch (e) {
      toast({ title: '复制失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function remove(inst) {
    const input = h('input', { class: 'input', placeholder: inst.name });
    const delFiles = h('button', { class: 'checkbox', 'aria-checked': 'false', role: 'checkbox', type: 'button', tabindex: '0' });
    delFiles.addEventListener('click', () => delFiles.setAttribute('aria-checked', delFiles.getAttribute('aria-checked') === 'true' ? 'false' : 'true'));

    const r = await dialog({
      title: '删除实例', icon: 'alert-triangle', width: 470,
      body: h('div', { class: 'col', style: { gap: 'var(--sp-6)' } },
        h('div', { text: `即将删除实例「${inst.name}」。此操作不可撤销。` }),
        h('div', { class: 'field' },
          h('label', { class: 'field__label', text: `输入实例名称以确认` }),
          input,
        ),
        h('label', { class: 'check-row', style: { padding: '0' } }, delFiles,
          h('div', {}, h('div', { style: { fontSize: 'var(--fs-sm)' }, text: '同时删除磁盘上的游戏文件' }),
            h('div', { class: 'field__hint', text: '不勾选则只从启动器中移除，文件保留。' }))),
      ),
      actions: [{ label: '取消', variant: 'ghost', value: null }, { label: '删除', variant: 'danger', value: 'ok' }],
    });
    if (r !== 'ok') return;
    if (input.value.trim() !== (inst.name ?? '')) {
      toast({ title: '名称不匹配，已取消', kind: 'warning' });
      return;
    }
    try {
      await invoke('instance.remove', { id: inst.id, deleteFiles: delFiles.getAttribute('aria-checked') === 'true' });
      toast({ title: '实例已删除', kind: 'success' });
      await refresh();
      go(SECTION);
    } catch (e) {
      toast({ title: '删除失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function refresh() {
    const list = await invoke('instance.list').catch(() => null);
    if (list) setState({ instances: list.instances ?? [], activeInstanceId: list.activeId ?? getState().activeInstanceId });
  }

  function paint() {
    const list = getState().instances;
    const active = getState().activeInstanceId;

    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '实例管理' }),
        h('div', { class: 'page__subtitle', text: list.length ? `共 ${list.length} 个实例` : '还没有实例' }),
      ),
      h('div', { class: 'page__actions' },
        button('导入整合包', { variant: 'secondary', icon: 'folder-open', onClick: () => invoke('system.pickFile', { filter: '整合包 (*.zip;*.mrpack)|*.zip;*.mrpack' }).then((r) => r?.path && invoke('instance.importModpack', { path: r.path })).then(refresh).catch(() => {}) }),
        button('新建实例', { variant: 'primary', icon: 'plus', onClick: doCreate }),
      ),
    );

    if (!list.length) {
      fill(scroll, emptyState({
        icon: 'package-open',
        title: '还没有任何实例',
        desc: '创建一个实例来开始游戏。每个实例都是独立的游戏环境，可以安装不同版本与模组。',
        actions: [
          button('新建实例', { variant: 'primary', icon: 'plus', onClick: doCreate }),
          button('去下载版本', { variant: 'ghost', icon: 'download', onClick: () => go('download') }),
        ],
      }));
      return;
    }

    fill(scroll, h('div', { class: 'page__inner' },
      h('div', { class: 'inst-grid stagger' }, ...list.map((i) => instCard(i, i.id === active))),
    ));
  }

  paint();
  const off = subscribe('instances', paint);
  const off2 = subscribe('activeInstanceId', paint);
  return { destroy: () => { off(); off2(); } };
}

/* ===========================================================================
   实例内部页面（概览 / 内容管理 / 导出 / 高级）
   ========================================================================== */

function instanceShell(el, { title, subtitle, actions = [], body, icon: ic }) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);
  fill(head,
    h('div', {},
      h('div', { class: 'ghost-title' },
        h('span', { class: 'ghost-title__ghost', text: title }),
        h('div', { class: 'ghost-title__real', text: title }),
      ),
      subtitle ? h('div', { class: 'page__subtitle', text: subtitle }) : null,
    ),
    actions.length ? h('div', { class: 'page__actions' }, ...actions) : null,
  );
  void ic;
  return scroll;
}

/* —— 概览 —— */
function mountOverview(el) {
  const scroll = instanceShell(el, { title: '实例概览' });

  function paint() {
    const inst = getActiveInstance();
    if (!inst) { fill(scroll, emptyState({ icon: 'boxes', title: '没有选中的实例', desc: '先在左侧选择一个实例。' })); return; }

    const memSlider = h('input', {
      class: 'slider', type: 'range', min: '512', max: String(Math.max(4096, getState().app?.totalMemoryMb ?? 8192)), step: '256',
      value: String(inst.memoryMb ?? 4096),
      onInput: (e) => { e.target.style.setProperty('--pct', `${((e.target.value - e.target.min) / (e.target.max - e.target.min)) * 100}%`); }
    });
    memSlider.style.setProperty('--pct', `${((memSlider.value - 512) / (memSlider.max - 512)) * 100}%`);
    const memLabel = h('span', { class: 'mono', style: { fontSize: 'var(--fs-sm)', minWidth: '78px', textAlign: 'right' }, text: `${(inst.memoryMb ?? 4096) / 1024} GB` });
    memSlider.addEventListener('input', () => { memLabel.textContent = `${(memSlider.value / 1024).toFixed(1)} GB`; });

    const javas = getState().javas;
    const javaSel = customSelect({
      value: inst.javaPath ?? '',
      ariaLabel: 'Java 运行时',
      placeholder: javas.length ? '自动选择（推荐）' : '未检测到 Java，请前往设置添加',
      options: javas.length
        ? [
            { value: '', label: '自动选择（推荐）' },
            ...javas.map((j) => ({
              value: j.path,
              label: `Java ${j.major} · ${j.vendor ?? ''} ${j.arch ?? ''}`,
              hint: j.version ?? '',
            })),
          ]
        : [{ value: '', label: '未检测到 Java，请前往设置添加', disabled: true }],
    });

    fill(scroll, h('div', { class: 'page__inner' },
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('info', 'md'), h('span', { text: '基本信息' })),
        h('div', { class: 'set-row' }, h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '实例名称' }), h('div', { class: 'set-row__desc', text: inst.path ?? '' })),
          h('div', { class: 'set-row__control' }, h('span', { class: 'mono', style: { fontSize: 'var(--fs-sm)' }, text: inst.name ?? '' }))),
        h('div', { class: 'set-row' }, h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '游戏版本' }), h('div', { class: 'set-row__desc', text: '可从「下载」页安装新版本，或在启动页切换。' })),
          h('div', { class: 'set-row__control' },
            h('span', { class: 'version-tag', text: inst.versionId ?? '未选择' }),
            button('更改', { variant: 'ghost', size: 'sm', onClick: () => go('download') }))),
        h('div', { class: 'set-row' }, h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '模组加载器' })),
          h('div', { class: 'set-row__control' }, chip(loaderLabel(inst.loader ?? 'vanilla'), { variant: inst.loader && inst.loader !== 'vanilla' ? 'accent' : undefined }))),
        h('div', { class: 'set-row' }, h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '磁盘占用' })),
          h('div', { class: 'set-row__control' }, h('span', { class: 'mono', style: { fontSize: 'var(--fs-sm)' }, text: fmtBytes(inst.sizeBytes ?? 0) }))),
        h('div', { class: 'set-row' }, h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '上次游玩' })),
          h('div', { class: 'set-row__control' }, h('span', { style: { fontSize: 'var(--fs-sm)', color: 'var(--text-2)' }, text: inst.lastPlayedAt ? fmtDateTime(inst.lastPlayedAt) : '尚未启动' }))),
      ),

      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('cpu', 'md'), h('span', { text: '运行参数' })),
        h('div', { class: 'set-section__desc', text: '这些参数只作用于当前实例。' }),
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '最大内存' }), h('div', { class: 'set-row__desc', text: `本机共 ${(getState().app?.totalMemoryMb ?? 0) / 1024 || '?'} GB。分配给游戏的内存越大，能装的模组越多，但留给系统要留足余量。` })),
          h('div', { class: 'set-row__control' }, memSlider, memLabel)),
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: 'Java 运行时' }), h('div', { class: 'set-row__desc', text: '不同游戏版本对 Java 版本有要求，留空则由启动器自动匹配。' })),
          h('div', { class: 'set-row__control' }, javaSel)),
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '全屏启动' }), h('div', { class: 'set-row__desc', text: '以全屏模式进入游戏。' })),
          h('div', { class: 'set-row__control' }, switchControl(!!inst.fullscreen))),
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, h('div', { class: 'set-row__label', text: '游戏窗口尺寸' }), h('div', { class: 'set-row__desc', text: '非全屏时的初始窗口大小。' })),
          h('div', { class: 'set-row__control' }, sizeInputs())),
      ),

      h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-8)' } },
        button('保存并启动', { variant: 'primary', icon: 'play', onClick: async () => {
          await save({ memoryMb: Number(memSlider.value), javaPath: javaSel.value || null });
          go(SECTION);
        } }),
        button('保存', { variant: 'secondary', icon: 'check', onClick: () => save({ memoryMb: Number(memSlider.value), javaPath: javaSel.value || null }) }),
        button('打开文件夹', { variant: 'ghost', icon: 'folder-open', onClick: () => invoke('system.openInstanceFolder', { id: inst.id }).catch(() => {}) }),
      ),
    ));
  }

  function switchControl(checked) {
    const b = h('button', { class: 'switch', type: 'button', role: 'switch', 'aria-checked': checked ? 'true' : 'false', onClick: () => b.setAttribute('aria-checked', b.getAttribute('aria-checked') === 'true' ? 'false' : 'true') });
    return b;
  }

  function sizeInputs() {
    const inst = getActiveInstance() ?? {};
    return h('div', { class: 'row', style: { gap: 'var(--sp-3)' } },
      h('input', { class: 'input', style: { width: '86px' }, type: 'number', value: String(inst.windowWidth ?? 854) }),
      h('span', { class: 'dim', text: '×' }),
      h('input', { class: 'input', style: { width: '86px' }, type: 'number', value: String(inst.windowHeight ?? 480) }),
    );
  }

  async function save(patch) {
    const inst = getActiveInstance();
    if (!inst) return;
    await invoke('instance.update', { id: inst.id, patch }).catch(() => {});
    const r = await invoke('instance.list').catch(() => null);
    if (r) setState({ instances: r.instances ?? [] });
    toast({ title: '已保存', kind: 'success' });
  }

  paint();
  const off = subscribe('activeInstanceId', paint);
  const off2 = subscribe('instances', paint);
  return { destroy: () => { off(); off2(); } };
}

/* —— 内容管理（模组 / 资源包 / 光影 / 存扭 / 备份 共用） —— */
const CONTENT_META = {
  mods: { title: '模组', icon: 'puzzle', kind: 'mod', accept: '.jar', hint: '模组需要与游戏版本和加载器都匹配。' },
  resourcepacks: { title: '资源包', icon: 'image', kind: 'resourcepack', accept: '.zip', hint: '资源包可以叠加，顺序决定优先级。' },
  shaders: { title: '光影包', icon: 'sparkles', kind: 'shader', accept: '.zip', hint: '光影包需要先安装 Iris 或 OptiFine。' },
  worlds: { title: '存档', icon: 'earth', kind: 'world', accept: '', hint: '每个存档都可以单独备份或导出。' },
  backups: { title: '备份', icon: 'file-archive', kind: 'backup', accept: '.zip', hint: '备份保存了当前实例的完整状态。' },
};

function mountContent(kind) {
  return (el) => {
    const meta = CONTENT_META[kind];
    const scroll = instanceShell(el, { title: meta.title });
    let query = '';
    let fil = 'all';

    async function load() {
      const inst = getActiveInstance();
      if (!inst) return { items: [] };
      return invoke('content.scan', { instanceId: inst.id, kind: meta.kind }).catch(() => ({ items: [] }));
    }

    async function paint() {
      const inst = getActiveInstance();
      if (!inst) { fill(scroll, emptyState({ icon: 'boxes', title: '没有选中的实例' })); return; }

      fill(scroll, h('div', { class: 'page__inner' }, h('div', { class: 'skeleton', style: { height: '120px' } })));

      const res = await load();
      const all = res?.items ?? [];
      const items = all.filter((it) => {
        if (query && !((it.name ?? '') + (it.fileName ?? '')).toLowerCase().includes(query.toLowerCase())) return false;
        if (fil === 'enabled' && it.disabled) return false;
        if (fil === 'disabled' && !it.disabled) return false;
        return true;
      });

      const enabled = all.filter((i) => !i.disabled).length;

      const searchInput = h('input', { class: 'input', placeholder: `搜索${meta.title}…`, onInput: (e) => { query = e.target.value.trim(); paint(); } });
      searchInput.value = query;

      fill(scroll, h('div', { class: 'page__inner' },
        h('div', { class: 'toolbar' },
          h('div', { class: 'search' }, icon('search', 'sm'), searchInput),
          segButton('全部', fil === 'all', () => { fil = 'all'; paint(); }),
          segButton('已启用', fil === 'enabled', () => { fil = 'enabled'; paint(); }),
          segButton('已禁用', fil === 'disabled', () => { fil = 'disabled'; paint(); }),
          h('div', { class: 'toolbar__divider' }),
          button('本地导入', { variant: 'ghost', size: 'sm', icon: 'upload', onClick: () => importLocal() }),
          button('在线获取', { variant: 'soft', size: 'sm', icon: 'cloud-download', onClick: () => go('resources', meta.kind === 'mod' ? 'mod' : meta.kind === 'shader' ? 'shader' : 'resourcepack') }),
        ),
        h('div', { class: 'card', style: { marginBottom: 'var(--sp-7)', padding: 'var(--sp-6)' } },
          h('div', { class: 'row-between' },
            h('div', {},
              h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' }, text: `${meta.title}概览` }),
              h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px' }, text: all.length ? `共 ${all.length} 项，已启用 ${enabled} 项` : `尚未安装任何${meta.title}` }),
            ),
            h('div', { class: 'row', style: { gap: 'var(--sp-4)' } },
              button('打开文件夹', { variant: 'ghost', size: 'sm', icon: 'folder-open', onClick: () => invoke('system.openContentFolder', { instanceId: inst.id, kind: meta.kind }).catch(() => {}) }),
            ),
          ),
        ),

        items.length
          ? h('div', { class: 'rows' },
              h('div', { class: 'rows-head' },
                h('span'), h('span', { text: '名称' }), h('span', { text: '大小' }), h('span', { text: '状态' })),
              ...items.map((it) => contentRow(inst, it)),
            )
          : emptyState({
              icon: meta.icon,
              title: all.length ? '没有匹配的结果' : `还没有${meta.title}`,
              desc: all.length ? '换个关键词或切换筛选条件试试。' : meta.hint,
              actions: [
                button('在线获取', { variant: 'primary', icon: 'cloud-download', onClick: () => go('resources') }),
                button('本地导入', { variant: 'secondary', icon: 'upload', onClick: () => importLocal() }),
              ],
            }),
      ));
    }

    function segButton(label, active, onClick) {
      return h('button', { class: 'filter-btn', type: 'button', 'data-active': active ? 'true' : 'false', onClick }, h('span', { text: label }));
    }

    function contentRow(inst, it) {
      const sw = h('button', { class: 'switch', type: 'button', role: 'switch', 'aria-checked': it.disabled ? 'false' : 'true' });
      sw.addEventListener('click', async () => {
        const next = sw.getAttribute('aria-checked') !== 'true';
        sw.setAttribute('aria-checked', next ? 'true' : 'false');
        await invoke('content.toggle', { instanceId: inst.id, kind: meta.kind, path: it.path, enabled: next }).catch((e) => toast({ title: '操作失败', desc: String(e?.message ?? e), kind: 'danger' }));
      });

      return h('div', { class: 'row-item', 'data-disabled': it.disabled ? 'true' : 'false' },
        h('div', { class: 'row-item__icon' }, it.iconUrl ? h('img', { src: it.iconUrl, alt: '' }) : icon(meta.icon, 'sm')),
        h('div', { style: { minWidth: 0 } },
          h('div', { class: 'row-item__name' },
            h('span', { class: 'truncate', text: it.name ?? it.fileName ?? '未知' }),
            h('div', { class: 'row-item__chips' }, ...(it.tags ?? []).slice(0, 3).map((t) => chip(t))),
          ),
          h('div', { class: 'row-item__file truncate', text: it.fileName ?? '' }),
        ),
        h('div', { class: 'row-item__trail', text: fmtBytes(it.sizeBytes ?? 0) }),
        h('div', { class: 'row-item__trail' },
          sw,
          iconButton('more-vertical', { size: 'sm', tip: '更多', onClick: (e) => menu(e.currentTarget, [
            { label: it.disabled ? '启用' : '禁用', icon: it.disabled ? 'check' : 'x', onClick: () => invoke('content.toggle', { instanceId: inst.id, kind: meta.kind, path: it.path, enabled: it.disabled }).then(() => paint()) },
            { label: '在文件夹中显示', icon: 'folder-open', onClick: () => invoke('system.revealPath', { path: it.path }).catch(() => {}) },
            { separator: true },
            { label: '删除', icon: 'trash-2', danger: true, onClick: () => confirm({ title: '删除确认', message: `确定删除「${it.name ?? it.fileName}」？文件将从磁盘移除。`, danger: true }).then((ok) => ok && invoke('content.remove', { instanceId: inst.id, kind: meta.kind, path: it.path }).then(paint)) },
          ]) }),
        ),
      );
    }

    async function importLocal() {
      const inst = getActiveInstance();
      try {
        const r = await invoke('system.pickFiles', { title: `选择要导入的${meta.title}`, filter: `${meta.title}|*${meta.accept || '.*'}` });
        if (!r?.paths?.length) return;
        await invoke('content.import', { instanceId: inst.id, kind: meta.kind, paths: r.paths });
        toast({ title: `已导入 ${r.paths.length} 个文件`, kind: 'success' });
        paint();
      } catch (e) {
        toast({ title: '导入失败', desc: e?.message ?? String(e), kind: 'danger' });
      }
    }

    paint();
    const off = subscribe('activeInstanceId', paint);
    return { destroy: () => off() };
  };
}

/* —— 导出 —— */
function mountExport(el) {
  const scroll = instanceShell(el, { title: '导出整合包' });
  function paint() {
    const inst = getActiveInstance();
    if (!inst) { fill(scroll, emptyState({ icon: 'boxes', title: '没有选中的实例' })); return; }

    const nameIn = h('input', { class: 'input', value: inst.name ?? '' });
    const verIn = h('input', { class: 'input', value: '1.0.0' });
    const authorIn = h('input', { class: 'input', placeholder: '你的名字' });
    const includeSaves = checkLine('包含存档', '把地图一并打包，整合包会明显变大。');
    const includeConfig = checkLine('包含配置', 'mods 的配置文件与光影设置。');
    const packMcmeta = checkLine('生成 Modrinth 格式', '可直接发布到 Modrinth 的 .mrpack。');

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '760px' } },
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('upload', 'md'), h('span', { text: '导出为整合包' })),
        h('div', { class: 'set-section__desc', text: '把当前实例的模组与配置打包成一个可分发的整合包文件。' }),
        h('div', { class: 'field', style: { marginBottom: 'var(--sp-6)' } }, h('label', { class: 'field__label', text: '整合包名称' }), nameIn),
        h('div', { class: 'row', style: { gap: 'var(--sp-6)' } },
          h('div', { class: 'field grow' }, h('label', { class: 'field__label', text: '版本号' }), verIn),
          h('div', { class: 'field grow' }, h('label', { class: 'field__label', text: '作者' }), authorIn),
        ),
      ),
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('sliders-horizontal', 'md'), h('span', { text: '包含内容' })),
        includeSaves, includeConfig, packMcmeta,
      ),
      h('div', { class: 'row', style: { gap: 'var(--sp-4)' } },
        button('开始导出', { variant: 'primary', icon: 'file-archive', onClick: async () => {
          try {
            const r = await invoke('instance.export', {
              id: inst.id,
              name: nameIn.value.trim(),
              version: verIn.value.trim(),
              author: authorIn.value.trim(),
              includeSaves: includeSaves.dataset.on === 'true',
              includeConfig: includeConfig.dataset.on === 'true',
              modrinthFormat: packMcmeta.dataset.on === 'true',
            });
            if (r?.path) toast({ title: '导出完成', desc: r.path, kind: 'success', timeout: 9000 });
          } catch (e) {
            toast({ title: '导出失败', desc: e?.message ?? String(e), kind: 'danger' });
          }
        } }),
      ),
    ));
  }

  function checkLine(title, desc, on = false) {
    const box = h('button', { class: 'checkbox', type: 'button', role: 'checkbox', 'aria-checked': on ? 'true' : 'false' });
    const row = h('label', { class: 'check-row' }, box,
      h('div', {}, h('div', { style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)' }, text: title }), h('div', { class: 'field__hint', text: desc })));
    row.dataset.on = String(on);
    box.addEventListener('click', (e) => {
      e.preventDefault();
      const next = box.getAttribute('aria-checked') !== 'true';
      box.setAttribute('aria-checked', next ? 'true' : 'false');
      row.dataset.on = String(next);
    });
    return row;
  }

  paint();
  const off = subscribe('activeInstanceId', paint);
  return { destroy: () => off() };
}

/* —— 高级 —— */
function mountAdvanced(el) {
  const scroll = instanceShell(el, { title: '高级设置' });
  function paint() {
    const inst = getActiveInstance();
    if (!inst) { fill(scroll, emptyState({ icon: 'boxes', title: '没有选中的实例' })); return; }

    const argsIn = h('textarea', { class: 'textarea', rows: '4', placeholder: '-XX:+UseG1GC -Dfile.encoding=UTF-8' });
    argsIn.value = inst.jvmArgs ?? '';
    const gameArgsIn = h('textarea', { class: 'textarea', rows: '3', placeholder: '传给游戏本身的参数，例如 --demo' });
    gameArgsIn.value = inst.gameArgs ?? '';

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '820px' } },
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('terminal', 'md'), h('span', { text: 'JVM 参数' })),
        h('div', { class: 'set-section__desc', text: '会追加到默认参数之后。不确定就不要改。' }),
        argsIn,
      ),
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('gamepad-2', 'md'), h('span', { text: '游戏参数' })),
        gameArgsIn,
      ),
      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('wrench', 'md'), h('span', { text: '启动前 / 后命令' })),
        h('div', { class: 'set-section__desc', text: '可用于自动打开加速器、同步存档等。留空则不执行。' }),
        h('div', { class: 'col', style: { gap: 'var(--sp-5)' } },
          h('input', { class: 'input mono', placeholder: '启动前命令', value: inst.preLaunchCommand ?? '' }),
          h('input', { class: 'input mono', placeholder: '启动后命令', value: inst.postLaunchCommand ?? '' }),
        ),
      ),
      h('div', { class: 'row', style: { gap: 'var(--sp-4)' } },
        button('保存', { variant: 'primary', icon: 'check', onClick: async () => {
          await invoke('instance.update', { id: inst.id, patch: { jvmArgs: argsIn.value, gameArgs: gameArgsIn.value } }).catch(() => {});
          toast({ title: '已保存', kind: 'success' });
        } }),
        button('恢复默认', { variant: 'ghost', icon: 'rotate-ccw', onClick: async () => {
          await invoke('instance.update', { id: inst.id, patch: { jvmArgs: '', gameArgs: '' } }).catch(() => {});
          paint();
        } }),
      ),
    ));
  }
  paint();
  const off = subscribe('activeInstanceId', paint);
  return { destroy: () => off() };
}

/* ===========================================================================
   导出模块定义
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [`${SECTION}`]: {
      title: '启动',
      icon: 'play',
      mount: (el) => mountHero(el),
    },
    [`${SECTION}:list`]: {
      title: '实例管理',
      icon: 'boxes',
      mount: (el) => mountList(el),
    },
    [`${SECTION}:overview`]: {
      title: '实例概览',
      icon: 'layout-dashboard',
      mount: (el) => mountOverview(el),
    },
    [`${SECTION}:worlds`]: { title: '存档', icon: 'earth', mount: mountContent('worlds') },
    [`${SECTION}:mods`]: { title: '模组', icon: 'puzzle', mount: mountContent('mods') },
    [`${SECTION}:resourcepacks`]: { title: '资源包', icon: 'image', mount: mountContent('resourcepacks') },
    [`${SECTION}:shaders`]: { title: '光影包', icon: 'sparkles', mount: mountContent('shaders') },
    [`${SECTION}:backups`]: { title: '备份', icon: 'file-archive', mount: mountContent('backups') },
    [`${SECTION}:export`]: { title: '导出整合包', icon: 'upload', mount: (el) => mountExport(el) },
    [`${SECTION}:advanced`]: { title: '高级设置', icon: 'sliders-horizontal', mount: (el) => mountAdvanced(el) },
  },
};
