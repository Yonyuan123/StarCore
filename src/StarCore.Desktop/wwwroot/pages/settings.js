/**
 * settings.js —— 「设置」分区。
 *
 * 页面组成：
 *   settings            通用：启动行为 / 下载 / 语言与区域 / 数据与隐私
 *   settings:appearance 外观：明暗模式 / 强调色 / 背景 / 动效
 *   settings:java       Java 运行时：扫描、手动添加、移除
 *   settings:about      关于：版本信息、运行环境、开源许可
 *
 * 所有设置项的当前值都从 getState().settings 读取，写入一律走 patchSettings()，
 * 因此不会出现"界面上显示的"和"实际生效的"不一致。
 */

import { invoke, on } from '../scripts/bridge.js';
import { getState, setState, subscribe, patchSettings } from '../scripts/store.js';
import {
  h, fill, icon, iconButton, button, emptyState, chip,
  toast, dialog, confirm, switchEl, segment, skeleton, menu, fmtBytes,
  selectControl as customSelect,
} from '../scripts/ui.js';
import { ACCENTS, BACKGROUNDS, MOTIONS, PERFS } from '../scripts/theme.js';

const SECTION = 'settings';

/** 重置全部设置时使用的默认值（与内核 Settings 模型的默认值一致）。 */
const DEFAULT_SETTINGS = {
  theme: 'light',
  accent: 'blue',
  background: 'aurora',
  uiMotion: 'subtle',
  uiPerf: 'high',
  language: 'zh-CN',
  keepLauncherOpen: true,
  closeToTray: false,
  launchCloseLauncher: false,
  autoUpdate: true,
  downloadSource: 'official',
  customDownloadSource: '',
  maxThreads: 32,
  proxy: '',
};

/* ===========================================================================
   通用小工具
   ========================================================================== */

/** 保存并给出轻量反馈。所有控件的变化都经这里落盘。 */
function save(patch, title = '已保存') {
  patchSettings(patch);
  toast({ title, kind: 'success', timeout: 1600 });
}

/** 一行设置：左侧标题 + 说明，右侧控件。 */
function setRow(label, desc, control) {
  return h('div', { class: 'set-row' },
    h('div', { class: 'grow' },
      h('div', { class: 'set-row__label', text: label }),
      desc ? h('div', { class: 'set-row__desc', text: desc }) : null,
    ),
    h('div', { class: 'set-row__control' }, control),
  );
}

/** 一个设置区块。 */
function setSection(title, iconName, desc, ...rows) {
  return h('div', { class: 'set-section' },
    h('div', { class: 'set-section__title' }, icon(iconName, 'md'), h('span', { text: title })),
    desc ? h('div', { class: 'set-section__desc', text: desc }) : null,
    ...rows,
  );
}

/** 通用下拉。 */
function selectControl(value, options, onChange) {
  // 委托给自绘组件：原生 <select> 的弹出列表由系统渲染，和这套界面格格不入。
  // 改在这里等于一次性把所有设置项的下拉都换掉。
  return customSelect({ value: value ?? '', options, onChange });
}

/** 让一个本来不可点的元素可被键盘触达。 */
function activatable(node, onActivate) {
  node.setAttribute('role', 'button');
  node.setAttribute('tabindex', '0');
  node.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onActivate(); }
  });
  return node;
}

/* ===========================================================================
   三选一控件
   动效强度与渲染开销都是三档，用分段控件比开关更直观，也更容易写清代价。
   ========================================================================== */

function choiceControl(current, options, onPick) {
  const box = h('div', { class: 'segment', role: 'tablist' });
  for (const o of options) {
    box.appendChild(h('button', {
      class: 'segment__btn',
      type: 'button',
      role: 'tab',
      title: o.note ?? '',
      'aria-selected': o.id === current ? 'true' : 'false',
      onClick: (e) => {
        for (const b of box.children) b.setAttribute('aria-selected', 'false');
        e.currentTarget.setAttribute('aria-selected', 'true');
        onPick(o.id);
      },
    }, h('span', { text: o.label })));
  }
  return box;
}

/* ===========================================================================
   页面：通用设置
   ========================================================================== */

function mountGeneral(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);
  fill(head,
    h('div', {},
      h('div', { class: 'page__title', text: '设置' }),
      h('div', { class: 'page__subtitle', text: '启动器的通用偏好，改动即时生效。' }),
    ),
  );

  function paint() {
    const s = getState().settings;
    const dataDir = getState().app?.dataDir ?? '';

    const openDir = () => {
      if (!dataDir) { toast({ title: '数据目录暂不可用', kind: 'warning' }); return; }
      invoke('system.openPath', { path: dataDir }).catch((e) => toast({ title: '打开失败', desc: String(e?.message ?? e), kind: 'danger' }));
    };

    const dirTag = activatable(h('span', {
      class: 'version-tag',
      title: dataDir,
      style: { cursor: 'pointer', maxWidth: '320px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
      text: dataDir || '未获取到数据目录',
      onClick: openDir,
    }), openDir);

    const threadSlider = h('input', {
      class: 'slider', type: 'range', min: '4', max: '64', step: '4',
      value: String(s.maxThreads ?? 32),
      'aria-label': '并发下载数',
    });
    const threadLabel = h('span', {
      class: 'mono',
      style: { fontSize: 'var(--fs-sm)', minWidth: '38px', textAlign: 'right' },
      text: String(s.maxThreads ?? 32),
    });
    const setPct = () => {
      const min = Number(threadSlider.min);
      const max = Number(threadSlider.max);
      const v = Number(threadSlider.value);
      threadSlider.style.setProperty('--pct', `${((v - min) / (max - min)) * 100}%`);
    };
    setPct();
    threadSlider.addEventListener('input', () => {
      threadLabel.textContent = threadSlider.value;
      setPct();
    });
    threadSlider.addEventListener('change', () => save({ maxThreads: Number(threadSlider.value) }));

    const proxyInput = h('input', {
      class: 'input', placeholder: 'http://127.0.0.1:7890',
      value: s.proxy ?? '',
      style: { width: '240px' },
      'aria-label': '代理地址',
    });
    proxyInput.addEventListener('blur', () => {
      const v = proxyInput.value.trim();
      if (v === (s.proxy ?? '')) return;
      save({ proxy: v });
    });

    const general = h('div', { class: 'page__inner', style: { maxWidth: '820px' } },
      setSection('启动行为', 'play', '控制启动器在启动游戏前后的行为。',
        setRow('启动后保持启动器打开', '游戏启动后，启动器继续在后台运行。',
          switchEl(!!s.keepLauncherOpen, (v) => save({ keepLauncherOpen: v }), { label: '启动后保持启动器打开' })),
        setRow('关闭窗口时最小化到托盘', '点击关闭按钮时隐藏到系统托盘，而不是退出。',
          switchEl(!!s.closeToTray, (v) => save({ closeToTray: v }), { label: '关闭窗口时最小化到托盘' })),
        setRow('启动完成后自动关闭启动器', '游戏进入主菜单后自动退出启动器，适合日常直接开玩。',
          switchEl(!!s.launchCloseLauncher, (v) => save({ launchCloseLauncher: v }), { label: '启动完成后自动关闭启动器' })),
        setRow('允许同一实例多开', '同时开多个同一个实例的游戏进程。两个进程会写同一个存档目录，同时游玩同一存档可能损坏进度 —— 确认需要再开。',
          switchEl(!!s.allowMultiInstance, (v) => save({ allowMultiInstance: v }), { label: '允许同一实例多开' })),
      ),

      setSection('下载', 'cloud-download', '影响版本、资源与 Java 的下载方式。',
        setRow('下载源', '镜像源在国内通常更快，但可能滞后于官方源。',
          selectControl(s.downloadSource ?? 'official', [
            { value: 'official', label: '官方源' },
            { value: 'mirror', label: '镜像源（BMCLAPI）' },
            { value: 'custom', label: '自定义' },
          ], (v) => save({ downloadSource: v }))),
        // 测速：PCL 的经典能力。不猜哪个源快，实测。
        // 探测目标是版本清单（启动时必拉的文件），结果能反映真实下载体验。
        setRow('源测速', '实测各下载源的延迟与吞吐，并自动切到最快的一个。',
          (() => {
            const box = h('div', { class: 'col', style: { gap: 'var(--sp-2)' } });
            const btn = button('开始测速', {
              variant: 'secondary', size: 'sm', icon: 'gauge',
              onClick: async () => {
                btn.disabled = true;
                box.replaceChildren(h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '正在测速（各源拉取 256KB 清单）…' }));
                try {
                  const list = await invoke('source.ping');
                  const ok = list.filter((x) => x.ok).sort((a, b) => a.latencyMs - b.latencyMs);
                  const best = ok[0];
                  box.replaceChildren(...list.map((x) => h('div', {
                    style: {
                      display: 'flex', justifyContent: 'space-between', gap: 'var(--sp-4)',
                      fontSize: 'var(--fs-xs)',
                      color: x.ok ? 'var(--text-2)' : 'var(--danger)',
                    },
                  },
                    h('span', { text: `${x.ok && best && x.name === best.name ? '★ ' : ''}${x.name}` }),
                    h('span', { class: 'mono', text: x.ok ? `${x.latencyMs} ms · ${x.kbps.toFixed(0)} KB/s` : (x.error ?? '不可达') }),
                  )));
                  if (best) {
                    // 探测结果的名称映射回设置值
                    const v = /官方/.test(best.name) ? 'official'
                            : /自定义/.test(best.name) ? 'custom'
                            : 'mirror';
                    if ((s.downloadSource ?? 'official') !== v) {
                      save({ downloadSource: v });
                      toast({
                        title: `已切换到最快的源`,
                        desc: `${best.name} · ${best.latencyMs} ms · ${best.kbps.toFixed(0)} KB/s`,
                        kind: 'success', timeout: 7000,
                      });
                    } else {
                      toast({ title: `当前源已是最快（${best.name}）`, kind: 'neutral', timeout: 5000 });
                    }
                  }
                } catch (e) {
                  box.replaceChildren(h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '测速失败：' + (e?.message ?? e) }));
                } finally {
                  btn.disabled = false;
                }
              },
            });
            return h('div', { class: 'col', style: { gap: 'var(--sp-2)' } }, btn, box);
          })()),
        setRow('并发下载数', '同时进行的下载任务数量。太高可能被下载源限速。',
          threadSlider, threadLabel),
        setRow('代理地址', '留空表示不使用代理；仅支持 HTTP / SOCKS5。',
          proxyInput),
      ),

      setSection('游戏目录', 'folder-open', '注册多个游戏目录后，个别实例可以选择"共用"其中一个（相当于关闭该实例的版本隔离）。默认隔离最安全。',
        (() => {
          function gameDirsRow() {
            const listBox = h('div', { class: 'col', style: { gap: 'var(--sp-2)', marginTop: 'var(--sp-3)' } });
            const load = async () => {
              const r = await invoke('gamedir.list').catch(() => ({ dirs: [] }));
              const dirs = r?.dirs ?? [];
              listBox.replaceChildren(
                ...(dirs.length
                  ? dirs.map((d) => h('div', {
                      class: 'row-between',
                      style: { gap: 'var(--sp-3)', padding: 'var(--sp-2) var(--sp-3)', background: 'var(--hover)', borderRadius: 'var(--r-md)' },
                    },
                      h('span', { class: 'mono truncate', style: { fontSize: 'var(--fs-xs)', flex: '1' }, text: d }),
                      iconButton('trash-2', { size: 'sm', tip: '移除', onClick: async () => {
                        await invoke('gamedir.remove', { path: d });
                        toast({ title: '已移除', kind: 'neutral', timeout: 3000 });
                        await load();
                      } }),
                    ))
                  : [h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: '还没有注册任何游戏目录' })]),
              );
            };
            load();
            return h('div', { class: 'set-row', style: { alignItems: 'flex-start' } },
              h('div', { class: 'grow' },
                h('div', { class: 'set-row__label', text: '已注册的目录' }),
                listBox,
              ),
              h('div', { class: 'set-row__control' },
                button('添加目录', { variant: 'secondary', size: 'sm', icon: 'plus', onClick: async () => {
                  const r = await invoke('system.pickFolder', { title: '选择游戏目录' }).catch(() => null);
                  if (!r?.path) return;
                  const res = await invoke('gamedir.add', { path: r.path });
                  toast({ title: res?.message ?? '已添加', kind: 'success', timeout: 4000 });
                  await load();
                } })),
            );
          }
          return gameDirsRow();
        })()),

      setSection('账户', 'key', '微软登录由启动器内置的应用身份完成，用户无需任何配置。用户名与密码只在微软官方页面上输入，启动器全程接触不到你的凭据。',
        setRow('应用身份', '已内置。可随时点右侧按钮验证与微软之间的链路是否正常。',
          button('自检登录链路', {
            variant: 'secondary', size: 'sm', icon: 'shield-check',
            onClick: async (e) => {
              const btn = e.currentTarget;
              btn.disabled = true;
              try {
                const r = await invoke('account.msProbe');
                if (r?.ok) {
                  toast({
                    title: '登录链路正常',
                    desc: `微软已受理设备码申请（用户码 ${r.userCode}）。可以直接去登录了。`,
                    kind: 'success', timeout: 9000,
                  });
                } else {
                  toast({ title: '登录链路不通', desc: r?.message ?? '未知原因', kind: 'danger', timeout: 0 });
                }
              } catch (err) {
                toast({ title: '自检失败', desc: err?.message ?? String(err), kind: 'danger', timeout: 0 });
              } finally {
                btn.disabled = false;
              }
            },
          })),
        h('div', { class: 'set-row', style: { alignItems: 'flex-start' } },
          h('div', { class: 'grow' },
            h('div', { class: 'set-row__label', text: '登录流程' }),
            h('ol', { style: { margin: 'var(--sp-4) 0 0', paddingLeft: '20px', fontSize: 'var(--fs-xs)', color: 'var(--text-3)', lineHeight: 'var(--lh-relaxed)' } },
              h('li', { text: '在「账户」页点「微软账户登录」，启动器会给出一个 8 位用户码和一个网址。' }),
              h('li', { text: '在任意设备打开 microsoft.com/link，输入用户码，用你的微软账户确认授权。' }),
              h('li', { text: '启动器会自动完成后续换票，无需再操作。' }),
              h('li', { text: '整个过程中密码只在微软页面上输入，启动器不接触、也不存储任何凭据。' }),
            ),
          ),
        ),
        h('div', { class: 'set-row', style: { alignItems: 'flex-start' } },
          h('div', { class: 'grow' },
            h('div', { class: 'set-row__label', text: '开发备忘：Mojang 的 API 允许列表' }),
            h('div', { style: { marginTop: 'var(--sp-3)', fontSize: 'var(--fs-xs)', color: 'var(--text-3)', lineHeight: 'var(--lh-relaxed)' },
              text: 'Mojang 对 Java 版游戏服务 API 实行人工允许列表：新注册的 Azure 应用在通过审核之前，'
                  + 'login_with_xbox 一律返回 403（Invalid app registration）。审核通过后无需改代码即可生效。'
                  + '申请地址：https://aka.ms/mce-reviewappid —— 尚未通过之前，微软登录会以这条错误结束，'
                  + '离线账户与其余功能不受影响。' }),
          ),
          h('div', { class: 'set-row__control' },
            button('打开申请表', {
              variant: 'secondary', size: 'sm', icon: 'external-link',
              onClick: () => invoke('system.openUrl', { url: 'https://aka.ms/mce-reviewappid' }).catch(() => {}),
            }),
          ),
        ),
      ),

      setSection('第三方皮肤库', 'shirt',
        '除了 Mojang 官方皮肤，还可以接入第三方皮肤站。留空表示只用官方皮肤。',
        setRow('皮肤站地址', '支持 LittleSkin、Blessing Skin 等站点，填站点根地址即可（例如 https://littleskin.cn）。',
          (() => {
            const input = h('input', {
              class: 'input mono', style: { width: '320px' },
              placeholder: 'https://littleskin.cn',
              value: s.skinSiteUrl ?? '',
            });
            input.addEventListener('change', () => {
              const v = input.value.trim().replace(/\/+$/, '');
              save({ skinSiteUrl: v });
              toast({
                title: v ? '已保存皮肤站' : '已清空皮肤站',
                desc: v ? '皮肤与披风会优先从该站获取。' : null,
                kind: v ? 'success' : 'neutral',
                timeout: 1800,
              });
            });
            return input;
          })()),
        h('div', { class: 'field__hint', style: { marginTop: 'var(--sp-4)' },
          text: '第三方皮肤通过 authlib-injector 在启动时注入生效，需要该站在其接口中提供皮肤与披风数据。' }),
      ),

      setSection('更新', 'download',
        'StarCore 用 GitHub Releases 作为更新源 —— 零运维、自带版本归档与变更日志，公开接口也不需要密钥。',
        setRow('更新源仓库', '格式为「用户名/仓库名」。填你自己的仓库地址即可发布自己的版本。',
          (() => {
            const input = h('input', {
              class: 'input mono', style: { width: '320px' },
              placeholder: 'owner/repo',
              value: s.updateRepo ?? '',
            });
            input.addEventListener('change', () => {
              const v = input.value.trim().replace(/^https?:\/\/github\.com\//i, '').replace(/\/+$/, '');
              save({ updateRepo: v });
              toast({ title: v ? '已保存更新源' : '已清空更新源', kind: v ? 'success' : 'neutral', timeout: 1800 });
            });
            return input;
          })()),
        setRow('检查更新', '读取最新 Release 并与当前版本比较。',
          h('div', { class: 'row', style: { gap: 'var(--sp-4)', flexWrap: 'wrap' } },
            button('立即检查', {
              variant: 'secondary', size: 'sm', icon: 'refresh-cw',
              onClick: async (e) => {
                const btn = e.currentTarget;
                btn.disabled = true;
                try {
                  const r = await invoke('app.checkUpdate');
                  if (r?.error) {
                    toast({ title: '检查更新失败', desc: r.error, kind: 'warning', timeout: 9000 });
                  } else if (r?.hasUpdate) {
                    toast({
                      title: `发现新版本 ${r.latestVersion}`,
                      desc: r.title ?? '',
                      kind: 'accent',
                      timeout: 12000,
                      action: r.htmlUrl ? { label: '查看', onClick: () => invoke('system.openUrl', { url: r.htmlUrl }).catch(() => {}) } : undefined,
                    });
                  } else {
                    toast({ title: '已是最新版本', desc: `当前 ${r?.currentVersion ?? ''}`, kind: 'success', timeout: 2600 });
                  }
                } catch (err) {
                  toast({ title: '检查更新失败', desc: err?.message ?? String(err), kind: 'danger' });
                } finally { btn.disabled = false; }
              },
            }),
            button('打开 Releases 页面', {
              variant: 'ghost', size: 'sm', icon: 'external-link',
              onClick: () => invoke('system.openUrl', {
                url: `https://github.com/${s.updateRepo ?? ''}/releases`,
              }).catch(() => {}),
            }),
          )),
      ),

      setSection('语言与区域', 'languages', null,
        setRow('界面语言', '切换界面文字语言。',
          selectControl(s.language ?? 'zh-CN', [
            { value: 'zh-CN', label: '简体中文' },
            { value: 'en-US', label: 'English' },
          ], (v) => save({ language: v }))),
      ),

      setSection('数据与隐私', 'database', '版本、实例、账户与配置都保存在数据目录中。',
        setRow('数据目录', '点击可直接在文件管理器中打开。', dirTag),
        setRow('备份与迁移', '导出的设置文件可在另一台机器上导入。',
          h('div', { class: 'row', style: { gap: 'var(--sp-4)', flexWrap: 'wrap' } },
            button('打开数据目录', { variant: 'ghost', size: 'sm', icon: 'folder-open', onClick: openDir }),
            button('导出设置', { variant: 'ghost', size: 'sm', icon: 'upload', onClick: exportSettings }),
            button('导入设置', { variant: 'ghost', size: 'sm', icon: 'download', onClick: importSettings }),
          )),
        setRow('清除缓存', '删除已下载的临时文件与校验缓存，不会影响已安装的游戏。',
          button('清除缓存', { variant: 'danger-ghost', size: 'sm', icon: 'trash-2', onClick: clearCache })),
      ),
    );

    fill(scroll, general);
  }

  async function exportSettings() {
    try {
      const r = await invoke('system.pickFolder', { title: '选择导出目录' });
      if (!r?.path) return;
      await invoke('settings.export', { dir: r.path });
      toast({ title: '设置已导出', desc: r.path, kind: 'success', timeout: 6000 });
    } catch (e) {
      toast({ title: '导出失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function importSettings() {
    try {
      const r = await invoke('system.pickFile', { title: '选择设置文件', filter: '设置文件 (*.json)|*.json' });
      if (!r?.path) return;
      const imported = await invoke('settings.import', { path: r.path });
      if (imported && typeof imported === 'object') {
        setState({ settings: { ...getState().settings, ...imported } });
      }
      toast({ title: '设置已导入', kind: 'success' });
    } catch (e) {
      toast({ title: '导入失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function clearCache() {
    const ok = await confirm({
      title: '清除缓存',
      message: '将删除缓存目录中的临时文件与校验数据。已安装的版本与实例不受影响。',
      confirmLabel: '清除',
      danger: true,
    });
    if (!ok) return;
    try {
      await invoke('system.clearCache');
      toast({ title: '缓存已清除', kind: 'success' });
    } catch (e) {
      toast({ title: '清除失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  paint();
  const off = subscribe('settings', paint);
  const off2 = subscribe('app', paint);
  return { destroy: () => { off(); off2(); } };
}

/* ===========================================================================
   页面：外观
   ========================================================================== */

function mountAppearance(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);
  fill(head,
    h('div', {},
      h('div', { class: 'page__title', text: '外观' }),
      h('div', { class: 'page__subtitle', text: '主题、强调色与背景，全部即时预览。' }),
    ),
  );

  function paint() {
    const s = getState().settings;

    // 强调色
    const swatches = h('div', { class: 'accent-swatches' });
    for (const a of ACCENTS) {
      const selected = (s.accent ?? ACCENTS[0].id) === a.id;
      const sw = h('button', {
        class: 'accent-swatch', type: 'button',
        style: { background: a.value },
        'aria-label': a.label,
        'aria-selected': selected ? 'true' : 'false',
        title: a.label,
        onClick: () => { patchSettings({ accent: a.id }); toast({ title: '已保存', kind: 'success', timeout: 1600 }); },
      });
      swatches.appendChild(sw);
    }
    const curAccent = ACCENTS.find((a) => a.id === s.accent) ?? ACCENTS[0];

    // 背景
    const picker = h('div', { class: 'bg-picker' });
    for (const b of BACKGROUNDS) {
      const selected = (s.background ?? BACKGROUNDS[0].id) === b.id;
      const thumb = h('button', {
        class: 'bg-thumb', type: 'button', title: b.label,
        'aria-label': b.label,
        'aria-selected': selected ? 'true' : 'false',
        onClick: () => { patchSettings({ background: b.id }); toast({ title: '已保存', kind: 'success', timeout: 1600 }); },
      });
      if (b.file) thumb.appendChild(h('img', { src: `assets/img/${b.file}`, alt: '', loading: 'lazy' }));
      else {
        thumb.appendChild(icon('circle-x', 'sm'));
        thumb.appendChild(h('span', { class: 'dim', style: { fontSize: 'var(--fs-micro)' }, text: '纯色' }));
      }
      picker.appendChild(thumb);
    }

    const appearance = h('div', { class: 'page__inner', style: { maxWidth: '820px' } },
      setSection('主题', 'contrast', '跟随系统会在操作系统切换深浅色时自动变化。',
        setRow('明暗模式', '选择界面的明暗偏好。',
          segment([
            { id: 'light', label: '浅色', icon: 'sun' },
            { id: 'dark', label: '深色', icon: 'moon' },
            { id: 'system', label: '跟随系统', icon: 'monitor' },
          ], s.theme ?? 'light', (id) => { patchSettings({ theme: id }); toast({ title: '已保存', kind: 'success', timeout: 1600 }); })),
      ),

      setSection('强调色', 'palette', '强调色会同步到按钮、选中态与图表。',
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, swatches),
        ),
        h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', paddingTop: 'var(--sp-5)' }, text: `当前强调色：${curAccent.label}` }),
      ),

      setSection('背景', 'image', '背景仅在首页 Hero 区域显示。',
        h('div', { class: 'set-row' },
          h('div', { class: 'grow' }, picker),
        ),
      ),

      setSection('自定义背景与音乐', 'image',
        '可以用自己的图片做背景、自己的视频做动态背景、自己的音乐做背景乐。文件会被复制进启动器的数据目录，之后移动或删除原文件都不影响。',
        mediaRow(s, 'image', '背景图片', 'PNG / JPG / WebP / GIF，最大 64 MB'),
        mediaRow(s, 'video', '背景视频', 'MP4 / WebM，最大 1 GB。设置视频后会覆盖图片背景'),
        mediaRow(s, 'music', '背景音乐', 'MP3 / OGG / WAV / M4A / FLAC，最大 128 MB'),
        setRow('播放背景音乐', '默认关闭。开启后需要先点击一下界面才会开始播放（浏览器的自动播放限制）。',
          switchEl(!!s.bgMusicEnabled, (v) => save({ bgMusicEnabled: v }), { label: '播放背景音乐' })),
        setRow('音乐音量', '建议调低一些，音乐是背景而不是主角。',
          (() => {
            const slider = h('input', {
              type: 'range', class: 'slider', min: '0', max: '100',
              value: String(Math.round((s.bgMusicVolume ?? 0.45) * 100)),
              style: { width: '200px', display: 'block' },
              'aria-label': '背景音乐音量',
            });
            const label = h('span', { class: 'mono', style: { fontSize: 'var(--fs-sm)', minWidth: '42px', textAlign: 'right' },
              text: `${Math.round((s.bgMusicVolume ?? 0.45) * 100)}%` });
            slider.addEventListener('input', () => { label.textContent = `${slider.value}%`; });
            slider.addEventListener('change', () => save({ bgMusicVolume: Number(slider.value) / 100 }));
            return h('div', { class: 'row', style: { gap: 'var(--sp-5)' } }, slider, label);
          })()),
      ),

      setSection('动效', 'zap', '控制界面过渡与动画的强度。默认「轻微」，只在关键处做淡入与位移。',
        setRow('动效强度', '「关闭」完全不做过渡，最省资源；「完整」会加入错峰入场与页面切换。',
          choiceControl(s.uiMotion ?? 'subtle', MOTIONS, (v) => save({ uiMotion: v }))),
      ),

      setSection('性能', 'gauge', '界面开销分级。若启动器检测到显卡加速不可用，会自动降到「流畅」并保持这个档位。',
        setRow('渲染开销', '「流畅」关闭毛玻璃与颗粒特效；「精细」启用全部效果，需要可用的显卡加速。',
          choiceControl(s.uiPerf ?? 'high', PERFS, (v) => save({ uiPerf: v }))),
      ),
    );

    fill(scroll, appearance);
  }

  paint();
  const off = subscribe('settings', paint);
  return { destroy: off };
}

/* ===========================================================================
   页面：Java 运行时
   ========================================================================== */

function mountJava(el) {
  const head = h('div', { class: 'page__header' });
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, head, scroll);

  let javas = null; // null 表示仍在加载
  let pendingJavaTask = null;   // 正在下载的 Java 任务 id

  // 常见需要的大版本。为什么是这几个而不是全部：这几个覆盖了
  // 1.12 及更早（8）、1.16~1.17（11/16）、1.17~1.20.4（17）、1.20.5+（21）。
  const DOWNLOADABLE = [
    { major: 8,  note: '1.12 及更早' },
    { major: 17, note: '1.17 ~ 1.20.4' },
    { major: 21, note: '1.20.5 及以上 · 推荐' },
    { major: 25, note: '最新的快照版本' },
  ];

  const downloadMenuBtn = button('下载 Java', {
    variant: 'primary', icon: 'download',
    onClick: (e) => menu(e.currentTarget, DOWNLOADABLE.map((d) => ({
      label: `Java ${d.major}`,
      hint: d.note,
      icon: 'coffee',
      onClick: () => downloadJava(d.major),
    }))),
  });

  const paintHead = () => fill(head,
    h('div', {},
      h('div', { class: 'page__title', text: 'Java 运行时' }),
      h('div', {
        class: 'page__subtitle',
        text: javas === null ? '正在检测…' : (javas.length ? `已配置 ${javas.length} 个运行时` : '还没有配置 Java'),
      }),
    ),
    h('div', { class: 'page__actions' },
      button('自动扫描', { variant: 'secondary', icon: 'search', onClick: scan }),
      // 「下载 Java」必须始终可见，不能只在"一个 Java 都没有"时才出现。
      // 最常见的场景恰恰是：机器上有 Java 8，但 1.20.6 需要 Java 21 ——
      // 那种时候列表非空，按钮却消失，用户就彻底没有出路了。
      h('div', { style: { position: 'relative' } }, downloadMenuBtn),
      button('手动添加', { variant: 'secondary', icon: 'plus', onClick: addManual }),
    ),
  );

  function paint() {
    paintHead();

    if (javas === null) {
      fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '860px' } },
        skeleton('', { height: '160px', borderRadius: 'var(--r-lg)' }),
      ));
      return;
    }

    const note = h('div', {
      class: 'set-section__desc',
      style: { marginBottom: 'var(--sp-6)' },
      text: '不同 Minecraft 版本需要不同的 Java：1.17 及以上需要 Java 17，1.20.5 及以上需要 Java 21，更早的版本用 Java 8。',
    });

    if (!javas.length) {
      fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '860px' } }, note,
        emptyState({
          icon: 'coffee',
          title: '还没有配置 Java',
          desc: '启动器可以自动扫描系统中的 Java，也可以从 Adoptium 下载。',
          actions: [
            button('自动扫描', { variant: 'primary', icon: 'search', onClick: scan }),
            button('下载 Java 21', { variant: 'secondary', icon: 'download', onClick: () => downloadJava(21) }),
          ],
        }),
      ));
      return;
    }

    const rows = h('div', { class: 'rows' },
      h('div', { class: 'rows-head' },
        h('span'), h('span', { text: '运行时' }), h('span', { text: '状态' }), h('span'),
      ),
      ...javas.map(javaRow),
    );

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '860px' } }, note, rows));
  }

  function javaRow(j) {
    const title = `${j.vendor ? `${j.vendor} ` : ''}Java ${j.major ?? j.version ?? '未知版本'}`;
    return h('div', { class: 'row-item' },
      h('div', { class: 'row-item__icon' }, icon('coffee', 'sm')),
      h('div', { style: { minWidth: '0' } },
        h('div', { class: 'row-item__name' },
          h('span', { class: 'truncate', text: title }),
          j.arch ? chip(j.arch) : null,
        ),
        h('div', { class: 'row-item__file truncate', title: j.path, text: j.path ?? '' }),
      ),
      h('div', { class: 'row-item__trail' },
        j.valid ? chip('已就绪', { variant: 'success' }) : chip('不可用', { variant: 'danger' }),
      ),
      h('div', { class: 'row-item__trail' },
        // 运行自检：文件在 ≠ 能跑（缺运行库、架构不对都会到启动游戏时才炸）。
        // 实测 java -version，把失败提前到列表页。
        iconButton('activity', {
          size: 'sm', tip: '运行自检',
          onClick: async (e) => {
            const btn = e.currentTarget;
            btn.disabled = true;
            toast({ title: '正在自检…', desc: j.path, kind: 'neutral', timeout: 4000 });
            try {
              const r = await invoke('java.test', { path: j.path });
              if (r?.ok) {
                toast({
                  title: '自检通过，可正常启动游戏',
                  desc: (r.output ?? '').split('\n').find((l) => l.trim()) ?? j.path,
                  kind: 'success', timeout: 12000,
                });
              } else {
                dialog({
                  title: '自检失败', icon: 'alert-triangle', width: 560,
                  body: h('div', { class: 'col', style: { gap: 'var(--sp-4)' } },
                    h('div', { text: `这个 Java 无法运行。原因：${r?.error ?? '进程返回非零退出码'}` }),
                    h('div', { class: 'field' },
                      h('label', { class: 'field__label', text: '原始输出' }),
                      h('pre', { class: 'mono', style: { fontSize: 'var(--fs-xs)', whiteSpace: 'pre-wrap', maxHeight: '220px', overflow: 'auto', background: 'var(--hover)', padding: 'var(--sp-3)', borderRadius: 'var(--r-md)' }, text: r?.output || '(无输出)' })),
                  ),
                  actions: [{ label: '知道了', variant: 'primary', value: 'ok' }],
                });
              }
            } catch (e2) {
              toast({ title: '自检失败', desc: e2?.message ?? String(e2), kind: 'danger' });
            } finally {
              btn.disabled = false;
            }
          },
        }),
        iconButton('trash-2', { size: 'sm', tip: '移除', onClick: () => removeJava(j) }),
      ),
    );
  }

  async function load() {
    const r = await invoke('java.list').catch(() => null);
    javas = r?.javas ?? [];
    paint();
  }

  async function scan() {
    try {
      await invoke('java.scan');
      await load();
      toast({ title: '扫描完成', kind: 'success' });
    } catch (e) {
      toast({ title: '扫描失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function addManual() {
    try {
      const r = await invoke('system.pickFile', { title: '选择 javaw.exe', filter: 'Java 可执行文件 (*.exe)|*.exe' });
      if (!r?.path) return;
      const info = await invoke('java.validate', { path: r.path });
      if (info && info.valid === false) {
        toast({ title: '该文件不是有效的 Java', desc: r.path, kind: 'warning', timeout: 7000 });
      } else {
        toast({ title: '已添加 Java 运行时', kind: 'success' });
      }
      await load();
    } catch (e) {
      toast({ title: '添加失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function removeJava(j) {
    const ok = await confirm({
      title: '移除 Java 运行时',
      message: `将从启动器的列表中移除「${j.path}」，磁盘上的文件不会被删除。`,
      confirmLabel: '移除',
      danger: true,
    });
    if (!ok) return;
    try {
      await invoke('java.remove', { path: j.path });
      await load();
      toast({ title: '已移除', kind: 'success' });
    } catch (e) {
      toast({ title: '移除失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  async function downloadJava(major) {
    try {
      const r = await invoke('java.download', { major });
      if (!r?.taskId) {
        toast({ title: '下载未能开始', desc: r?.message ?? '内核没有返回任务号。', kind: 'warning' });
        return;
      }
      // 记录 taskId，任务完成后再刷新列表 —— 直接 await 是没用的，
      // 内核把下载放在后台跑并立即返回，await 到的是启动结果而不是下载结果。
      pendingJavaTask = r.taskId;
      toast({
        title: `正在下载 Java ${major}`,
        desc: '进度显示在底部的任务中心，完成后会自动出现在列表里。',
        kind: 'accent',
        timeout: 5000,
      });
    } catch (e) {
      toast({ title: '下载失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  // 监听任务完成：下载结束后重扫一次，否则用户看到的还是旧列表，会以为没成功
  const offTask = on('task.update', (t) => {
    if (!pendingJavaTask || t?.id !== pendingJavaTask) return;
    if (t.state === 'completed' || t.state === 'failed') {
      const id = pendingJavaTask;
      pendingJavaTask = null;
      if (t.state === 'completed') {
        toast({ title: 'Java 已就绪', desc: '已加入下方列表，可直接用于启动游戏。', kind: 'success' });
        load();
      } else {
        toast({ title: 'Java 下载失败', desc: t.error ?? t.detail ?? '未知原因', kind: 'danger', timeout: 9000 });
      }
      console.debug('[settings] Java 下载任务结束', id);
    }
  });

  paint();
  load();
  return { destroy: () => { offTask(); pendingJavaTask = null; } };
}

/* ===========================================================================
   页面：关于
   ========================================================================== */

const LICENSES = [
  { name: 'Microsoft.Web.WebView2', license: 'MIT', note: '界面宿主' },
  { name: 'Lucide', license: 'MIT', note: '界面图标' },
  { name: 'Noto Sans SC', license: 'OFL-1.1', note: '中文字体' },
  { name: 'Bricolage Grotesque', license: 'OFL-1.1', note: '标题字体' },
  { name: 'JetBrains Mono', license: 'OFL-1.1', note: '等宽字体' },
];

/* ===========================================================================
   自定义媒体：一行 = 一个选择按钮 + 当前文件名 + 清除
   ========================================================================== */

const MEDIA_FILTERS = {
  image: '图片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif',
  video: '视频 (*.mp4;*.webm;*.m4v)|*.mp4;*.webm;*.m4v',
  music: '音频 (*.mp3;*.ogg;*.wav;*.m4a;*.flac;*.aac)|*.mp3;*.ogg;*.wav;*.m4a;*.flac;*.aac',
};

const MEDIA_SETTING_KEY = { image: 'bgImageUrl', video: 'bgVideoUrl', music: 'bgMusicUrl' };

function mediaRow(s, kind, title, hint) {
  const current = s[MEDIA_SETTING_KEY[kind]] ?? '';
  const name = current ? decodeURIComponent(current.split('/').pop() ?? '') : '';

  const info = h('div', {
    class: current ? 'truncate' : 'dim truncate',
    style: { fontSize: 'var(--fs-xs)', maxWidth: '260px', marginTop: 'var(--sp-3)' },
    text: current ? name : '未设置',
  });

  const pick = button(current ? '更换' : '选择文件', {
    variant: current ? 'secondary' : 'primary', size: 'sm', icon: 'folder-open',
    onClick: async () => {
      const picked = await invoke('system.pickFile', { title, filter: MEDIA_FILTERS[kind] }).catch(() => null);
      if (!picked?.path) return;

      const r = await invoke('theme.importMedia', { path: picked.path, kind }).catch(() => null);
      if (!r?.url) {
        toast({ title: '导入失败', desc: r?.message ?? '文件无法读取。', kind: 'danger' });
        return;
      }
      // 存的是导入后的虚拟地址，不是用户的原始路径
      save({ [MEDIA_SETTING_KEY[kind]]: r.url });
      toast({ title: `已设置${title}`, desc: `${r.fileName}（${fmtBytes(r.size)}）`, kind: 'success' });
    },
  });

  return setRow(title, hint,
    h('div', { class: 'col', style: { alignItems: 'flex-end' } },
      h('div', { class: 'row', style: { gap: 'var(--sp-4)' } },
        pick,
        current ? button('清除', {
          variant: 'ghost', size: 'sm', icon: 'trash-2',
          onClick: async () => {
            await invoke('theme.clearMedia', { kind }).catch(() => {});
            save({ [MEDIA_SETTING_KEY[kind]]: '' });
          },
        }) : null,
      ),
      info,
    ));
}

/* ===========================================================================
   翻译
   ---------------------------------------------------------------------------
   两条后端，界面上直接写明代价与效果，而不是让用户自己猜：

     免费机翻  —— 零配置，用的是公开接口。质量中等，专业词汇靠术语表兜住。
     AI 翻译   —— 需要填一个 OpenAI 兼容的接口。质量高很多，长文尤其明显。

   之所以默认给"免费"，是因为大多数人不会为了看一段模组介绍去注册 API Key；
   但必须让愿意配置的人一眼看到这条路，否则高质量翻译就等于不存在。
   ========================================================================== */

/** 常见服务商预设：填这一个下拉，比让用户去翻文档抄地址实际得多。 */
const TRANSLATE_PRESETS = [
  { id: 'deepseek',  label: 'DeepSeek',      endpoint: 'https://api.deepseek.com/v1/chat/completions', model: 'deepseek-chat' },
  { id: 'siliconflow', label: '硅基流动',    endpoint: 'https://api.siliconflow.cn/v1/chat/completions', model: 'Qwen/Qwen2.5-7B-Instruct' },
  { id: 'dashscope', label: '阿里云百炼',    endpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions', model: 'qwen-plus' },
  { id: 'custom',    label: '自定义',        endpoint: '', model: '' },
];

function mountTranslate(el) {
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, scroll);

  function paint() {
    const s = getState().settings ?? {};
    const isLlm = (s.translateProvider ?? 'free') === 'llm';

    const endpoint = h('input', {
      class: 'input mono', style: { width: '100%' },
      placeholder: 'https://api.deepseek.com/v1/chat/completions',
      value: s.translateEndpoint ?? '',
      'aria-label': '接口地址',
    });
    endpoint.addEventListener('blur', () => {
      const v = endpoint.value.trim();
      if (v !== (s.translateEndpoint ?? '')) save({ translateEndpoint: v });
    });

    const model = h('input', {
      class: 'input mono', style: { width: '220px' },
      placeholder: 'deepseek-chat',
      value: s.translateModel ?? '',
      'aria-label': '模型名',
    });
    model.addEventListener('blur', () => {
      const v = model.value.trim();
      if (v !== (s.translateModel ?? '')) save({ translateModel: v });
    });

    const key = h('input', {
      class: 'input mono', style: { width: '100%' }, type: 'password',
      placeholder: 'sk-...',
      value: s.translateApiKey ?? '',
      'aria-label': 'API Key',
    });
    key.addEventListener('change', () => {
      const v = key.value.trim();
      if (v !== (s.translateApiKey ?? '')) save({ translateApiKey: v });
    });

    // 选预设时把地址与模型一并填好，省掉抄文档这一步
    const preset = customSelect({
      ariaLabel: '服务商',
      placeholder: '选择服务商',
      options: TRANSLATE_PRESETS.map((x) => ({ value: x.id, label: x.label })),
      onChange: (v) => {
        const found = TRANSLATE_PRESETS.find((x) => x.id === v);
        if (!found || found.id === 'custom') return;
        save({ translateEndpoint: found.endpoint, translateModel: found.model });
        toast({ title: `已填入 ${found.label} 的接口`, desc: '再填上 API Key 就能用了。', kind: 'success' });
      },
    });

    fill(scroll, h('div', { class: 'page__inner', style: { maxWidth: '820px' } },
      setSection('翻译后端', 'languages',
        '资源详情页的「翻译为中文」按钮走哪条路径。随时可切换，改完立即生效。',
        setRow('使用方式', '免费机翻零配置即可用；AI 翻译质量明显更好，但需要一个接口密钥。',
          choiceControl(s.translateProvider ?? 'free', [
            { id: 'free', label: '免费机翻', note: '零配置' },
            { id: 'llm',  label: 'AI 翻译',  note: '质量更好' },
          ], (v) => save({ translateProvider: v }))),
      ),

      isLlm
        ? setSection('接口配置', 'key',
            '任何兼容 OpenAI 的接口都能用。密钥只保存在本机数据目录，不会写进日志或诊断报告。',
            setRow('服务商', '选一个就把地址和模型填好，只需再补密钥。',
              h('div', { style: { minWidth: '200px' } }, preset)),
            setRow('接口地址', '完整的 chat/completions 地址。',
              h('div', { style: { width: '100%', maxWidth: '520px' } }, endpoint)),
            setRow('模型名', '例如 deepseek-chat、Qwen/Qwen2.5-7B-Instruct。',
              model),
            setRow('API Key', '形如 sk- 开头的一串字符。',
              h('div', { style: { width: '100%', maxWidth: '520px' } }, key)),
          )
        : setSection('当前使用免费机翻', 'info',
            null,
            h('div', { class: 'set-row', style: { alignItems: 'flex-start' } },
              h('div', { class: 'grow' },
                h('div', {
                  style: { fontSize: 'var(--fs-sm)', color: 'var(--text-2)', lineHeight: 'var(--lh-relaxed)' },
                  text: '免费机翻用的是公开接口，不需要任何配置，但它的限制值得先说清楚：',
                }),
                h('ul', { style: { margin: 'var(--sp-4) 0 0', paddingLeft: '20px', fontSize: 'var(--fs-xs)', color: 'var(--text-3)', lineHeight: 'var(--lh-relaxed)' } },
                  h('li', { text: '长句语序偏生硬，读起来像"翻译腔"。' }),
                  h('li', { text: '每天有免费额度，热门时段可能失败。' }),
                  h('li', { text: '专有名词已经由内置术语表修正（模组 / 光影 / 整合包 等），这部分是准的。' }),
                ),
              ),
              h('div', { class: 'set-row__control' },
                button('改用 AI 翻译', {
                  variant: 'primary', size: 'sm', icon: 'sparkles',
                  onClick: () => save({ translateProvider: 'llm' }),
                }),
              ),
            ),
          ),

      setSection('术语表', 'file-text',
        '无论用哪种后端，翻译前都会先把这些词替换成固定译名，翻完再还原，避免出现「模丁」「着色器包」这类读不懂的译法。',
        h('div', { class: 'row', style: { gap: 'var(--sp-3)', flexWrap: 'wrap' } },
          ...['mod → 模组', 'modpack → 整合包', 'resource pack → 资源包', 'shader → 光影',
              'datapack → 数据包', 'vanilla → 原版', 'chunk → 区块', 'biome → 生物群系',
              'mob → 生物', 'snapshot → 快照', 'client → 客户端', 'server → 服务端']
            .map((t) => chip(t, { variant: 'outline' }))),
      ),
    ));
  }

  paint();
  const off = subscribe('settings', paint);
  return { destroy: off };
}

function mountAbout(el) {
  const scroll = h('div', { class: 'page__scroll' });
  fill(el, scroll);

  function metaRow(k, v) {
    return h('div', { class: 'meta-row' },
      h('div', { class: 'meta-row__k', text: k }),
      h('div', { class: 'meta-row__v', text: v }),
    );
  }

  function paint() {
    const app = getState().app ?? {};
    const totalGb = app.totalMemoryMb ? `${(app.totalMemoryMb / 1024).toFixed(1)} GB` : '未知';

    const mark = h('div', {
      class: 'about-mark',
      style: {
        width: '84px', height: '84px', flex: '0 0 auto',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        borderRadius: 'var(--r-xl)',
        background: 'var(--accent)',
        backgroundImage: 'linear-gradient(145deg, color-mix(in oklab, var(--accent) 92%, var(--text-on-accent)), var(--accent) 50%, var(--accent-active))',
        boxShadow: 'var(--shadow-2)',
      },
      html: '<svg width="44" height="44" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 1.9 14.15 9.85 22.1 12 14.15 14.15 12 22.1 9.85 14.15 1.9 12 9.85 9.85Z" fill="var(--accent-ink)"/></svg>',
    });

    fill(scroll, h('div', { class: 'welcome', style: { minHeight: '100%', overflow: 'visible' } },
      h('div', { class: 'welcome__inner', style: { maxWidth: '620px' } },
        mark,
        h('div', {
          style: { fontFamily: 'var(--font-display)', fontSize: 'var(--fs-h1)', fontWeight: 'var(--fw-bold)', letterSpacing: 'var(--ls-tight)' },
          text: app.name ?? 'StarCore',
        }),
        h('div', { class: 'dim', style: { fontSize: 'var(--fs-sm)' }, text: `版本 ${app.version ?? '—'} · 内核 ${app.coreVersion ?? app.version ?? '—'}` }),

        h('div', { class: 'card', style: { width: '100%', padding: 'var(--sp-7)', textAlign: 'left' } },
          h('div', { class: 'meta-table' },
            metaRow('操作系统', app.osName ?? '未知'),
            metaRow('架构', app.arch ?? '未知'),
            metaRow('数据目录', app.dataDir ?? '未知'),
            metaRow('总内存', totalGb),
          ),
        ),

        h('div', { class: 'welcome__actions' },
          button('检查更新', { variant: 'primary', icon: 'refresh-cw', onClick: checkUpdate }),
          button('开源许可', { variant: 'ghost', icon: 'file-text', onClick: showLicenses }),
        ),

        /* Mojang 的 Usage Guidelines 要求"必须在产品、列表、描述、网站及所有相关材料上
           显著标注"其指定的免责声明原文，而且必须同时说明**谁对这个产品负责**以及
           **联系方式**（并明确说 chat / 论坛链接不算有效联系方式）。
           中文版表述意思对，但审核是按原文找的 —— 所以两段都留，英文那段必须逐字保留。 */
        h('div', {
          style: {
            marginTop: 'var(--sp-5)', padding: 'var(--sp-5)',
            border: '1px solid var(--border)', borderRadius: 'var(--r-md)',
            background: 'var(--surface-sunken)', maxWidth: '60ch',
          },
        },
          h('div', {
            style: { fontSize: 'var(--fs-xs)', fontWeight: 'var(--fw-semibold)', letterSpacing: '.02em', color: 'var(--text-2)' },
            text: 'NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.',
          }),
          h('div', {
            style: { marginTop: 'var(--sp-4)', fontSize: 'var(--fs-xs)', color: 'var(--text-3)', lineHeight: 'var(--lh-relaxed)' },
            text: 'StarCore 是非官方第三方启动器，与 Mojang Studios、Microsoft 及其关联公司均无隶属、赞助或背书关系。'
                + '「Minecraft」是 Mojang Studios 的商标。本产品由 StarCore 项目独立开发与维护，'
                + '不对 Mojang 或 Microsoft 的任何产品或服务负责。'
                + '使用本产品即表示你已阅读并同意 Minecraft 最终用户许可协议（EULA）与使用准则。',
          }),
          h('div', {
            style: { marginTop: 'var(--sp-4)', fontSize: 'var(--fs-xs)', color: 'var(--text-3)', lineHeight: 'var(--lh-relaxed)' },
            text: '责任方：StarCore 项目（开源，非商业）。联系方式：见项目仓库的 Issues 页或 SECURITY.md 中的邮件地址。',
          }),
        ),
      ),
    ));
  }

  async function checkUpdate() {
    try {
      const r = await invoke('app.checkUpdate');
      if (r?.hasUpdate) {
        toast({
          title: '发现新版本',
          desc: r.version ?? r.latest ?? '',
          kind: 'accent',
          timeout: 9000,
          action: r.url ? { label: '前往下载', onClick: () => invoke('system.openUrl', { url: r.url }).catch(() => {}) } : undefined,
        });
      } else {
        toast({ title: '已是最新版本', kind: 'success' });
      }
    } catch (e) {
      toast({ title: '检查更新失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  function showLicenses() {
    const list = h('div', { class: 'col', style: { gap: '0' } });
    LICENSES.forEach((l, i) => {
      list.appendChild(h('div', {
        class: 'row-between',
        style: { gap: 'var(--sp-6)', padding: '9px 0', borderTop: i ? '1px solid var(--border)' : 'none' },
      },
        h('div', {},
          h('div', { style: { fontSize: 'var(--fs-sm)' }, text: l.name }),
          h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)' }, text: l.note }),
        ),
        h('span', { class: 'mono dim', style: { fontSize: 'var(--fs-xs)' }, text: l.license }),
      ));
    });

    dialog({
      title: '开源许可', icon: 'file-text', width: 520,
      body: h('div', {},
        h('div', { class: 'dim', style: { fontSize: 'var(--fs-xs)', marginBottom: 'var(--sp-5)' }, text: 'StarCore 依赖以下开源组件，在此致谢。' }),
        list,
      ),
      actions: [{ label: '关闭', variant: 'ghost', value: null }],
    });
  }

  paint();
  const off = subscribe('app', paint);
  return { destroy: off };
}

/* ===========================================================================
   二级面板
   ========================================================================== */

function renderPanel(el, hh, ctx) {
  const go = ctx?.go;
  const body = h('div', { class: 'panel__body' });
  const head = h('div', { class: 'panel__head' });
  const foot = h('div', { class: 'panel__foot' });
  fill(el, head, body, foot);
  fill(head, h('div', { class: 'panel__title' }, icon('settings', 'md'), h('span', { text: '设置' })));

  const navItem = (label, iconName, selected, onClick) => h('div', {
    class: 'nav-item', role: 'button', tabindex: '0',
    'aria-selected': selected ? 'true' : 'false',
    onClick,
    onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } },
  },
    icon(iconName, 'sm'),
    h('span', { class: 'grow truncate', text: label }),
  );

  function paint() {
    const sub = getState().route.sub ?? null;
    fill(body,
      h('div', { class: 'panel__group', text: '启动器' }),
      navItem('通用', 'settings', sub === null, () => go(SECTION)),
      navItem('外观', 'palette', sub === 'appearance', () => go(SECTION, 'appearance')),
      h('div', { class: 'panel__group', text: '游戏' }),
      navItem('Java 运行时', 'coffee', sub === 'java', () => go(SECTION, 'java')),
      // 「下载」的细项在通用页内，这里直接跳到该页即可
      navItem('下载', 'cloud-download', false, () => go(SECTION)),
      navItem('翻译', 'languages', sub === 'translate', () => go(SECTION, 'translate')),
      h('div', { class: 'panel__group', text: '关于' }),
      navItem('关于 StarCore', 'info', sub === 'about', () => go(SECTION, 'about')),
    );
  }

  fill(foot, button('重置全部设置', {
    variant: 'danger-ghost', size: 'md', icon: 'rotate-ccw', block: true,
    onClick: resetAll,
  }));

  async function resetAll() {
    const ok = await confirm({
      title: '重置全部设置',
      message: '所有偏好将恢复为默认值。实例、账户与已安装的游戏不受影响。',
      confirmLabel: '重置',
      danger: true,
    });
    if (!ok) return;
    try {
      const saved = await invoke('settings.set', DEFAULT_SETTINGS);
      setState({ settings: { ...getState().settings, ...DEFAULT_SETTINGS, ...(saved && typeof saved === 'object' ? saved : {}) } });
      document.documentElement.dataset.motion = 'subtle';
      toast({ title: '设置已重置', kind: 'success' });
    } catch (e) {
      toast({ title: '重置失败', desc: e?.message ?? String(e), kind: 'danger' });
    }
  }

  paint();
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
    [SECTION]: { title: '设置', icon: 'settings', mount: mountGeneral },
    [`${SECTION}:appearance`]: { title: '外观', icon: 'palette', mount: mountAppearance },
    [`${SECTION}:java`]: { title: 'Java 运行时', icon: 'coffee', mount: mountJava },
    [`${SECTION}:translate`]: { title: '翻译', icon: 'languages', mount: mountTranslate },
    [`${SECTION}:about`]: { title: '关于', icon: 'info', mount: mountAbout },
  },
};
