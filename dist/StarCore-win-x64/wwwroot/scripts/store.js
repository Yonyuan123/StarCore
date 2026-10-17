/**
 * store.js —— 全局状态。
 *
 * 刻意保持极小：一个扁平的键值对象 + 按 key 的订阅。
 * 页面只订阅自己关心的键，避免"任一状态变化就整页重绘"。
 * 真正的持久化由 C# 内核负责（settings.json / instances.json），
 * 这里只是内存镜像，写操作一律经 bridge 回落到内核再回灌。
 */

import { invoke } from './bridge.js';
import { initLocale } from './i18n.js';

const state = {
  /** 内核与运行环境信息 */
  app: null,

  /** 用户设置（主题、强调色、语言、下载源…） */
  settings: {
    theme: 'light',
    accent: 'terracotta',
    background: 'dusk',
    uiMotion: 'subtle',   // off | subtle | full —— 默认取中间值
    uiPerf: 'high',       // low | mid | high —— 软件渲染时会被自动压到 low
    language: 'zh-CN',
    closeToTray: false,
    autoUpdate: true,
    downloadSource: 'official',
    maxThreads: 32,
    proxy: '',
    keepLauncherOpen: true,
  },

  /** 账户 */
  accounts: [],
  activeUuid: null,

  /** 实例 */
  instances: [],
  activeInstanceId: null,

  /** 版本清单（Mojang） */
  versions: [],
  versionsLoadedAt: null,
  versionsStale: true,

  /** 已探测到的 Java 运行时 */
  javas: [],

  /** 运行中的任务：安装 / 下载 / 校验 */
  tasks: [],

  /** 启动状态机：idle | preparing | running | exited | crashed */
  launch: { state: 'idle', instanceId: null, pid: null, startedAt: null, exitCode: null },

  /** 界面：当前导航（由 router 维护，便于面包屑与返回） */
  route: { section: 'home', sub: null },

  /** 界面：面板是否可见 */
  panelVisible: true,
};

const subscribers = new Map(); // key -> Set<fn>
let _batching = false;
const _dirty = new Set();

export function getState() {
  return state;
}

export function read(key) {
  return state[key];
}

/** 浅合并若干个键，并在微任务末尾统一通知（合并同一轮的多次写入）。 */
export function setState(patch) {
  let changed = false;
  for (const [k, v] of Object.entries(patch)) {
    if (state[k] === v) continue;
    state[k] = v;
    _dirty.add(k);
    changed = true;
  }
  if (!changed) return;
  if (_batching) return;
  _batching = true;
  queueMicrotask(flush);
}

function flush() {
  const keys = [..._dirty];
  _dirty.clear();
  _batching = false;

  const called = new Set();
  for (const k of keys) {
    const set = subscribers.get(k);
    if (!set) continue;
    for (const fn of set) { if (!called.has(fn)) { called.add(fn); safeCall(fn, state, k); } }
  }
  const wild = subscribers.get('*');
  if (wild) for (const fn of wild) safeCall(fn, state, keys);
}

function safeCall(fn, ...args) {
  try { fn(...args); } catch (e) { console.error('[store] 订阅回调异常', e); }
}

/** 订阅一个键（或 '*' 表示任意变化）。返回退订函数。 */
export function subscribe(key, fn) {
  if (!subscribers.has(key)) subscribers.set(key, new Set());
  subscribers.get(key).add(fn);
  return () => subscribers.get(key)?.delete(fn);
}

/** 一次性等待某个键满足条件。 */
export function when(key, predicate = () => true) {
  if (predicate(state[key])) return Promise.resolve(state[key]);
  return new Promise((resolve) => {
    const off = subscribe(key, (s) => {
      if (predicate(s[key])) { off(); resolve(s[key]); }
    });
  });
}

/* ===========================================================================
   领域动作：读写设置 / 账户 / 实例
   统一在这里收口，页面不直接碰 bridge 的对应方法，避免状态漂移。
   ========================================================================== */

export async function loadAll() {
  const [app, settings, accounts, instances, versions] = await Promise.all([
    invoke('app.info').catch(() => null),
    invoke('settings.get').catch(() => null),
    invoke('account.list').catch(() => null),
    invoke('instance.list').catch(() => null),
    // 版本清单必须在启动时就取：新建实例、首页切换版本都要用它，
    // 之前只在「下载」页拉取，导致其它页面显示"版本清单尚未载入"。
    invoke('version.manifest.list').catch(() => null),
  ]);

  setState({
    app: app ?? null,
    settings: settings ? { ...state.settings, ...settings } : state.settings,
    accounts: accounts?.profiles ?? [],
    activeUuid: accounts?.activeUuid ?? null,
    instances: instances?.instances ?? [],
    activeInstanceId: instances?.activeId ?? instances?.instances?.[0]?.id ?? null,
    versions: versions?.versions ?? [],
    versionsLoadedAt: versions?.cachedAt ?? null,
    versionsStale: versions?.stale ?? true,
  });

  // 内核设置灌回后必须重设语言 —— initLocale 只在模块加载时跑过一次，
  // 那时 loadAll 还没执行，current 永远是默认 'zh-CN'，
  // 这就是"切换语言永远变回中文"的根因。
  initLocale(state.settings.language);
}

export async function patchSettings(patch) {
  setState({ settings: { ...state.settings, ...patch } });
  try {
    const saved = await invoke('settings.set', patch);
    if (saved && typeof saved === 'object') setState({ settings: { ...state.settings, ...saved } });
  } catch (e) {
    console.error('[store] 设置保存失败', e);
  }
}

export function getActiveAccount() {
  return state.accounts.find((a) => a.uuid === state.activeUuid) ?? null;
}

export function getActiveInstance() {
  return state.instances.find((i) => i.id === state.activeInstanceId) ?? null;
}

export function getInstance(id) {
  return state.instances.find((i) => i.id === id) ?? null;
}

/* ===========================================================================
   任务中心
   安装、下载、校验、启动准备都注册成任务，状态条与相关页面订阅它。
   ========================================================================== */

export function upsertTask(task) {
  const list = state.tasks.slice();
  const i = list.findIndex((t) => t.id === task.id);
  if (i >= 0) list[i] = { ...list[i], ...task };
  else list.push(task);
  setState({ tasks: list });
}

export function removeTask(id) {
  setState({ tasks: state.tasks.filter((t) => t.id !== id) });
}

export function getTask(id) {
  return state.tasks.find((t) => t.id === id) ?? null;
}

// 语言初始化（i18n 的 t() 与文档方向）
initLocale(state.settings.language);
