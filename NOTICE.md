# NOTICE — 第三方资源与许可

StarCore 自身以 [GPL-3.0](LICENSE) 发布。以下是它使用或参考的第三方内容。

---

## 一、随包分发的资源

### 1. 版本方块图标（`src/StarCore.Desktop/wwwroot/assets/img/blocks/`）

**来源：PCL-CE（Plain Craft Launcher 2 社区版）**，未做视觉修改，
仅重命名为语义名称并把调色板统一为 RGBA。

- 上游：https://github.com/PCL-Community/PCL-CE
- 许可：**Apache License 2.0**
- 原路径：`Plain Craft Launcher 2/Images/Blocks/`

| 文件 | 原文件 | 语义 |
|---|---|---|
| `version-release.png` | `Grass.png` | 草方块 —— 正式版 |
| `version-snapshot.png` | `CommandBlock.png` | 命令方块 —— 快照 |
| `version-old.png` | `CobbleStone.png` | 圆石 —— 远古 beta/alpha |
| `version-fool.png` | `GoldBlock.png` | 金块 —— 愚人节版本 |
| `version-unknown.png` | `RedstoneBlock.png` | 红石块 —— 异常或未知 |
| `loader-forge.png` | `Anvil.png` | 铁砧 —— Forge |
| `loader-optifine.png` | `GrassPath.png` | 草径 —— OptiFine |
| `loader-liteloader.png` | `Egg.png` | 蛋 —— LiteLoader |
| `loader-cleanroom.png` | `Cleanroom.png` | Cleanroom |
| `loader-litemod.png` | `LabyMod.png` | LabyMod |
| `loader-optifabric.png` | `OptiFabric.png` | OptiFabric |
| `loader-neoforge.png` | `NeoForge.png` | NeoForge |
| `loader-fabric.png` | `Fabric.png` | Fabric |
| `loader-quilt.png` | `Quilt.png` | Quilt |
| `lamp-on.png` / `lamp-off.png` | `RedstoneLampOn/Off.png` | 状态指示 |

**Apache-2.0 的署名要求已在此满足。** 派生自这些文件的分发必须保留本文件。

### 2. 加载器品牌标志（`assets/img/loaders/`）

均取自各项目**官方**发布的彩色标志，未做视觉修改，仅用于标识对应的加载器。

| 加载器 | 来源 |
|---|---|
| Fabric | `fabricmc.net/assets/favicon.png`（官网） |
| Quilt | `quiltmc.org/assets/img/logo.svg`（官网） |
| NeoForge | `github.com/neoforged/Documentation` → `static/img/logo.svg`（官方仓库） |
| OptiFine | `optifine.net` 官网标志 |
| LiteLoader | `github.com/Mumfrey/LiteLoaderInstaller` → `liteloader_installer_icon_32.png` |
| 原版 | 复用 PCL-CE 的草方块（Apache-2.0） |

各标志的商标权归各自项目所有。本项目使用它们仅用于**指示兼容性**，不表示任何形式的
隶属、赞助或背书。若某个项目希望调整使用方式，请提 issue，我们会配合。

> **Forge 使用铁砧图标而非官方 logo**：Forge 官网只提供横向字标（`viewBox="0 0 316 66.7"`），
> 塞进方形图标位会缩成一条细线。PCL-CE 的铁砧图标是方形、彩色的，语义上也正好对应
> Forge 的「锻造」，因此采用它。

### 3. 内置背景图（`assets/img/bg-*.jpg`）

StarCore 项目自行生成的程序化图像，无第三方版权。

---

## 二、代码依赖

| 依赖 | 用途 | 许可 |
|---|---|---|
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 界面宿主 | MIT |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | 皮肤解码与头像裁切 | MIT |
| [Lucide](https://lucide.dev/) | 图标（以 SVG 路径内联，非运行时依赖） | ISC |
| [Noto Sans SC](https://fonts.google.com/noto) | 中文字体（系统字体回退） | OFL-1.1 |

---

## 三、参考过的开源项目

StarCore 是**从零实现**的，没有复制以下项目的代码。但在设计阶段研读了它们的实现，
以确认某些做法在真实世界里是可行的（尤其是加载器安装的上游接口形态）：

| 项目 | 许可 | 参考了什么 |
|---|---|---|
| [PCL-CE](https://github.com/PCL-Community/PCL-CE) | Apache-2.0 | 版本方块的语义体系、实例命名规则（`1.20.1-Fabric_0.15.0`）、Forge/NeoForge installer 的处理策略 |
| [HMCL](https://github.com/HMCL-dev/HMCL) | GPL-3.0 | 加载器清单的完整枚举、Java 运行时选择策略 |
| [BlockHelm](https://github.com/BlockHelm/BlockHelm) | GPL-3.0 | 安装沙箱思路、扁平化版本 JSON 的做法 |

**Apache-2.0 与 GPL-3.0 都允许阅读后独立实现。** 本项目与其无任何代码级派生关系，
也未使用它们的商标作为自身标识。

---

## 四、上游数据来源

运行时从以下**公开接口**获取数据，这些接口的数据版权归各自所有：

- **Mojang**：`piston-meta.mojang.com`（版本清单、资源索引、Java 运行时）
- **BMCLAPI**：`bmclapi2.bangbang93.com`（社区维护的镜像）
- **FabricMC**：`meta.fabricmc.net`
- **QuiltMC**：`meta.quiltmc.org`
- **MinecraftForge**：`maven.minecraftforge.net`、`files.minecraftforge.net`
- **NeoForged**：`maven.neoforged.net`
- **Modrinth**：`api.modrinth.com`
- **MyMemory**：`api.mymemory.translated.net`（免费翻译）

数据抓取遵守各站点的公开使用条款，仅用于为用户展示与安装内容。

---

## 五、合规声明

- 本项目**不包含** Minecraft 的任何游戏文件、材质、音效或代码。
- 本项目**不提供**绕过正版验证的任何能力。
- 本项目遵守《中华人民共和国网络安全法》《中华人民共和国著作权法》等相关法律法规，
  以及 Minecraft 最终用户许可协议。
- 使用者须自行确保对所安装的内容拥有合法权利。

---

_本文件最后更新：2026-10-16_
