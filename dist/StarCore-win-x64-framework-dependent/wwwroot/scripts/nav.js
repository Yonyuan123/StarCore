/**
 * nav.js —— 导航结构。
 *
 * 三层信息架构：
 *   图标栏（rail）   —— 一级分区，全局恒定，共 7 项
 *   二级面板（panel）—— 每个分区的子视图 / 列表，由各页面自己渲染
 *   内容区（content）—— 具体页面
 *
 * 每一层都只描述"有什么"，不关心"长什么样"，渲染由 router 与各页面负责。
 */

/** 一级导航。顺序即图标栏顺序，快捷键 Ctrl+1..7。 */
export const RAIL = [
  { id: 'instances',     label: '实例',  icon: 'boxes',    hint: '游戏实例与启动' },
  { id: 'download',      label: '下载',  icon: 'download', hint: '版本与加载器安装' },
  { id: 'resources',     label: '资源',  icon: 'puzzle',   hint: '模组 · 光影 · 整合包' },
  { id: 'accounts',      label: '账户',  icon: 'user',     hint: '登录与皮肤管理' },
  { id: 'announcement',  label: '公告',  icon: 'megaphone', hint: '官方公告与更新' },
  { id: 'logs',          label: '日志',  icon: 'terminal', hint: '启动记录与崩溃分析' },
  { id: 'settings',      label: '设置',  icon: 'settings', hint: '偏好、Java 与下载' },
];

/** 分区元信息：标题用于面包屑与窗口标题。 */
export const SECTIONS = Object.fromEntries(
  RAIL.map((r) => [r.id, { id: r.id, title: r.label, icon: r.icon }]),
);

/** 资源中心的子分类（多处复用，集中定义避免漂移）。 */
export const RESOURCE_KINDS = [
  { id: 'mod',           label: '模组',   icon: 'puzzle',   modrinth: 'mod' },
  { id: 'resourcepack',  label: '资源包', icon: 'image',    modrinth: 'resourcepack' },
  { id: 'shader',        label: '光影包', icon: 'sparkles', modrinth: 'shader' },
  { id: 'datapack',      label: '数据包', icon: 'database', modrinth: 'datapack' },
  { id: 'world',         label: '世界',   icon: 'earth',    modrinth: 'world' },
  { id: 'modpack',       label: '整合包', icon: 'boxes',    modrinth: 'modpack' },
];

/** 实例内部的子页面（进入某个实例后的第二层）。 */
export const INSTANCE_TABS = [
  { id: 'overview',      label: '概览',    icon: 'layout-dashboard' },
  { id: 'worlds',        label: '存档',    icon: 'earth' },
  { id: 'mods',          label: '模组',    icon: 'puzzle' },
  { id: 'resourcepacks', label: '资源包',  icon: 'image' },
  { id: 'shaders',       label: '光影包',  icon: 'sparkles' },
  { id: 'backups',       label: '备份',    icon: 'file-archive' },
  { id: 'export',        label: '导出',    icon: 'upload' },
  { id: 'advanced',      label: '高级',    icon: 'sliders-horizontal' },
];

export function sectionTitle(id) {
  return SECTIONS[id]?.title ?? 'StarCore';
}

export function sectionIcon(id) {
  return SECTIONS[id]?.icon ?? 'box';
}
