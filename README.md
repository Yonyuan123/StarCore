# StarCore Launcher（SCL 启动器）

> 现代化、开源的 Minecraft 启动器。

[![build](https://github.com/Yonyuan123/StarCore/actions/workflows/build.yml/badge.svg)](https://github.com/Yonyuan123/StarCore/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/Yonyuan123/StarCore?include_prereleases&label=release)](https://github.com/Yonyuan123/StarCore/releases/latest)
[![license](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)](#下载)

StarCore 是一个现代化 Minecraft 启动器。.NET 10 内核负责版本解析、下载与启动，
界面用原生 HTML/CSS/ES Module 经 WebView2 渲染，不引入任何前端框架。

---

## 下载

| 版本 | 说明 | 获取方式 |
|---|---|---|
| **自包含版**（推荐） | 解压后双击 `StarCore.exe` 即可，**无需安装任何运行时** | [Releases](https://github.com/Yonyuan123/StarCore/releases/latest) 下载 `StarCore-win-x64-selfcontained.zip` |
| **框架依赖版** | 体积小，但需要先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) | 本仓库 [`dist/StarCore-win-x64/`](dist/StarCore-win-x64) |

> 两份都是同一次构建的产物，功能完全一致，区别只在于要不要自带 .NET 运行时。
> 仓库里直接放框架依赖版，是为了让 clone 不至于背上上百 MB 的二进制。

**系统要求**：Windows 10 1809 及以上（64 位）。界面依赖 WebView2 运行时，
Windows 11 与较新的 Windows 10 已自带；若提示缺失，装一次
[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) 即可。

### 从源码构建

```bash
# 需要 .NET 10 SDK
./build.sh                # Debug 构建（含前端静态检查）
./build.sh Release run    # Release 构建并直接运行
./publish.sh              # 产出 dist/StarCore-win-x64（框架依赖版）
```

构建脚本不是必需品，但它替你做掉了三件容易踩的事：把前端静态检查挡在编译之前
（前端语法错误不会让 C# 编译失败，只会让应用卡在启动画面）、
在复制前释放占用输出文件的进程、以及规避本机 MSBuild 并行 Copy 的栈溢出崩溃。

---

## 它解决什么问题

现有的启动器要么界面陈旧、要么把简单的事做得很绕。StarCore 的取舍是：

**把「装一个游戏然后玩上」压缩成两步。**
下载页找到版本 → 点安装 → 弹窗问你要不要现在开始。不需要先建实例、再选中、再去下载。
实例是安装的产物，而不是前置条件。

**默认值必须是好的默认值。**
自动选合适的 Java、自动带上必需的前置模组、自动挑推荐的加载器版本。
用户不需要知道 Fabric API 是什么，也不需要在五个 Java 之间做选择。

**出问题时要能说清楚。**
崩溃分析给出的是「可能原因 + 依据的日志原文 + 建议怎么办」，不是一句「启动失败」。
一键导出诊断报告，求助时只需要发一个 zip。

---

## 功能

### 游戏与加载器

- 完整版本清单（正式版 / 快照 / 远古版本 / 愚人节版本），按 PCL 的方块语义区分
- 加载器：**Fabric、Quilt、Forge、NeoForge、Legacy Fabric**，可选具体版本（各 60 条）
- 一键安装即建实例，命名遵循社区习惯（`1.20.1-Fabric_0.15.0`）
- 已安装的版本直接显示「启动」按钮，不再绕到实例页
- 下载源可选官方 / BMCLAPI 镜像 / 自定义，镜像不可用时自动回落官方

### 下载

- **两级并行**：文件级并发（可调，最高 256）+ 单个大文件内部分块（HTTP Range）
- 断点续传：已存在且大小一致的文件直接跳过
- 实时进度、速度、已下载量；任务可随时取消
- 修改主线程不被阻塞（有长任务探针专门盯着这件事）

### Java 运行时

- **发现式探测**：不认目录名，只在若干根目录下找「含 `bin\java.exe` 的目录」，
  因此 `Program Files\Honor\HNPCAIService\jdk` 这种名字也能找到
- 覆盖注册表（12 个发行版键）、PATH、JAVA_HOME、`.jdks`、
  Minecraft 官方启动器的 `runtime/`、HMCL、scoop、chocolatey
- **一键自动下载** Java（Mojang 运行时清单 + Adoptium 兜底），先写临时目录再原子改名
- 启动前校验：Java 主版本不匹配时给出可操作的提示，而不是让游戏崩在半路

### 内容与资源

- Modrinth 集成：模组 / 资源包 / 光影 / 数据包 / 整合包，真实下载量与图标
- **一键下载前置依赖**：递归解析必需前置（不碰 optional），自动排成「前置在前」的顺序
- 模组图标按 SHA1 向 Modrinth 反查
- 详情页说明可**一键翻译为中文**（内置 90 条 MC 术语表）

### 体验

- 无边框窗口、自绘标题栏、原生圆角与深色边框
- 暖色调色板（陶土 + 松墨），可换强调色与背景
- **自定义背景图片 / 背景视频（MP4）/ 背景音乐（MP3）**
- 动效三档可调，默认「轻微」；检测到显卡加速不可用会自动降低渲染开销
- 全部控件自绘，没有系统原生的下拉框与对话框

### 诊断

- 崩溃分析：14+ 条规则的**可解释**引擎，每条结论都附日志原文依据
- 一键导出诊断报告（环境 / Java / 内存 / 模组清单 / 崩溃分析 / 日志）
- 界面异常自动上报到启动器日志（含文件名与行号）
- 内核调用轨迹与慢调用记录

---

## 技术栈

| 层 | 技术 |
|---|---|
| 内核 | .NET 10，`StarCore.Core`（无 UI 依赖） |
| 界面宿主 | WPF + WebView2（`StarCore.Desktop`） |
| 界面 | 原生 HTML / CSS / ES Module，零框架、零构建步骤 |
| 图标 | Lucide（掩膜着色，自动跟随主题） |

内核与界面之间是一条 JSON 消息桥：`{ kind: "invoke", id, method, params }` →
`{ kind: "result", id, ok, data }`，另有内核主动推送的 `{ kind: "event", name, payload }`。

**为什么界面不用框架**：这个项目的界面复杂度不足以让框架带来净收益，
而零构建意味着改一行 CSS 刷新就能看到效果，开发和排查都快得多。

---

## 目录结构

```
.
├── src/
│   ├── StarCore.Core/            内核：安装 / 账户 / Java / 启动 / 内容 / 下载
│   │   ├── App/                  路径、日志、JSON、版本比较
│   │   ├── Models/               跨层数据模型
│   │   ├── Platform/             IRuntimePlatform 平台抽象（安卓预留）
│   │   └── Services/             各业务服务
│   └── StarCore.Desktop/         宿主：WPF 窗口 + WebView2 + JSON 桥
│       ├── Bridge/               前端 ↔ 内核的消息桥
│       └── wwwroot/              界面本体（HTML / CSS / ES Module）
│           ├── pages/            各页面模块
│           ├── scripts/          路由、状态、UI 组件、主题
│           └── styles/           设计令牌与组件样式
├── _tools/diag/                  无界面诊断工具（刻意不加入解决方案）
├── docs/                         安卓接入说明、页面开发契约
├── dist/                         发布产物（框架依赖版）
├── build.sh                      开发构建
├── publish.sh                    发布构建
└── check-web.sh                  前端静态检查
```

**为什么 `_tools/diag` 不在 `StarCore.slnx` 里**：它是排查工具而不是产品的一部分，
不应该因为它的编译问题挡住正式构建。用 `bash _tools/diag/run.sh` 单独跑。

---

## 自检

```bash
StarCore.exe --dev-page _selftest.html    # 界面内的端到端自检
bash _tools/diag/run.sh                   # 无界面诊断（推荐，不需要点界面）
```

`_selftest.html` 是一个真实调用内核的自检页，覆盖 16 项：
一键安装、四个加载器的版本列表、Java 探测、收藏、翻译、资源作者与版本排序、
**真实分块下载 + 任务取消**、前置依赖递归解析、启动命令行、下载源、未安装实例的拒绝路径。

每条结论都会写进启动器日志（`ui.log`），所以核对结果只要：

```bash
grep "\[自检" "%APPDATA%\StarCore\logs\*_launcher.log"
```

---

## 免责声明

> ### NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

**StarCore 是非官方第三方启动器，与 Mojang Studios、Microsoft 均无隶属关系。**

- 「Minecraft」是 Mojang Studios 的商标。本项目仅用于启动用户**合法拥有**的 Minecraft。
- 本项目不包含、不分发 Minecraft 的任何游戏文件或资源。所有游戏内容均由用户在首次运行时
  从 Mojang 官方源下载。
- 本项目**不提供**任何形式的盗版、破解或绕过正版验证的功能。微软账户登录走的是
  Microsoft 官方 OAuth 流程。
- 本项目不隐藏、不修改 Minecraft 自带的任何对话框与提示（含 EULA、警告与错误信息）。
- 使用本软件即表示你已阅读并同意 [Minecraft 最终用户许可协议](https://www.minecraft.net/eula)
  与 [Minecraft 使用指南](https://www.minecraft.net/usage-guidelines)。
- 因使用本软件产生的任何后果由使用者自行承担。

**责任方与联系方式**（Minecraft 使用指南要求明确说明）：

- 责任方：StarCore 项目 —— 一个开源、非商业的独立项目。
- 联系方式：本项目仓库的 Issues 页面，或 [SECURITY.md](SECURITY.md) 中列出的邮件地址。

StarCore 与 PCL、HMCL、BlockHelm 等项目没有隶属或背书关系。

---

## 第三方资源

见 [NOTICE.md](NOTICE.md)。本项目使用了 PCL-CE 的方块图标（Apache-2.0）与
各加载器官方发布的品牌标志，均已在 `assets/img/*/SOURCES.md` 中逐项标注来源。

---

## 许可证

[GPL-3.0](LICENSE)

选择它的理由：Minecraft 启动器生态里 GPL 是事实标准，它保证衍生作品——包括二次分发的
整合包启动器——都必须继续开源。如果你要基于 StarCore 做闭源发行，请联系作者讨论另行授权。

---

## 已知限制

- **微软账户登录暂时不可用**：Mojang 对 Java 版游戏服务 API 实行**人工允许列表**制度，
  新注册的 Azure 应用在通过审核之前，`login_with_xbox` 一律返回 403
  （`Invalid app registration`）。本应用已提交申请，**通过后无需更新即可自动生效**。
  申请期间离线账户与其余全部功能不受影响。

  这不是配置问题、也没有代码层面的绕过办法 —— PCL / HMCL 能直接用，
  是因为它们早已在名单里。申请入口：<https://aka.ms/mce-reviewappid>

- 第三方皮肤库（LittleSkin / Blessing Skin 等）的登录尚未实现，当前仅支持官方皮肤。
- OptiFine 未提供公开下载直链，需用户自行提供安装器；推荐改用 Iris + Sodium 方案。
- LiteLoader 已停止维护且没有可用的自动安装方式，明确不支持。

---

## 参与

见 [CONTRIBUTING.md](CONTRIBUTING.md) 与 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
安全问题请按 [SECURITY.md](SECURITY.md) 的方式报告，不要直接开 issue。

变更历史见 [CHANGELOG.md](CHANGELOG.md)。
