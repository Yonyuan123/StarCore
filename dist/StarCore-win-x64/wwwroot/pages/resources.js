/**
 * resources.js —— 「资源」分区：Modrinth 资源浏览器。
 *
 * 页面组成：
 *   resources / resources:mod / :resourcepack / :shader / :datapack / :world / :modpack
 *                          六个分类列表（共用 makeListPage 工厂，不复制粘贴）
 *   resources:detail       资源详情：说明、依赖、安装到实例
 *
 * 数据全部来自内核 modrinth.* 方法，无任何内置演示数据；
 * 请求失败一律给出可重试的错误态，不留白屏。
 */

import { invoke, on } from '../scripts/bridge.js';
import { getState, subscribe, upsertTask } from '../scripts/store.js';
import {
  h, fill, icon, iconButton, button, emptyState, chip, avatar,
  toast, menu, skeleton, fmtCount, fmtAgo, fmtDate, switchEl, blockCover,
} from '../scripts/ui.js';
import { go, back } from '../scripts/router.js';
import { RESOURCE_KINDS } from '../scripts/nav.js';

const SECTION = 'resources';
const PAGE_SIZE = 20;
const SITE = 'https://modrinth.com';

/** 当前在详情页展示的项目；列表页点击卡片时写入。 */
let projectId = null;
let projectKind = 'mod';

/**
 * 收藏按钮。已收藏时显示实心红心与「已收藏」，未收藏时是描边心形。
 * 心形用同一个图标，只靠变体与文案区分 —— 换图标反而会让人以为点了别的功能。
 *
 * @param detail  项目详情
 * @param onChanged 收藏状态变化后的重绘回调。**必须由调用方传入** ——
 *        详情页的 render() 是 createDetail 内部的局部函数，模块级的 favButton
 *        看不到它（之前直接写 render() 会抛 ReferenceError，按钮点了没反应）。
 */
function favButton(detail, onChanged) {
  const id = detail.id ?? projectId;
  const on = favoriteIds.has(id);

  return button(on ? '已收藏' : '收藏', {
    variant: on ? 'soft' : 'ghost',
    icon: 'heart',
    onClick: async () => {
      await toggleFavorite({
        id,
        slug: detail.slug ?? '',
        title: detail.title ?? '',
        iconUrl: detail.iconUrl ?? null,
        description: detail.description ?? '',
        kind: projectKind,
        downloads: detail.downloads ?? 0,
      });
      onChanged?.();
    },
  });
}

/** 当前该显示原文还是译文。 */
function currentBody(detail) {
  const original = detail.body ?? detail.description ?? '';
  if (!showingTranslation.get(detail.id ?? projectId)) return original;
  return translations.get(detail.id ?? projectId)?.text ?? original;
}

/**
 * 「说明」标题右侧的翻译条。
 * 三种状态：未翻译 → 翻译中 → 已翻译（可切换原文/译文）。
 */
function translateBar(detail, onChanged) {
  const key = detail.id ?? projectId;
  const original = detail.body ?? detail.description ?? '';
  const done = translations.get(key);
  const showing = !!showingTranslation.get(key);

  // 内容本来就以中文为主时不给翻译按钮，那是白费一次请求
  const cjk = (original.match(/[\u4e00-\u9fff]/g) ?? []).length;
  const looksChinese = original.length > 0 && cjk / original.length > 0.25;

  if (looksChinese && !done) {
    return h('span', { class: 'dim', style: { fontSize: 'var(--fs-micro)', fontWeight: '400' }, text: '原文即中文' });
  }

  const box = h('div', { class: 'row', style: { gap: 'var(--sp-3)', marginLeft: 'auto' } });

  if (done) {
    box.appendChild(button(showing ? '看原文' : '看译文', {
      variant: 'ghost', size: 'sm', icon: 'languages',
      onClick: () => { showingTranslation.set(key, !showing); onChanged?.(); },
    }));
    box.appendChild(h('span', {
      class: 'dim',
      style: { fontSize: 'var(--fs-micro)', alignSelf: 'center' },
      text: done.provider?.startsWith('llm') ? 'AI 翻译' : '机翻',
      title: done.provider?.startsWith('llm') ? '由 AI 翻译，术语与语句都经过处理' : '免费机翻，建议在「设置 → 翻译」里配置 AI 接口以获得更好质量',
    }));
    return box;
  }

  let busy = false;
  const btn = button('翻译为中文', {
    variant: 'soft', size: 'sm', icon: 'languages',
    onClick: async () => {
      if (busy) return;
      busy = true;
      btn.disabled = true;
      btn.textContent = '翻译中…';
      try {
        const r = await invoke('resource.translate', { text: original, target: 'zh-CN' });
        if (!r?.text) {
          toast({ title: '翻译未返回内容', desc: r?.message ?? '请稍后重试。', kind: 'warning' });
          return;
        }
        translations.set(key, { text: r.text, provider: r.provider });
        showingTranslation.set(key, true);
        if (r.cached) toast({ title: '来自缓存', desc: '这段内容之前翻译过。', kind: 'neutral', timeout: 1600 });
        onChanged?.();
      } catch (e) {
        toast({
          title: '翻译失败',
          desc: e?.message ?? String(e),
          kind: 'danger',
          timeout: 9000,
          action: { label: '去设置', onClick: () => go('settings', 'translate') },
        });
      } finally {
        busy = false;
        btn.disabled = false;
      }
    },
  });
  box.appendChild(btn);
  return box;
}

/* ===========================================================================
   收藏

   之前这里放的是一个「关注」按钮，点了只会弹"功能待接入"——因为它绑定的是
   Modrinth 的账号级关注，而 StarCore 的用户大多用离线账户，那个按钮对他们
   等于永久是灰的。现在改成启动器自己的收藏夹：不依赖任何账号，本地落盘，
   离线也能用，也更贴合"这个我待会儿想装"的真实意图。
   ========================================================================== */

/** 已收藏的项目 id 集合，供列表页给卡片标心形。 */
let favoriteIds = new Set();

/** 详情页用的完整收藏列表（侧栏「已收藏」页要显示标题和图标）。 */
let favoriteItems = [];

async function loadFavorites() {
  try {
    const r = await invoke('favorite.list');
    favoriteItems = r?.items ?? [];
    favoriteIds = new Set(favoriteItems.map((i) => i.id));
  } catch {
    // 收藏读取失败不该影响浏览，静默降级为空
    favoriteItems = [];
    favoriteIds = new Set();
  }
}

/** 收藏 / 取消收藏，并把结果写回内核。 */
async function toggleFavorite(payload) {
  try {
    const r = await invoke('favorite.toggle', payload);
    // 本地先改，界面立即响应；内核返回后再对齐一次
    if (r?.favorited) favoriteIds.add(payload.id);
    else favoriteIds.delete(payload.id);
    await loadFavorites();
    toast({
      title: r?.favorited ? '已加入收藏' : '已取消收藏',
      desc: r?.favorited ? `共 ${r.count} 项` : null,
      kind: r?.favorited ? 'success' : 'neutral',
      timeout: 1600,
    });
    return !!r?.favorited;
  } catch (e) {
    toast({ title: '操作失败', desc: e?.message ?? String(e), kind: 'danger' });
    return false;
  }
}

/* ===========================================================================
   翻译
   ---------------------------------------------------------------------------
   翻译结果只在内存里缓存（内核另有落盘缓存，同一段文本永远不会翻第二次）。
   不直接覆盖原文，而是提供"原文 / 译文"切换 —— 机器翻译再准也可能把
   版本号或代码片段弄错，保留原文是必要的退路。
   ========================================================================== */

/** projectId → { text, provider } */
const translations = new Map();
/** projectId → true 时显示译文 */
const showingTranslation = new Map();

const KIND_BY_ID = Object.fromEntries(RESOURCE_KINDS.map((k) => [k.id, k]));

const GAME_VERSIONS = [
  { id: 'all', label: '全部' },
  { id: '1.21.4', label: '1.21.4' },
  { id: '1.21.1', label: '1.21.1' },
  { id: '1.20.6', label: '1.20.6' },
  { id: '1.20.4', label: '1.20.4' },
  { id: '1.20.1', label: '1.20.1' },
  { id: '1.19.2', label: '1.19.2' },
  { id: '1.18.2', label: '1.18.2' },
  { id: '1.16.5', label: '1.16.5' },
];

const LOADERS = [
  { id: 'all', label: '全部' },
  { id: 'fabric', label: 'Fabric' },
  { id: 'neoforge', label: 'NeoForge' },
  { id: 'forge', label: 'Forge' },
  { id: 'quilt', label: 'Quilt' },
];

const SORTS = [
  { id: 'relevance', label: '相关度' },
  { id: 'downloads', label: '下载量' },
  { id: 'follows', label: '收藏数' },
  { id: 'updated', label: '最近更新' },
  { id: 'newest', label: '最新发布' },
];

function loaderLabel(id) {
  return { vanilla: '原版', forge: 'Forge', neoforge: 'NeoForge', fabric: 'Fabric', quilt: 'Quilt', optifine: 'OptiFine', liteloader: 'LiteLoader' }[id] ?? id;
}

function kindSite(meta) {
  return `${SITE}/${meta.modrinth}s`;
}

function openSite(url) {
  invoke('system.openUrl', { url }).catch((e) => toast({ title: '打开失败', desc: e?.message ?? String(e), kind: 'danger' }));
}

/* ===========================================================================
   二级面板
   ========================================================================== */

function renderPanel(el, _h, { go: nav }) {
  const head = h('div', { class: 'panel__head' });
  const body = h('div', { class: 'panel__body' });
  fill(el, head, body);

  fill(head, h('div', { class: 'panel__title' },
    icon('puzzle', 'md'),
    h('span', { text: '资源中心' }),
    h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)', fontWeight: '400' }, text: 'Modrinth 开源资源库' }),
  ));

  const navItem = (ic, label, onClick, selected, hint) => h('div', {
    class: 'nav-item',
    role: 'button',
    tabindex: '0',
    'aria-selected': selected ? 'true' : 'false',
    onClick,
    onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } },
  },
    icon(ic, 'sm'),
    h('span', { class: 'grow', text: label }),
    hint ? h('span', { class: 'nav-item__count', text: hint }) : null,
  );

  // 进入资源分区就同步一次收藏：侧栏的「已收藏」与卡片上的心形都依赖它
  loadFavorites();

  function paint() {
    const sub = getState().route.sub || 'mod';
    fill(body,
      h('div', { class: 'panel__group', text: '分类' }),
      ...RESOURCE_KINDS.map((k) => navItem(k.icon, k.label, () => nav(SECTION, k.id), sub === k.id)),
      h('div', { class: 'panel__group', text: '快捷' }),
      navItem('folder-open', '已安装内容', () => nav('instances', 'mods'), false),
      navItem('refresh-cw', '更新检查', () => toast({ title: '更新检查', desc: '已安装内容的更新检查会在实例页中进行。', kind: 'neutral' }), false),
      navItem('heart', '已收藏', () => nav(SECTION, 'saved'), sub === 'saved'),
    );
  }

  paint();
  const off = subscribe('route', paint);
  return () => off();
}

/* ===========================================================================
   列表页：六个分类共用同一套渲染器
   ========================================================================== */

/** 与真实卡片同高的骨架。 */
function skeletonCard() {
  return h('div', { class: 'res-card', 'aria-hidden': 'true', style: { cursor: 'default' } },
    skeleton(null, { width: '46px', height: '46px', flex: '0 0 auto', borderRadius: 'var(--r-md)' }),
    h('div', { class: 'grow', style: { display: 'flex', flexDirection: 'column', gap: 'var(--sp-4)', minWidth: '0' } },
      skeleton(null, { width: '62%', height: '14px' }),
      skeleton(null, { width: '32%', height: '10px' }),
      skeleton(null, { width: '100%', height: '10px' }),
      skeleton(null, { width: '84%', height: '10px' }),
      skeleton(null, { width: '48%', height: '10px', marginTop: 'var(--sp-2)' }),
    ),
  );
}

function createList(el, meta) {
  const kind = meta.id;
  const st = { query: '', gameVersion: 'all', loader: 'all', sort: 'relevance', offset: 0 };
  let hits = [];
  let total = 0;
  let loading = true;
  let error = null;
  let timer = null;
  let destroyed = false;

  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  const mark = h('div', {
    'aria-hidden': 'true',
    style: { position: 'absolute', right: 'var(--pad-page)', bottom: 'var(--pad-page)', opacity: '0.06', pointerEvents: 'none', color: 'var(--text)' },
  }, icon('sparkles', '3xl'));
  fill(el, head, scroll, mark);

  fill(head,
    h('div', {},
      h('div', { class: 'page__title', text: meta.label }),
      h('div', { class: 'page__subtitle', text: '来自 Modrinth 的开源资源库' }),
    ),
    h('div', { class: 'page__actions' },
      button('打开 Modrinth 网页', { variant: 'ghost', icon: 'external-link', onClick: () => openSite(kindSite(meta)) }),
    ),
  );

  /* —— 工具栏：只构建一次，刷新时不重建，避免输入框失焦 —— */
  const searchInput = h('input', { class: 'input', placeholder: `搜索${meta.label}…` });
  const searchIcon = icon('search', 'sm');
  searchIcon.classList.add('icon--lead');

  const toolbar = h('div', { class: 'toolbar' },
    h('div', { class: 'search' }, searchIcon, searchInput),
  );

  function filterButton(name, options, fallback, get, set) {
    const label = h('span');
    const btn = h('button', {
      class: 'filter-btn',
      type: 'button',
      'data-active': 'false',
      onClick: () => menu(btn, options.map((o) => ({
        label: o.label,
        icon: o.id === get() ? 'check' : null,
        hint: o.id === get() ? '当前' : '',
        onClick: () => { set(o.id); sync(); st.offset = 0; reload(); },
      }))),
    }, label, icon('chevron-down', 'xs'));

    function sync() {
      const cur = get();
      const def = cur === fallback;
      btn.dataset.active = def ? 'false' : 'true';
      const found = options.find((o) => o.id === cur);
      label.textContent = def ? name : `${name} · ${found?.label ?? cur}`;
    }
    sync();
    return { btn, sync };
  }

  const fVersion = filterButton('游戏版本', GAME_VERSIONS, 'all', () => st.gameVersion, (v) => { st.gameVersion = v; });
  const fLoader = filterButton('加载器', LOADERS, 'all', () => st.loader, (v) => { st.loader = v; });
  const fSort = filterButton('排序', SORTS, 'relevance', () => st.sort, (v) => { st.sort = v; });
  toolbar.append(fVersion.btn, fLoader.btn, fSort.btn);

  const results = h('div');
  fill(scroll, h('div', { class: 'page__inner' }, toolbar, results));

  /* —— 搜索：回车立刻查，输入防抖 400ms —— */
  const commitSearch = () => {
    clearTimeout(timer);
    const q = searchInput.value.trim();
    if (q === st.query) return;
    st.query = q;
    st.offset = 0;
    reload();
  };
  searchInput.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(commitSearch, 400); });
  searchInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') commitSearch(); });

  /* —— 数据 —— */
  async function reload() {
    if (destroyed) return;
    loading = true;
    error = null;
    renderResults();
    try {
      const params = { kind, query: st.query, sort: st.sort, offset: st.offset, limit: PAGE_SIZE };
      if (st.gameVersion !== 'all') params.gameVersion = st.gameVersion;
      if (st.loader !== 'all') params.loader = st.loader;
      const r = await invoke('modrinth.search', params);
      if (destroyed) return;
      hits = r?.hits ?? [];
      total = r?.total ?? hits.length;
      st.offset = r?.offset ?? st.offset;
    } catch (e) {
      if (destroyed) return;
      error = e?.message ?? String(e);
      hits = [];
      total = 0;
      toast({ title: '资源库暂不可达', desc: error, kind: 'danger' });
    } finally {
      if (!destroyed) { loading = false; renderResults(); }
    }
  }

  /* —— 渲染 —— */
  function resCard(hit) {
    return h('button', {
      class: 'res-card',
      type: 'button',
      onClick: () => { projectId = hit.projectId; projectKind = kind; go(SECTION, 'detail'); },
    },
      h('div', { class: 'res-card__icon' },
        hit.iconUrl ? h('img', { src: hit.iconUrl, alt: '', loading: 'lazy' }) : icon(meta.icon, 'md'),
      ),
      h('div', { class: 'grow', style: { minWidth: '0' } },
        h('div', { class: 'res-card__title truncate', text: hit.title ?? '未命名资源' }),
        h('div', { class: 'res-card__author truncate', text: hit.author ?? '未知作者' }),
        h('div', { class: 'res-card__desc clamp-2', text: hit.description ?? '' }),
        h('div', { class: 'res-card__stats' },
          h('span', {}, icon('download', 'xs'), h('span', { text: fmtCount(hit.downloads ?? 0) })),
          h('span', {}, icon('clock', 'xs'), h('span', { text: fmtAgo(hit.dateModified) })),
          h('span', {}, icon('heart', 'xs'), h('span', { text: fmtCount(hit.follows ?? 0) })),
          favoriteIds.has(hit.projectId ?? hit.project_id)
            ? h('span', { class: 'dim', title: '已收藏', style: { marginLeft: 'var(--sp-3)' } }, icon('heart', 'xs'))
            : null,
        ),
      ),
    );
  }

  function pagination() {
    const pageNo = Math.floor(st.offset / PAGE_SIZE) + 1;
    const hasPrev = st.offset > 0;
    const hasNext = st.offset + PAGE_SIZE < total;
    return h('div', { class: 'row', style: { justifyContent: 'center', alignItems: 'center', gap: 'var(--sp-4)', marginTop: 'var(--sp-8)' } },
      button('上一页', {
        variant: 'ghost', size: 'sm', icon: 'chevron-left', disabled: !hasPrev,
        onClick: () => { st.offset = Math.max(0, st.offset - PAGE_SIZE); reload(); },
      }),
      h('span', { class: 'dim', style: { fontSize: 'var(--fs-sm)', fontVariantNumeric: 'tabular-nums' }, text: `第 ${pageNo} 页` }),
      button('下一页', {
        variant: 'ghost', size: 'sm', iconAfter: 'chevron-right', disabled: !hasNext,
        onClick: () => { st.offset += PAGE_SIZE; reload(); },
      }),
    );
  }

  function renderResults() {
    if (destroyed) return;
    if (loading) {
      fill(results, h('div', { class: 'res-grid' }, ...Array.from({ length: 6 }, skeletonCard)));
      return;
    }
    if (error) {
      fill(results, emptyState({
        icon: 'cloud',
        title: '资源库暂不可达',
        desc: error,
        actions: [button('重试', { variant: 'primary', icon: 'refresh-cw', onClick: reload })],
      }));
      return;
    }
    if (!hits.length) {
      fill(results, emptyState({
        icon: 'search',
        title: '没有找到匹配的资源',
        desc: '换个关键词，或放宽版本与加载器筛选。',
      }));
      return;
    }
    fill(results,
      h('div', { class: 'res-grid stagger' }, ...hits.map(resCard)),
      total > PAGE_SIZE ? pagination() : null,
    );
  }

  renderResults();

  return {
    reload,
    destroy() {
      destroyed = true;
      clearTimeout(timer);
    },
  };
}

/** 把「首次挂载 + 每次进入重新拉数据」两件事包成一份页面定义。 */
function withActivate(create) {
  let ctl = null;
  return {
    mount(el) {
      ctl = create(el);
      return { destroy: () => { ctl?.destroy?.(); ctl = null; } };
    },
    activate() { ctl?.reload?.(); },
  };
}

const makeListPage = (kindId) => withActivate((el) => createList(el, KIND_BY_ID[kindId] ?? RESOURCE_KINDS[0]));

/* ===========================================================================
   详情页
   ========================================================================== */

/** 极简 Markdown：只认标题、列表、图片与段落，不引入任何依赖。 */
function mdInline(s) {
  return s
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/`([^`]+)`/g, '$1');
}

const MD_P = { fontSize: 'var(--fs-sm)', color: 'var(--text-2)', lineHeight: 'var(--lh-relaxed)', margin: 'var(--sp-4) 0' };
const MD_H = { fontSize: 'var(--fs-h2)', fontWeight: 'var(--fw-semibold)', color: 'var(--text)', margin: 'var(--sp-7) 0 var(--sp-4)' };

function mdBody(text) {
  const lines = String(text ?? '').split(/\r?\n/);
  const nodes = [];
  let list = null;
  let listTag = '';
  const flush = () => { if (list) { nodes.push(list); list = null; listTag = ''; } };

  for (const raw of lines) {
    const line = raw.trim();
    if (!line) { flush(); continue; }

    const img = line.match(/^!\[[^\]]*\]\(([^)\s]+)/);
    if (img) {
      flush();
      nodes.push(h('img', { src: img[1], alt: '', loading: 'lazy', style: { maxWidth: '100%', borderRadius: 'var(--r-md)', margin: 'var(--sp-5) 0', border: '1px solid var(--border)' } }));
      continue;
    }

    const heading = line.match(/^#{1,6}\s+(.*)$/);
    if (heading) { flush(); nodes.push(h('h3', { style: MD_H, text: mdInline(heading[1]) })); continue; }

    const bullet = line.match(/^[-*]\s+(.*)$/);
    const ordered = line.match(/^\d+\.\s+(.*)$/);
    if (bullet || ordered) {
      const tag = bullet ? 'ul' : 'ol';
      if (listTag !== tag) { flush(); listTag = tag; }
      if (!list) list = h(tag, { style: { margin: 'var(--sp-4) 0', paddingLeft: 'var(--sp-8)', display: 'flex', flexDirection: 'column', gap: 'var(--sp-3)' } });
      list.appendChild(h('li', { style: MD_P, text: mdInline((bullet ?? ordered)[1]) }));
      continue;
    }

    flush();
    nodes.push(h('p', { style: MD_P, text: mdInline(line) }));
  }
  flush();

  if (!nodes.length) nodes.push(h('p', { style: MD_P, text: '该项目没有提供说明。' }));
  return h('div', { class: 'md-body' }, ...nodes);
}

function createDetail(el) {
  let detail = null;
  let versions = [];
  let loading = true;
  let error = null;
  let busy = false;
  let selectedVersionId = null;

  /* ---- 前置依赖 ----
     装一个 Sodium 需要 Fabric API，装 Iris 又需要 Sodium。用户不知道这些，
     装完发现游戏起不来只会觉得启动器有毛病。这里在安装前把必需前置解析出来，
     默认一并安装，用户也可以取消勾选。 */
  let depPlan = null;          // { items, missing, dependencies, alreadyInstalled }
  let depLoading = false;
  let includeDeps = true;      // 默认带上前置 —— 这是绝大多数人想要的
  let depFor = null;           // 已解析的 (instanceId, versionId) 组合，避免重复请求
  let selectedInstanceId = getState().activeInstanceId ?? getState().instances[0]?.id ?? null;
  let destroyed = false;

  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  const meta = () => KIND_BY_ID[projectKind] ?? KIND_BY_ID.mod;

  if (!projectId) {
    requireProject();
    fill(scroll, h('div', { class: 'page__inner' }, emptyState({
      icon: 'puzzle',
      title: '还没有选中资源',
      desc: '从左侧分类里挑一个资源，就能看到它的详情。',
      actions: [button('浏览模组', { variant: 'primary', icon: 'puzzle', onClick: () => go(SECTION, 'mod') })],
    })));
    return { reload() {}, destroy() { destroyed = true; } };
  }

  /** 没有记住项目时退回模组列表（延后一拍，避免在路由切换过程中再次导航）。 */
  function requireProject() {
    if (!projectId) {
      setTimeout(() => { if (!destroyed) go(SECTION, 'mod'); }, 0);
      return false;
    }
    return true;
  }

  async function reload() {
    if (destroyed) return;
    if (!requireProject()) return;
    loading = true;
    error = null;
    render();
    try {
      const [proj, vers] = await Promise.all([
        invoke('modrinth.project', { id: projectId }),
        invoke('modrinth.versions', { id: projectId }).catch(() => null),
      ]);
      if (destroyed) return;
      detail = proj?.project ?? proj ?? null;
      versions = vers?.versions ?? (Array.isArray(vers) ? vers : []);

      // 版本与实例的兼容性校正。
      // 两者是各自独立初始化的：selectedInstanceId 来自当前激活实例，
      // selectedVersionId 来自资源版本列表的第一个 —— 从来没人检查过它们
      // 是否匹配，于是会出现"选着 1.20.1 的实例、版本却是 26.4 的"这种错位，
      // 用户一点安装就失败。切换实例时有 pick() 兜着，但"打开页面"这条路
      // 之前没人管。
      const compatible = compatibleVersions();
      if (!compatible.some((v) => v.id === selectedVersionId)) {
        selectedVersionId = compatible[0]?.id ?? versions[0]?.id ?? null;
      }
    } catch (e) {
      if (destroyed) return;
      error = e?.message ?? String(e);
      toast({ title: '资源详情读取失败', desc: error, kind: 'danger' });
    } finally {
      if (!destroyed) { loading = false; render(); }
    }
  }

  function metaRow(k, ...v) {
    return h('div', { class: 'meta-row' },
      h('div', { class: 'meta-row__k', text: k }),
      h('div', { class: 'meta-row__v' }, ...v),
    );
  }

  /**
   * 只保留能装到当前实例上的资源版本。
   *
   * 不筛的话，一个热门模组动辄几十个版本、横跨十几个游戏版本和四五种加载器，
   * 下拉框一展开就是一面墙（实测 40 条能占满整屏），根本没法看。
   * PCL / HMCL 都是先锁定"装到哪个实例"，再只列兼容的版本。
   */
  function compatibleVersions() {
    // 注意：这里不能引用 render() 里的局部 `instances`。
    // 本函数定义在页面作用域，而 `const instances = getState().instances`
    // 是 render 内的局部变量 —— 直接引用会抛 ReferenceError，
    // 表现是详情页一直转圈（reload 的 finally 被异常打断，loading 回不到 false）。
    const all = getState().instances ?? [];
    const inst = all.find((i) => i.id === selectedInstanceId);
    if (!inst) return versions;

    const gv = inst.versionId ?? '';
    const ld = inst.loader && inst.loader !== 'vanilla' ? String(inst.loader).toLowerCase() : null;

    return versions.filter((v) => {
      // 游戏版本必须对得上。一个资源版本通常支持一个小范围，取"包含"即可。
      const gvs = v.gameVersions ?? [];
      if (gv && gvs.length && !gvs.includes(gv)) return false;

      // 只有模组才严格比加载器。资源包 / 数据包 / 光影 / 地图的 loaders
      // 语义各不相同（资源包常是 ["minecraft"]，光影是 ["iris"]），
      // 一刀切会把本来能装的版本也筛掉。
      if (projectKind === 'mod' && ld) {
        const lds = (v.loaders ?? []).map((x) => String(x).toLowerCase());
        if (lds.length && !lds.includes(ld)) return false;
      }
      return true;
    });
  }

  function versionPicker() {
    const list = compatibleVersions();
    const cur = list.find((v) => v.id === selectedVersionId)
             ?? versions.find((v) => v.id === selectedVersionId);

    // 已过滤的列表通常只有几条；仍给个上限，防止个别资源的元数据缺失导致整屏铺开。
    const LIMIT = 30;
    const shown = list.slice(0, LIMIT);

    const label = h('span', { text: cur ? (cur.versionNumber ?? cur.name ?? cur.id) : '选择版本' });
    const btn = h('button', {
      class: 'filter-btn',
      type: 'button',
      'data-active': cur ? 'true' : 'false',
      onClick: () => menu(btn, shown.length
        ? [
            ...shown.map((v) => ({
              label: v.versionNumber ?? v.name ?? v.id,
              // 游戏版本已被筛成同一个，这里只留加载器与发布通道，读起来更省事
              hint: [
                ...(v.loaders ?? []).slice(0, 2),
                v.versionType && v.versionType !== 'release' ? v.versionType : null,
              ].filter(Boolean).join(' · '),
              icon: v.id === selectedVersionId ? 'check' : null,
              onClick: () => { selectedVersionId = v.id; render(); },
            })),
            ...(list.length > LIMIT
              ? [{ separator: true }, { label: `另有 ${list.length - LIMIT} 个版本未列出`, disabled: true }]
              : []),
          ]
        : [{
            label: selectedInstanceId ? '这个实例没有兼容版本' : '暂无可安装版本',
            hint: selectedInstanceId ? '换一个游戏版本或加载器不同的实例试试' : '',
            icon: 'info', disabled: true,
          }]),
    }, label, icon('chevron-down', 'xs'));
    return btn;
  }

  function installTarget(inst) {
    const pick = () => {
      selectedInstanceId = inst.id;
      // 换实例后，原先选中的资源版本多半不再兼容（游戏版本/加载器变了），
      // 顺手挑一个能用的；否则会出现"看着选中了、点安装却装不上"的状态。
      const list = compatibleVersions();
      if (!list.some((v) => v.id === selectedVersionId)) {
        selectedVersionId = list[0]?.id ?? null;
      }
      render();
    };
    return h('div', {
      class: 'install-target',
      role: 'radio',
      tabindex: '0',
      'aria-selected': inst.id === selectedInstanceId ? 'true' : 'false',
      onClick: pick,
      onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); } },
    },
      // inst.cover 是内核给的裸路径（blocks/xxx.png），avatar 不会自己补
      // assets/img/ 前缀 —— 直接传会 404，界面上就是一个破图图标。
      avatar(inst.name, { size: 'sm', src: blockCover(inst.cover), round: true }),
      h('div', { class: 'grow', style: { minWidth: '0' } },
        h('div', { class: 'truncate', style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)' }, text: inst.name ?? '未命名实例' }),
        h('div', { class: 'truncate', style: { fontSize: 'var(--fs-xs)', color: 'var(--text-3)' },
          text: [inst.versionId, inst.loader && inst.loader !== 'vanilla' ? loaderLabel(inst.loader) : null].filter(Boolean).join(' · ') || '未配置版本' }),
      ),
      inst.id === selectedInstanceId ? icon('check', 'sm') : null,
    );
  }

  async function install() {
    if (!selectedInstanceId) { toast({ title: '请先选择一个实例', kind: 'warning' }); return; }
    if (!selectedVersionId) { toast({ title: '该资源没有可安装的版本', kind: 'warning' }); return; }

    busy = true;
    render();

    try {
      const inst = getState().instances.find((i) => i.id === selectedInstanceId);
      const gameVersion = inst?.versionId ?? null;
      const loader = inst?.loader ?? null;

      // 有待装的前置且用户勾了"一并安装" → 走批量安装
      const deps = includeDeps
        ? (depPlan?.items ?? []).filter((i) => !i.alreadyInstalled)
        : [];

      if (deps.length > 1) {
        const r = await invoke('modrinth.installPlan', {
          items: deps.map((d) => ({ projectId: d.projectId, versionId: d.versionId, title: d.title })),
          instanceId: selectedInstanceId,
          kind: projectKind,
          gameVersion,
          loader,
        });
        if (r?.taskId) {
          upsertTask({ id: r.taskId, title: `安装 ${r.count} 项内容`, state: 'running', progress: 0 });
          toast({
            title: '已开始安装',
            desc: `包含 ${deps.length - 1} 个前置，进度见任务中心`,
            kind: 'accent',
          });
        } else {
          toast({ title: '安装未能开始', desc: r?.message ?? '内核没有返回任务号。', kind: 'warning' });
        }
      } else {
        await invoke('modrinth.install', {
          projectId, versionId: selectedVersionId, instanceId: selectedInstanceId,
          kind: projectKind, gameVersion, loader,
        });
        toast({ title: '已加入安装队列', desc: detail?.title ?? projectId, kind: 'success' });
      }
    } catch (e) {
      toast({ title: '安装失败', desc: e?.message ?? String(e), kind: 'danger' });
    } finally {
      busy = false;
      render();
    }
  }

  /** 解析前置。同一组 (实例, 版本) 只查一次。 */
  async function ensureDepPlan() {
    if (!selectedInstanceId || !selectedVersionId) { depPlan = null; return; }
    const key = `${selectedInstanceId}:${selectedVersionId}`;
    if (depFor === key) return;

    depFor = key;
    depLoading = true;
    depPlan = null;
    render();

    try {
      const inst = getState().instances.find((i) => i.id === selectedInstanceId);
      const r = await invoke('modrinth.resolveDeps', {
        projectId,
        versionId: selectedVersionId,
        instanceId: selectedInstanceId,
        gameVersion: inst?.versionId ?? null,
        loader: inst?.loader ?? null,
      });
      if (depFor !== key) return;              // 期间用户又改了选择，丢弃这次结果
      depPlan = r?.items ? r : null;
      if (r?.message) toast({ title: '前置解析失败', desc: r.message, kind: 'warning' });
    } catch (e) {
      if (depFor === key) {
        depPlan = null;
        toast({ title: '前置解析失败', desc: '将只安装本资源。', kind: 'warning' });
      }
    } finally {
      if (depFor === key) { depLoading = false; render(); }
    }
  }

  /** 安装卡片里的「前置依赖」区块。 */
  function depSection() {
    if (!selectedInstanceId || !selectedVersionId) return null;

    if (depLoading) {
      return h('div', { class: 'col', style: { gap: 'var(--sp-4)', marginBottom: 'var(--sp-6)' } },
        skeleton(null, { height: '14px', width: '58%' }),
        skeleton(null, { height: '14px', width: '40%' }),
      );
    }

    if (!depPlan || depPlan.total <= 1) return null;

    const missingDeps = depPlan.items.filter((i) => !i.isRoot && !i.alreadyInstalled);
    if (!missingDeps.length) {
      return h('div', { class: 'row', style: { gap: 'var(--sp-3)', marginBottom: 'var(--sp-6)', fontSize: 'var(--fs-xs)', color: 'var(--text-3)' } },
        icon('circle-check', 'sm'),
        h('span', { text: depPlan.total > 1 ? '所需前置已全部安装' : '没有前置依赖' }),
      );
    }

    return h('div', { class: 'dep-box', style: { marginBottom: 'var(--sp-6)' } },
      h('div', { class: 'row-between', style: { gap: 'var(--sp-4)' } },
        h('div', { class: 'row', style: { gap: 'var(--sp-3)', minWidth: '0' } },
          icon('puzzle', 'sm'),
          h('span', { style: { fontSize: 'var(--fs-sm)' }, text: `还需要 ${missingDeps.length} 个前置` }),
        ),
        switchEl(includeDeps, (v) => { includeDeps = v; render(); }, { label: '一并安装前置' }),
      ),
      h('div', { class: 'col', style: { gap: 'var(--sp-2)', marginTop: 'var(--sp-4)' } },
        ...missingDeps.slice(0, 6).map((d) => h('div', { class: 'row', style: { gap: 'var(--sp-3)', minWidth: '0' } },
          d.iconUrl
            ? h('img', { class: 'loader-logo loader-logo--xs', src: d.iconUrl, alt: '', loading: 'lazy' })
            : icon('puzzle', 'xs'),
          h('span', { class: 'truncate', style: { fontSize: 'var(--fs-xs)', color: 'var(--text-2)' }, text: d.title || d.projectId }),
          h('span', { class: 'dim truncate', style: { fontSize: 'var(--fs-micro)' }, text: d.versionNumber ?? '' }),
        )),
        missingDeps.length > 6
          ? h('div', { class: 'dim', style: { fontSize: 'var(--fs-micro)' }, text: `…还有 ${missingDeps.length - 6} 个` })
          : null,
      ),
      includeDeps ? null : h('div', { class: 'dim', style: { fontSize: 'var(--fs-micro)', marginTop: 'var(--sp-4)' }, text: '已关闭：只装本资源，游戏可能因为缺少前置而崩溃。' }),
    );
  }

  let installCard = null;

  function render() {
    if (destroyed) return;
    const km = meta();

    fill(head,
      h('div', { class: 'row', style: { gap: 'var(--sp-5)', alignItems: 'center' } },
        iconButton('arrow-left', { tip: '返回', onClick: () => back() }),
        chip(km.label, { variant: 'accent', icon: km.icon }),
      ),
      h('div', { class: 'page__actions' },
        detail?.slug
          ? button('打开 Modrinth 网页', { variant: 'ghost', icon: 'external-link', onClick: () => openSite(`${SITE}/${km.modrinth}/${detail.slug}`) })
          : null,
      ),
    );

    if (loading) {
      fill(scroll, h('div', { class: 'page__inner' },
        h('div', { class: 'skeleton', style: { height: '96px', marginBottom: 'var(--sp-7)' } }),
        h('div', { class: 'skeleton', style: { height: '260px' } }),
      ));
      return;
    }

    if (error || !detail) {
      fill(scroll, h('div', { class: 'page__inner' }, emptyState({
        icon: 'cloud',
        title: '资源库暂不可达',
        desc: error ?? '未能读取该资源的信息。',
        actions: [
          button('重试', { variant: 'primary', icon: 'refresh-cw', onClick: reload }),
          button('返回列表', { variant: 'ghost', icon: 'arrow-left', onClick: () => go(SECTION, projectKind) }),
        ],
      })));
      return;
    }

    const gameVersions = detail.gameVersions ?? [...new Set(versions.flatMap((v) => v.gameVersions ?? []))];
    const loaders = [...new Set(detail.loaders ?? versions.flatMap((v) => v.loaders ?? []))];
    const categories = detail.categories ?? [];
    const deps = (detail.dependencies ?? []).filter((d) => d && (d.projectId || d.id));

    const shownVers = gameVersions.slice(0, 12);
    const restVers = gameVersions.length - shownVers.length;

    const hero = h('div', { class: 'detail-hero' },
      h('div', { class: 'detail-hero__icon' },
        detail.iconUrl ? h('img', { src: detail.iconUrl, alt: '', loading: 'lazy' }) : icon(km.icon, 'xl'),
      ),
      h('div', { class: 'grow', style: { minWidth: '0' } },
        h('div', { class: 'ghost-title' },
          h('span', { class: 'ghost-title__ghost', text: detail.title ?? projectId }),
          h('div', { class: 'ghost-title__real detail-title', text: detail.title ?? projectId }),
        ),
        h('div', { style: { marginTop: 'var(--sp-4)', fontSize: 'var(--fs-sm)', color: 'var(--text-3)' },
          text: `作者 ${detail.author ?? '未知'}` }),
        detail.description ? h('div', { style: { marginTop: 'var(--sp-3)', fontSize: 'var(--fs-sm)', color: 'var(--text-2)', lineHeight: 'var(--lh-snug)', maxWidth: '72ch' }, text: detail.description }) : null,
        h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-7)', flexWrap: 'wrap' } },
          button('安装到实例', { variant: 'primary', icon: 'download', onClick: () => installCard?.scrollIntoView({ behavior: 'smooth', block: 'center' }) }),
          button('在浏览器打开', { variant: 'ghost', icon: 'external-link', onClick: () => openSite(`${SITE}/${km.modrinth}/${detail.slug ?? projectId}`) }),
          favButton(detail, render),
        ),
      ),
    );

    const main = h('div', {},
      h('div', { class: 'meta-table' },
        metaRow('加载器', ...(loaders.length ? loaders.map((l) => chip(loaderLabel(l))) : [h('span', { class: 'dim', text: '—' })])),
        metaRow('支持版本', ...(shownVers.length
          ? [...shownVers.map((v) => chip(v, { variant: 'outline' })), restVers > 0 ? chip(`+${restVers}`, { variant: 'outline' }) : null]
          : [h('span', { class: 'dim', text: '—' })])),
        metaRow('分类', ...(categories.length ? categories.map((c) => chip(c)) : [h('span', { class: 'dim', text: '—' })])),
        metaRow('许可证', h('span', { text: detail.license?.name ?? detail.license?.id ?? '—' })),
        metaRow('下载量', h('span', {}, icon('download', 'xs'), h('span', { text: fmtCount(detail.downloads ?? 0) }))),
        metaRow('收藏数', h('span', {}, icon('heart', 'xs'), h('span', { text: fmtCount(detail.follows ?? 0) }))),
        metaRow('最近更新', h('span', { text: fmtDate(detail.updated ?? detail.dateModified) })),
      ),

      h('div', { class: 'set-section', style: { marginTop: 'var(--sp-8)' } },
        h('div', { class: 'set-section__title' }, icon('link', 'md'), h('span', { text: '前置依赖' })),
        deps.length
          ? h('div', { class: 'row', style: { gap: 'var(--sp-4)', flexWrap: 'wrap' } },
              ...deps.map((d) => chip(d.title ?? d.projectId ?? d.id, {
                variant: 'outline',
                icon: 'box',
                title: d.dependency_type === 'required' ? '必需' : '可选',
              })))
          : h('div', { class: 'dim', style: { fontSize: 'var(--fs-sm)' }, text: '没有前置依赖' }),
      ),

      h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('file-text', 'md'), h('span', { text: '说明' }), translateBar(detail, render)),
        mdBody(currentBody(detail)),
      ),
    );

    const instances = getState().instances;
    if (selectedInstanceId && !instances.some((i) => i.id === selectedInstanceId)) {
      selectedInstanceId = getState().activeInstanceId ?? instances[0]?.id ?? null;
    }
    installCard = h('div', { class: 'card install-card' },
      h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' }, text: '选择要安装到的游戏' }),
      h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px', marginBottom: 'var(--sp-5)' }, text: '安装后会出现在该实例的内容列表中。' }),
      h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginBottom: 'var(--sp-5)' } }, versionPicker()),
      instances.length
        ? h('div', { class: 'col', style: { gap: 'var(--sp-2)', marginBottom: 'var(--sp-6)' } }, ...instances.map(installTarget))
        : emptyState({
            icon: 'package-open',
            title: '还没有实例',
            desc: '先创建一个实例，再回来安装。',
            actions: [button('去创建实例', { variant: 'primary', size: 'sm', icon: 'plus', onClick: () => go('instances', 'list') })],
          }),
      depSection(),
      button('安装', {
        variant: 'primary',
        block: true,
        icon: 'download',
        disabled: busy || !selectedInstanceId || !selectedVersionId || !instances.length,
        onClick: install,
      }),
    );

    fill(scroll, h('div', { class: 'page__inner' },
      hero,
      h('div', { class: 'split' }, main, installCard),
    ));

    // 选中实例与版本后自动解析前置。放在渲染之后触发，避免递归渲染。
    if (selectedInstanceId && selectedVersionId && depFor !== `${selectedInstanceId}:${selectedVersionId}` && !depLoading) {
      queueMicrotask(() => { if (!destroyed) ensureDepPlan(); });
    }
  }

  const offs = [
    subscribe('instances', () => { if (!destroyed) render(); }),
    subscribe('activeInstanceId', () => { if (!destroyed) render(); }),
  ];

  return {
    reload,
    destroy() {
      destroyed = true;
      offs.forEach((f) => f());
    },
  };
}

/* ===========================================================================
   已收藏
   ---------------------------------------------------------------------------
   刻意做成"列表 + 移除"这么简单：收藏夹的价值是"把我一时兴起记下的东西找回来"，
   加排序、加分组、加分类反而会让它变成一个需要维护的负担。
   ========================================================================== */

function createSaved(el) {
  let destroyed = false;

  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  async function render() {
    await loadFavorites();
    if (destroyed) return;

    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '已收藏' }),
        h('div', { class: 'page__subtitle', text: favoriteItems.length ? `共 ${favoriteItems.length} 项` : '还没有收藏任何资源' }),
      ),
      h('div', { class: 'page__actions' },
        button('去逛逛', { variant: 'ghost', icon: 'compass', onClick: () => go(SECTION, 'mod') }),
      ),
    );

    if (!favoriteItems.length) {
      fill(scroll, h('div', { class: 'page__inner' }, emptyState({
        icon: 'heart',
        title: '收藏夹是空的',
        desc: '在任意资源的详情页点「收藏」，它就会出现在这里，方便下次直接安装。',
        actions: [button('浏览模组', { variant: 'primary', icon: 'puzzle', onClick: () => go(SECTION, 'mod') })],
      })));
      return;
    }

    fill(scroll, h('div', { class: 'page__inner' },
      h('div', { class: 'rows' },
        ...favoriteItems.map((f) => h('div', { class: 'row-item', role: 'button', tabindex: '0',
          onClick: () => { projectId = f.id; projectKind = f.kind || 'mod'; go(SECTION, 'detail'); },
          onKeydown: (e) => { if (e.key === 'Enter') { projectId = f.id; projectKind = f.kind || 'mod'; go(SECTION, 'detail'); } },
        },
          h('div', { class: 'row-item__icon' },
            f.iconUrl
              ? h('img', { src: f.iconUrl, alt: '', loading: 'lazy' })
              : icon('puzzle', 'md')),
          h('div', { class: 'grow', style: { minWidth: '0' } },
            h('div', { class: 'row-item__name truncate', text: f.title || f.id }),
            h('div', { class: 'row-item__meta truncate' },
              h('span', { text: KIND_BY_ID[f.kind]?.label ?? '资源' }),
              h('span', { class: 'dim', text: ' · ' }),
              h('span', { text: f.description || '—' }),
            ),
          ),
          iconButton('trash-2', {
            size: 'sm', tip: '取消收藏',
            onClick: async (e) => {
              e.stopPropagation();
              await toggleFavorite({ id: f.id, kind: f.kind || 'mod' });
              render();
            },
          }),
        )),
      ),
    ));
  }

  render();

  // 内核在收藏变化时会推 favorites.changed；别的页面改了收藏，这一页也要跟着刷
  const off = on('favorites.changed', () => { if (!destroyed) render(); });
  return {
    reload: render,
    destroy() { destroyed = true; off(); },
  };
}

/* ===========================================================================
   导出模块定义
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [SECTION]: { title: '资源中心', icon: 'puzzle', ...makeListPage('mod') },
    [`${SECTION}:mod`]: { title: '模组', icon: 'puzzle', ...makeListPage('mod') },
    [`${SECTION}:resourcepack`]: { title: '资源包', icon: 'image', ...makeListPage('resourcepack') },
    [`${SECTION}:shader`]: { title: '光影包', icon: 'sparkles', ...makeListPage('shader') },
    [`${SECTION}:datapack`]: { title: '数据包', icon: 'database', ...makeListPage('datapack') },
    [`${SECTION}:world`]: { title: '世界', icon: 'earth', ...makeListPage('world') },
    [`${SECTION}:modpack`]: { title: '整合包', icon: 'boxes', ...makeListPage('modpack') },
    [`${SECTION}:detail`]: { title: '资源详情', icon: 'file-text', ...withActivate(createDetail) },
    [`${SECTION}:saved`]: { title: '已收藏', icon: 'heart', ...withActivate(createSaved) },
  },
};
