/**
 * theme.js —— 主题、强调色、动效强度、渲染开销。
 *
 * 四件事都只做一件事：把一个属性写到 <html> 上，剩下的交给 CSS 令牌层。
 * 强调色尤其如此 —— 只写 --accent-base 一个变量，令牌层里所有由它
 * color-mix 推导出的色阶（软底、描边、文字、光晕）会整体跟随，
 * 因此不存在"漏改某个组件"的可能。
 */

import { subscribe } from './store.js';
import { invoke } from './bridge.js';

/* ===========================================================================
   强调色预设
   全部取"有材质感"的低饱和色，刻意避开 Material 蓝那一类科技色 ——
   那种蓝是 AI 生成界面的第一默认值，也是这一版要摆脱的东西。
   ========================================================================== */

export const ACCENTS = [
  { id: 'terracotta', label: '陶土', value: '#c2603a', note: '默认 · 温暖、有手工感' },
  { id: 'pine',       label: '松墨', value: '#2f6f5e', note: '沉静的自然绿' },
  { id: 'amber',      label: '琥珀', value: '#b8842a', note: '偏暖的金属黄' },
  { id: 'indigo',     label: '靛青', value: '#4a5fa5', note: '克制的蓝，不刺眼' },
  { id: 'sakura',     label: '落樱', value: '#c2638c', note: '柔和的粉' },
  { id: 'moss',       label: '苔绿', value: '#6b8e3c', note: '偏黄的草绿' },
  { id: 'plum',       label: '暮紫', value: '#7a5480', note: '低饱和的紫' },
  { id: 'graphite',   label: '石墨', value: '#5a5a57', note: '几乎无色，最中性' },
];

/** 首页可选背景。none = 纯渐变。默认取暖调那张，与陶土色相呼应。 */
export const BACKGROUNDS = [
  { id: 'dusk',    label: '暮色', file: 'bg-dusk.jpg' },
  { id: 'aurora',  label: '极光', file: 'bg-aurora.jpg' },
  { id: 'ocean',   label: '深海', file: 'bg-ocean.jpg' },
  { id: 'blossom', label: '樱花', file: 'bg-blossom.jpg' },
  { id: 'nether',  label: '下界', file: 'bg-nether.jpg' },
  { id: 'end',     label: '末地', file: 'bg-end.jpg' },
  { id: 'none',    label: '无背景', file: null },
];

/** 动效强度。中间档为默认值。 */
export const MOTIONS = [
  { id: 'off',    label: '关闭', note: '完全不做过渡，最省资源' },
  { id: 'subtle', label: '轻微', note: '默认 · 只有淡入与轻微位移' },
  { id: 'full',   label: '完整', note: '错峰入场、悬停位移、页面切换' },
];

/** 渲染开销。软件光栅化时会被自动降到 low。 */
export const PERFS = [
  { id: 'low',  label: '流畅', note: '关闭毛玻璃与颗粒，优先帧率' },
  { id: 'mid',  label: '平衡', note: '保留配色与层次，去掉颗粒' },
  { id: 'high', label: '精细', note: '全部效果（需要可用的 GPU）' },
];

/**
 * 旧版本用过的强调色 id → 新 id。
 * 这一版把整套色板换成了低饱和的暖色系，旧的 `blue` / `emerald` 等名字已不存在。
 * 不做映射的话，老用户升级后会看到"设置里明明存的是 blue，界面却是别的颜色"。
 */
const LEGACY_ACCENTS = {
  // blue 是上一版的**默认值**（不是用户主动挑的），因此映射到新的默认色；
  // 真正想用蓝色的人可以在色板里选「靛青」。
  blue: 'terracotta',
  cyan: 'pine',
  green: 'moss',
  emerald: 'pine',
  purple: 'plum',
  pink: 'sakura',
  orange: 'terracotta',
};

function normalizeAccentId(id) {
  if (!id) return ACCENTS[0].id;
  if (ACCENTS.some((a) => a.id === id)) return id;
  const mapped = LEGACY_ACCENTS[id];
  return mapped && ACCENTS.some((a) => a.id === mapped) ? mapped : ACCENTS[0].id;
}

export function accentValue(id) {
  const norm = normalizeAccentId(id);
  return (ACCENTS.find((a) => a.id === norm) ?? ACCENTS[0]).value;
}

export function backgroundFile(id) {
  // 背景列表没有换过 id，但仍然做一次存在性判断，避免旧配置引用已删除的项
  return (BACKGROUNDS.find((b) => b.id === id) ?? BACKGROUNDS[0]).file;
}

/**
 * 按 WCAG 相对亮度决定强调色上的前景用白还是近黑。
 * 像"琥珀"这种亮色底上压白字会掉到 2:1 对比度，必须换成深色字。
 */
export function inkOn(hex) {
  const { r, g, b } = hexToRgb(hex);
  const lin = (c) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  const L = 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  return 1.05 / (L + 0.05) >= (L + 0.05) / 0.05 ? '#ffffff' : '#211e19';
}

function hexToRgb(hex) {
  const s = String(hex).replace('#', '').trim();
  const full = s.length === 3 ? s.split('').map((c) => c + c).join('') : s;
  const n = parseInt(full, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

/* ===========================================================================
   应用
   ========================================================================== */

/** 宿主报告的真实渲染后端。软件光栅化时强制降到低开销档。 */
let renderMode = { software: false, level: 0, known: false };

function resolveTheme(pref) {
  if (pref === 'system') {
    return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  return pref === 'dark' ? 'dark' : 'light';
}

/** 实际生效的渲染档位：用户设置与"是否软件渲染"取较低者。 */
function effectivePerf(pref) {
  const order = ['low', 'mid', 'high'];
  let want = order.indexOf(pref);
  if (want < 0) want = 2;

  // 软件光栅化下，毛玻璃与混合模式会让整屏重新合成，帧率会掉到个位数 ——
  // 这时无论用户选什么，都压到 low。
  if (renderMode.software) return 'low';

  // 系统开了"减少动态效果"时，主动把开销也压一档
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return order[Math.min(want, 1)];

  return order[want];
}

function effectiveMotion(pref) {
  if (pref === 'off') return 'off';
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return 'off';
  return pref === 'full' ? 'full' : 'subtle';
}

export function applyTheme(settings) {
  const root = document.documentElement;
  const resolved = resolveTheme(settings.theme);

  root.dataset.theme = resolved;
  root.dataset.themePref = settings.theme;

  const accent = accentValue(settings.accent);
  root.style.setProperty('--accent-base', accent);
  root.style.setProperty('--accent-ink', inkOn(accent));

  root.dataset.motion = effectiveMotion(settings.uiMotion ?? 'subtle');
  root.dataset.perf = effectivePerf(settings.uiPerf ?? 'high');

  // 通知宿主：无边框窗口的圆角、深色边框与窗口底色都要跟着主题走。
  // 宿主不在（浏览器预览）时静默忽略。
  invoke('window.setTheme', { dark: resolved === 'dark' }).catch(() => {});
}

/** 绑定 store 里的 settings，相关字段一变就重绘。返回一个手动刷新函数。 */
export function bindTheme(getSettings) {
  let last = '';
  const run = (force) => {
    const s = getSettings();
    const key = `${s.theme}|${s.accent}|${s.uiMotion}|${s.uiPerf}|${renderMode.software}|${renderMode.level}`;
    if (!force && key === last) return;
    last = key;
    applyTheme(s);
  };

  run(true);
  subscribe('settings', () => run(false));

  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (getSettings().theme === 'system') run(true);
  });
  matchMedia('(prefers-reduced-motion: reduce)').addEventListener('change', () => run(true));

  return run;
}

/**
 * 宿主在页面加载完成后调用，告诉我们当前是不是软件光栅化。
 * 收到之后立即重算一次，避免首帧先按高开销档画一遍。
 */
globalThis.__scSetRenderMode = (mode) => {
  renderMode = { software: !!mode?.software, level: mode?.level ?? 0, known: true };
  document.dispatchEvent(new CustomEvent('sc:rendermode', { detail: renderMode }));
};
