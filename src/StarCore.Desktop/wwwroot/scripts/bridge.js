/**
 * bridge.js —— 前端与 C# 内核之间的唯一通道。
 *
 * 协议（全部为 JSON 字符串，双向）：
 *   JS -> C#   { kind: "invoke", id, method, params }
 *   C# -> JS   { kind: "result", id, ok, data | error }
 *   C# -> JS   { kind: "event",  name, payload }          // 主动推送，如安装进度
 *
 * 设计约束：
 *   1. 所有 invoke 都是 Promise，宿主异常会以 HostError 形式 reject，
 *      调用方可以据此给出准确的错误态，而不是静默失败。
 *   2. 事件订阅返回退订函数，页面卸载时必须调用，避免内存泄漏。
 *   3. 脱离 WebView2 环境打开（例如用浏览器调试界面）时自动退化为
 *      内置样例数据，界面结构照样完整可见 —— 方便纯视觉迭代。
 */

const webview = globalThis.chrome?.webview;
export const hasHost = typeof webview?.postMessage === 'function';

export class HostError extends Error {
  constructor(err) {
    const msg = typeof err === 'string' ? err : (err?.message ?? '宿主调用失败');
    super(msg);
    this.name = 'HostError';
    this.code = err?.code ?? 'UNKNOWN';
    this.detail = err?.detail ?? '';
  }
}

let _seq = 0;
const _pending = new Map();
const _listeners = new Map();

function _emit(name, payload) {
  const direct = _listeners.get(name);
  if (direct) for (const fn of [...direct]) { try { fn(payload); } catch (e) { console.error('[bridge] 事件处理异常', name, e); } }
  const wild = _listeners.get('*');
  if (wild) for (const fn of [...wild]) { try { fn({ name, payload }); } catch (e) { console.error('[bridge] 通配监听异常', e); } }
}

function _receive(raw) {
  let msg = raw;
  if (typeof raw === 'string') {
    try { msg = JSON.parse(raw); } catch { return; }
  }
  if (!msg || typeof msg !== 'object') return;

  if (msg.kind === 'result') {
    const slot = _pending.get(msg.id);
    if (!slot) return;
    _pending.delete(msg.id);
    msg.ok ? slot.resolve(msg.data) : slot.reject(new HostError(msg.error));
    return;
  }
  if (msg.kind === 'event') _emit(msg.name, msg.payload);
}

if (hasHost) {
  webview.addEventListener('message', (e) => _receive(e.data));
} else {
  console.warn('[bridge] 未检测到 WebView2 宿主，已切换到界面预览模式（数据为样例）。');
}

/** 调用内核方法。 */
export async function invoke(method, params = {}) {
  if (!hasHost) return mockInvoke(method, params);

  const id = String(++_seq);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      if (_pending.has(id)) {
        _pending.delete(id);
        reject(new HostError(`调用超时：${method}`));
      }
    }, 120_000);

    _pending.set(id, {
      resolve: (v) => { clearTimeout(timer); resolve(v); },
      reject: (e) => { clearTimeout(timer); reject(e); },
    });

    try {
      webview.postMessage(JSON.stringify({ kind: 'invoke', id, method, params }));
    } catch (e) {
      clearTimeout(timer);
      _pending.delete(id);
      reject(new HostError(e));
    }
  });
}

/** 订阅内核推送事件；返回退订函数。 */
export function on(name, fn) {
  if (!_listeners.has(name)) _listeners.set(name, new Set());
  _listeners.get(name).add(fn);
  return () => _listeners.get(name)?.delete(fn);
}

/* ============================================================================
   预览模式样例数据
   只在没有宿主时生效。目的不是模拟业务，而是保证每个页面的
   "有数据 / 空数据" 两种形态都能在浏览器里被检查到。
   ========================================================================== */

const MOCK = {
  'app.info': {
    name: 'StarCore', version: '1.0.0', coreVersion: '1.0.0',
    osName: 'Windows 11', arch: 'x64', locale: 'zh-CN',
    dataDir: 'C:\\Users\\<用户>\\AppData\\Roaming\\StarCore',
    totalMemoryMb: 32768,
    preview: true,
  },
  'settings.get': {
    theme: 'light', accent: 'terracotta', background: 'dusk',uiMotion: 'subtle', uiPerf: 'high',
    language: 'zh-CN', closeToTray: false, autoUpdate: true,
    downloadSource: 'official', maxThreads: 32, proxy: '',
  },
  'account.list': { profiles: [], activeUuid: null },
  'instance.list': { instances: [] },
  'version.manifest.list': { versions: [], cachedAt: null, stale: true },
  'java.list': { javas: [] },
  'log.list': { files: [] },
};

async function mockInvoke(method, params) {
  await new Promise((r) => setTimeout(r, 60));
  if (method in MOCK) return structuredClone(MOCK[method]);
  if (method.startsWith('modrinth.')) {
    return { hits: [], total: 0, offset: 0, limit: 20, preview: true };
  }
  if (method.startsWith('content.')) return { items: [] };
  console.info('[bridge/mock] 未定义的方法:', method, params);
  return null;
}
