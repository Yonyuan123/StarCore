/**
 * download.js —— 「下载」分区。
 *
 * 页面组成：
 *   download             版本下载：按类型筛选 + 搜索，单个或批量安装到实例
 *   download:loaders     加载器安装：Forge / NeoForge / Fabric / Quilt / OptiFine
 *   download:modpack     整合包安装：从本地文件导入 .mrpack / CurseForge zip / HMCL 整合包
 *
 * 约定：
 *   · 数据全部来自 invoke()，没有任何内置演示数据；
 *   · 版本清单可能上千条，列表最多渲染前 300 行，靠搜索缩小范围；
 *   · 安装进度一律靠内核推送的 task.update 驱动，页面不做任何轮询。
 */

import { invoke, on } from '../scripts/bridge.js';
import { getState, setState, subscribe, getActiveInstance, upsertTask } from '../scripts/store.js';
import {
  h, fill, icon, iconButton, button, emptyState, chip, loaderLogo, versionIcon,
  toast, menu, dialog, fmtDate, fmtAgo, progress,
  selectControl as customSelect,
} from '../scripts/ui.js';
import { go } from '../scripts/router.js';

const SECTION = 'download';

/** 清单最长只渲染这么多行，避免上千个版本一次性塞进 DOM。 */
const MAX_ROWS = 300;

/** 加载器版本下拉最多列这么多条，避免长列表把面板撑爆。 */
const MAX_LOADER_VERSIONS = 80;

/* ===========================================================================
   模块级共享状态：二级面板与页面约定同一个偏好
   ========================================================================== */

/** 版本类型筛选偏好：all | release | snapshot。面板与页面共用。 */
let versionFilter = 'all';

/** 版本下载页选定的安装目标实例 id。0 表示「自动创建新实例」——这是默认值。 */
const AUTO_INSTANCE = 0;
let installTargetId = AUTO_INSTANCE;

/**
 * Fabric API 在 Modrinth 的项目 ID（slug 是 fabric-api）。
 *
 * 装 Fabric 加载器时顺带把它装上：在 Fabric 生态里它不是"可选的增强模组"，
 * 而是绝大多数模组的事实前提 —— 没有它，模组会被静默跳过。
 * 新手通常不知道这一点，装完模组发现游戏里什么都没有，只会觉得启动器有问题。
 */
const FABRIC_API_ID = 'P7dR8mSH';

/* ===========================================================================
   一键安装
   ---------------------------------------------------------------------------
   旧流程要求「先建实例 → 再选中 → 才能下载」，三步里有两步是纯仪式感。
   PCL / HMCL 都是点下载就直接开装，实例是安装的产物。这里改成同一个模型：
   默认不指定实例，交给内核按「版本-加载器_版本号」自动建好并命名。
   ========================================================================== */

/** taskId → 安装信息，用于在安装完成后询问「是否立即启动」。 */
const pendingLaunch = new Map();

/** 从版本页「带加载器安装…」跳过来时，预选哪个游戏版本。 */
let pendingGameVersion = null;

/** 找出已经装了这个版本的实例，用于把「安装」按钮换成「启动」。 */
function installedFor(versionId) {
  if (!versionId) return null;
  return getState().instances.find((i) => i.versionId === versionId) ?? null;
}

/** 启动指定实例，并同步界面状态。 */
async function launchInstance(inst) {
  try {
    await invoke('instance.setActive', { id: inst.id }).catch(() => {});
    setState({ activeInstanceId: inst.id });
    await invoke('launch.start', { instanceId: inst.id });
    toast({ title: '正在启动', desc: `${inst.name}`, kind: 'accent' });
  } catch (e) {
    toast({
      title: '启动失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000,
      action: { label: '查看日志', onClick: () => go('logs') },
    });
  }
}

/* 安装完成 → 问一句要不要直接玩。
   把它挂在模块级而不是页面里：安装是后台任务，用户很可能已经切到别的页面去了。 */
on('install.done', (payload) => {
  const info = pendingLaunch.get(payload?.taskId);
  if (!info) return;
  pendingLaunch.delete(payload.taskId);

  const instId = payload.instanceId ?? info.instanceId;
  const name = info.instanceName ?? payload.versionId ?? '新实例';

  dialog({
    title: '安装完成',
    icon: 'circle-check',
    width: 420,
    body: h('div', { class: 'col', style: { gap: 'var(--sp-4)' } },
      h('div', { style: { fontSize: 'var(--fs-sm)' }, text: `${name} 已经准备好了，现在开始游戏吗？` }),
      h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '也可以稍后在「实例」页启动，或先装点模组再玩。' }),
    ),
    actions: [
      { label: '稍后', variant: 'ghost' },
      {
        label: '立即启动', variant: 'primary', icon: 'play',
        onClick: async () => {
          const inst = (await invoke('instance.list').catch(() => null))?.instances
            ?.find((i) => i.id === instId);
          if (inst) await launchInstance(inst);
          else toast({ title: '找不到刚安装的实例', kind: 'warning' });
        },
      },
    ],
  });
});

/* 安装失败也要清掉等待表，否则用户下次成功安装时会弹出上一次的提示 */
on('install.failed', (payload) => { pendingLaunch.delete(payload?.taskId); });

/** 面板点「刷新版本清单」后，通知已经挂载的页面重新从内核拉取清单。 */
const manifestListeners = new Set();
function notifyManifest() {
  for (const fn of [...manifestListeners]) {
    try { fn(); } catch (e) { console.error('[download] 清单刷新通知失败', e); }
  }
}

/** 面板切换筛选偏好后，通知已经挂载的版本页按新偏好就地重绘（不必重新拉清单）。 */
const filterListeners = new Set();
function notifyFilter() {
  for (const fn of [...filterListeners]) {
    try { fn(); } catch (e) { console.error('[download] 筛选重绘通知失败', e); }
  }
}

function targetInstance() {
  return getState().instances.find((i) => i.id === installTargetId) ?? null;
}

/** 默认目标：显式选择过就用它，否则退回到当前活动实例。 */
function resolveInstance() {
  return targetInstance() ?? getActiveInstance();
}

function instanceName(inst) {
  return inst?.name ?? '未命名实例';
}

const TYPE_META = {
  release: { cls: 'type-pill--release', label: '正式版' },
  snapshot: { cls: 'type-pill--snapshot', label: '快照' },
  old_beta: { cls: 'type-pill--old_beta', label: '旧版' },
  old_alpha: { cls: 'type-pill--old_alpha', label: '旧版' },
};
const typeMeta = (t) => TYPE_META[t] ?? { cls: 'type-pill--old_beta', label: '旧版' };

const filterMatches = (type) => {
  if (versionFilter === 'release') return type === 'release';
  if (versionFilter === 'snapshot') return type === 'snapshot';
  return true;
};

/* ===========================================================================
   公共零件
   ========================================================================== */

/** 取路径里的文件名（导入整合包时用于任务标题）。 */
function baseName(p) {
  return String(p ?? '').split(/[\\/]/).pop() || '整合包';
}

/** 正在运行的任务卡片；没有运行中的任务时返回 null。 */
function taskCard(title, match) {
  const running = getState().tasks.filter(
    (t) => (t.state === 'running' || t.state === 'queued') && (!match || match(t)),
  );
  if (!running.length) return null;

  return h('div', { class: 'card', style: { marginBottom: 'var(--sp-7)', padding: 'var(--sp-6)' } },
    h('div', { style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)' }, text: title }),
    ...running.map((t) => h('div', { class: 'col', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-5)' } },
      h('div', { class: 'row-between', style: { gap: 'var(--sp-5)' } },
        h('div', { class: 'truncate', style: { fontSize: 'var(--fs-sm)' }, text: t.title ?? '任务' }),
        h('div', { class: 'dim mono', style: { fontSize: 'var(--fs-micro)', whiteSpace: 'nowrap' }, text: t.detail ?? `${Math.round(t.progress ?? 0)}%` }),
      ),
      progress(t.progress ?? 0, { size: 'sm' }),
    )),
  );
}

/** 复制到剪贴板；WebView 里失败时给一句可操作的提示，不抛异常。 */
async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast({ title: '已复制', desc: text, kind: 'success' });
  } catch {
    toast({ title: '复制失败', desc: '剪贴板不可用，请手动选中复制。', kind: 'warning' });
  }
}

/**
 * 开始一个安装任务：内核返回 taskId 后交给任务中心跟踪。
 * 返回内核的完整响应（含自动创建的实例 id / 名称），调用方据此显示反馈。
 */
/**
 * 给刚装好 Fabric 的实例补上 Fabric API。
 *
 * 版本由**用户所选的游戏版本**决定（Fabric API 的版本号跟着游戏版本走，
 * 例如 0.92.x 对应 1.20.1），而不是跟着 Fabric 加载器版本 —— 加载器版本
 * 只影响运行时，与 API 的兼容性无关。
 *
 * 这里刻意不让失败中断主流程：加载器本身已经装好了，
 * 少一个 API 是"模组可能不生效"，而抛错会让用户以为整个安装失败。
 */
async function installFabricApi(instanceId, gameVersion) {
  if (!instanceId || !gameVersion) return;

  try {
    const r = await invoke('modrinth.versions', { id: FABRIC_API_ID });
    const list = r?.versions ?? (Array.isArray(r) ? r : []);
    if (!list.length) return;

    const compatible = list
      .filter((v) => {
        const gvs = v.gameVersions ?? [];
        const lds = (v.loaders ?? []).map((x) => String(x).toLowerCase());
        return gvs.includes(gameVersion) && lds.includes('fabric');
      })
      // 上游（Modrinth）本身就是按发布时间倒序，但这里显式再排一次 ——
      // 后端重新映射过字段，不该让"选哪个"依赖上游的返回顺序。
      .sort((a, b) => String(b.datePublished ?? '').localeCompare(String(a.datePublished ?? '')));

    // 优先正式版；某个游戏版本只出了测试版时也认
    const hit = compatible.find((v) => !v.versionType || v.versionType === 'release')
             ?? compatible[0];

    if (!hit) {
      toast({
        title: 'Fabric API 没有匹配的版本',
        desc: `Modrinth 上没有面向 ${gameVersion} 的 Fabric API，需要时可以稍后手动安装。`,
        kind: 'warning', timeout: 9000,
      });
      return;
    }

    const res = await invoke('modrinth.install', {
      projectId: FABRIC_API_ID,
      versionId: hit.id,
      instanceId,
      kind: 'mod',
      gameVersion,
      loader: 'fabric',
    });

    if (res?.ok === false) {
      toast({ title: 'Fabric API 安装失败', desc: res?.message ?? '未知原因', kind: 'warning', timeout: 8000 });
    } else {
      toast({
        title: 'Fabric API 已一并安装',
        desc: `${hit.versionNumber ?? hit.name ?? hit.id} —— 大多数 Fabric 模组都依赖它`,
        kind: 'success', timeout: 7000,
      });
    }
  } catch (e) {
    toast({ title: 'Fabric API 安装失败', desc: e?.message ?? String(e), kind: 'warning', timeout: 8000 });
  }
}

async function startInstall(payload, title, trackLaunch = true) {
  try {
    const r = await invoke('version.install', payload);
    if (!r?.taskId) {
      toast({ title: '安装未能开始', desc: r?.message ?? '内核没有返回任务号。', kind: 'warning' });
      return null;
    }
    upsertTask({ id: r.taskId, title, state: 'running', progress: 0 });
    if (trackLaunch) {
      pendingLaunch.set(r.taskId, {
        versionId: payload.versionId,
        instanceId: r.instanceId,
        instanceName: r.instanceName,
      });
    }
    return r;
  } catch (e) {
    toast({ title: '安装失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
    return null;
  }
}

/**
 * 选择安装目标的弹出菜单（版本页与加载器页共用）。
 * 第一项永远是「自动创建新实例」——它是默认值，也是绝大多数人想要的。
 */
function openTargetMenu(anchor, onChange) {
  const list = getState().instances;
  const apply = (id) => { installTargetId = id; onChange?.(); };

  menu(anchor, [
    {
      label: '自动创建新实例',
      hint: installTargetId === AUTO_INSTANCE ? '当前' : '推荐',
      icon: 'sparkles',
      onClick: () => apply(AUTO_INSTANCE),
    },
    ...(list.length
      ? [
          { separator: true },
          ...list.map((i) => ({
            label: instanceName(i),
            hint: i.id === installTargetId ? '当前' : (i.versionId ?? '未配置版本'),
            icon: 'boxes',
            onClick: () => apply(i.id),
          })),
        ]
      : []),
  ]);
}

/** 工具栏里的「安装目标」按钮。 */
function targetButton(onChange) {
  const label = h('span', { text: '自动创建新实例' });
  const btn = h('button', {
    class: 'filter-btn', type: 'button', 'data-active': 'false',
    'aria-label': '选择安装到哪个实例',
    onClick: (e) => openTargetMenu(e.currentTarget, onChange),
  }, icon('boxes', 'xs'), h('span', { class: 'dim', text: '安装到' }), label, icon('chevron-down', 'xs'));

  const sync = () => {
    const inst = targetInstance();
    // 默认不显示"自动创建"这个长文案之外的东西；选中具体实例才高亮，
    // 让人一眼看出"我现在是在往某个已有实例里装"，避免误装。
    label.textContent = inst ? instanceName(inst) : '自动创建新实例';
    btn.setAttribute('data-active', inst ? 'true' : 'false');
  };
  sync();
  return { btn, sync };
}

/* ===========================================================================
   页面 1：版本下载
   ========================================================================== */

function createVersionsPage() {
  let reload = () => {};

  return {
    title: '版本下载',
    icon: 'download',

    mount(el) {
      const head = h('div', { class: 'page__header' });
      const scroll = h('div', { class: 'page__scroll' });
      fill(el, head, scroll);

      /** 清单缓存：versions / cachedAt / stale 全部来自内核。 */
      let manifest = { versions: [], cachedAt: null, stale: false };
      let query = '';
      let loading = true;   // 一进页面就进入加载态，避免先闪一帧「清单为空」
      let failed = false;
      const selected = new Set();

      /* —— 工具栏：只构建一次，搜索框才不会因重绘丢焦点 —— */
      const searchInput = h('input', {
        class: 'input',
        placeholder: '搜索版本号，例如 1.20.1',
        'aria-label': '搜索版本号',
        onInput: (e) => { query = e.target.value.trim(); paintList(); },
      });

      const filterBtns = {};
      const makeFilter = (id, label) => {
        const btn = h('button', {
          class: 'filter-btn', type: 'button', 'data-active': 'false',
          onClick: () => { versionFilter = id; notifyFilter(); },
        }, h('span', { text: label }));
        filterBtns[id] = btn;
        return btn;
      };

      const target = targetButton(() => { syncToolbar(); paintList(); });
      const toolbar = h('div', { class: 'toolbar' },
        h('div', { class: 'search' }, icon('search', 'sm'), searchInput),
        makeFilter('all', '全部'),
        makeFilter('release', '正式版'),
        makeFilter('snapshot', '快照'),
        h('div', { class: 'toolbar__divider' }),
        target.btn,
      );

      const taskBox = h('div');
      const listBox = h('div');
      fill(scroll, h('div', { class: 'page__inner' }, toolbar, taskBox, listBox));

      function syncToolbar() {
        for (const [id, btn] of Object.entries(filterBtns)) {
          btn.setAttribute('data-active', versionFilter === id ? 'true' : 'false');
        }
        target.sync();
      }

      function subtitle() {
        if (loading && !manifest.versions.length) return '正在载入版本清单…';
        const total = manifest.versions.length;
        const parts = [total ? `共 ${total} 个版本` : '版本清单为空'];
        if (manifest.cachedAt) parts.push(`清单更新于 ${fmtAgo(manifest.cachedAt)}`);
        if (manifest.stale) parts.push('缓存已过期');
        return parts.join(' · ');
      }

      function paintHead() {
        fill(head,
          h('div', {},
            h('div', { class: 'page__title', text: '版本下载' }),
            h('div', { class: 'page__subtitle', text: subtitle() }),
          ),
          h('div', { class: 'page__actions' },
            button('刷新清单', { variant: 'ghost', icon: 'refresh-cw', onClick: doRefresh }),
            button('安装所选', {
              variant: 'primary', icon: 'download', disabled: !selected.size,
              onClick: () => installSelected(),
            }),
          ),
        );
      }

      function paintTasks() {
        fill(taskBox, taskCard('进行中的任务'));
      }

      function paintList() {
        const all = manifest.versions;
        const list = all.filter((v) => filterMatches(v.type) && (!query || String(v.id ?? '').toLowerCase().includes(query.toLowerCase())));

        if (loading && !all.length) {
          fill(listBox, h('div', { class: 'skeleton', style: { height: '180px' } }));
          return;
        }
        if (!all.length) {
          fill(listBox, emptyState({
            icon: 'cloud-download',
            title: failed ? '版本清单载入失败' : '版本清单尚未载入',
            desc: failed
              ? '无法从 Mojang 官方源获取版本列表，检查网络或代理设置后重试。'
              : '从 Mojang 官方源获取 Minecraft 的全部版本列表。',
            actions: [button('立即获取', { variant: 'primary', icon: 'refresh-cw', onClick: doRefresh })],
          }));
          return;
        }

        const shown = list.slice(0, MAX_ROWS);
        const rows = shown.length
          ? shown.map(versionRow)
          : [h('div', { class: 'dim', style: { padding: 'var(--sp-8) var(--sp-6)', fontSize: 'var(--fs-sm)', textAlign: 'center' }, text: '没有匹配的版本，换个关键词或切换筛选条件试试。' })];

        fill(listBox, h('div', { class: 'rows' },
          h('div', { class: 'rows-head', style: { gridTemplateColumns: 'minmax(0, 1fr) auto auto' } },
            h('span', { text: '版本' }),
            h('span', { text: '类型' }),
            h('span', { text: '操作' }),
          ),
          ...rows,
          list.length > MAX_ROWS
            ? h('div', { class: 'dim', style: { padding: 'var(--sp-5) var(--sp-6)', fontSize: 'var(--fs-xs)' }, text: `仅显示前 ${MAX_ROWS} 个版本，请用搜索缩小范围` })
            : null,
        ));
      }

      function versionRow(v) {
        const meta = typeMeta(v.type);
        const on = selected.has(v.id);
        const installed = installedFor(v.id);

        return h('div', {
          class: 'ver-row',
          'data-active': on ? 'true' : 'false',
          role: 'button',
          tabindex: '0',
          'aria-pressed': on ? 'true' : 'false',
          onClick: () => toggle(v.id),
          onKeydown: (e) => {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(v.id); }
          },
        },
          h('div', { class: 'ver-row__lead' },
            // 图标沿用 PCL 的方块隐喻：草方块=正式版、命令方块=快照、
            // 圆石=远古版本、金块=愚人节 —— 一眼就能看出这是哪一类版本
            versionIcon(v, 'md'),
            h('div', { style: { minWidth: '0' } },
              h('div', { class: 'row', style: { gap: 'var(--sp-4)', minWidth: '0' } },
                h('div', { class: 'ver-row__id truncate', text: v.id ?? '未知版本' }),
                // 已经装过的版本直接标出来，并把主按钮换成「启动」——
                // 玩家最常做的事是"打开已经装好的游戏"，不该每次都找一遍实例页
                installed ? chip('已安装', { variant: 'success' }) : null,
              ),
              h('div', { class: 'ver-row__date', text: installed ? `实例：${installed.name}` : fmtDate(v.releaseTime) }),
            ),
          ),
          h('span', { class: ['type-pill', meta.cls], text: meta.label }),
          h('div', { class: 'row', style: { gap: 'var(--sp-3)' } },
            installed
              ? button('启动', {
                  variant: 'primary', size: 'sm', icon: 'play',
                  onClick: (e) => { e.stopPropagation(); launchInstance(installed); },
                })
              : button('安装', {
                  variant: 'soft', size: 'sm', icon: 'download',
                  onClick: (e) => { e.stopPropagation(); installOne(v); },
                }),
            iconButton('more-vertical', {
              size: 'sm', tip: '更多操作',
              onClick: (e) => { e.stopPropagation(); versionMenu(e.currentTarget, v); },
            }),
          ),
        );
      }

      function versionMenu(anchor, v) {
        const inst = targetInstance();
        const done = installedFor(v.id);
        menu(anchor, [
          { label: '复制版本号', icon: 'clipboard', onClick: () => copyText(v.id ?? '') },
          ...(done
            ? [{ label: '启动这个版本', icon: 'play', hint: done.name, onClick: () => launchInstance(done) }]
            : []),
          {
            label: inst ? '安装到此实例' : '安装到新实例', icon: 'download',
            hint: inst ? instanceName(inst) : '自动命名',
            onClick: () => installOne(v),
          },
          {
            label: '带加载器安装…', icon: 'puzzle',
            // 路由本身不接受业务参数（第三参是 replace/force），
            // 同一模块内用这个变量把"要装的版本"递给加载器页即可。
            onClick: () => { pendingGameVersion = v.id; go('download', 'loaders'); },
          },
          { separator: true },
          {
            label: '查看该版本日志', icon: 'external-link',
            onClick: () => {
              if (!v.url) { toast({ title: '该版本没有提供日志链接', kind: 'warning' }); return; }
              invoke('system.openUrl', { url: v.url })
                .catch((e) => toast({ title: '无法打开链接', desc: e?.message ?? String(e), kind: 'danger' }));
            },
          },
        ]);
      }

      function toggle(id) {
        if (selected.has(id)) selected.delete(id);
        else selected.add(id);
        paintHead();
        paintList();
      }

      /**
       * 安装单个版本。
       * 不传实例就自动建一个 —— 这是默认路径，用户不需要先去实例页做任何准备。
       */
      async function installOne(v, opts = {}) {
        const target0 = opts.instance ?? targetInstance();
        const r = await startInstall(
          {
            // 0 让内核走"自动创建"分支；这是刻意的，不是缺省值兜底
            instanceId: target0?.id ?? AUTO_INSTANCE,
            versionId: v.id,
            loader: opts.loader ?? target0?.loader ?? 'vanilla',
            loaderVersion: opts.loaderVersion ?? null,
          },
          `安装 ${v.id}`,
        );
        if (!r) return false;

        toast({
          title: r.created ? '已开始安装到新实例' : '已开始安装',
          desc: r.created ? `${v.id} → ${r.instanceName}` : `${v.id} → ${instanceName(target0)}`,
          kind: 'accent',
        });
        return true;
      }

      async function installSelected() {
        if (!selected.size) {
          toast({ title: '还没有选择版本', desc: '在列表里点选要安装的版本。', kind: 'warning' });
          return;
        }

        const inst = targetInstance();
        const list = manifest.versions.filter((v) => selected.has(v.id));
        let started = 0;

        // 批量安装时不再询问是否启动：一次装好几个版本，问"启动哪个"没有意义。
        for (const v of list) {
          // 顺序发起，避免同时抢占带宽；失败不打断其余版本。
          const r = await startInstall(
            {
              instanceId: inst?.id ?? AUTO_INSTANCE,
              versionId: v.id,
              loader: inst?.loader ?? 'vanilla',
            },
            `安装 ${v.id}`,
            false,
          );
          if (r) started += 1;
        }
        selected.clear();
        paintHead();
        paintList();
        if (started) {
          toast({
            title: '已开始安装',
            desc: inst ? `${started} 个版本 → ${instanceName(inst)}` : `${started} 个版本，各自建了新实例`,
            kind: 'accent',
          });
        }
      }

      /** 从内核拉取清单（进入页面与刷新后都会走这里）。 */
      async function doLoad() {
        loading = true;
        paintHead();
        paintList();
        try {
          const r = await invoke('version.manifest.list');
          manifest = {
            versions: r?.versions ?? [],
            cachedAt: r?.cachedAt ?? null,
            stale: !!r?.stale,
          };
          failed = false;
          setState({
            versions: manifest.versions,
            versionsLoadedAt: manifest.cachedAt,
            versionsStale: manifest.stale,
          });
        } catch (e) {
          failed = true;
          toast({ title: '版本清单载入失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
        } finally {
          loading = false;
          paintHead();
          paintList();
        }
      }

      async function doRefresh() {
        try {
          await invoke('version.manifest.refresh');
          toast({ title: '已刷新版本清单', kind: 'success' });
        } catch (e) {
          toast({ title: '刷新清单失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
        }
        await doLoad();
      }

      syncToolbar();
      paintHead();
      paintList();

      const offTask = on('task.update', paintTasks);
      const offInst = subscribe('instances', () => { syncToolbar(); paintList(); });
      const offActive = subscribe('activeInstanceId', () => { syncToolbar(); paintList(); });

      reload = () => { doLoad(); };
      const applyFilter = () => { syncToolbar(); paintList(); };
      manifestListeners.add(reload);
      filterListeners.add(applyFilter);

      return {
        destroy: () => {
          offTask();
          offInst();
          offActive();
          manifestListeners.delete(reload);
          filterListeners.delete(applyFilter);
          reload = () => {};
        },
      };
    },

    activate() { reload(); },
  };
}

/* ===========================================================================
   页面 2：加载器安装
   ========================================================================== */

const LOADERS = [
  { id: 'neoforge', name: 'NeoForge', icon: 'flame', desc: 'Forge 的现代分支，更新更积极，适合较新的版本。' },
  { id: 'forge', name: 'Forge', icon: 'hammer', desc: '生态最广，老牌模组大多只支持它。' },
  { id: 'fabric', name: 'Fabric', icon: 'wrench', desc: '轻量、更新快，适合跟进最新快照与正式版。' },
  { id: 'quilt', name: 'Quilt', icon: 'layers', desc: 'Fabric 的衍生分支，兼容大部分 Fabric 模组。' },
  { id: 'optifine', name: 'OptiFine', icon: 'sparkles', desc: '以加载器方式安装，提供光影与性能优化。' },
];

function createLoadersPage() {
  let repaint = () => {};

  return {
    title: '加载器安装',
    icon: 'hammer',

    mount(el) {
      const head = h('div', { class: 'page__header' });
      const scroll = h('div', { class: 'page__scroll' });
      fill(el, head, scroll);

      const taskBox = h('div');
      const bodyBox = h('div');
      fill(scroll, h('div', { class: 'page__inner' }, taskBox, bodyBox));

      let selectedLoader = null;   // 选中卡片的加载器 id
      let gameVersion = '';        // 选中的游戏版本
      let loaderVersion = '';      // 选中的加载器版本（留空 = 自动选择）
      let versionToken = 0;        // 丢弃过期的加载器版本请求
      let listError = null;        // 加载器版本列表的失败说明（不阻断安装）

      const target = targetButton(paint);

      function paintHead(inst) {
        fill(head,
          h('div', {},
            h('div', { class: 'page__title', text: '加载器安装' }),
            h('div', { class: 'page__subtitle', text: '为已有实例安装 Forge / NeoForge / Fabric / Quilt / OptiFine' }),
          ),
          h('div', { class: 'page__actions' },
            // 没选实例不再是"警告"，而是默认的正常路径 —— 会自动建一个
            inst
              ? chip(`安装到：${instanceName(inst)}`, { variant: 'accent' })
              : chip('自动创建实例', { variant: 'success' }),
            target.btn,
          ),
        );
      }

      function paintTasks() {
        fill(taskBox, taskCard('进行中的任务'));
      }

      function loaderCard(l) {
        const on = selectedLoader === l.id;
        return h('button', {
          class: 'loader-card', type: 'button', role: 'radio',
          'aria-selected': on ? 'true' : 'false',
          onClick: () => { selectedLoader = on ? null : l.id; gameVersion = ''; loaderVersion = ''; paint(); },
        },
          h('div', { class: 'loader-card__logo' }, loaderLogo(l.id, 'lg')),
          h('div', { class: 'loader-card__name', text: l.name }),
          h('div', { class: 'loader-card__desc', text: l.desc }),
        );
      }

      /** 加载器版本下拉：先问内核要清单，取不到就交给启动器自动选择。 */
      async function loadLoaderVersions(sel, loaderId) {
        const my = ++versionToken;

        if (!gameVersion) {
          sel.disabled = true;
          sel.setOptions([{ value: '', label: '先选择游戏版本', disabled: true }], '');
          return;
        }

        sel.disabled = true;
        sel.setOptions([{ value: '', label: '正在获取加载器版本…', disabled: true }], '');

        let list = [];
        try {
          // 注意接口名：version.loaders 返回的是"支持哪些加载器"（静态目录），
          // version.loaders.list 才是"这个加载器有哪些版本"。之前用错了，
          // 结果下拉框里列出来的是加载器名字（vanilla/fabric/forge…）。
          const r = await invoke('version.loaders.list', { loader: loaderId, versionId: gameVersion });
          const raw = r?.versions ?? [];
          list = raw.map((x) => (typeof x === 'string' ? { id: x } : x)).filter((x) => x?.id);
          listError = r?.error ?? null;
        } catch (e) {
          if (my !== versionToken) return;
          sel.setOptions([{ value: '', label: '自动选择（推荐）' }], '');
          sel.disabled = false;
          toast({ title: '加载器版本获取失败', desc: '可让启动器自动选择版本。', kind: 'warning' });
          return;
        }
        if (my !== versionToken) return;

        // 重绘后尽量保留用户已经选过的加载器版本
        const keep = loaderVersion && list.some((x) => x.id === loaderVersion) ? loaderVersion : '';
        loaderVersion = keep;

        sel.setOptions([
          { value: '', label: '自动选择（推荐）' },
          ...list.slice(0, MAX_LOADER_VERSIONS).map((x) => ({
            value: x.id,
            // 上游给的推荐版直接标出来，省得用户去翻文档对照
            label: x.recommended ? `${x.id}（推荐）` : x.id,
            hint: x.note ?? (x.recommended ? '社区推荐' : ''),
          })),
          ...(listError ? [{ value: '', label: listError, disabled: true }] : []),
        ], keep);

        sel.disabled = false;
      }

      function installPanel(inst, loaderId) {
        const loader = LOADERS.find((l) => l.id === loaderId);
        const releases = getState().versions.filter((v) => v.type === 'release').slice(0, 60);

        const gameSel = customSelect({
          value: gameVersion ?? '',
          ariaLabel: '游戏版本',
          placeholder: releases.length ? '请选择游戏版本' : '版本清单尚未载入，先去「版本下载」获取',
          options: releases.map((v) => ({ value: v.id, label: v.id })),
          onChange: (v) => {
            gameVersion = v;
            loadLoaderVersions(loaderSel, loaderId);
          },
        });

        const loaderSel = customSelect({
          ariaLabel: '加载器版本',
          placeholder: '先选择游戏版本',
          options: [{ value: '', label: '先选择游戏版本', disabled: true }],
          onChange: (v) => { loaderVersion = v; },
        });
        loaderSel.disabled = true;

        return h('div', { class: 'install-panel', style: { marginTop: 'var(--sp-8)' } },
          h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' },
            text: inst ? `安装 ${loader.name} 到 ${instanceName(inst)}` : `安装 ${loader.name}（自动创建实例）` }),
          h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px', lineHeight: 'var(--lh-snug)' },
            text: inst
              ? '加载器版本留空时由启动器按游戏版本自动选择；同名文件不会重复下载。'
              : '装完会自动建一个以「版本-加载器」命名的实例，不需要你先去实例页准备。' }),
          // 提前说清楚会附带装什么，避免用户事后在 mods 里发现一个没见过的 jar
          loader.id === 'fabric'
            ? h('div', {
                class: 'dim',
                style: {
                  fontSize: 'var(--fs-xs)', marginTop: 'var(--sp-3)',
                  display: 'flex', alignItems: 'center', gap: 'var(--sp-2)',
                },
              },
                icon('info', 'xs'),
                h('span', { text: '会一并安装与该游戏版本匹配的 Fabric API（大多数 Fabric 模组的前提）' }))
            : null,
          h('div', { class: 'row', style: { gap: 'var(--sp-6)', marginTop: 'var(--sp-6)', flexWrap: 'wrap' } },
            h('div', { class: 'field grow', style: { minWidth: '200px' } },
              h('label', { class: 'field__label', text: '游戏版本' }), gameSel),
            h('div', { class: 'field grow', style: { minWidth: '200px' } },
              h('label', { class: 'field__label', text: '加载器版本' }), loaderSel),
          ),
          h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-7)' } },
            button('开始安装', {
              variant: 'primary', icon: 'download',
              onClick: () => doInstall(inst, loader, gameSel, loaderSel),
            }),
            button('取消', { variant: 'ghost', onClick: () => { selectedLoader = null; gameVersion = ''; loaderVersion = ''; paint(); } }),
          ),
        );
      }

      async function doInstall(inst, loader, gameSel, loaderSel) {
        const versionId = gameSel.value;
        if (!versionId) {
          toast({ title: '请先选择游戏版本', desc: '加载器要装在一个具体的游戏版本上。', kind: 'warning' });
          return;
        }
        const lv = loaderSel.value || null;

        // 这里同样走"不指定实例就自动创建"的路径，和版本页保持一致。
        const r = await startInstall(
          {
            instanceId: inst?.id ?? AUTO_INSTANCE,
            versionId,
            loader: loader.id,
            loaderVersion: lv,
          },
          `安装 ${loader.name}`,
        );
        if (!r) return;

        toast({
          title: r.created ? '已开始安装到新实例' : '已开始安装',
          desc: `${loader.name}${lv ? ` ${lv}` : ''} → ${versionId}${r.created ? `（${r.instanceName}）` : ''}`,
          kind: 'accent',
        });

        // Fabric：顺手把 Fabric API 装上。
        // 放在 toast 之后，让用户先看到"加载器开始装了"，再看到"API 也装好了"。
        if (loader.id === 'fabric' && r.instanceId) {
          await installFabricApi(r.instanceId, versionId);
        }
      }

      function paint() {
        const inst = resolveInstance();

        // 从版本页「带加载器安装…」跳过来时会带上游戏版本，直接替用户选好
        if (pendingGameVersion && !gameVersion) {
          gameVersion = pendingGameVersion;
          pendingGameVersion = null;
        }

        paintHead(inst);
        paintTasks();
        target.sync();

        // 注意：这里不再要求"必须先有实例"。
        // 加载器安装同样支持自动建实例，空状态只在用户真的没选任何东西时才出现。
        fill(bodyBox,
          h('div', { class: 'loader-grid' }, ...LOADERS.map(loaderCard)),
          selectedLoader ? installPanel(inst, selectedLoader) : null,
        );

        // 面板重建后，把已选中的游戏版本对应的加载器版本重新拉一遍。
        if (selectedLoader && gameVersion) {
          const panel = bodyBox.querySelector('.install-panel');
          const sel = panel?.querySelectorAll('select')?.[1];
          if (sel) loadLoaderVersions(sel, selectedLoader);
        }
      }

      paint();

      const offTask = on('task.update', paintTasks);
      const offInst = subscribe('instances', paint);
      const offActive = subscribe('activeInstanceId', paint);

      repaint = paint;
      return { destroy: () => { offTask(); offInst(); offActive(); repaint = () => {}; } };
    },

    activate() { repaint(); },
  };
}

/* ===========================================================================
   页面 3：整合包安装
   ========================================================================== */

const MODPACK_FORMATS = [
  { name: '.mrpack', desc: 'Modrinth 官方整合包格式，内含依赖清单，可增量补齐文件。' },
  { name: 'CurseForge zip', desc: 'CurseForge 导出的整合包压缩包，内含 manifest.json。' },
  { name: 'HMCL 整合包', desc: 'HMCL 导出的整合包压缩包，按其中的配置还原实例。' },
];

function createModpackPage() {
  let repaint = () => {};

  return {
    title: '整合包安装',
    icon: 'file-archive',

    mount(el) {
      const head = h('div', { class: 'page__header' });
      const scroll = h('div', { class: 'page__scroll' });
      fill(el, head, scroll);

      const taskBox = h('div');
      fill(scroll, h('div', { class: 'page__inner' }, formatSection(), taskBox));

      function paintHead() {
        fill(head,
          h('div', {},
            h('div', { class: 'page__title', text: '整合包安装' }),
            h('div', { class: 'page__subtitle', text: '导入整合包会新建一个实例，并按其清单补齐版本、加载器与模组' }),
          ),
          h('div', { class: 'page__actions' },
            button('从文件导入', { variant: 'primary', icon: 'folder-open', onClick: importModpack }),
          ),
        );
      }

      function formatSection() {
        return h('div', {},
          h('div', { class: 'card', style: { padding: 'var(--sp-6)', marginBottom: 'var(--sp-7)' } },
            h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' }, text: '从本地文件安装整合包' }),
            h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px', lineHeight: 'var(--lh-snug)' }, text: '选择整合包文件后，启动器会在后台新建实例并下载所需文件；进度见下方任务列表，导入过程中可以继续使用其他页面。' }),
          ),
          h('div', { style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)', marginBottom: 'var(--sp-5)' }, text: '支持的格式' }),
          h('div', { class: 'rows' }, ...MODPACK_FORMATS.map(formatRow)),
        );
      }

      function formatRow(f) {
        return h('div', { class: 'row-item' },
          h('div', { class: 'row-item__icon' }, icon('file-archive', 'sm')),
          h('div', { style: { minWidth: '0' } },
            h('div', { class: 'row-item__name', text: f.name }),
            h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px', lineHeight: 'var(--lh-snug)' }, text: f.desc }),
          ),
        );
      }

      function paintTasks() {
        const card = taskCard('正在导入', (t) => /导入|整合包/.test(t.title ?? ''));
        fill(taskBox, card ?? emptyState({
          icon: 'file-archive',
          title: '当前没有正在导入的整合包',
          desc: '选择本地的 .mrpack 或整合包 zip，导入过程会显示在这里。',
          actions: [button('从文件导入', { variant: 'primary', icon: 'folder-open', onClick: importModpack })],
        }));
      }

      async function importModpack() {
        let picked;
        try {
          picked = await invoke('system.pickFile', {
            title: '选择整合包文件',
            filter: '整合包 (*.zip;*.mrpack)|*.zip;*.mrpack',
          });
        } catch (e) {
          toast({ title: '打开文件选择器失败', desc: e?.message ?? String(e), kind: 'danger' });
          return;
        }
        if (!picked?.path) return;

        const name = baseName(picked.path);
        try {
          const r = await invoke('instance.importModpack', { path: picked.path });
          if (r?.taskId) upsertTask({ id: r.taskId, title: `导入 ${name}`, state: 'running', progress: 0 });
          toast({ title: '已开始导入整合包', desc: name, kind: 'accent' });
        } catch (e) {
          toast({ title: '导入失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
        }
        paintTasks();
      }

      paintHead();
      paintTasks();

      const offTask = on('task.update', paintTasks);
      repaint = paintTasks;

      return { destroy: () => { offTask(); repaint = () => {}; } };
    },

    activate() { repaint(); },
  };
}

/* ===========================================================================
   二级面板：下载
   ========================================================================== */

function renderPanel(el, _hh, _ctx) {
  void _hh; void _ctx;   // 面板只需要 (el)；h 与 go 直接用模块导入的
  const head = h('div', { class: 'panel__head' });
  const body = h('div', { class: 'panel__body' });
  const foot = h('div', { class: 'panel__foot' });
  fill(el, head, body, foot);

  const items = [];   // { node, match(section, sub) }

  function navItem(label, ic, onClick, match) {
    const node = h('div', {
      class: 'nav-item', role: 'button', tabindex: '0', 'aria-selected': 'false',
      onClick,
      onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } },
    },
      icon(ic, 'sm'),
      h('span', { class: 'grow truncate', text: label }),
    );
    items.push({ node, match });
    return node;
  }

  function paintHead() {
    const total = getState().versions.length;
    fill(head,
      h('div', { class: 'panel__title' },
        icon('download', 'md'),
        h('span', { text: '下载' }),
      ),
      h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', padding: '0 2px' }, text: total ? `${total} 个版本可用` : '版本 · 加载器 · 整合包' }),
    );
  }

  function currentSub() {
    return getState().route?.section === SECTION ? (getState().route?.sub ?? null) : null;
  }

  function syncState() {
    const sub = currentSub();
    for (const it of items) it.node.setAttribute('aria-selected', it.match(sub) ? 'true' : 'false');
  }

  /** 版本页的三个筛选项：点一下就带着偏好进入版本页。 */
  const versionEntry = (id, label, ic) => navItem(
    label, ic,
    () => { versionFilter = id; go(SECTION); notifyFilter(); },
    (sub) => sub === null && versionFilter === id,
  );

  paintHead();
  fill(body,
    h('div', { class: 'panel__group', text: '游戏版本' }),
    versionEntry('all', '全部版本', 'layers'),
    versionEntry('release', '正式版', 'circle-check'),
    versionEntry('snapshot', '快照', 'camera'),
    h('div', { class: 'panel__group', text: '扩展安装' }),
    navItem('加载器安装', 'hammer', () => { go(SECTION, 'loaders'); syncState(); }, (sub) => sub === 'loaders'),
    navItem('整合包', 'boxes', () => { go(SECTION, 'modpack'); syncState(); }, (sub) => sub === 'modpack'),
    navItem('Java 运行时', 'cpu', () => go('settings', 'java'), () => false),
  );

  fill(foot, button('刷新版本清单', {
    variant: 'ghost', block: true, icon: 'refresh-cw',
    onClick: async () => {
      try {
        await invoke('version.manifest.refresh');
        toast({ title: '已刷新版本清单', kind: 'success' });
        notifyManifest();
      } catch (e) {
        toast({ title: '刷新清单失败', desc: e?.message ?? String(e), kind: 'danger', timeout: 9000 });
      }
    },
  }));

  syncState();
  const offRoute = subscribe('route', syncState);
  const offVersions = subscribe('versions', paintHead);
  return () => { offRoute(); offVersions(); };
}

/* ===========================================================================
   模块定义
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [SECTION]: createVersionsPage(),
    [`${SECTION}:loaders`]: createLoadersPage(),
    [`${SECTION}:modpack`]: createModpackPage(),
  },
};

