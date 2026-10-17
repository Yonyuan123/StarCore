/**
 * router.js —— 页面注册、挂载与切换。
 *
 * 关键设计：
 *   1. 页面**懒挂载**：第一次访问才构造 DOM，之后常驻，切回时只切 class，
 *      因此来回切页不会有重建开销（上一版卡顿的一大来源）。
 *   2. 每页只有一个 .is-active 的 .page 元素参与布局，其余 visibility:hidden，
 *      既不参与绘制也不可命中，但保留滚动位置与局部状态。
 *   3. 二级面板以"分区"为单位渲染：同一分区内切换子页面不会重建面板，
 *      避免列表闪一下再回来。
 */

import { fill, icon, h } from './ui.js';
import { setState, getState } from './store.js';
import { RAIL, sectionTitle } from './nav.js';
import { t } from './i18n.js';

const sections = new Map(); // sectionId -> { panel, onEnter }
const pages = new Map();    // pageKey   -> { def, el, mounted }
const order = [];           // 访问顺序，便于返回上一页

let currentKey = null;
let currentSection = null;
const history = [];

export const pageKey = (section, sub) => (sub ? `${section}:${sub}` : section);

/** 注册一个分区的二级面板。 */
export function registerSection(section, { panel, onEnter } = {}) {
  sections.set(section, { panel, onEnter });
}

/**
 * 注册页面。
 * def = {
 *   title,          面包屑末级文字
 *   icon,           面包屑图标
 *   rail?,          若该页面希望图标栏高亮到别的分区（默认取 section）
 *   mount(el, ctx), 首次挂载（同步或异步）
 *   activate(el),   每次进入
 *   deactivate(el), 每次离开
 *   destroy(),      卸载时清理订阅
 *   hidePanel?,     是否强制隐藏二级面板（如首页）
 * }
 */
export function register(key, def) {
  pages.set(key, { def, el: null, mounted: false, cleanups: [] });
}

export async function go(section, sub = null, { replace = false, force = false } = {}) {
  const key = pageKey(section, sub);
  const entry = pages.get(key);

  if (!entry) {
    console.error('[router] 未注册的页面:', key);
    return;
  }

  // 同一分区内换子页：不重复进 onEnter
  const sectionChanged = section !== currentSection;

  if (sectionChanged) {
    panelOff?.();
    const sec = sections.get(section);
    panelOff = await renderPanel(section, sec);
    if (sec?.onEnter) await sec.onEnter();
    currentSection = section;
  }

  if (key === currentKey && !force) {
    setState({ route: { section, sub } });
    return;
  }

  const prev = currentKey ? pages.get(currentKey) : null;
  if (prev?.el && prev.def.deactivate) {
    try { prev.def.deactivate(prev.el); } catch (e) { console.error('[router] deactivate 异常', e); }
  }

  const content = document.getElementById('content');

  // 懒挂载
  if (!entry.el) {
    const el = h('div', { class: 'page', 'data-page': key });
    content.appendChild(el);
    entry.el = el;
    try {
      // ctx.off 是给页面的"退订收集器"：页面可以直接 push 退订函数，
      // 也可以从 mount 返回 { destroy }。两条路都会在卸载时被调用，
      // 这样即使作者忘了返回值也不会漏退订（见 instance.js 的双写写法）。
      const ctx = { section, sub, off: [] };
      const ret = await entry.def.mount?.(el, ctx);
      entry.cleanups = [
        ...(Array.isArray(ctx.off) ? ctx.off : []),
        ...(typeof ret?.destroy === 'function' ? [ret.destroy] : []),
      ];
    } catch (e) {
      console.error('[router] 页面挂载失败:', key, e);
      fill(el, errorPane(entry.def.title ?? key, e));
    }
    entry.mounted = true;
    el.classList.add('is-mounted');
    // 入场动画：先渲染一帧再激活，保证 transition 生效
    requestAnimationFrame(() => el.classList.add('is-active'));
  } else {
    entry.el.classList.add('is-mounted');
    requestAnimationFrame(() => entry.el.classList.add('is-active'));
  }

  if (prev?.el && prev.el !== entry.el) prev.el.classList.remove('is-active');

  if (entry.def.activate) {
    try { entry.def.activate(entry.el); } catch (e) { console.error('[router] activate 异常', e); }
  }

  if (!replace) {
    history.push({ section, sub });
    if (history.length > 40) history.shift();
  }

  currentKey = key;

  // 面板显隐
  const panelEl = document.getElementById('panel');
  const hidePanel = !!entry.def.hidePanel;
  panelEl.hidden = hidePanel;
  setState({ route: { section, sub }, panelVisible: !hidePanel });

  renderRail(entry.def.rail ?? section);
  renderCrumb(entry.def.title ?? sectionTitle(section), entry.def.icon);
  document.title = `${entry.def.title ?? sectionTitle(section)} · StarCore`;
}

export function back() {
  history.pop();
  const prev = history.pop();
  if (prev) go(prev.section, prev.sub, { replace: true });
  else go('home', null, { replace: true });
}

export function current() {
  return { key: currentKey, section: currentSection };
}

/* ===========================================================================
   二级面板
   ========================================================================== */

let panelOff = null;

async function renderPanel(section, sec) {
  const el = document.getElementById('panel');
  if (!sec?.panel) return null;
  try {
    return await sec.panel(el, h, { go });
  } catch (e) {
    console.error('[router] 面板渲染失败:', section, e);
    fill(el, h('div', { class: 'panel__body' }, h('div', { class: 'dim', style: { padding: '16px', fontSize: 'var(--fs-sm)' }, text: '面板加载失败' })));
    return null;
  }
}

/* ===========================================================================
   图标栏与面包屑
   ========================================================================== */

export function renderRail(activeId) {
  const rail = document.getElementById('rail');
  if (!rail || rail.dataset.active === activeId) return;
  rail.dataset.active = activeId;

  const existing = rail.querySelectorAll('.rail-btn[data-section]');
  if (existing.length === RAIL.length) {
    for (const b of existing) b.setAttribute('aria-selected', b.dataset.section === activeId ? 'true' : 'false');
    return;
  }

  // 首次渲染：前缀插入导航按钮，保留底部区（由 app.js 追加，带 data-tail）
  const tail = rail.querySelector('[data-tail]');
  for (const item of RAIL) {
    const btn = h('button', {
      class: 'rail-btn',
      type: 'button',
      'data-section': item.id,
      'data-tip': `${t(item.label)} · ${t(item.hint)}`,
      'aria-label': t(item.label),
      'aria-selected': item.id === activeId ? 'true' : 'false',
      onClick: () => go(item.id),
    }, icon(item.icon, 'lg'));
    rail.insertBefore(btn, tail ?? null);
  }
}

function renderCrumb(title, ic) {
  const crumb = document.getElementById('crumb');
  if (!crumb) return;
  fill(crumb,
    h('span', { class: 'sep', text: '·' }),
    ic ? icon(ic, 'xs') : null,
    h('strong', { text: title }),
  );
}

/** 页面级错误兜底：不把异常抛到白屏，而是给出可恢复的界面。 */
function errorPane(title, err) {
  return h('div', { class: 'merge-empty', style: { padding: '48px' } },
    h('div', { class: 'empty' },
      h('div', { class: 'empty__art' }, icon('alert-triangle', 'xl')),
      h('div', { class: 'empty__title', text: `${title} 加载失败` }),
      h('div', { class: 'empty__desc', text: String(err?.message ?? err ?? '未知错误') }),
      h('div', { class: 'empty__actions' },
        h('button', { class: 'btn btn--secondary', type: 'button', onClick: () => location.reload() }, h('span', { text: '重新加载界面' })),
      ),
    ),
  );
}

/* ===========================================================================
   与外部（宿主窗口）联动：Ctrl+Tab / 鼠标侧键 切页
   ========================================================================== */

export function initRouteShortcuts() {
  document.addEventListener('keydown', (e) => {
    if (!e.ctrlKey) return;
    const n = Number(e.key);
    if (Number.isInteger(n) && n >= 1 && n <= RAIL.length) {
      e.preventDefault();
      go(RAIL[n - 1].id);
    }
  });
}

export function currentSectionId() {
  return getState().route.section;
}
