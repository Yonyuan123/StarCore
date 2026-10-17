/**
 * logs.js —— 「日志」分区。
 *
 * 页面组成：
 *   logs        启动日志：查看、搜索、按级别过滤、导出、清空
 *   logs:crash  崩溃分析：定位原因、查看原始堆栈、给出建议操作
 *
 * 日志文件列表来自 invoke('log.list')；正文来自 invoke('log.read')；
 * 游戏运行期间内核会推送 log.line，页面据此实时追加。
 */

import { invoke, on } from '../scripts/bridge.js';
import { getState, subscribe } from '../scripts/store.js';
import {
  h, fill, icon, iconButton, button, emptyState, chip,
  toast, confirm, switchEl, fmtBytes, fmtDateTime,
  selectControl as customSelect,
} from '../scripts/ui.js';
import { go } from '../scripts/router.js';

const SECTION = 'logs';

/** 只渲染最后 N 行，避免超长日志把内存吃满。 */
const MAX_LINES = 5000;

const RE_ERROR = /ERROR|Exception|Caused by/;
const RE_WARN = /WARN/;
const RE_DEBUG = /DEBUG|TRACE/;
const RE_TIME = /^(\[\d{2}:\d{2}:\d{2}\])([\s\S]*)$/;

/** 二级面板里点中的日志文件（跨页面共享）。 */
let pickedFile = null;

function dirOf(p) {
  if (!p) return null;
  // 同时兼容 \ 与 / 两种分隔符
  const idx = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'));
  return idx > 0 ? p.slice(0, idx) : null;
}

function levelFilterMatch(line, level) {
  if (level === 'error') return RE_ERROR.test(line);
  if (level === 'warn') return RE_WARN.test(line);
  return true;
}

function lineClass(text) {
  if (RE_ERROR.test(text)) return 'log-line--error';
  if (RE_WARN.test(text)) return 'log-line--warn';
  if (RE_DEBUG.test(text)) return 'log-line--debug';
  return '';
}

/** 把一段文本按关键词高亮后追加到父节点。 */
function appendHighlighted(parent, text, q) {
  const ql = q.toLowerCase();
  const lower = text.toLowerCase();
  let i = 0;
  for (;;) {
    const idx = lower.indexOf(ql, i);
    if (idx < 0) { parent.appendChild(document.createTextNode(text.slice(i))); break; }
    if (idx > i) parent.appendChild(document.createTextNode(text.slice(i, idx)));
    parent.appendChild(h('mark', {
      style: { background: 'var(--accent-soft)', color: 'var(--accent-text)', borderRadius: 'var(--r-xs)', padding: '0 1px' },
      text: text.slice(idx, idx + q.length),
    }));
    i = idx + q.length;
  }
}

/** 渲染一行日志：按内容判级别，并把行首时间戳单独着色。 */
function renderLine(text, q) {
  const cls = lineClass(text);
  const div = h('div', { class: cls || null });
  const m = RE_TIME.exec(text);
  let rest = text;
  if (m) {
    div.appendChild(h('span', { class: 'log-line--time', text: m[1] }));
    rest = m[2];
  }
  if (q) appendHighlighted(div, rest, q);
  else div.appendChild(document.createTextNode(rest));
  return div;
}

/* ===========================================================================
   页面：启动日志
   ========================================================================== */

function mountLogs(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let lines = [];
  let files = [];
  let truncated = false;
  let fileName = null;
  let query = '';
  let level = 'all';
  let autoScroll = true;
  let shownCount = 0;
  let inited = false;

  const searchInput = h('input', { class: 'input', placeholder: '在日志中搜索…', 'aria-label': '搜索日志' });
  const view = h('div', { class: 'log-view' });
  const viewHead = h('div', { class: 'log-view__head' });
  const bodyEl = h('div', { class: 'log-view__body' });
  fill(view, viewHead, bodyEl);

  const mountPoint = h('div');

  const filterBtn = (label, id) => h('button', {
    class: 'filter-btn', type: 'button', 'data-active': level === id ? 'true' : 'false',
    onClick: (e) => {
      level = id;
      for (const b of e.currentTarget.parentElement.querySelectorAll('.filter-btn')) {
        b.setAttribute('data-active', b === e.currentTarget ? 'true' : 'false');
      }
      paintBody();
    },
  }, h('span', { text: label }));

  searchInput.addEventListener('input', () => { query = searchInput.value; paintBody(); });

  const toolbar = h('div', { class: 'toolbar' },
    h('div', { class: 'search' }, icon('search', 'sm'), searchInput),
    filterBtn('全部', 'all'),
    filterBtn('仅错误', 'error'),
    filterBtn('仅警告', 'warn'),
    h('div', { class: 'toolbar__divider' }),
    h('div', { class: 'row', style: { gap: 'var(--sp-4)' } },
      switchEl(true, (v) => { autoScroll = v; if (v) bodyEl.scrollTop = bodyEl.scrollHeight; }, { label: '自动滚到底部' }),
      h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '自动滚到底部' }),
    ),
  );

  function paintHead() {
    const entry = files.find((f) => f.path === pickedFile) ?? files[0] ?? null;
    const size = entry?.sizeBytes;
    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '启动日志' }),
        h('div', {
          class: 'page__subtitle',
          text: fileName
            ? `${fileName}${Number.isFinite(size) ? ` · ${fmtBytes(size)}` : ''}`
            : '还没有可显示的日志文件',
        }),
      ),
      h('div', { class: 'page__actions' },
        button('刷新', { variant: 'ghost', icon: 'refresh-cw', onClick: refresh }),
        button('打开日志目录', { variant: 'ghost', icon: 'folder-open', onClick: openDir }),
        button('导出日志', { variant: 'ghost', icon: 'download', onClick: exportLog }),
        button('清空', { variant: 'danger-ghost', icon: 'trash-2', onClick: clearLog }),
      ),
    );
  }

  function paintBody() {
    const q = query.trim();
    const ql = q.toLowerCase();
    const shown = lines.filter((l) => levelFilterMatch(l, level) && (!ql || l.toLowerCase().includes(ql)));
    shownCount = shown.length;

    if (!lines.length) {
      fill(viewHead);
      fill(bodyEl);
      mountPoint.replaceChildren(emptyState({
        icon: 'terminal',
        title: '还没有启动日志',
        desc: '第一次启动游戏后，这里会记录完整的过程与错误信息。',
        actions: [button('去启动游戏', { variant: 'primary', icon: 'play', onClick: () => go('instances') })],
      }));
      return;
    }

    mountPoint.replaceChildren(view);

    const entry = files.find((f) => f.path === pickedFile) ?? files[0] ?? null;
    fill(viewHead,
      h('span', { text: `共 ${lines.length} 行` }),
      h('span', { text: `匹配 ${shownCount} 行` }),
      h('span', { class: 'grow' }),
      truncated ? chip('仅显示最后 5000 行', { variant: 'warning' }) : null,
      h('span', { text: entry?.modifiedAt ? `最后写入 ${fmtDateTime(entry.modifiedAt)}` : '' }),
    );

    if (!shown.length) {
      fill(bodyEl, h('div', { class: 'dim', text: '没有匹配的日志行。' }));
      return;
    }
    fill(bodyEl, ...shown.map((l) => renderLine(l, q)));
    if (autoScroll) bodyEl.scrollTop = bodyEl.scrollHeight;
  }

  async function loadFiles() {
    const r = await invoke('log.list').catch(() => null);
    files = r?.files ?? [];
  }

  async function refresh() {
    await loadFiles();
    try {
      const r = await invoke('log.read', { file: pickedFile ?? undefined, maxLines: MAX_LINES });
      lines = r?.lines ?? [];
      truncated = !!r?.truncated;
      fileName = r?.file ?? (files.find((f) => f.path === pickedFile)?.name ?? files[0]?.name ?? null);
    } catch (e) {
      toast({ title: '读取日志失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
    paintHead();
    paintBody();
  }

  function openDir() {
    const entry = files.find((f) => f.path === pickedFile) ?? files[0] ?? null;
    const dir = dirOf(entry?.path) ?? (getState().app?.dataDir ? `${getState().app.dataDir}/logs` : null);
    if (!dir) { toast({ title: '暂无法定位日志目录', kind: 'warning' }); return; }
    invoke('system.openPath', { path: dir }).catch((e) => toast({ title: '打开失败', desc: String(e?.message ?? e), kind: 'danger' }));
  }

  async function exportLog() {
    try {
      const r = await invoke('system.pickFolder', { title: '选择导出目录' });
      if (!r?.path) return;
      await invoke('log.export', { file: pickedFile ?? undefined, dir: r.path });
      toast({ title: '日志已导出', desc: r.path, kind: 'success', timeout: 6000 });
    } catch (e) {
      toast({ title: '导出失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function clearLog() {
    const ok = await confirm({
      title: '清空日志',
      message: '将删除当前日志文件，此操作不可撤销。历史崩溃报告不受影响。',
      confirmLabel: '清空',
      danger: true,
    });
    if (!ok) return;
    try {
      await invoke('log.delete', { file: pickedFile ?? undefined });
      toast({ title: '日志已清空', kind: 'success' });
      pickedFile = null;
      await refresh();
    } catch (e) {
      toast({ title: '清空失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  /** 内核推送的实时日志行。 */
  function onLiveLine(p) {
    if (typeof p?.line !== 'string') return;
    if (p.level === 'crash') return;
    lines.push(p.line);
    if (lines.length > MAX_LINES) lines.shift();
    if (levelFilterMatch(p.line, level)) {
      const q = query.trim();
      if (!q || p.line.toLowerCase().includes(q.toLowerCase())) {
        shownCount += 1;
        if (view.isConnected) {
          bodyEl.appendChild(renderLine(p.line, q));
          if (autoScroll) bodyEl.scrollTop = bodyEl.scrollHeight;
        }
      }
    }
    if (view.isConnected) {
      fill(viewHead,
        h('span', { text: `共 ${lines.length} 行` }),
        h('span', { text: `匹配 ${shownCount} 行` }),
        h('span', { class: 'grow' }),
        h('span', { text: '实时接收中' }),
      );
    }
  }

  fill(scroll, h('div', { class: 'page__inner' }, toolbar, mountPoint));

  paintHead();
  paintBody();
  refresh();

  const offLive = on('log.line', onLiveLine);
  // 面板里换了文件后再次进入本页：重新读取。router 在同 key 时不会回调 activate，
  // 所以这里监听 route（每次 go 都会触发）。
  const offRoute = subscribe('route', () => {
    const r = getState().route;
    if (!inited || r.section !== SECTION || (r.sub ?? null) !== null) return;
    refresh();
  });
  inited = true;
  return { destroy: () => { offLive(); offRoute(); } };
}

/* ===========================================================================
   页面：崩溃分析
   ========================================================================== */

const SUGGESTION_ACTIONS = {
  openMods: { label: '打开模组文件夹', icon: 'folder-open', run: () => openContent('mods') },
  openModsFolder: { label: '打开模组文件夹', icon: 'folder-open', run: () => openContent('mods') },
  openResourcepacks: { label: '打开资源包文件夹', icon: 'folder-open', run: () => openContent('resourcepacks') },
  openShaders: { label: '打开光影包文件夹', icon: 'folder-open', run: () => openContent('shaders') },
  openInstanceFolder: { label: '打开实例文件夹', icon: 'folder-open', run: openInstance },
  openLogFolder: { label: '打开日志目录', icon: 'folder-open', run: openLogDir },
  checkJava: { label: '检查 Java 版本', icon: 'coffee', run: () => go(SECTION, 'java') },
  updateJava: { label: '配置 Java 版本', icon: 'coffee', run: () => go(SECTION, 'java') },
  manageMods: { label: '管理模组', icon: 'puzzle', run: () => openContent('mods') },
};

function openContent(kind) {
  const id = getState().activeInstanceId;
  if (id) invoke('system.openContentFolder', { instanceId: id, kind }).catch(() => {});
  else toast({ title: '请先选择一个实例', kind: 'warning' });
}

function openInstance() {
  const id = getState().activeInstanceId;
  if (id) invoke('system.openInstanceFolder', { id }).catch(() => {});
  else toast({ title: '请先选择一个实例', kind: 'warning' });
}

function openLogDir() {
  const dataDir = getState().app?.dataDir;
  if (!dataDir) { toast({ title: '暂无法定位日志目录', kind: 'warning' }); return; }
  invoke('system.openPath', { path: `${dataDir}/logs` }).catch(() => {});
}

function mountCrash(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let crashFiles = [];
  let selected = null;
  let analysis = null;
  let analyzing = false;
  let exporting = false;
  let inited = false;

  const sel = customSelect({
    ariaLabel: '选择崩溃报告',
    placeholder: '选择崩溃报告',
    width: '380px',
    onChange: (v) => { selected = crashFiles.find((f) => (f.path ?? f.name) === v) ?? null; analysis = null; paint(); },
  });

  const toolbar = h('div', { class: 'toolbar' },
    sel,
    button('分析', { variant: 'primary', icon: 'search', onClick: () => analyze(selected) }),
    button('上传到 mclo.gs', { variant: 'ghost', icon: 'upload', onClick: upload }),
    // 导出诊断报告：把环境、Java、内存、模组清单、崩溃分析打包成一个 zip。
    // 求助时只需要发这一个文件，不用来回问"你什么系统、什么 Java"。
    button('导出诊断报告', { variant: 'secondary', icon: 'file-archive', onClick: exportReport }),
  );

  const mountPoint = h('div');

  function paint() {
    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '崩溃分析' }),
        h('div', { class: 'page__subtitle', text: crashFiles.length ? `发现 ${crashFiles.length} 份崩溃报告` : '没有检测到异常退出的记录' }),
      ),
    );

    // 下拉选项：交给组件自己维护，这里只提供数据
    const keep = sel.value;
    sel.setOptions(
      crashFiles.length
        ? crashFiles.map((f) => ({ value: f.path ?? f.name, label: f.name ?? (f.path ?? '') }))
        : [{ value: '', label: '没有崩溃报告', disabled: true }],
      keep && crashFiles.some((f) => (f.path ?? f.name) === keep)
        ? keep
        : (selected ? (selected.path ?? selected.name) : ''),
    );

    const hasReports = crashFiles.length > 0;
    toolbar.style.display = hasReports ? '' : 'none';

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '960px' } }, toolbar, mountPoint));

    if (!hasReports) {
      mountPoint.replaceChildren(emptyState({
        icon: 'circle-check',
        title: '没有崩溃记录',
        desc: '这是好消息 —— 目前没有检测到异常退出的记录。',
      }));
      return;
    }

    if (analyzing) {
      mountPoint.replaceChildren(h('div', { class: 'col', style: { gap: 'var(--sp-6)' } },
        h('div', { class: 'skeleton', style: { height: '140px', borderRadius: 'var(--r-lg)' } }),
        h('div', { class: 'skeleton', style: { height: '200px', borderRadius: 'var(--r-lg)' } }),
      ));
      return;
    }

    if (!analysis) {
      mountPoint.replaceChildren(emptyState({
        icon: 'alert-triangle',
        title: '选择一份崩溃报告开始分析',
        desc: '分析会定位崩溃原因，并给出可执行的修复建议。',
        actions: [button('分析当前报告', { variant: 'primary', icon: 'search', onClick: () => analyze(selected) })],
      }));
      return;
    }

    mountPoint.replaceChildren(...crashBody(analysis));
  }

  function crashBody(res) {
    const reasons = Array.isArray(res.reasons) ? res.reasons : [];
    const suggestions = Array.isArray(res.suggestions) ? res.suggestions : [];
    const stack = res.stackTrace ?? res.stack ?? '';
    const summary = res.summary ?? [res.exceptionType ?? res.exception, res.message].filter(Boolean).join(': ');

    const nodes = [];

    const card = h('div', { class: 'crash-card' },
      h('div', { class: 'crash-card__title' }, icon('alert-triangle', 'md'), h('span', { text: '检测到崩溃' })),
      summary ? h('div', { style: { fontSize: 'var(--fs-sm)', color: 'var(--text-2)', lineHeight: 'var(--lh-snug)' }, text: summary }) : null,
    );
    if (reasons.length) {
      for (let i = 0; i < reasons.length; i++) {
        const r = reasons[i];
        card.appendChild(h('div', { class: 'crash-reason' },
          h('div', { class: 'crash-reason__n', text: String(i + 1) }),
          h('div', { class: 'grow' },
            h('div', { style: { fontWeight: 'var(--fw-medium)', color: r.severity === 'high' ? 'var(--danger)' : 'var(--text)' }, text: r.title ?? '未知原因' }),
            r.detail ? h('div', { class: 'set-row__desc', text: r.detail }) : null,
          ),
        ));
      }
    } else {
      card.appendChild(h('div', { class: 'crash-reason' }, h('div', { class: 'dim', text: '未能定位到明确原因，请查看原始堆栈。' })));
    }
    nodes.push(card);

    // 原始堆栈
    if (stack) {
      const stackBody = h('div', { class: 'log-view__body', text: stack });
      nodes.push(h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('file-text', 'md'), h('span', { text: '原始堆栈' })),
        h('div', { class: 'log-view', style: { maxHeight: '46vh' } },
          h('div', { class: 'log-view__head' },
            h('span', { text: '可选中复制' }),
            h('span', { class: 'grow' }),
            iconButton('copy', {
              size: 'sm', tip: '复制堆栈',
              onClick: () => {
                navigator.clipboard?.writeText(stack).then(
                  () => toast({ title: '已复制到剪贴板', kind: 'success' }),
                  () => toast({ title: '复制失败', kind: 'danger' }),
                );
              },
            }),
          ),
          stackBody,
        ),
      ));
    }

    // 建议操作
    if (suggestions.length) {
      nodes.push(h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('wrench', 'md'), h('span', { text: '建议操作' })),
        h('div', { class: 'rows' }, ...suggestions.map(suggestionRow)),
      ));
    }

    return nodes;
  }

  function suggestionRow(s) {
    const key = s.action ?? '';
    const known = SUGGESTION_ACTIONS[key];
    const label = known?.label ?? s.actionLabel ?? (key && !/[\\/:]/.test(key) ? key : '处理');
    const ic = known?.icon ?? 'wrench';
    const run = () => {
      if (known) { known.run(); return; }
      if (/^https?:/i.test(key)) { invoke('system.openUrl', { url: key }).catch(() => {}); return; }
      if (/[\\/]/.test(key)) { invoke('system.openPath', { path: key }).catch(() => {}); return; }
      toast({ title: s.title ?? '请按建议手动处理', desc: s.detail ?? '', kind: 'accent', timeout: 7000 });
    };

    return h('div', { class: 'row-item' },
      h('div', { class: 'row-item__icon' }, icon(ic, 'sm')),
      h('div', { style: { minWidth: '0' } },
        h('div', { class: 'row-item__name', text: s.title ?? '建议' }),
        s.detail ? h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px' }, text: s.detail }) : null,
      ),
      h('div', { class: 'row-item__trail' }),
      h('div', { class: 'row-item__trail' },
        button(label, { variant: 'secondary', size: 'sm', icon: ic, onClick: run }),
      ),
    );
  }

  async function loadFiles() {
    const r = await invoke('log.list').catch(() => null);
    crashFiles = (r?.files ?? []).filter((f) => f.kind === 'crash');
    if (!selected) selected = crashFiles[0] ?? null;
  }

  async function analyze(file) {
    const target = file ?? selected ?? crashFiles[0];
    if (!target) { toast({ title: '请先选择一份崩溃报告', kind: 'warning' }); return; }
    selected = target;
    analyzing = true;
    analysis = null;
    paint();
    try {
      const res = await invoke('log.analyze', { file: target.path ?? target.name });
      analysis = res ?? null;
      if (!analysis) toast({ title: '分析未返回结果', kind: 'warning' });
    } catch (e) {
      toast({ title: '分析失败', desc: e?.message ?? String(e), kind: 'danger' });
    } finally {
      analyzing = false;
      paint();
    }
  }

  async function exportReport() {
    exporting = true;
    paint();
    try {
      const r = await invoke('log.export', { instanceId: getState().activeInstanceId ?? null });
      if (!r?.ok && r?.message && !r?.path) {
        toast({ title: '导出失败', desc: r.message, kind: 'danger' });
        return;
      }
      toast({
        title: '诊断报告已导出',
        desc: r?.path ?? r?.message ?? '已放到桌面',
        kind: 'success',
        timeout: 12000,
        action: r?.path ? { label: '打开所在文件夹', onClick: () => invoke('system.revealPath', { path: r.path }).catch(() => {}) } : undefined,
      });
    } catch (e) {
      toast({ title: '导出失败', desc: e?.message ?? String(e), kind: 'danger' });
    } finally {
      exporting = false;
      paint();
    }
  }

  async function upload() {
    const target = selected ?? crashFiles[0];
    if (!target) { toast({ title: '请先选择一份崩溃报告', kind: 'warning' }); return; }
    try {
      const r = await invoke('log.upload', { file: target.path ?? target.name });
      if (r?.url) {
        toast({
          title: '已上传到 mclo.gs',
          desc: r.url,
          kind: 'success',
          timeout: 12000,
          action: { label: '复制链接', onClick: () => navigator.clipboard?.writeText(r.url) },
        });
      } else {
        toast({ title: '上传完成', kind: 'success' });
      }
    } catch (e) {
      toast({ title: '上传失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  (async () => {
    await loadFiles();
    paint();
    if (selected) analyze(selected);
    inited = true;
  })();

  const offRoute = subscribe('route', () => {
    const r = getState().route;
    if (!inited || r.section !== SECTION || r.sub !== 'crash') return;
    (async () => {
      await loadFiles();
      paint();
      if (!analysis && selected) analyze(selected);
    })();
  });

  return { destroy: offRoute };
}

/* ===========================================================================
   二级面板
   ========================================================================== */

function renderPanel(el, hh, ctx) {
  const goFn = ctx?.go ?? go;
  const head = h('div', { class: 'panel__head' });
  const body = h('div', { class: 'panel__body' });
  fill(el, head, body);
  fill(head, h('div', { class: 'panel__title' }, icon('terminal', 'md'), h('span', { text: '日志' })));

  let files = [];

  const navItem = (label, iconName, count, selected, onClick) => h('div', {
    class: 'nav-item', role: 'button', tabindex: '0',
    'aria-selected': selected ? 'true' : 'false',
    title: label,
    onClick,
    onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } },
  },
    icon(iconName, 'sm'),
    h('span', { class: 'grow truncate', text: label }),
    count ? h('span', { class: 'nav-item__count', text: count }) : null,
  );

  function paint() {
    const sub = getState().route.sub ?? null;
    fill(body,
      h('div', { class: 'panel__group', text: '视图' }),
      navItem('启动日志', 'terminal', null, sub === null && !pickedFile, () => { pickedFile = null; goFn(SECTION); }),
      navItem('崩溃分析', 'alert-triangle', null, sub === 'crash', () => goFn(SECTION, 'crash')),
      h('div', { class: 'panel__group', text: '日志文件' }),
      files.length
        ? files.map((f) => {
            const isCrash = f.kind === 'crash';
            const v = f.path ?? f.name;
            const selected = !isCrash && sub === null && pickedFile === v;
            return navItem(
              f.name ?? v,
              isCrash ? 'alert-triangle' : 'terminal',
              Number.isFinite(f.sizeBytes) ? fmtBytes(f.sizeBytes) : null,
              selected,
              () => {
                if (isCrash) { goFn(SECTION, 'crash'); return; }
                pickedFile = f.path ?? f.name ?? null;
                goFn(SECTION);
              },
            );
          })
        : h('div', { class: 'dim', style: { padding: '4px 18px', fontSize: 'var(--fs-xs)' }, text: '暂无日志文件' }),
    );
  }

  paint();
  (async () => {
    const r = await invoke('log.list').catch(() => null);
    files = r?.files ?? [];
    paint();
  })();

  const off = subscribe('route', paint);
  return () => off();
}

/* ===========================================================================
   模块定义
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [SECTION]: { title: '启动日志', icon: 'terminal', mount: mountLogs },
    [`${SECTION}:crash`]: { title: '崩溃分析', icon: 'alert-triangle', mount: mountCrash },
  },
};
