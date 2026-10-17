/**
 * ui.js —— DOM 构造与通用浮层组件。
 *
 * 这一层只解决"怎么把界面拼出来"，不含任何业务逻辑：
 *   h()       极简 hyperscript，替代字符串拼 HTML（天然免疫 XSS 注入）
 *   icon()    生成图标元素（CSS mask 着色，自动跟随主题）
 *   toast()   右下角通知
 *   dialog()  模态对话框
 *   confirm() 二次确认
 *   menu()    弹出菜单
 *   格式化工具：字节 / 数量 / 日期 / 相对时间
 */

import { maskUrl, ICONS } from './icons.js';
import { t } from './i18n.js';

/* ===========================================================================
   DOM 基础
   ========================================================================== */

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

/** 创建元素。props 支持 class / style(对象) / dataset / 事件(on*)。 */
export function h(tag, props = null, ...children) {
  const el = document.createElement(tag);

  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === null || v === undefined || v === false) continue;

      if (k === 'class' || k === 'className') {
        el.className = Array.isArray(v) ? v.filter(Boolean).join(' ') : String(v);
      } else if (k === 'style' && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v)) {
          if (sv === null || sv === undefined) continue;
          sk.startsWith('--') ? el.style.setProperty(sk, String(sv)) : (el.style[sk] = sv);
        }
      } else if (k === 'dataset' && typeof v === 'object') {
        for (const [dk, dv] of Object.entries(v)) if (dv !== null && dv !== undefined) el.dataset[dk] = String(dv);
      } else if (k === 'html') {
        el.innerHTML = String(v);
      } else if (k === 'text') {
        el.textContent = String(v);
      } else if (k.startsWith('on') && typeof v === 'function') {
        el.addEventListener(k.slice(2).toLowerCase(), v);
      } else if (k === 'ref' && typeof v === 'function') {
        v(el);
      } else {
        el.setAttribute(k, v === true ? '' : String(v));
      }
    }
  }

  append(el, children);
  return el;
}

function append(parent, children) {
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false || c === true) continue;
    parent.appendChild(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

/** 清空并重新填充一个容器。 */
export function fill(container, ...children) {
  container.replaceChildren();
  append(container, children);
  return container;
}

export function frag(...children) {
  const f = document.createDocumentFragment();
  append(f, children);
  return f;
}

/* ===========================================================================
   图标
   ========================================================================== */

/** 生成一个图标元素。size: xs|sm|md|lg|xl|2xl|3xl */
export function icon(name, size) {
  const url = maskUrl(name);
  const el = h('span', { class: 'icon' + (size ? ` icon--${size}` : ''), 'aria-hidden': 'true' });
  el.style.webkitMaskImage = url;
  el.style.maskImage = url;
  return el;
}

/**
 * 加载器官方彩色标志。
 *
 * 文件名在**前端**维护而不是让内核返回完整路径，原因是这批资源的格式并不统一
 * （Fabric 是官网 PNG，Forge/Quilt/NeoForge 是官网 SVG，OptiFine/LiteLoader 是安装器里的 PNG），
 * 让 C# 记住"哪个用哪个后缀"既别扭又容易和资源目录脱节。这里用 id 做键一次性收口。
 */
const LOADER_LOGOS = {
  vanilla: 'vanilla.png',
  fabric: 'fabric.png',
  quilt: 'quilt.svg',
  // Forge 用 PCL 的铁砧：官方只有横向字标（316×66），塞进方形位就是一条细线；
  // 铁砧既方正又是彩色，语义上也正好对应 Forge 的"锻造"
  forge: '../blocks/loader-forge.png',
  neoforge: 'neoforge.svg',
  optifine: 'optifine.png',
  liteloader: 'liteloader.png',
  // LegacyFabric 就是 Fabric 工具链，官方没有独立标志，沿用 Fabric 的
  legacyfabric: 'fabric.png',
  // 这两个官方没有独立彩色标志，用 PCL 的方块图标兜底
  cleanroom: '../blocks/loader-cleanroom.png',
  litemod: '../blocks/loader-litemod.png',
};

/** 生成加载器标志元素。size: xs | sm | md | lg */
export function loaderLogo(loaderId, size = 'md') {
  const id = String(loaderId ?? 'vanilla').toLowerCase();
  const file = LOADER_LOGOS[id] ?? LOADER_LOGOS.vanilla;
  return h('img', {
    class: `loader-logo loader-logo--${size}`,
    src: `assets/img/loaders/${file}`,
    alt: '', loading: 'lazy', decoding: 'async', draggable: 'false',
  });
}

/** 版本类型对应的方块图标（内核已经把路径算好放在 v.icon 里）。 */
export function versionIcon(version, size = 'sm') {
  const rel = typeof version === 'string' ? version : version?.icon;
  if (!rel) return icon('box', size === 'xs' ? 'xs' : 'sm');
  return h('img', {
    class: `version-icon version-icon--${size}`,
    src: `assets/img/${rel}`,
    alt: '', loading: 'lazy', decoding: 'async', draggable: 'false',
  });
}

/**
 * 把内核给出的实例封面转成图片地址。
 * 内核可能给 `blocks/version-release.png`（带目录），也可能只给文件名，
 * 这里都兜住；早期版本给的是 .svg 后缀，统一按 .png 处理。
 */
export function blockCover(rel) {
  if (!rel) return null;
  const raw = String(rel);
  // 自定义封面是 DataHost 虚拟主机的完整 URL（内核 WebAssets.Data 生成），直接透传
  if (/^https?:\/\//i.test(raw)) return raw;
  const path = raw.includes('/') ? raw : `blocks/${raw.replace(/\.svg$/i, '.png')}`;
  return `assets/img/${path}`;
}

/**
 * 从内核给的路径反推加载器图标元素（兼容旧字段 loaderIcon）。
 * 拿不到就回落到内置矢量图标，绝不留下空白格子。
 */
export function loaderIcon(rel, size = 'sm') {
  const m = /([a-z0-9-]+)\.(svg|png)$/i.exec(String(rel ?? ''));
  const name = m ? m[1] : null;
  if (name && (name in LOADER_LOGOS || name.startsWith('loader-'))) {
    return loaderLogo(name.replace(/^loader-/, ''), size === 'xs' ? 'xs' : size === 'lg' ? 'lg' : 'md');
  }
  return icon('box', size);
}

/** 就地切换一个已有元素的图标（列表复用节点时用）。 */
export function setIcon(el, name) {
  const url = maskUrl(name);
  el.style.webkitMaskImage = url;
  el.style.maskImage = url;
}

/* ===========================================================================
   按钮工厂
   统一出口，避免各处手写 class 与尺寸组合。
   ========================================================================== */

export function button(label, opts = {}) {
  label = t(label);
  const { variant = 'secondary', size, icon: ic, iconAfter, onClick, title, disabled, block, id, className } = opts;
  const cls = ['btn', `btn--${variant}`];
  if (size) cls.push(`btn--${size}`);
  if (block) cls.push('btn--block');
  if (className) cls.push(className);
  if (ic && !label) cls.push('btn--icon');

  const btn = h('button', {
    class: cls,
    type: 'button',
    title,
    id,
    disabled: disabled || undefined,
    'aria-disabled': disabled ? 'true' : null,
    onClick,
  });
  if (ic) btn.appendChild(icon(ic, size === 'lg' ? 'md' : 'sm'));
  if (label) btn.appendChild(h('span', { text: label }));
  if (iconAfter) btn.appendChild(icon(iconAfter, 'sm'));
  return btn;
}

/** 单纯的图标按钮。 */
export function iconButton(ic, opts = {}) {
  if (opts?.tip) opts.tip = t(opts.tip);
  const { variant = 'ghost', size = 'md', tip, onClick, id, className, label } = opts;
  const btn = h('button', {
    class: ['btn', `btn--${variant}`, 'btn--icon', `btn--${size}`, className],
    type: 'button',
    id,
    'aria-label': label || tip || '',
    'data-tip': tip || null,
    onClick,
  });
  btn.appendChild(icon(ic, size === 'lg' ? 'md' : size === 'sm' ? 'xs' : 'sm'));
  return btn;
}

/* ===========================================================================
   格式化
   ========================================================================== */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

export function fmtBytes(n, digits = 1) {
  if (!Number.isFinite(n) || n <= 0) return '0 B';
  let i = 0;
  let v = n;
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(i === 0 ? 0 : digits)} ${UNITS[i]}`;
}

/** 中文数量级：万 / 亿 */
export function fmtCount(n) {
  if (!Number.isFinite(n)) return '0';
  const a = Math.abs(n);
  if (a >= 1e8) return `${(n / 1e8).toFixed(1)} 亿`;
  if (a >= 1e4) return `${(n / 1e4).toFixed(1)} 万`;
  return String(Math.round(n));
}

export function fmtDate(input) {
  const d = toDate(input);
  if (!d) return '—';
  const p = (x) => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export function fmtDateTime(input) {
  const d = toDate(input);
  if (!d) return '—';
  const p = (x) => String(x).padStart(2, '0');
  return `${fmtDate(d)} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

export function fmtAgo(input) {
  const d = toDate(input);
  if (!d) return '—';
  const s = (Date.now() - d.getTime()) / 1000;
  if (s < 0) return fmtDate(d);
  if (s < 60) return '刚刚';
  if (s < 3600) return `${Math.floor(s / 60)} 分钟前`;
  if (s < 86400) return `${Math.floor(s / 3600)} 小时前`;
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} 天前`;
  if (s < 86400 * 365) return `${Math.floor(s / 86400 / 30)} 个月前`;
  return `${Math.floor(s / 86400 / 365)} 年前`;
}

export function fmtDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const total = Math.floor(ms / 1000);
  const hh = Math.floor(total / 3600);
  const mm = Math.floor((total % 3600) / 60);
  const ss = total % 60;
  const p = (x) => String(x).padStart(2, '0');
  return hh > 0 ? `${hh}:${p(mm)}:${p(ss)}` : `${mm}:${p(ss)}`;
}

export function fmtSpeed(bytesPerSec) {
  if (!Number.isFinite(bytesPerSec) || bytesPerSec <= 0) return '—';
  return `${fmtBytes(bytesPerSec)}/s`;
}

function toDate(v) {
  if (!v) return null;
  if (v instanceof Date) return Number.isNaN(v.getTime()) ? null : v;
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? null : d;
}

export const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/* ===========================================================================
   通知
   ========================================================================== */

const TOAST_ICONS = { success: 'circle-check', danger: 'circle-x', warning: 'alert-triangle', accent: 'info', neutral: 'info' };

export function toast({ title, desc, kind = 'neutral', timeout = 4200, action } = {}) {
  title = title == null ? title : t(title);
  desc = desc == null ? desc : t(desc);
  const host = $('#toasts');
  if (!host) return () => {};

  const node = h('div', { class: `toast toast--${kind}`, role: 'status' },
    h('div', { class: 'toast__icon' }, icon(TOAST_ICONS[kind] ?? 'info', 'sm')),
    h('div', { class: 'grow' },
      title ? h('div', { class: 'toast__title', text: title }) : null,
      desc ? h('div', { class: 'toast__desc', text: desc }) : null,
    ),
    action ? button(action.label, { variant: 'ghost', size: 'sm', onClick: () => { action.onClick?.(); close(); } }) : null,
    iconButton('x', { size: 'sm', tip: '关闭', onClick: () => close() }),
  );

  let closed = false;
  function close() {
    if (closed) return;
    closed = true;
    node.classList.add('is-leaving');
    node.addEventListener('animationend', () => node.remove(), { once: true });
  }

  host.appendChild(node);
  if (timeout > 0) {
    const timer = setTimeout(close, timeout);
    node.addEventListener('mouseenter', () => clearTimeout(timer));
  }
  return close;
}

/* ===========================================================================
   对话框
   ========================================================================== */

let _dialogStack = [];

/**
 * 显示模态对话框。
 * body 可以是节点、节点数组，或 (resolveClose) => Node 的工厂函数。
 * actions 形如 [{ label, variant, value, onClick }]。
 * 返回一个 Promise，resolve 为被点击按钮的 value（或 null）。
 */
export function dialog({ title, icon: ic, body, actions = [], width = 520, dismissible = true, onClose } = {}) {
  title = t(title);
  const scrim = $('#scrim');
  if (!scrim) return Promise.resolve(null);

  return new Promise((resolve) => {
    const slot = h('div', { class: 'dialog', style: { '--dialog-w': `${width}px` } });
    const head = h('div', { class: 'dialog__head' },
      ic ? icon(ic, 'md') : null,
      h('div', { class: 'dialog__title grow', text: title ?? '' }),
      dismissible ? iconButton('x', { size: 'sm', tip: '关闭', onClick: () => finish(null) }) : null,
    );
    const bodyEl = h('div', { class: 'dialog__body' });
    const footEl = h('div', { class: 'dialog__foot' });

    slot.append(head, bodyEl);
    if (actions.length) {
      for (const a of actions) {
        footEl.appendChild(button(a.label, {
          variant: a.variant ?? 'secondary',
          icon: a.icon,
          onClick: async () => {
            if (a.onClick) {
              const keep = await a.onClick();
              if (keep === false) return;
            }
            finish(a.value ?? a.label);
          },
        }));
      }
      slot.appendChild(footEl);
    }

    const content = typeof body === 'function' ? body(finish) : body;
    if (content) fill(bodyEl, content);
    if (!footEl.childElementCount) footEl.remove();

    function finish(value) {
      const idx = _dialogStack.indexOf(entry);
      if (idx >= 0) _dialogStack.splice(idx, 1);
      slot.style.transform = 'scale(0.97) translateY(6px)';
      slot.style.opacity = '0';
      setTimeout(() => {
        slot.remove();
        if (!_dialogStack.length) scrim.classList.remove('is-open');
        onClose?.(value);
        resolve(value);
      }, 180);
    }

    function onKey(e) {
      if (e.key === 'Escape' && dismissible) { finish(null); }
    }
    function onScrim(e) {
      if (e.target === scrim && dismissible) finish(null);
    }

    const entry = { finish };
    _dialogStack.push(entry);

    scrim.replaceChildren(slot);
    scrim.classList.add('is-open');
    scrim.onclick = onScrim;

    // 焦点交给第一个可聚焦元素，保证纯键盘可用
    requestAnimationFrame(() => {
      const focusable = slot.querySelector('input, textarea, select, button:not([disabled])');
      focusable?.focus();
    });

    const keyHandler = (e) => {
      if (_dialogStack[_dialogStack.length - 1] === entry) onKey(e);
    };
    document.addEventListener('keydown', keyHandler);
    slot.addEventListener('remove', () => document.removeEventListener('keydown', keyHandler));
  });
}

export function confirm({ title = '确认操作', message, confirmLabel = '确认', cancelLabel = '取消', danger = false } = {}) {
  title = t(title); message = t(message); confirmLabel = t(confirmLabel); cancelLabel = t(cancelLabel);
  return dialog({
    title,
    icon: danger ? 'alert-triangle' : 'help-circle',
    width: 440,
    body: h('div', { style: { lineHeight: 'var(--lh-relaxed)' }, text: message ?? '' }),
    actions: [
      { label: cancelLabel, variant: 'ghost', value: false },
      { label: confirmLabel, variant: danger ? 'danger' : 'primary', value: true },
    ],
  }).then((v) => v === true);
}

/* ===========================================================================
   弹出菜单
   ========================================================================== */

let _openMenu = null;

export function menu(anchor, items, { align = 'start', width } = {}) {
  closeMenu();

  const node = h('div', { class: 'menu', style: width ? { minWidth: `${width}px` } : null });
  for (const it of items) {
    if (it.separator) { node.appendChild(h('div', { class: 'menu__sep' })); continue; }
    const inert = !!it.disabled;
    node.appendChild(h('div', {
      // active 标出"当前选中的那一项"，disabled 让它看起来就点不动 ——
      // 两者都只是视觉表达，行为上的拦截分别在 onClick 是否挂、以及调用方自己判断。
      class: ['menu__item', it.danger && 'menu__item--danger', it.active && 'menu__item--active', inert && 'menu__item--disabled'],
      role: 'menuitem',
      tabindex: inert ? '-1' : '0',
      'aria-disabled': inert ? 'true' : null,
      onClick: inert ? null : (e) => { e.stopPropagation(); closeMenu(); it.onClick?.(); },
      onKeydown: inert ? null : (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); closeMenu(); it.onClick?.(); } },
    },
      // iconNode 优先：调用方需要显示位图（版本方块、加载器标志）时用它。
      // 只传 icon 名的话没法表达"这张图是哪个版本"，而版本选择器恰恰需要。
      it.iconNode ?? (it.icon ? icon(it.icon, 'sm') : null),
      h('span', { class: 'grow', text: it.label }),
      it.hint ? h('span', { class: 'dim mono', style: { fontSize: 'var(--fs-micro)' }, text: it.hint }) : null,
    ));
  }

  document.body.appendChild(node);
  const r = anchor.getBoundingClientRect();
  const mw = node.offsetWidth;
  const mh = node.offsetHeight;

  let left = align === 'end' ? r.right - mw : r.left;
  left = clamp(left, 8, innerWidth - mw - 8);
  let top = r.bottom + 6;
  if (top + mh > innerHeight - 8) top = Math.max(8, r.top - mh - 6);

  node.style.left = `${left}px`;
  node.style.top = `${top}px`;
  requestAnimationFrame(() => node.classList.add('is-open'));

  const onDoc = (e) => { if (!node.contains(e.target)) closeMenu(); };
  const onScroll = () => closeMenu();
  setTimeout(() => {
    document.addEventListener('mousedown', onDoc);
    window.addEventListener('resize', onScroll);
    document.addEventListener('scroll', onScroll, true);
  }, 0);

  _openMenu = () => {
    document.removeEventListener('mousedown', onDoc);
    window.removeEventListener('resize', onScroll);
    document.removeEventListener('scroll', onScroll, true);
    node.remove();
    _openMenu = null;
  };
  return _openMenu;
}

export function closeMenu() { _openMenu?.(); }

/* ===========================================================================
   工具提示（全局委托：任何带 data-tip 的元素自动获得）
   ========================================================================== */

let _tipEl = null;
let _tipTimer = null;

function tipNode() {
  if (!_tipEl) {
    _tipEl = h('div', { class: 'tooltip', role: 'tooltip' });
    document.body.appendChild(_tipEl);
  }
  return _tipEl;
}

/* ===========================================================================
   自绘下拉选择器

   为什么不用原生 <select>：它的弹出列表由操作系统渲染 —— 白色方块、系统字体、
   系统高亮色，和这套暖色调自绘界面放在一起像两个软件拼起来的。
   而且在高 DPI 下原生列表的字号不受我们控制。这里用已有的 menu() 弹层重画一遍，
   观感、键盘操作、选中态就都统一了。

   同时提供 setOptions()，供"选项依赖另一个字段"的场景使用
   （例如加载器版本要随游戏版本变化）。
   =========================================================================== */

/**
 * @param {object} cfg
 * @param {string} [cfg.value]        当前值
 * @param {Array}  cfg.options        [{ value, label, hint, disabled }]
 * @param {Function} [cfg.onChange]   (value) => void
 * @param {string} [cfg.placeholder]  没有选中项时的占位文案
 * @param {string} [cfg.ariaLabel]
 * @param {string} [cfg.width]        控件宽度（CSS 值）
 */
export function selectControl({ value, options = [], onChange, placeholder = '请选择', ariaLabel, width } = {}) {
  let opts = options;
  let current = value ?? '';
  let disabled = false;

  const label = h('span', { class: 'grow truncate' });
  const el = h('button', {
    class: 'select select--custom', type: 'button',
    style: width ? { width } : null,
    'aria-haspopup': 'listbox',
    'aria-label': ariaLabel ?? '',
    onClick: (e) => {
      if (disabled) return;
      const rect = e.currentTarget.getBoundingClientRect();
      menu(e.currentTarget, opts.length
        ? opts.map((o) => ({
            label: o.label,
            hint: o.hint ?? '',
            icon: o.value === current ? 'check' : null,
            disabled: !!o.disabled,
            onClick: () => { current = o.value; sync(); onChange?.(o.value); },
          }))
        : [{ label: '没有可选项', icon: 'info', disabled: true }],
        { width: Math.max(rect.width, 180) });
    },
  }, label, icon('chevron-down', 'xs'));

  function sync() {
    const found = opts.find((o) => o.value === current);
    label.textContent = found?.label ?? placeholder;
    // 未选中时用次要色，让人一眼看出"还没选"
    label.style.color = found ? '' : 'var(--text-3)';
    el.setAttribute('data-empty', found ? 'false' : 'true');

    // ⚠️ 这里**绝不能**写 el.disabled = disabled。
    // 上面给 el.disabled 定义了 setter，而那个 setter 会调用 sync()，
    // 于是 sync → setter → sync → … 无限递归，直接抛
    // "Maximum call stack size exceeded"，整个页面变成错误页。
    // 禁用状态由 data-disabled（样式）与 onClick 里的判断（行为）共同表达就够了。
    el.setAttribute('data-disabled', disabled ? 'true' : 'false');
  }

  Object.defineProperty(el, 'value', {
    get: () => current,
    set: (v) => { current = v ?? ''; sync(); },
  });
  Object.defineProperty(el, 'disabled', {
    get: () => disabled,
    set: (v) => { disabled = !!v; el.setAttribute('data-disabled', disabled ? 'true' : 'false'); sync(); },
  });

  /** 重新填充选项。value 省略时尽量保留当前选中项（存在的话）。 */
  el.setOptions = (next, nextValue) => {
    opts = next ?? [];
    if (nextValue !== undefined) current = nextValue ?? '';
    else if (!opts.some((o) => o.value === current)) current = '';
    sync();
  };

  el.getOptions = () => opts;

  sync();
  return el;
}

export function initTooltips() {
  const show = (target) => {
    const text = target.dataset.tip;
    if (!text) return;
    const el = tipNode();
    el.textContent = text;
    el.classList.add('is-visible');
    const r = target.getBoundingClientRect();
    el.style.left = '0px';
    el.style.top = '0px';
    const w = el.offsetWidth;
    const hgt = el.offsetHeight;
    let left = r.left + r.width / 2 - w / 2;
    left = clamp(left, 8, innerWidth - w - 8);
    let top = r.top - hgt - 8;
    if (top < 8) top = r.bottom + 8;
    el.style.left = `${left}px`;
    el.style.top = `${top}px`;
  };

  const hide = () => {
    clearTimeout(_tipTimer);
    _tipEl?.classList.remove('is-visible');
  };

  document.addEventListener('mouseover', (e) => {
    const t = e.target.closest?.('[data-tip]');
    if (!t) return;
    clearTimeout(_tipTimer);
    _tipTimer = setTimeout(() => show(t), 420);
  });
  document.addEventListener('mouseout', (e) => {
    if (e.target.closest?.('[data-tip]')) hide();
  });
  document.addEventListener('mousedown', hide);
  window.addEventListener('blur', hide);
  document.addEventListener('scroll', hide, true);
}

/* ===========================================================================
   常用组合件
   ========================================================================== */

/** 空状态。 */
export function emptyState({ icon: ic = 'box', title, desc, actions = [] } = {}) {
  title = title == null ? title : t(title);
  desc = desc == null ? desc : t(desc);
  return h('div', { class: 'empty' },
    h('div', { class: 'empty__art' }, icon(ic, 'xl')),
    title ? h('div', { class: 'empty__title', text: title }) : null,
    desc ? h('div', { class: 'empty__desc', text: desc }) : null,
    actions.length ? h('div', { class: 'empty__actions' }, ...actions) : null,
  );
}

/** 区块标题行：左标题 + 右动作。 */
export function sectionHead(title, { icon: ic, desc, actions = [] } = {}) {
  return h('div', { class: 'section-head' },
    h('div', { class: 'grow' },
      h('div', { class: 'section-title' }, ic ? icon(ic, 'md') : null, h('span', { text: title })),
      desc ? h('div', { class: 'section-desc', text: desc }) : null,
    ),
    actions.length ? h('div', { class: 'row', style: { gap: 'var(--sp-4)' } }, ...actions) : null,
  );
}

export function chip(label, { variant, icon: ic, removable, onRemove, title } = {}) {
  const c = h('span', { class: ['chip', variant && `chip--${variant}`, removable && 'chip--removable'], title },
    ic ? icon(ic, 'sm') : null,
    label ? h('span', { text: label }) : null,
    removable ? h('button', { class: 'chip__close', type: 'button', 'aria-label': '移除', onClick: (e) => { e.stopPropagation(); onRemove?.(); } }, icon('x', 'sm')) : null,
  );
  return c;
}

export function avatar(label, { size = 'md', src, round, className, tip } = {}) {
  const el = h('div', {
    class: ['avatar', `avatar--${size}`, round && 'avatar--round', className],
    'data-tip': tip || null,
  });

  const fallback = () => {
    if (el.querySelector('span')) return;
    el.appendChild(h('span', { text: (label ?? '?').slice(0, 1).toUpperCase() }));
  };

  if (src) {
    const img = h('img', { src, alt: label ?? '', loading: 'lazy' });
    // 加载失败就回退成首字母。
    // 不这么做的话，路径一旦不对（比如调用方传的是内核给的 "blocks/xxx.png"
    // 裸路径，少了 assets/img/ 前缀），界面上会出现浏览器的破图图标，
    // 而且 alt 文本会被圆形裁成半截，看起来像"图片显示错误"。
    img.addEventListener('error', () => { img.remove(); fallback(); });
    el.appendChild(img);
  } else {
    fallback();
  }
  return el;
}

export function progress(value, { indeterminate = false, size } = {}) {
  return h('div', { class: ['progress', indeterminate && 'progress--indeterminate', size && `progress--${size}`], role: 'progressbar' },
    h('div', { class: 'progress__bar', style: { '--pct': `${clamp(value ?? 0, 0, 100)}%` } }),
  );
}

export function switchEl(checked, onChange, { disabled, id, label } = {}) {
  label = label == null ? label : t(label);
  const el = h('button', {
    class: 'switch', type: 'button', role: 'switch', id,
    'aria-checked': checked ? 'true' : 'false',
    'aria-label': label ?? '',
    disabled: disabled || undefined,
    onClick: () => {
      const next = el.getAttribute('aria-checked') !== 'true';
      el.setAttribute('aria-checked', next ? 'true' : 'false');
      onChange?.(next);
    },
  });
  return el;
}

export function segment(items, activeId, onSelect) {
  const el = h('div', { class: 'segment', role: 'tablist' });
  for (const it of items) {
    el.appendChild(h('button', {
      class: 'segment__btn', type: 'button', role: 'tab',
      'aria-selected': it.id === activeId ? 'true' : 'false',
      onClick: () => {
        for (const b of el.children) b.setAttribute('aria-selected', 'false');
        el.querySelector(`[data-seg="${it.id}"]`)?.setAttribute('aria-selected', 'true');
        onSelect?.(it.id);
      },
      'data-seg': it.id,
    }, it.icon ? icon(it.icon, 'xs') : null, h('span', { text: it.label }), it.count != null ? h('span', { class: 'dim', text: String(it.count) }) : null));
  }
  return el;
}

/** 设置行：左侧标题+说明，右侧控件。 */
export function settingRow(title, desc, control) {
  return h('div', {
    class: 'setting-row',
    style: { display: 'flex', alignItems: 'center', gap: 'var(--sp-8)', padding: 'var(--sp-6) 0' },
  },
    h('div', { class: 'grow' },
      h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' }, text: title }),
      desc ? h('div', { style: { fontSize: 'var(--fs-xs)', color: 'var(--text-3)', marginTop: '3px', lineHeight: 'var(--lh-snug)' }, text: desc }) : null,
    ),
    h('div', { style: { flex: '0 0 auto' } }, control),
  );
}

/** 骨架块，用于加载态。 */
export function skeleton(className, style) {
  return h('div', { class: ['skeleton', className], style });
}
