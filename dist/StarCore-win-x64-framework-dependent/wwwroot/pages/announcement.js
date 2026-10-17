/**
 * announcement.js —— 「公告」分区。
 *
 * 启动器自己的消息通道：
 *   announcement          更新状态卡 + 公告列表
 *   announcement:history  版本历史（只取 type === 'release' 的公告）
 *
 * 公告正文只做极简的行级 Markdown 处理（标题 / 列表 / 段落），不引入任何解析库。
 */

import { invoke } from '../scripts/bridge.js';
import { getState, subscribe } from '../scripts/store.js';
import {
  h, fill, icon, button, emptyState, chip, toast, fmtDate, skeleton,
} from '../scripts/ui.js';
import { go } from '../scripts/router.js';

const SECTION = 'announcement';

/** 每个页面挂载后把自己「重新拉数据」的函数登记在这里，供 router 的 activate 调用。 */
const activators = new WeakMap();

const errText = (e) => e?.message ?? String(e ?? '未知错误');

/* ===========================================================================
   公共小工具
   ========================================================================== */

async function openUrl(url) {
  if (!url) { toast({ title: '没有可用的链接', kind: 'warning' }); return; }
  try {
    await invoke('system.openUrl', { url });
  } catch (e) {
    toast({ title: '打开链接失败', desc: errText(e), kind: 'danger' });
  }
}

async function checkUpdate() {
  try {
    const r = await invoke('app.checkUpdate');
    if (r?.hasUpdate) {
      toast({ title: '发现新版本', desc: r.latestVersion ?? '', kind: 'accent', timeout: 8000 });
    } else {
      toast({ title: '已是最新版本', kind: 'success' });
    }
    return r;
  } catch (e) {
    toast({ title: '检查更新失败', desc: errText(e), kind: 'danger' });
    return null;
  }
}

function typeChip(type) {
  switch (type) {
    case 'release': return chip('版本发布', { variant: 'accent' });
    case 'security': return chip('安全', { variant: 'danger' });
    default: return chip('公告');
  }
}

/** 段落内的行级 Markdown：#/##/### 标题、- 或 * 列表，其余按段落处理。 */
function renderMarkdown(text) {
  const out = [];
  const lines = String(text ?? '').split(/\r?\n/);

  for (const raw of lines) {
    const line = raw.trim();
    if (!line) continue;

    const heading = /^(#{1,3})\s+(.*)$/.exec(line);
    if (heading) {
      const level = heading[1].length;
      out.push(h('div', {
        style: {
          fontSize: level === 1 ? 'var(--fs-body)' : 'var(--fs-sm)',
          fontWeight: 'var(--fw-semibold)',
          color: 'var(--text)',
        },
        text: heading[2],
      }));
      continue;
    }

    const bullet = /^[-*]\s+(.*)$/.exec(line);
    if (bullet) {
      out.push(h('div', { class: 'row', style: { gap: 'var(--sp-4)', alignItems: 'flex-start' } },
        h('span', { class: 'dot', style: { marginTop: '9px', flex: '0 0 auto' } }),
        h('span', { class: 'grow', text: bullet[1] }),
      ));
      continue;
    }

    out.push(h('div', { text: line }));
  }

  return out;
}

/** 版本历史里的一句话摘要：取正文第一行，去掉 Markdown 前缀。 */
function summaryOf(item) {
  const first = String(item?.body ?? '').split(/\r?\n/).map((l) => l.trim()).find(Boolean) ?? '';
  const plain = first.replace(/^#{1,3}\s+/, '').replace(/^[-*]\s+/, '');
  if (!plain) return '暂无说明';
  return plain.length > 90 ? `${plain.slice(0, 90)}…` : plain;
}

/* ===========================================================================
   二级面板
   ========================================================================== */

function renderPanel(el) {
  const head = h('div', { class: 'panel__head' });
  const body = h('div', { class: 'panel__body' });
  const foot = h('div', { class: 'panel__foot' });
  fill(el, head, body, foot);

  const navItem = (label, { icon: ic, selected = false, onClick }) => h('div', {
    class: 'nav-item',
    role: 'button',
    tabindex: '0',
    'aria-selected': selected ? 'true' : 'false',
    onClick,
    onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick?.(); } },
  },
    icon(ic, 'sm'),
    h('span', { class: 'grow truncate', text: label }),
  );

  const paintHead = () => {
    fill(head, h('div', { class: 'panel__title' }, icon('megaphone', 'md'), h('span', { text: '公告' })));
  };

  const paintBody = () => {
    const route = getState().route;
    const onHistory = route.section === SECTION && route.sub === 'history';
    fill(body,
      h('div', { class: 'panel__group', text: '消息' }),
      navItem('全部公告', { icon: 'megaphone', selected: !onHistory, onClick: () => go(SECTION) }),
      navItem('版本历史', { icon: 'history', selected: onHistory, onClick: () => go(SECTION, 'history') }),
    );
  };

  const paintFoot = () => {
    fill(foot, button('检查更新', { variant: 'ghost', block: true, icon: 'refresh-cw', onClick: () => checkUpdate() }));
  };

  paintHead();
  paintBody();
  paintFoot();

  const off = subscribe('route', paintBody);
  return () => off();
}

/* ===========================================================================
   页面：公告
   ========================================================================== */

function mountAnnouncement(el, ctx) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let destroyed = false;
  let updateState = 'loading'; // loading | ready | error
  let updateInfo = null;
  let newsState = 'loading';   // loading | ready | error
  let items = [];

  async function load({ silent = false } = {}) {
    updateState = 'loading';
    newsState = 'loading';
    paint();

    const [u, n] = await Promise.all([
      invoke('app.checkUpdate').then((r) => ({ ok: true, data: r })).catch((e) => ({ ok: false, error: e })),
      invoke('app.announcements').then((r) => ({ ok: true, data: r })).catch((e) => ({ ok: false, error: e })),
    ]);
    if (destroyed) return;

    if (u.ok) {
      updateInfo = u.data ?? {};
      updateState = 'ready';
    } else {
      updateInfo = null;
      updateState = 'error';
      if (!silent) toast({ title: '检查更新失败', desc: errText(u.error), kind: 'danger' });
    }

    if (n.ok) {
      items = n.data?.items ?? [];
      newsState = 'ready';
    } else {
      items = [];
      newsState = 'error';
      if (!silent) toast({ title: '读取公告失败', desc: errText(n.error), kind: 'danger' });
    }

    paint();
  }

  function updateActions() {
    if (updateState === 'loading') {
      return [skeleton(null, { width: '148px', height: '30px', borderRadius: 'var(--r-pill)' })];
    }
    if (updateState === 'error') {
      return [h('div', { class: 'dim', style: { fontSize: 'var(--fs-sm)' }, text: '暂时无法检查更新' })];
    }
    if (updateInfo?.hasUpdate) {
      return [
        chip('有新版本', { variant: 'accent', icon: 'sparkles' }),
        button('前往下载', { variant: 'primary', icon: 'download', onClick: () => openUrl(updateInfo.downloadUrl) }),
      ];
    }
    return [chip('已是最新', { variant: 'success', icon: 'circle-check' })];
  }

  function updateCard() {
    const version = getState().app?.version ?? '—';
    const latest = updateState === 'ready' ? updateInfo?.latestVersion : null;
    const publishedAt = updateState === 'ready' ? updateInfo?.publishedAt : null;

    return h('div', {
      class: 'card card--pad-lg row-between',
      style: { gap: 'var(--sp-7)', marginBottom: 'var(--sp-9)', flexWrap: 'wrap' },
    },
      h('div', {},
        h('div', { style: { fontSize: 'var(--fs-xs)', color: 'var(--text-3)' }, text: '启动器版本' }),
        h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-3)', alignItems: 'baseline', flexWrap: 'wrap' } },
          h('span', { class: 'version-tag mono', style: { fontSize: 'var(--fs-h3)' }, text: version }),
          latest ? h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: `最新 ${latest}` }) : null,
          publishedAt ? h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: `发布于 ${fmtDate(publishedAt)}` }) : null,
        ),
      ),
      h('div', { class: 'row', style: { gap: 'var(--sp-5)', alignItems: 'center', flexWrap: 'wrap' } }, ...updateActions()),
    );
  }

  function newsItem(it) {
    const meta = [fmtDate(it?.publishedAt), it?.author].filter(Boolean).join(' · ') || '—';
    return h('div', { class: 'news-item', role: 'article' },
      h('div', { class: 'news-item__tag' }, typeChip(it?.type)),
      h('div', { class: 'grow', style: { minWidth: '0' } },
        h('div', { class: 'news-item__title', text: it?.title ?? '未命名公告' }),
        h('div', { class: 'news-item__meta', text: meta }),
        it?.body
          ? h('div', {
              class: 'news-item__body',
              style: { display: 'flex', flexDirection: 'column', gap: 'var(--sp-3)' },
            }, ...renderMarkdown(it.body))
          : null,
        it?.url
          ? h('div', { style: { marginTop: 'var(--sp-5)' } },
              button('查看原文', { variant: 'ghost', size: 'sm', icon: 'external-link', onClick: () => openUrl(it.url) }),
            )
          : null,
      ),
    );
  }

  function newsList() {
    if (newsState === 'loading') {
      return h('div', { class: 'news-list', 'aria-hidden': 'true' },
        ...[0, 1, 2].map(() => h('div', { class: 'news-item' },
          skeleton(null, { width: '76px', height: '24px', borderRadius: 'var(--r-pill)', flex: '0 0 auto' }),
          h('div', { class: 'grow', style: { minWidth: '0' } },
            skeleton(null, { width: '46%', height: '18px', marginBottom: 'var(--sp-4)' }),
            skeleton(null, { width: '72%', height: '14px' }),
          ),
        )),
      );
    }

    if (newsState === 'error') {
      return emptyState({
        icon: 'megaphone',
        title: '暂时无法读取公告',
        desc: '服务或网络暂时不可用，稍后可以重试。',
        actions: [button('重试', { variant: 'secondary', icon: 'refresh-cw', onClick: () => load() })],
      });
    }

    if (!items.length) {
      return emptyState({
        icon: 'megaphone',
        title: '暂时没有公告',
        desc: '有新消息时会显示在这里。',
        actions: [button('检查更新', { variant: 'secondary', icon: 'refresh-cw', onClick: () => load() })],
      });
    }

    return h('div', { class: 'news-list' }, ...items.map(newsItem));
  }

  function paint() {
    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '公告' }),
        h('div', { class: 'page__subtitle', text: 'StarCore 的版本动态与官方消息' }),
      ),
      h('div', { class: 'page__actions' },
        button('检查更新', { variant: 'ghost', icon: 'refresh-cw', onClick: () => load() }),
      ),
    );

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '880px' } },
      updateCard(),
      newsList(),
    ));
  }

  paint();
  load({ silent: true });
  activators.set(el, () => load({ silent: true }));

  const off = subscribe('app', paint);
  if (ctx?.off) ctx.off.push(off);
  return {
    destroy: () => {
      destroyed = true;
      activators.delete(el);
      off();
    },
  };
}

/* ===========================================================================
   页面：版本历史
   ========================================================================== */

function mountHistory(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let destroyed = false;
  let state = 'loading'; // loading | ready | error
  let releases = [];

  async function load({ silent = false } = {}) {
    state = 'loading';
    paint();
    try {
      const r = await invoke('app.announcements');
      if (destroyed) return;
      releases = (r?.items ?? []).filter((it) => it?.type === 'release');
      state = 'ready';
    } catch (e) {
      if (destroyed) return;
      releases = [];
      state = 'error';
      if (!silent) toast({ title: '读取版本历史失败', desc: errText(e), kind: 'danger' });
    }
    paint();
  }

  function releaseRow(it) {
    const version = it?.version ?? it?.title ?? '未知版本';
    return h('div', { class: 'row-item' },
      h('div', { class: 'row-item__icon' }, icon('tag', 'sm')),
      h('div', { style: { minWidth: '0' } },
        h('div', { class: 'row-item__name' },
          h('span', { class: 'version-tag mono', text: version }),
        ),
        h('div', { class: 'row-item__file truncate', text: summaryOf(it) }),
      ),
      h('div', { class: 'row-item__trail', text: fmtDate(it?.publishedAt) }),
      h('div', { class: 'row-item__trail' },
        it?.url
          ? button('查看', { variant: 'ghost', size: 'sm', icon: 'external-link', onClick: () => openUrl(it.url) })
          : h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '—' }),
      ),
    );
  }

  function body() {
    if (state === 'loading') {
      return h('div', { class: 'rows' },
        ...[0, 1, 2, 3].map(() => h('div', { class: 'row-item' },
          h('div', { class: 'row-item__icon' }, icon('tag', 'sm')),
          h('div', { style: { minWidth: '0' } }, skeleton(null, { width: '46%', height: '16px' })),
          skeleton(null, { width: '82px', height: '14px' }),
          skeleton(null, { width: '58px', height: '26px', borderRadius: 'var(--r-md)' }),
        )),
      );
    }

    if (state === 'error') {
      return emptyState({
        icon: 'history',
        title: '暂时无法读取版本历史',
        desc: '服务或网络暂时不可用，稍后可以重试。',
        actions: [button('重试', { variant: 'secondary', icon: 'refresh-cw', onClick: () => load() })],
      });
    }

    if (!releases.length) {
      return emptyState({
        icon: 'history',
        title: '还没有版本历史',
        desc: 'StarCore 发布新版本后，记录会显示在这里。',
        actions: [button('检查更新', { variant: 'ghost', icon: 'refresh-cw', onClick: () => checkUpdate() })],
      });
    }

    return h('div', { class: 'rows' }, ...releases.map(releaseRow));
  }

  function paint() {
    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '版本历史' }),
        h('div', { class: 'page__subtitle', text: 'StarCore 历次版本发布记录' }),
      ),
      h('div', { class: 'page__actions' },
        button('返回公告', { variant: 'ghost', icon: 'chevron-left', onClick: () => go(SECTION) }),
      ),
    );

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '880px' } }, body()));
  }

  paint();
  load({ silent: true });
  activators.set(el, () => load({ silent: true }));

  return {
    destroy: () => {
      destroyed = true;
      activators.delete(el);
    },
  };
}

/* ===========================================================================
   模块导出
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [SECTION]: {
      title: '公告',
      icon: 'megaphone',
      mount: mountAnnouncement,
      activate: (el) => activators.get(el)?.(),
    },
    [`${SECTION}:history`]: {
      title: '版本历史',
      icon: 'history',
      mount: mountHistory,
      activate: (el) => activators.get(el)?.(),
    },
  },
};
