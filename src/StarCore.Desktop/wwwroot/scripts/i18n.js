/**
 * i18n.js —— 多语言支持（第 5 批附加）。
 *
 * 设计决策：
 * - **中文是源语言**：key 就是中文原文。t('实例') 在中文环境零开销直返，
 *   其它语言查表，查不到回退中文 —— 缺一条翻译界面也不会坏，
 *   翻译可以随批次逐步补齐，而不是一次性把几千条文案 key 化。
 * - 覆盖 6 种联合国官方语言：中文、英文、法文、俄文、西班牙文、阿拉伯文。
 * - 本批接入：侧栏导航 / 分区标题 / 实例子页 / 设置区标题 / 通用按钮。
 *   其余文案随后续批次逐步包进 t()。
 * - 阿拉伯语是 RTL：切换后设置 document.dir，布局方向交给 CSS（flex/grid
 *   天然跟随）；尚未做深度 RTL 适配的组件保持 LTR，可读不受影响。
 */

const zhCN = new Map(); // 源语言，直返

/** en —— English */
const en = {
  // 侧栏
  '实例': 'Instances', '游戏实例与启动': 'Game instances & launch',
  '下载': 'Download', '版本与加载器安装': 'Version & loader install',
  '资源': 'Resources', '模组 · 光影 · 整合包': 'Mods · Shaders · Modpacks',
  '账户': 'Accounts', '登录与皮肤管理': 'Login & skins',
  '公告': 'News', '官方公告与更新': 'Announcements & updates',
  '日志': 'Logs', '启动记录与崩溃分析': 'Launch log & crash analysis',
  '设置': 'Settings', '偏好、Java 与下载': 'Preferences, Java & download',
  // 实例子页
  '概览': 'Overview', '存档': 'Worlds', '模组': 'Mods',
  '资源包': 'Resource Packs', '光影包': 'Shaders', '备份': 'Backups',
  '导出': 'Export', '导出整合包': 'Export Modpack', '高级': 'Advanced', '高级设置': 'Advanced Settings',
  '列表': 'List', '新建实例': 'New Instance',
  // 通用按钮 / 操作
  '保存': 'Save', '取消': 'Cancel', '删除': 'Delete', '确认': 'Confirm',
  '重试': 'Retry', '关闭': 'Close', '打开': 'Open', '启动': 'Launch',
  '恢复默认': 'Reset to Defaults', '本地导入': 'Import Local', '在线获取': 'Browse Online',
  '打开文件夹': 'Open Folder', '在文件夹中显示': 'Show in Folder',
  '备份存档': 'Backup World', '从备份恢复': 'Restore from Backup',
  '补全缺失文件': 'Repair Missing Files', '自定义图标': 'Custom Icon',
  '隐藏实例': 'Hide Instance', '取消隐藏': 'Unhide',
  '新建实例…': 'New Instance…', '安装新版本…': 'Install New Version…',
  '管理实例（重命名 / 复制 / 删除）…': 'Manage Instances (rename / copy / delete)…',
  '未安装的实例': 'Uninstalled instances',
  '版本下载': 'Version Download',
  // 设置区标题
  '启动行为': 'Launch Behavior', '下载源': 'Download Source',
  '并发下载数': 'Concurrent Downloads', '游戏目录': 'Game Directories',
  '语言与区域': 'Language & Region', '界面语言': 'Interface Language',
  '数据与隐私': 'Data & Privacy', '数据目录': 'Data Directory',
  '备份与迁移': 'Backup & Migration', '主题': 'Theme',
  '内存': 'Memory', '最大内存': 'Maximum Memory',
  '自动选择（推荐）': 'Auto (recommended)',
  // 设置项
  '启动后保持启动器打开': 'Keep launcher open after launch',
  '关闭窗口时最小化到托盘': 'Minimize to tray on close',
  '启动完成后自动关闭启动器': 'Close launcher once game starts',
  '允许同一实例多开': 'Allow multiple instances of the same game',
  '镜像源（BMCLAPI）': 'Mirror (BMCLAPI)',
  '官方源': 'Official',
  '自定义': 'Custom',
};

/** fr —— Français */
const fr = {
  '实例': 'Instances', '游戏实例与启动': 'Instances de jeu et lancement',
  '下载': 'Télécharger', '版本与加载器安装': 'Versions et chargeurs',
  '资源': 'Ressources', '模组 · 光影 · 整合包': 'Mods · Shaders · Modpacks',
  '账户': 'Comptes', '登录与皮肤管理': 'Connexion et skins',
  '公告': 'Actualités', '官方公告与更新': 'Annonces et mises à jour',
  '日志': 'Journaux', '启动记录与崩溃分析': 'Historique et plantages',
  '设置': 'Paramètres', '偏好、Java 与下载': 'Préférences, Java, téléchargement',
  '概览': 'Aperçu', '存档': 'Sauvegardes', '模组': 'Mods',
  '资源包': 'Packs de ressources', '光影包': 'Shaders', '备份': 'Sauvegardes',
  '导出': 'Exporter', '导出整合包': 'Exporter un modpack', '高级': 'Avancé', '高级设置': 'Paramètres avancés',
  '列表': 'Liste', '新建实例': 'Nouvelle instance',
  '保存': 'Enregistrer', '取消': 'Annuler', '删除': 'Supprimer', '确认': 'Confirmer',
  '重试': 'Réessayer', '关闭': 'Fermer', '打开': 'Ouvrir', '启动': 'Lancer',
  '恢复默认': 'Réinitialiser', '本地导入': 'Importer', '在线获取': 'Parcourir en ligne',
  '打开文件夹': 'Ouvrir le dossier', '在文件夹中显示': 'Afficher dans le dossier',
  '备份存档': 'Sauvegarder le monde', '从备份恢复': 'Restaurer',
  '补全缺失文件': 'Réparer les fichiers manquants', '自定义图标': 'Icône personnalisée',
  '隐藏实例': 'Masquer', '取消隐藏': 'Afficher',
  '新建实例…': 'Nouvelle instance…', '安装新版本…': 'Installer une version…',
  '管理实例（重命名 / 复制 / 删除）…': 'Gérer les instances (renommer / copier / supprimer)…',
  '版本下载': 'Téléchargement de versions',
  '启动行为': 'Comportement de lancement', '下载源': 'Source de téléchargement',
  '并发下载数': 'Téléchargements simultanés', '游戏目录': 'Dossiers de jeu',
  '语言与区域': 'Langue et région', '界面语言': 'Langue de l\'interface',
  '数据与隐私': 'Données et confidentialité', '数据目录': 'Répertoire des données',
  '备份与迁移': 'Sauvegarde et migration', '主题': 'Thème',
  '内存': 'Mémoire', '最大内存': 'Mémoire maximale',
  '自动选择（推荐）': 'Automatique (recommandé)',
  '启动后保持启动器打开': 'Garder le lanceur ouvert',
  '关闭窗口时最小化到托盘': 'Réduire dans la zone de notification',
  '启动完成后自动关闭启动器': 'Fermer le lanceur au démarrage du jeu',
  '允许同一实例多开': 'Autoriser plusieurs instances identiques',
  '镜像源（BMCLAPI）': 'Miroir (BMCLAPI)',
  '官方源': 'Officielle', '自定义': 'Personnalisée',
};

/** ru — Русский */
const ru = {
  '实例': 'Сборки', '游戏实例与启动': 'Игровые сборки и запуск',
  '下载': 'Загрузки', '版本与加载器安装': 'Версии и загрузчики',
  '资源': 'Ресурсы', '模组 · 光影 · 整合包': 'Моды · Шейдеры · Сборки',
  '账户': 'Аккаунты', '登录与皮肤管理': 'Вход и скины',
  '公告': 'Новости', '官方公告与更新': 'Объявления и обновления',
  '日志': 'Журналы', '启动记录与崩溃分析': 'Журнал запуска и сбои',
  '设置': 'Настройки', '偏好、Java 与下载': 'Предпочтения, Java, загрузки',
  '概览': 'Обзор', '存档': 'Миры', '模组': 'Моды',
  '资源包': 'Ресурспаки', '光影包': 'Шейдеры', '备份': 'Резервные копии',
  '导出': 'Экспорт', '导出整合包': 'Экспорт сборки', '高级': 'Дополнительно', '高级设置': 'Дополнительные настройки',
  '列表': 'Список', '新建实例': 'Новая сборка',
  '保存': 'Сохранить', '取消': 'Отмена', '删除': 'Удалить', '确认': 'Подтвердить',
  '重试': 'Повторить', '关闭': 'Закрыть', '打开': 'Открыть', '启动': 'Запустить',
  '恢复默认': 'Сбросить', '本地导入': 'Импорт', '在线获取': 'Найти онлайн',
  '打开文件夹': 'Открыть папку', '在文件夹中显示': 'Показать в папке',
  '备份存档': 'Резервная копия мира', '从备份恢复': 'Восстановить',
  '补全缺失文件': 'Восстановить файлы', '自定义图标': 'Своя иконка',
  '隐藏实例': 'Скрыть', '取消隐藏': 'Показать',
  '新建实例…': 'Новая сборка…', '安装新版本…': 'Установить версию…',
  '管理实例（重命名 / 复制 / 删除）…': 'Управление сборками…',
  '版本下载': 'Загрузка версий',
  '启动行为': 'Поведение запуска', '下载源': 'Источник загрузки',
  '并发下载数': 'Параллельные загрузки', '游戏目录': 'Игровые папки',
  '语言与区域': 'Язык и регион', '界面语言': 'Язык интерфейса',
  '数据与隐私': 'Данные и приватность', '数据目录': 'Каталог данных',
  '备份与迁移': 'Резерв и перенос', '主题': 'Тема',
  '内存': 'Память', '最大内存': 'Максимум памяти',
  '自动选择（推荐）': 'Авто (рекомендуется)',
  '启动后保持启动器打开': 'Держать лаунчер открытым',
  '关闭窗口时最小化到托盘': 'Сворачивать в трей',
  '启动完成后自动关闭启动器': 'Закрывать лаунчер после запуска',
  '允许同一实例多开': 'Разрешить несколько копий сборки',
  '镜像源（BMCLAPI）': 'Зеркало (BMCLAPI)',
  '官方源': 'Официальный', '自定义': 'Свой',
};

/** es — Español */
const es = {
  '实例': 'Instancias', '游戏实例与启动': 'Instancias y lanzamiento',
  '下载': 'Descargar', '版本与加载器安装': 'Versiones y cargadores',
  '资源': 'Recursos', '模组 · 光影 · 整合包': 'Mods · Shaders · Modpacks',
  '账户': 'Cuentas', '登录与皮肤管理': 'Sesión y skins',
  '公告': 'Noticias', '官方公告与更新': 'Anuncios y actualizaciones',
  '日志': 'Registros', '启动记录与崩溃分析': 'Registros y errores',
  '设置': 'Ajustes', '偏好、Java 与下载': 'Preferencias, Java, descargas',
  '概览': 'Resumen', '存档': 'Mundos', '模组': 'Mods',
  '资源包': 'Packs de recursos', '光影包': 'Shaders', '备份': 'Copias',
  '导出': 'Exportar', '导出整合包': 'Exportar modpack', '高级': 'Avanzado', '高级设置': 'Ajustes avanzados',
  '列表': 'Lista', '新建实例': 'Nueva instancia',
  '保存': 'Guardar', '取消': 'Cancelar', '删除': 'Eliminar', '确认': 'Confirmar',
  '重试': 'Reintentar', '关闭': 'Cerrar', '打开': 'Abrir', '启动': 'Jugar',
  '恢复默认': 'Restaurar', '本地导入': 'Importar', '在线获取': 'Buscar en línea',
  '打开文件夹': 'Abrir carpeta', '在文件夹中显示': 'Mostrar en carpeta',
  '备份存档': 'Copiar mundo', '从备份恢复': 'Restaurar copia',
  '补全缺失文件': 'Reparar archivos', '自定义图标': 'Icono personalizado',
  '隐藏实例': 'Ocultar', '取消隐藏': 'Mostrar',
  '新建实例…': 'Nueva instancia…', '安装新版本…': 'Instalar versión…',
  '管理实例（重命名 / 复制 / 删除）…': 'Administrar instancias…',
  '版本下载': 'Descarga de versiones',
  '启动行为': 'Comportamiento de inicio', '下载源': 'Fuente de descarga',
  '并发下载数': 'Descargas simultáneas', '游戏目录': 'Carpetas de juego',
  '语言与区域': 'Idioma y región', '界面语言': 'Idioma de la interfaz',
  '数据与隐私': 'Datos y privacidad', '数据目录': 'Carpeta de datos',
  '备份与迁移': 'Copia y migración', '主题': 'Tema',
  '内存': 'Memoria', '最大内存': 'Memoria máxima',
  '自动选择（推荐）': 'Automático (recomendado)',
  '启动后保持启动器打开': 'Mantener el launcher abierto',
  '关闭窗口时最小化到托盘': 'Minimizar a la bandeja',
  '启动完成后自动关闭启动器': 'Cerrar el launcher al iniciar',
  '允许同一实例多开': 'Permitir instancias duplicadas',
  '镜像源（BMCLAPI）': 'Espejo (BMCLAPI)',
  '官方源': 'Oficial', '自定义': 'Personalizada',
};

/** ar — العربية */
const ar = {
  '实例': 'النسخ', '游戏实例与启动': 'نسخ اللعبة والتشغيل',
  '下载': 'تنزيل', '版本与加载器安装': 'الإصدارات والمحملات',
  '资源': 'الموارد', '模组 · 光影 · 整合包': 'المودات · الشيدرات · الحزم',
  '账户': 'الحسابات', '登录与皮肤管理': 'الدخول والعباءات',
  '公告': 'الأخبار', '官方公告与更新': 'الإعلانات والتحديثات',
  '日志': 'السجلات', '启动记录与崩溃分析': 'سجل التشغيل والأعطال',
  '设置': 'الإعدادات', '偏好、Java 与下载': 'التفضيلات وجافا والتنزيل',
  '概览': 'نظرة عامة', '存档': 'العوالم', '模组': 'المودات',
  '资源包': 'حزم الموارد', '光影包': 'الشيدرات', '备份': 'النسخ الاحتياطية',
  '导出': 'تصدير', '导出整合包': 'تصدير حزمة', '高级': 'متقدم', '高级设置': 'إعدادات متقدمة',
  '列表': 'القائمة', '新建实例': 'نسخة جديدة',
  '保存': 'حفظ', '取消': 'إلغاء', '删除': 'حذف', '确认': 'تأكيد',
  '重试': 'إعادة', '关闭': 'إغلاق', '打开': 'فتح', '启动': 'تشغيل',
  '恢复默认': 'استعادة الافتراضي', '本地导入': 'استيراد', '在线获取': 'تصفح عبر الإنترنت',
  '打开文件夹': 'فتح المجلد', '在文件夹中显示': 'إظهار في المجلد',
  '备份存档': 'نسخ العالم احتياطياً', '从备份恢复': 'استعادة نسخة',
  '补全缺失文件': 'إصلاح الملفات الناقصة', '自定义图标': 'أيقونة مخصصة',
  '隐藏实例': 'إخفاء', '取消隐藏': 'إظهار',
  '新建实例…': 'نسخة جديدة…', '安装新版本…': 'تثبيت إصدار…',
  '管理实例（重命名 / 复制 / 删除）…': 'إدارة النسخ…',
  '版本下载': 'تنزيل الإصدارات',
  '启动行为': 'سلوك التشغيل', '下载源': 'مصدر التنزيل',
  '并发下载数': 'التنزيلات المتوازية', '游戏目录': 'مجلدات اللعبة',
  '语言与区域': 'اللغة والمنطقة', '界面语言': 'لغة الواجهة',
  '数据与隐私': 'البيانات والخصوصية', '数据目录': 'مجلد البيانات',
  '备份与迁移': 'النسخ والنقل', '主题': 'المظهر',
  '内存': 'الذاكرة', '最大内存': 'أقصى ذاكرة',
  '自动选择（推荐）': 'تلقائي (موصى به)',
  '启动后保持启动器打开': 'إبقاء المشغل مفتوحاً',
  '关闭窗口时最小化到托盘': 'تصغير إلى شريط النظام',
  '启动完成后自动关闭启动器': 'إغلاق المشغل بعد بدء اللعبة',
  '允许同一实例多开': 'السماح بتشغيل النسخة أكثر من مرة',
  '镜像源（BMCLAPI）': 'مرآة (BMCLAPI)',
  '官方源': 'رسمي', '自定义': 'مخصص',
};

const TABLES = {
  'zh-CN': zhCN,
  'en-US': en,
  'fr-FR': fr,
  'ru-RU': ru,
  'es-ES': es,
  'ar-SA': ar,
};

/** 6 种联合国官方语言。value 是 settings.language 的取值。 */
export const LOCALES = [
  { value: 'zh-CN', label: '简体中文', native: '简体中文', dir: 'ltr', mc: 'zh_cn' },
  { value: 'en-US', label: 'English (United States)', native: 'English', dir: 'ltr', mc: 'en_us' },
  { value: 'fr-FR', label: 'Français', native: 'Français', dir: 'ltr', mc: 'fr_fr' },
  { value: 'ru-RU', label: 'Русский', native: 'Русский', dir: 'ltr', mc: 'ru_ru' },
  { value: 'es-ES', label: 'Español', native: 'Español', dir: 'ltr', mc: 'es_es' },
  { value: 'ar-SA', label: 'العربية', native: 'العربية', dir: 'rtl', mc: 'ar_sa' },
];

let current = 'zh-CN';

/** 初始化：从全局 state 读语言并应用方向。store.js 在启动时调用一次。 */
export function initLocale(lang) {
  current = TABLES[lang] ? lang : 'zh-CN';
  applyDirection();
}

export function getLocale() { return current; }

function applyDirection() {
  const loc = LOCALES.find((l) => l.value === current);
  document.documentElement.setAttribute('dir', loc?.dir ?? 'ltr');
}

/**
 * 翻译。key 就是中文源文案：
 * - 中文环境零开销直返；
 * - 其它语言查表，查不到回退中文（缺一条翻译界面不会坏）。
 */
export function t(text) {
  if (current === 'zh-CN' || typeof text !== 'string') return text;
  return TABLES[current]?.[text] ?? text;
}

/** MC 语言代码映射（--quickPlay 之外，options.txt 的 lang 用）。 */
export function mcLanguage(lang) {
  return LOCALES.find((l) => l.value === lang)?.mc ?? 'zh_cn';
}
