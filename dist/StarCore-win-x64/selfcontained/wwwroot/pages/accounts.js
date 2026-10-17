/**
 * accounts.js —— 「账户」分区。
 *
 * 账户是本应用的前置条件：没有账户就不能启动游戏。
 * 因此这里刻意不内置任何账户数据，一切都必须由用户自己登录或创建：
 *   accounts          账户列表：当前账户 + 全部账户（切换 / 设为默认 / 移除）
 *   accounts:skin     皮肤与披风（只作用于当前账户）
 *   accounts:add      添加账户：微软设备码登录 / 离线账户创建
 *
 * 数据全部来自内核（account.*），失败一律 toast 并且保留可重试的界面。
 */

import { invoke } from '../scripts/bridge.js';
import { getState, setState, subscribe } from '../scripts/store.js';
import {
  h, fill, icon, setIcon, button, emptyState, chip,
  toast, dialog, confirm, menu,
} from '../scripts/ui.js';
import { go } from '../scripts/router.js';

const SECTION = 'accounts';

/** 离线名字规则，与 Minecraft 一致：3–16 位，仅英文字母 / 数字 / 下划线。 */
const NAME_RE = /^[A-Za-z0-9_]{3,16}$/;

/** 内置皮肤：传给 account.skin.upload 的 builtin 字段。 */
const BUILTIN_SKINS = ['Steve', 'Alex', 'Ari', 'Zuri'];

/** 每个页面挂载后把自己的「重新拉数据」函数登记在这里，供 router 的 activate 调用。 */
const activators = new WeakMap();

const errText = (e) => e?.message ?? String(e ?? '未知错误');

/* ===========================================================================
   账户的公共读取与小工具
   ========================================================================== */

function isMicrosoft(acc) {
  return String(acc?.profileType ?? '').toLowerCase() === 'microsoft';
}

function typeIcon(acc) {
  return isMicrosoft(acc) ? 'globe' : 'user';
}

function typeChip(acc) {
  return isMicrosoft(acc)
    ? chip('正版账户', { variant: 'accent', icon: 'globe' })
    : chip('离线账户', { icon: 'user' });
}

function skinSourceText(acc) {
  if (acc?.localSkinPath) return '本地皮肤';
  if (acc?.skinUrl) return '账户皮肤';
  return '默认皮肤';
}

/** 当前账户；内核还没写回 activeUuid 时退回列表第一个，避免界面空转。 */
function displayAccount() {
  const s = getState();
  return s.accounts.find((a) => a.uuid === s.activeUuid) ?? s.accounts[0] ?? null;
}

/** 大号圆形头像；没有皮肤头部贴图时退回首字母。 */
function bigAvatar(acc) {
  return h('div', { class: 'avatar avatar--xl avatar--round' },
    acc?.skinFace
      ? h('img', { src: acc.skinFace, alt: acc.userName ?? '', style: { imageRendering: 'pixelated' } })
      : h('span', { text: (acc?.userName ?? '?').slice(0, 1).toUpperCase() }),
  );
}

async function refreshAccounts({ silent = false } = {}) {
  try {
    const r = await invoke('account.list');
    setState({
      accounts: r?.profiles ?? [],
      activeUuid: r?.activeUuid ?? getState().activeUuid,
    });
    return true;
  } catch (e) {
    // 失败时保留已有列表，不把界面清空
    if (!silent) toast({ title: '无法读取账户列表', desc: errText(e), kind: 'danger', timeout: 8000 });
    return false;
  }
}

async function setActiveAccount(acc) {
  if (!acc || acc.uuid === getState().activeUuid) return;
  try {
    await invoke('account.setActive', { uuid: acc.uuid });
    setState({ activeUuid: acc.uuid });
    toast({ title: '已切换账户', desc: acc.userName ?? '', kind: 'success' });
  } catch (e) {
    toast({ title: '切换账户失败', desc: errText(e), kind: 'danger' });
  }
}

async function refreshAccount(acc) {
  try {
    await invoke('account.refresh', { uuid: acc.uuid });
    await refreshAccounts({ silent: true });
    toast({ title: '账户信息已刷新', kind: 'success' });
  } catch (e) {
    toast({ title: '刷新账户失败', desc: errText(e), kind: 'danger' });
  }
}

async function removeAccount(acc) {
  const ok = await confirm({
    title: '移除账户',
    message: `确定要移除账户「${acc.userName ?? '未命名'}」吗？本机保存的登录信息会一并清除。`,
    confirmLabel: '移除',
    danger: true,
  });
  if (!ok) return;
  try {
    await invoke('account.remove', { uuid: acc.uuid });
    await refreshAccounts({ silent: true });
    toast({ title: '账户已移除', kind: 'success' });
  } catch (e) {
    toast({ title: '移除失败', desc: errText(e), kind: 'danger' });
  }
}

async function openUrl(url) {
  if (!url) { toast({ title: '没有可用的链接', kind: 'warning' }); return; }
  try {
    await invoke('system.openUrl', { url });
  } catch (e) {
    toast({ title: '打开链接失败', desc: errText(e), kind: 'danger' });
  }
}

/** 头像 + 名字 + 类型 + UUID + 动作，账户页与皮肤页共用的头部。 */
function accountHead(acc, extraActions = []) {
  return h('div', { class: 'acct-head' },
    bigAvatar(acc),
    h('div', { class: 'grow', style: { minWidth: '0' } },
      h('div', { class: 'row', style: { gap: 'var(--sp-4)', alignItems: 'center', flexWrap: 'wrap' } },
        h('div', { class: 'truncate', style: { fontSize: 'var(--fs-h2)', fontWeight: 'var(--fw-semibold)', letterSpacing: 'var(--ls-snug)' }, text: acc?.userName ?? '未命名账户' }),
        acc ? typeChip(acc) : null,
      ),
      h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-4)', alignItems: 'center' } },
        h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: 'UUID' }),
        h('span', {
          class: 'version-tag mono',
          style: { fontSize: 'var(--fs-xs)', color: 'var(--text-3)', userSelect: 'text', wordBreak: 'break-all' },
          text: acc?.uuid ?? '—',
        }),
      ),
    ),
    h('div', { class: 'row', style: { gap: 'var(--sp-4)', flexWrap: 'wrap' } }, ...extraActions),
  );
}

/* ===========================================================================
   微软设备码登录：完整的倒计时 + 轮询流程
   ========================================================================== */

let msFlowRunning = false;

async function startMsLogin() {
  if (msFlowRunning) {
    toast({ title: '登录流程正在进行中', desc: '请先完成或取消上一次登录。', kind: 'warning' });
    return;
  }

  // 守卫必须在 await 之前置位：否则连点两下时，两次调用都会在
  // await 期间看到 msFlowRunning === false 而各自申请一个设备码，
  // 于是出现"两个轮询循环对着两个设备码问"的诡异局面。
  msFlowRunning = true;

  let start;
  try {
    start = await invoke('account.msStart');
  } catch (e) {
    msFlowRunning = false;
    toast({ title: '无法开始登录', desc: errText(e), kind: 'danger', timeout: 9000 });
    return;
  }

  const deviceCode = start?.deviceCode;
  const userCode = start?.userCode ?? '';
  const verificationUri = start?.verificationUri ?? '';
  if (!deviceCode || !userCode) {
    msFlowRunning = false;
    toast({ title: '登录信息不完整', desc: '内核没有返回有效的设备码，请重试。', kind: 'danger', timeout: 9000 });
    return;
  }

  const totalSec = Math.max(1, Number(start.expiresIn) || 900);
  const pollSec = Math.max(1, Number(start.interval) || 5);

  let left = totalSec;
  let sinceLastPoll = 0;
  let busy = false;
  let stopped = false;
  let notifiedError = false;
  let consecutiveFailures = 0;
  let finishDialog = null;
  let profileName = null;

  const mmss = (s) => `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(Math.max(0, s % 60)).padStart(2, '0')}`;

  /* —— 对话框内容 —— */

  const codeEl = h('button', {
    type: 'button',
    class: 'mono',
    title: '点击复制',
    style: {
      display: 'block',
      width: '100%',
      padding: 'var(--sp-5)',
      borderRadius: 'var(--r-md)',
      border: '1px solid var(--border)',
      background: 'var(--surface-sunken)',
      color: 'var(--text)',
      fontFamily: 'var(--font-mono)',
      fontSize: 'var(--fs-h1)',
      fontWeight: 'var(--fw-semibold)',
      letterSpacing: '.1em',
      cursor: 'pointer',
      textAlign: 'center',
    },
    onClick: async () => {
      try {
        await navigator.clipboard.writeText(userCode);
        toast({ title: '代码已复制', kind: 'success' });
      } catch {
        toast({ title: '复制失败', desc: '请手动选中代码后复制。', kind: 'warning' });
      }
    },
  }, h('span', { text: userCode }));

  const countEl = h('span', { style: { fontSize: 'var(--fs-xs)', color: 'var(--text-3)' }, text: `代码将在 ${mmss(left)} 后失效` });
  const statusIconEl = icon('loader-circle', 'sm');
  const statusEl = h('span', { style: { fontSize: 'var(--fs-sm)', color: 'var(--text-2)' }, text: '等待授权…' });

  function setStatus(text, kind = 'pending') {
    statusEl.textContent = text;
    if (kind === 'ok') {
      setIcon(statusIconEl, 'circle-check');
      statusEl.style.color = 'var(--success)';
    } else if (kind === 'error') {
      setIcon(statusIconEl, 'alert-triangle');
      statusEl.style.color = 'var(--danger)';
    } else {
      setIcon(statusIconEl, 'loader-circle');
      statusEl.style.color = 'var(--text-2)';
    }
  }

  const content = h('div', { class: 'col', style: { gap: 'var(--sp-6)' } },
    h('div', { class: 'dim', style: { fontSize: 'var(--fs-sm)', lineHeight: 'var(--lh-relaxed)' }, text: '在浏览器中打开下面的地址，输入代码完成登录。' }),
    codeEl,
    h('div', { class: 'row-between', style: { gap: 'var(--sp-5)' } },
      h('span', { class: 'mono truncate', style: { fontSize: 'var(--fs-sm)', color: 'var(--text-2)' }, text: verificationUri || '—' }),
      button('打开浏览器', { variant: 'secondary', size: 'sm', icon: 'external-link', onClick: () => openUrl(verificationUri) }),
    ),
    h('div', { class: 'row', style: { gap: 'var(--sp-4)', alignItems: 'center' } }, icon('clock', 'xs'), countEl),
    h('div', { class: 'row', style: { gap: 'var(--sp-4)', alignItems: 'center' } }, statusIconEl, statusEl),
  );

  /* —— 轮询 —— */

  async function poll() {
    if (stopped || busy) return;
    busy = true;
    try {
      const r = await invoke('account.msPoll', { deviceCode });
      if (stopped) return;
      consecutiveFailures = 0;

      const status = r?.status ?? 'error';
      if (status === 'ok') {
        profileName = r?.profile?.userName ?? null;
        setStatus('登录成功', 'ok');
        stop();
        finishDialog?.('ok');
        return;
      }
      if (status === 'expired') {
        setStatus('代码已失效，请重新开始', 'error');
        countEl.textContent = '代码已失效';
        stop();
        return;
      }
      if (status === 'error') {
        setStatus(r?.message ?? '登录失败，请重新开始', 'error');
        stop();
        return;
      }
      // pending：继续等待
    } catch (e) {
      if (stopped) return;

      /* ⚠️ 不能无限重试。
         设备码在微软那边是**一次性**的：一旦它已经被换过票，再用同一个码去问
         只会拿到 AADSTS70000，那条报错会把真正的失败原因彻底盖掉。
         所以这里只容忍连续少量抖动，超过就停下来把错误摊开给用户看。 */
      consecutiveFailures += 1;
      const msg = errText(e);

      if (consecutiveFailures >= 3) {
        setStatus(`登录中断：${msg}`, 'error');
        stop();
        return;
      }

      setStatus(`网络异常，正在重试（${consecutiveFailures}/3）：${msg}`, 'error');
      if (!notifiedError) {
        notifiedError = true;
        toast({ title: '登录轮询失败', desc: `${msg}\n\n稍后会自动重试。`, kind: 'danger', timeout: 8000 });
      }
    } finally {
      busy = false;
    }
  }

  function stop() {
    if (stopped) return;
    stopped = true;
    clearInterval(ticker);
    msFlowRunning = false;
  }

  const ticker = setInterval(() => {
    if (stopped) return;
    left -= 1;
    if (left <= 0) {
      setStatus('代码已失效，请重新开始', 'error');
      countEl.textContent = '代码已失效';
      stop();
      return;
    }
    countEl.textContent = `代码将在 ${mmss(left)} 后失效`;
    sinceLastPoll += 1;
    if (sinceLastPoll >= pollSec) {
      sinceLastPoll = 0;
      poll();
    }
  }, 1000);

  // 立刻先探一次，缩短等待感
  poll();

  const res = await dialog({
    title: '登录微软账户',
    icon: 'globe',
    width: 480,
    dismissible: true,
    body: (finish) => { finishDialog = finish; return content; },
    actions: [{ label: '取消', variant: 'ghost', value: 'cancel' }],
    onClose: () => stop(),
  });

  stop();

  if (res === 'ok') {
    await refreshAccounts({ silent: true });
    toast({ title: '登录成功', desc: profileName ?? undefined, kind: 'success' });
    go(SECTION);
  }
}

/* ===========================================================================
   离线账户创建
   ========================================================================== */

async function createOfflineAccount() {
  const input = h('input', {
    class: 'input',
    placeholder: '例如：Steve',
    maxlength: '16',
    autocomplete: 'off',
    spellcheck: 'false',
  });
  const errorEl = h('div', { class: 'field__error', style: { display: 'none' } });
  const field = h('div', { class: 'field' },
    h('label', { class: 'field__label', text: '玩家名称' }),
    input,
    h('div', { class: 'field__hint', text: '3–16 个字符，仅限英文字母、数字和下划线。' }),
    errorEl,
  );

  const fail = (msg) => {
    input.setAttribute('aria-invalid', 'true');
    errorEl.textContent = msg;
    errorEl.style.display = '';
    input.focus();
    return false;
  };

  input.addEventListener('input', () => {
    input.removeAttribute('aria-invalid');
    errorEl.style.display = 'none';
  });

  const res = await dialog({
    title: '创建离线账户',
    icon: 'user-plus',
    width: 420,
    body: field,
    actions: [
      { label: '取消', variant: 'ghost', value: null },
      {
        label: '创建',
        variant: 'primary',
        value: 'ok',
        onClick: async () => {
          const name = input.value.trim();
          // 本地先校验，不合法就留在对话框里，不让内核白跑一趟
          if (!NAME_RE.test(name)) return fail('名称需为 3–16 个字符，只能包含英文字母、数字和下划线。');
          try {
            await invoke('account.addOffline', { name });
            return undefined;
          } catch (e) {
            toast({ title: '创建账户失败', desc: errText(e), kind: 'danger' });
            return fail(`创建失败：${errText(e)}`);
          }
        },
      },
    ],
  });

  if (res !== 'ok') return;
  await refreshAccounts({ silent: true });
  toast({ title: '离线账户已创建', kind: 'success' });
  go(SECTION);
}

/* ===========================================================================
   二级面板
   ========================================================================== */

function renderPanel(el) {
  const head = h('div', { class: 'panel__head' });
  const body = h('div', { class: 'panel__body' });
  const foot = h('div', { class: 'panel__foot' });
  fill(el, head, body, foot);

  let filter = '';

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
    const n = getState().accounts.length;
    const input = h('input', { class: 'input', placeholder: '筛选账户…', onInput: (e) => { filter = e.target.value.trim(); paintBody(); } });
    input.value = filter;
    fill(head,
      h('div', { class: 'panel__title' },
        icon('user', 'md'),
        h('span', { text: '账户' }),
        h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)', fontWeight: '400' }, text: n ? String(n) : '' }),
      ),
      h('div', { class: 'search' }, icon('search', 'sm'), input),
    );
  };

  const paintBody = () => {
    const list = getState().accounts;
    const activeUuid = getState().activeUuid;
    const shown = filter ? list.filter((a) => (a.userName ?? '').toLowerCase().includes(filter.toLowerCase())) : list;

    const addItem = navItem('添加账户', { icon: 'user-plus', onClick: () => go(SECTION, 'add') });

    if (!list.length) {
      fill(body,
        h('div', { class: 'panel__group', text: '账户' }),
        h('div', { class: 'dim', style: { padding: '20px 10px', fontSize: 'var(--fs-sm)', textAlign: 'center' }, text: '还没有账户' }),
        addItem,
      );
      return;
    }
    if (!shown.length) {
      fill(body,
        h('div', { class: 'panel__group', text: '账户' }),
        h('div', { class: 'dim', style: { padding: '20px 10px', fontSize: 'var(--fs-sm)', textAlign: 'center' }, text: '没有匹配的账户' }),
        addItem,
      );
      return;
    }

    fill(body,
      h('div', { class: 'panel__group', text: '账户' }),
      ...shown.map((acc) => navItem(acc.userName ?? '未命名账户', {
        icon: typeIcon(acc),
        selected: acc.uuid === activeUuid,
        onClick: async () => { await setActiveAccount(acc); go(SECTION); },
      })),
      addItem,
    );
  };

  const paintFoot = () => {
    fill(foot, h('div', { class: 'col', style: { gap: 'var(--sp-3)' } },
      button('微软账户', { variant: 'soft', block: true, icon: 'globe', onClick: () => startMsLogin() }),
      button('离线账户', { variant: 'ghost', block: true, icon: 'user-plus', onClick: () => createOfflineAccount() }),
    ));
  };

  paintHead();
  paintBody();
  paintFoot();

  const offs = [
    subscribe('accounts', () => { paintHead(); paintBody(); }),
    subscribe('activeUuid', paintBody),
  ];
  return () => offs.forEach((f) => f());
}

/* ===========================================================================
   页面：账户列表
   ========================================================================== */

function mountAccounts(el, ctx) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  const addMicrosoft = () => startMsLogin();
  const addOffline = () => createOfflineAccount();

  function accountRow(acc) {
    const isActive = acc.uuid === getState().activeUuid;
    const row = h('div', {
      class: 'row-item',
      role: 'button',
      tabindex: '0',
      'aria-selected': isActive ? 'true' : 'false',
      onClick: () => setActiveAccount(acc),
      onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setActiveAccount(acc); } },
    },
      h('div', { class: 'row-item__icon' },
        acc.skinFace
          ? h('img', { src: acc.skinFace, alt: '', style: { imageRendering: 'pixelated' } })
          : icon(typeIcon(acc), 'sm'),
      ),
      h('div', { style: { minWidth: '0' } },
        h('div', { class: 'row-item__name' },
          h('span', { class: 'truncate', text: acc.userName ?? '未命名账户' }),
          typeChip(acc),
          isActive ? chip('当前账户', { variant: 'success', icon: 'circle-check' }) : null,
        ),
        h('div', { class: 'row-item__file truncate', text: acc.uuid ?? '' }),
      ),
      h('div', { class: 'row-item__trail', text: skinSourceText(acc) }),
      h('div', { class: 'row-item__trail' },
        button(isActive ? '当前' : '切换', {
          variant: isActive ? 'ghost' : 'secondary',
          size: 'sm',
          disabled: isActive,
          onClick: (e) => { e.stopPropagation(); setActiveAccount(acc); },
        }),
      ),
    );
    return row;
  }

  function paint() {
    const s = getState();
    const list = s.accounts;
    const current = displayAccount();

    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '账户' }),
        h('div', { class: 'page__subtitle', text: list.length ? `共 ${list.length} 个账户` : '登录或创建一个账户来开始游戏' }),
      ),
      list.length
        ? h('div', { class: 'page__actions' },
            button('添加微软账户', { variant: 'secondary', icon: 'globe', onClick: addMicrosoft }),
            button('添加离线账户', { variant: 'primary', icon: 'user-plus', onClick: addOffline }),
          )
        : null,
    );

    if (!list.length) {
      fill(scroll, emptyState({
        icon: 'user-plus',
        title: '还没有添加账户',
        desc: '登录微软正版账户可以联机与同步皮肤；离线账户无需联网即可单机游玩。',
        actions: [
          button('登录微软账户', { variant: 'primary', icon: 'globe', onClick: addMicrosoft }),
          button('创建离线账户', { variant: 'secondary', icon: 'user-plus', onClick: addOffline }),
        ],
      }));
      return;
    }

    const isCurrent = current && current.uuid === s.activeUuid;

    fill(scroll, h('div', { class: 'page__inner' },
      accountHead(current, [
        isCurrent
          ? chip('当前账户', { variant: 'success', icon: 'circle-check' })
          : button('设为默认', { variant: 'secondary', size: 'sm', icon: 'check', onClick: () => setActiveAccount(current) }),
        button('刷新', { variant: 'ghost', size: 'sm', icon: 'refresh-cw', onClick: () => refreshAccount(current) }),
        button('移除', { variant: 'danger-ghost', size: 'sm', icon: 'trash-2', onClick: () => removeAccount(current) }),
      ]),

      h('div', { class: 'row', style: { gap: 'var(--sp-4)', margin: 'var(--sp-9) 0 var(--sp-5)' } },
        h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-medium)' }, text: '全部账户' }),
        h('span', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '点击任意一行即可切换' }),
      ),

      h('div', { class: 'rows' }, ...list.map(accountRow)),

      h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-8)' } },
        button('添加微软账户', { variant: 'secondary', icon: 'globe', onClick: addMicrosoft }),
        button('添加离线账户', { variant: 'ghost', icon: 'user-plus', onClick: addOffline }),
      ),
    ));
  }

  paint();
  activators.set(el, () => refreshAccounts({ silent: true }));

  const offs = [
    subscribe('accounts', paint),
    subscribe('activeUuid', paint),
  ];
  if (ctx?.off) ctx.off.push(...offs);
  return { destroy: () => offs.forEach((f) => f()) };
}

/* ===========================================================================
   页面：皮肤与披风
   ========================================================================== */

const SKIN_SOURCES = [
  { id: 'official', label: '账户皮肤', desc: '跟随账户同步' },
  { id: 'local', label: '本地皮肤', desc: '从本机上传' },
  { id: 'builtin', label: '内置皮肤', desc: '启动器自带' },
];

function skinPlaceholder() {
  return h('div', { class: 'col', style: { alignItems: 'center', justifyContent: 'center', gap: 'var(--sp-2)', padding: 'var(--sp-3)', textAlign: 'center' } },
    icon('shirt', 'xl'),
    h('div', { class: 'dim', style: { fontSize: 'var(--fs-micro)', lineHeight: 'var(--lh-snug)' }, text: '暂无贴图' }),
  );
}

/** 贴图可能来自磁盘路径，加载失败时换成占位图，绝不留下碎图。 */
function skinImage(url) {
  const img = h('img', {
    src: url,
    alt: '',
    loading: 'lazy',
    style: { imageRendering: 'pixelated', width: '100%', height: '100%', objectFit: 'contain' },
  });
  img.addEventListener('error', () => {
    img.replaceWith(skinPlaceholder());
  }, { once: true });
  return img;
}

function mountSkin(el, ctx) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let destroyed = false;
  let loading = true;
  let skinData = null;
  let capeItems = [];
  let capeState = 'idle'; // idle | loading | ready | error
  let selectedSource = 'official';
  let loadedUuid = null;

  const sourceUrl = (id, d) => (id === 'official' ? d?.skinUrl : id === 'local' ? d?.localSkinPath : null);

  function deriveSource(d) {
    if (d?.localSkinPath) return 'local';
    if (d?.skinUrl) return 'official';
    return 'builtin';
  }

  function activeCape() {
    return capeItems.find((c) => c?.active || c?.selected) ?? null;
  }

  /* —— 数据 —— */

  async function loadSkin(acc, { silent = false } = {}) {
    if (!acc) return;
    loading = true;
    loadedUuid = acc.uuid;
    paint();
    try {
      const r = await invoke('account.skin.get', { uuid: acc.uuid });
      if (destroyed || loadedUuid !== acc.uuid) return;
      skinData = r ?? {};
      selectedSource = deriveSource(skinData);
    } catch (e) {
      if (destroyed) return;
      skinData = null;
      selectedSource = 'builtin';
      if (!silent) toast({ title: '读取皮肤信息失败', desc: errText(e), kind: 'danger' });
    } finally {
      if (!destroyed && loadedUuid === acc.uuid) {
        loading = false;
        paint();
      }
    }
  }

  async function loadCapes(acc) {
    if (!isMicrosoft(acc)) {
      capeState = 'idle';
      capeItems = [];
      return;
    }
    capeState = 'loading';
    try {
      const r = await invoke('account.cape.list');
      if (destroyed) return;
      const raw = r?.capes ?? r?.items ?? (Array.isArray(r) ? r : []);
      capeItems = Array.isArray(raw) ? raw : [];
      capeState = 'ready';
    } catch {
      if (destroyed) return;
      capeItems = [];
      capeState = 'error';
    }
    if (!destroyed) paint();
  }

  async function reload(acc, { silent = false } = {}) {
    await Promise.all([
      loadSkin(acc, { silent }),
      loadCapes(acc),
    ]);
    if (!destroyed) paint();
  }

  /* —— 动作 —— */

  async function uploadSkin(acc) {
    try {
      const r = await invoke('system.pickFile', { title: '选择皮肤文件', filter: '皮肤 (*.png)|*.png' });
      if (!r?.path) return;
      await invoke('account.skin.upload', { uuid: acc.uuid, path: r.path });
      toast({ title: '皮肤已更新', kind: 'success' });
      await refreshAccounts({ silent: true });
      await loadSkin(acc, { silent: true });
    } catch (e) {
      toast({ title: '上传皮肤失败', desc: errText(e), kind: 'danger' });
    }
  }

  function builtinMenu(anchor, acc) {
    menu(anchor, BUILTIN_SKINS.map((name) => ({
      label: name,
      icon: 'shirt',
      onClick: async () => {
        try {
          await invoke('account.skin.upload', { uuid: acc.uuid, builtin: name });
          toast({ title: '已使用内置皮肤', desc: name, kind: 'success' });
          await refreshAccounts({ silent: true });
          await loadSkin(acc, { silent: true });
        } catch (e) {
          toast({ title: '设置皮肤失败', desc: errText(e), kind: 'danger' });
        }
      },
    })));
  }

  function modelMenu(anchor, acc) {
    const cur = skinData?.skinModel ?? 'classic';
    menu(anchor, [
      { label: '经典', icon: 'user', hint: cur === 'classic' ? '当前' : '', onClick: () => setModel(acc, 'classic') },
      { label: '纤细', icon: 'user-round', hint: cur === 'slim' ? '当前' : '', onClick: () => setModel(acc, 'slim') },
    ]);
  }

  async function setModel(acc, model) {
    try {
      await invoke('account.skin.setModel', { uuid: acc.uuid, model });
      toast({ title: '模型已切换', desc: model === 'slim' ? '纤细' : '经典', kind: 'success' });
      await refreshAccounts({ silent: true });
      await loadSkin(acc, { silent: true });
    } catch (e) {
      toast({ title: '切换模型失败', desc: errText(e), kind: 'danger' });
    }
  }

  function capeMenu(anchor, acc) {
    if (capeState === 'loading') { toast({ title: '披风列表还在加载', kind: 'neutral' }); return; }
    if (capeState === 'error') { toast({ title: '无法读取披风列表', desc: '请稍后重试。', kind: 'danger' }); return; }
    if (!capeItems.length) { toast({ title: '该账户没有可用披风', kind: 'neutral' }); return; }

    menu(anchor, capeItems.map((c) => ({
      label: c?.name ?? c?.id ?? '披风',
      icon: 'flag',
      hint: c?.active ? '当前' : '',
      onClick: async () => {
        try {
          await invoke('account.skin.upload', { uuid: acc.uuid, cape: c?.id ?? c?.name });
          toast({ title: '披风已更新', kind: 'success' });
          await loadCapes(acc);
        } catch (e) {
          toast({ title: '切换披风失败', desc: errText(e), kind: 'danger' });
        }
      },
    })));
  }

  /* —— 渲染 —— */

  function skinCard(src, selected) {
    const url = sourceUrl(src.id, skinData);
    const select = () => { selectedSource = src.id; paint(); };
    return h('div', {
      class: 'skin-card',
      role: 'radio',
      tabindex: '0',
      'aria-selected': selected ? 'true' : 'false',
      'aria-label': src.label,
      onClick: select,
      onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(); } },
    },
      h('div', { class: 'skin-preview' }, url ? skinImage(url) : skinPlaceholder()),
      h('div', { style: { textAlign: 'center' } },
        h('div', { style: { fontSize: 'var(--fs-sm)', fontWeight: 'var(--fw-medium)' }, text: src.label }),
        h('div', { class: 'dim', style: { fontSize: 'var(--fs-micro)', marginTop: '2px' }, text: src.desc }),
      ),
    );
  }

  function skinSection(acc) {
    const model = skinData?.skinModel === 'slim' ? '纤细' : '经典';
    return h('div', { class: 'set-section' },
      h('div', { class: 'set-section__title' },
        icon('shirt', 'md'),
        h('span', { text: '皮肤' }),
        chip(model, { icon: 'ruler' }),
      ),
      h('div', { class: 'set-section__desc', text: '三种来源里的贴图都可以单独预览；上传与切换模型会作用于当前账户。' }),
      loading
        ? h('div', { class: 'skeleton', style: { height: '186px', borderRadius: 'var(--r-lg)' } })
        : h('div', { class: 'skin-strip', role: 'radiogroup', 'aria-label': '皮肤来源' },
            ...SKIN_SOURCES.map((s) => skinCard(s, s.id === selectedSource)),
          ),
      loading
        ? null
        : h('div', { class: 'row', style: { gap: 'var(--sp-4)', marginTop: 'var(--sp-6)', flexWrap: 'wrap' } },
            button('上传皮肤', { variant: 'primary', icon: 'upload', onClick: () => uploadSkin(acc) }),
            button('使用内置皮肤', { variant: 'ghost', icon: 'shirt', onClick: (e) => builtinMenu(e.currentTarget, acc) }),
            button('切换模型', { variant: 'ghost', icon: 'sliders-horizontal', onClick: (e) => modelMenu(e.currentTarget, acc) }),
          ),
    );
  }

  function capeSection(acc) {
    if (!isMicrosoft(acc)) {
      return h('div', { class: 'set-section' },
        h('div', { class: 'set-section__title' }, icon('flag', 'md'), h('span', { text: '披风' })),
        h('div', { class: 'set-section__desc', text: '离线账户没有披风。' }),
      );
    }

    const cape = activeCape();
    const desc = capeState === 'error'
      ? '无法读取披风列表，请稍后重试。'
      : capeState === 'loading'
        ? '正在读取披风列表…'
        : capeItems.length
          ? '披风跟随微软账户，更换后会立即生效。'
          : '该账户没有可用披风。';

    return h('div', { class: 'set-section' },
      h('div', { class: 'set-section__title' }, icon('flag', 'md'), h('span', { text: '披风' })),
      h('div', { class: 'set-row' },
        h('div', { class: 'grow' },
          h('div', { class: 'set-row__label', text: '当前披风' }),
          h('div', { class: 'set-row__desc', text: desc }),
        ),
        h('div', { class: 'set-row__control' },
          chip(cape?.name ?? (skinData?.capeUrl ? '已启用' : '未使用'), { icon: 'flag' }),
          button('更换披风', { variant: 'ghost', size: 'sm', icon: 'flag', onClick: (e) => capeMenu(e.currentTarget, acc) }),
        ),
      ),
    );
  }

  function paint() {
    const acc = displayAccount();

    fill(head,
      h('div', {},
        h('div', { class: 'page__title', text: '皮肤与披风' }),
        h('div', { class: 'page__subtitle', text: acc ? `当前账户：${acc.userName ?? '未命名'}` : '先添加一个账户' }),
      ),
      h('div', { class: 'page__actions' },
        button('返回账户', { variant: 'ghost', icon: 'chevron-left', onClick: () => go(SECTION) }),
      ),
    );

    if (!acc) {
      fill(scroll, emptyState({
        icon: 'user-plus',
        title: '还没有可管理的账户',
        desc: '登录微软账户或创建离线账户后，就可以在这里更换皮肤与披风。',
        actions: [
          button('登录微软账户', { variant: 'primary', icon: 'globe', onClick: () => startMsLogin() }),
          button('创建离线账户', { variant: 'secondary', icon: 'user-plus', onClick: () => createOfflineAccount() }),
        ],
      }));
      return;
    }

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '980px' } },
      accountHead(acc),
      skinSection(acc),
      capeSection(acc),
    ));
  }

  const offs = [
    subscribe('activeUuid', () => {
      const acc = displayAccount();
      if (acc) reload(acc);
    }),
    subscribe('accounts', paint),
  ];

  const acc0 = displayAccount();
  if (acc0) reload(acc0);
  else { loading = false; paint(); }

  activators.set(el, () => {
    const acc = displayAccount();
    if (acc) reload(acc, { silent: true });
  });

  if (ctx?.off) ctx.off.push(...offs);
  return {
    destroy: () => {
      destroyed = true;
      activators.delete(el);
      offs.forEach((f) => f());
    },
  };
}

/* ===========================================================================
   页面：添加账户
   ========================================================================== */

function mountAdd(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  const pickCard = ({ icon: ic, title, desc, onClick }) => h('button', {
    class: 'pick-card',
    type: 'button',
    style: { fontFamily: 'var(--font-ui)', color: 'inherit' },
    onClick,
  },
    h('div', { class: 'row-item__icon' }, icon(ic, 'md')),
    h('div', { class: 'grow' },
      h('div', { style: { fontSize: 'var(--fs-body)', fontWeight: 'var(--fw-semibold)' }, text: title }),
      h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginTop: '3px', lineHeight: 'var(--lh-snug)' }, text: desc }),
    ),
    icon('chevron-right', 'sm'),
  );

  fill(head,
    h('div', {},
      h('div', { class: 'page__title', text: '添加账户' }),
      h('div', { class: 'page__subtitle', text: '选择一种方式登录或创建账户' }),
    ),
    h('div', { class: 'page__actions' },
      button('返回账户', { variant: 'ghost', icon: 'chevron-left', onClick: () => go(SECTION) }),
    ),
  );

  fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '760px' } },
    h('div', { class: 'pick-grid' },
      pickCard({
        icon: 'globe',
        title: '微软账户',
        desc: '使用正版账号登录，支持联机、皮肤与披风同步。',
        onClick: () => startMsLogin(),
      }),
      pickCard({
        icon: 'user',
        title: '离线账户',
        desc: '无需联网，仅用于单机游玩。名字需符合 Minecraft 命名规则。',
        onClick: () => createOfflineAccount(),
      }),
    ),
    h('div', { class: 'field__hint', style: { marginTop: 'var(--sp-7)' }, text: '账户信息只保存在本机，启动器不会把它发送给任何第三方。' }),
  ));
}

/* ===========================================================================
   模块导出
   ========================================================================== */

export default {
  section: SECTION,
  panel: renderPanel,
  pages: {
    [SECTION]: {
      title: '账户',
      icon: 'user',
      mount: mountAccounts,
      activate: (el) => activators.get(el)?.(),
    },
    [`${SECTION}:skin`]: {
      title: '皮肤与披风',
      icon: 'shirt',
      mount: mountSkin,
      activate: (el) => activators.get(el)?.(),
    },
    [`${SECTION}:add`]: {
      title: '添加账户',
      icon: 'user-plus',
      mount: mountAdd,
    },
  },
};
