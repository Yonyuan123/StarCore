# StarCore 页面开发契约

> 本文件是页面模块的**唯一接口约定**。写页面前先通读，写完后逐条自查。

## 0. 范式样板

**必读**：`src/StarCore.Desktop/wwwroot/pages/instance.js`

它是标准写法：模块结构、面板渲染、数据加载、空状态、错误处理、订阅清理，全部在里面。
新页面照它的骨架写，不要自创结构。

---

## 1. 文件与模块形状

文件位置：`src/StarCore.Desktop/wwwroot/pages/<name>.js`

必须是**默认导出**的一个对象：

```js
export default {
  section: 'download',           // 一级分区 id，必须与 nav.js 的 RAIL 一致
  panel: renderPanel,            // 可选：(el, h, { go }) => cleanupFn
  onEnter: async () => {},       // 可选：进入该分区时调用一次
  pages: {
    'download':            { title: '版本下载', icon: 'download', mount: (el) => {...} },
    'download:loaders':    { title: '加载器安装', icon: 'hammer',  mount: (el) => {...} },
  },
};
```

### mount 约定

```js
mount(el, ctx) { ... return { destroy() { off1(); off2(); } } | void }
```

- `el` 是一个空的 `div.page`，页面所有内容都在里面。
- 返回的 `destroy` 会在页面销毁时调用；**必须**在里面退订所有 `subscribe`，否则内存泄漏。
- `ctx = { section, sub, off: [] }`，也可以把退订函数推进 `ctx.off`。
- `mount` 可以是 `async`。

### 页面内部结构（用现成的 CSS 类）

```js
const head   = h('div', { class: 'page__header' });   // 标题行
const scroll = h('div', { class: 'page__scroll' });   // 滚动主体
fill(el, head, scroll);
// 内容宽度用 <div class="page__inner"> 包一层（最大 1180px 居中）
```

---

## 2. 硬性约束（违反即返工）

| # | 约束 |
|---|---|
| 1 | **禁止任何硬编码颜色**。只能用 `var(--*)` 令牌；不允许 `#fff`、`rgb()`、`rgba()` 字面量（`rgba(255,255,255,.14)` 这类"压在图片上的玻璃层"除外，但仅限 Hero/封面图层）。 |
| 2 | **禁止 emoji**。所有图形一律用 `<svg>`（经 `icon()`）或 CSS 绘制。 |
| 3 | **图标名必须存在于 `scripts/icons.js` 的 `ICONS` 键**。不确定就先 `grep` 一下，不要猜。 |
| 4 | **禁止内置任何演示数据**。账户、实例、模组、版本全部来自 `invoke()`；取不到就渲染空状态。 |
| 5 | 字体一律用 `var(--font-ui)` / `var(--font-display)` / `var(--font-mono)`，不写具体字体名。 |
| 6 | 所有可点元素必须可用键盘触达：`tabindex="0"` + 回车/空格触发，或直接用 `<button>`。 |
| 7 | 禁止动画特效（无 `requestAnimationFrame` 循环、无自绘 canvas 动画）。CSS 过渡只允许动 `transform` / `opacity`。 |
| 8 | 文案用简体中文，界面语气一致：简洁、直接、不用感叹号堆砌。 |

---

## 3. 可用的 UI 工具（`../scripts/ui.js`）

```js
import {
  h, fill, frag, $, $$,
  icon, setIcon,
  button, iconButton,
  fmtBytes, fmtCount, fmtDate, fmtDateTime, fmtAgo, fmtDuration, fmtSpeed, clamp,
  toast, dialog, confirm, menu, closeMenu,
  emptyState, sectionHead, chip, avatar, progress, switchEl, segment, settingRow, skeleton,
} from '../scripts/ui.js';
```

关键签名：

| 函数 | 用法 |
|---|---|
| `h(tag, props, ...children)` | `props` 支持 `class`（可传数组）、`style`（对象）、`dataset`、`text`、`html`、`onXxx`（事件）、`ref`（回调）。 |
| `icon(name, size)` | 返回 `<span class="icon">`，size ∈ `xs sm md lg xl 2xl 3xl`。 |
| `button(label, { variant, size, icon, iconAfter, onClick, disabled, block, className })` | variant ∈ `primary secondary ghost soft danger danger-ghost`；size ∈ `sm lg`（不传即中号）。 |
| `iconButton(iconName, { variant, size, tip, onClick })` | size ∈ `sm md lg`。`tip` 会显示悬浮提示。 |
| `emptyState({ icon, title, desc, actions })` | `actions` 是按钮数组。 |
| `chip(label, { variant, icon, removable, onRemove })` | variant ∈ `accent success warning danger outline`。 |
| `dialog({ title, icon, body, actions, width, dismissible })` | 返回 Promise，resolve 为被点按钮的 `value`。`body` 可以是节点或 `(close) => 节点`。 |
| `confirm({ title, message, confirmLabel, danger })` | 返回 `Promise<boolean>`。 |
| `menu(anchorEl, items)` | items: `{ label, icon, hint, danger, onClick }` 或 `{ separator: true }`。 |
| `progress(value, { indeterminate, size })` | `value` 为 0–100。 |
| `switchEl(checked, onChange)` | 返回开关节点。 |
| `settingRow(title, desc, control)` | 设置行。 |

---

## 4. 数据访问（`../scripts/bridge.js` 与 `../scripts/store.js`）

```js
import { invoke, on, HostError } from '../scripts/bridge.js';
import { getState, setState, subscribe, getActiveAccount, getActiveInstance, patchSettings, upsertTask, removeTask } from '../scripts/store.js';
import { go } from '../scripts/router.js';
```

- `invoke(method, params)` → `Promise`，失败会 reject（务必 `try/catch`）。
- `on(eventName, handler)` → 退订函数（内核主动推送，例如任务进度）。
- `subscribe(key, fn)` → 退订函数（本地状态变化）。

**所有 `invoke` 都必须包 `try/catch`**，失败时给出 toast 或错误态，绝不允许把异常抛到白屏。

### 内核方法清单（C# 侧实现）

```
app.info
window.minimize | window.toggleMaximize | window.close | window.drag

settings.get | settings.set

account.list | account.addOffline | account.remove | account.setActive
account.msStart | account.msPoll | account.refresh
account.skin.get | account.skin.upload | account.skin.setModel | account.validateName

version.manifest.list | version.manifest.refresh
version.install
version.loaders

instance.list | instance.create | instance.remove | instance.update
instance.setActive | instance.duplicate | instance.export | instance.importModpack

java.list | java.scan | java.validate | java.download

launch.start | launch.kill

content.scan | content.toggle | content.import | content.remove

modrinth.search | modrinth.project | modrinth.versions | modrinth.install

log.list | log.read | log.analyze | log.delete

system.pickFile | system.pickFiles | system.pickFolder
system.openFolder | system.openPath | system.revealPath | system.openUrl
system.openInstanceFolder | system.openContentFolder | system.createShortcut
system.memoryInfo

app.checkUpdate | app.licenses
```

### 内核推送事件

```
task.update      { id, title, state: queued|running|done|failed|cancelled, progress, speed, detail, error }
launch.state     { state: idle|preparing|running|exited|crashed, instanceId, pid, exitCode, startedAt }
accounts.changed
instances.changed
log.line         { instanceId, line, level }
toast            { title, desc, kind }
```

### 全局状态（`getState()`）关键字段

```js
{
  app:        { name, version, osName, arch, locale, dataDir, totalMemoryMb },
  settings:   { theme, accent, background, language, downloadSource, maxThreads, proxy, closeToTray, ... },
  accounts:   [ { uuid, userName, profileType, skinFace, skinUrl, capeUrl } ],
  activeUuid,
  instances:  [ { id, name, versionId, loader, path, memoryMb, javaPath, modCount, sizeBytes, lastPlayedAt, totalPlayTimeMs, cover } ],
  activeInstanceId,
  versions:   [ { id, type: 'release'|'snapshot'|'old_beta'|'old_alpha', releaseTime, url } ],
  javas:      [ { path, version, arch, vendor, major } ],
  tasks:      [ { id, title, state, progress, speed, detail } ],
  launch:     { state, instanceId, pid, exitCode, startedAt },
  route:      { section, sub },
}
```

---

## 5. 可复用的页面级 CSS 类（`pages/pages.css`）

| 用途 | 类 |
|---|---|
| 工具栏 / 筛选条 | `.toolbar`、`.toolbar__divider`、`.filter-btn[data-active]` |
| 实例卡片网格 | `.inst-grid`、`.inst-card`、`.inst-card__cover/__badge/__body/__title/__meta` |
| 实例列表行 | `.inst-row`、`.inst-row__cover/__name/__meta` |
| 高密度内容列表 | `.rows`、`.rows-head`、`.row-item`、`.row-item__icon/__name/__chips/__file/__trail` |
| 资源卡片 | `.res-grid`、`.res-card`、`.res-card__icon/__title/__author/__desc/__stats` |
| 详情页 | `.detail-hero`、`.detail-hero__icon`、`.detail-title`、`.meta-table`、`.meta-row` |
| 安装目标选择 | `.install-target` |
| 账户 | `.acct-head`、`.skin-strip`、`.skin-card`、`.skin-preview` |
| 设置 | `.set-section`、`.set-section__title/__desc`、`.set-row`、`.set-row__label/__desc/__control` |
| 强调色 / 背景选择 | `.accent-swatches`、`.accent-swatch`、`.bg-picker`、`.bg-thumb` |
| 日志 | `.log-view`、`.log-view__head/__body`、`.log-line--error/warn/debug/time` |
| 崩溃分析 | `.crash-card`、`.crash-card__title`、`.crash-reason`、`.crash-reason__n` |
| 公告 | `.news-list`、`.news-item`、`.news-item__tag/__title/__meta/__body` |
| 版本列表 | `.ver-row`、`.ver-row__id/__date`、`.type-pill--release/snapshot/old_beta/old_alpha` |
| 加载器卡片 | `.loader-grid`、`.loader-card`、`.loader-card__logo/__name/__desc` |
| 安装进度面板 | `.install-panel` |
| 幽灵水印标题 | `.ghost-title` > `.ghost-title__ghost` + `.ghost-title__real` |
| Hero 启动区 | `.hero-stage`、`.launch-stack`、`.launch-avatar`、`.launch-name`、`.btn-launch`、`.launch-version`、`.hero-dock`、`.instance-card-hero`、`.hero-stats` |
| 欢迎引导 | `.welcome`、`.welcome__inner/__badge/__title/__desc/__actions`、`.steps`、`.step-card` |

通用组件类见 `styles/components.css`（`.btn`、`.card`、`.chip`、`.input`、`.search`、`.segment`、`.list-row`、`.avatar`、`.empty`、`.progress`、`.switch`、`.checkbox`、`.slider`、`.menu`、`.dialog`、`.toast`、`.skeleton`）。

---

## 6. 交互规范

| 场景 | 要求 |
|---|---|
| 加载中 | 用 `skeleton()` 占位或 `.spinner`，**不要**用"加载中…"纯文字打天下 |
| 空数据 | 必须用 `emptyState({...})`，给出**下一步动作按钮** |
| 出错 | `toast({ kind:'danger' })` + 界面保留可恢复状态（不要清空已有内容） |
| 破坏性操作 | 必须 `confirm()` 二次确认，`danger: true` |
| 长任务 | 用 `invoke` 拿到 `taskId`，再靠 `on('task.update')` 更新 `progress()`，支持取消 |
| 每次进入页面 | 用 `activate()` 重新拉取数据，保证不是陈旧内容 |

---

## 7. 完成后自查（逐条打勾）

- [ ] 没有任何裸色值（`#`/`rgb(`/`hsl(`），除 Hero 玻璃层
- [ ] 没有任何 emoji
- [ ] 所有 `icon()` 的名字都在 `scripts/icons.js` 里
- [ ] 没有内置演示数据，全部来自 `invoke()`
- [ ] 每个 `invoke` 都有 `try/catch`
- [ ] 每个 `subscribe` / `on` 都在 `destroy()` 里退订
- [ ] 空状态有引导按钮
- [ ] 破坏性操作有二次确认
- [ ] 键盘可达（Tab 能走完主要操作）
- [ ] 没有自绘动画 / rAF 循环
