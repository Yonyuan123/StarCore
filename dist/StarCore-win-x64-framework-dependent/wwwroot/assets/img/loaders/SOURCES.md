# 加载器标志来源

全部为**各加载器项目官方发布**的彩色标志，未做任何视觉修改。
前端映射见 `scripts/ui.js` 的 `LOADER_LOGOS`。

| 用途 | 文件 | 来源 |
|---|---|---|
| 原版 | `vanilla.png` | PCL-CE `Blocks/Grass.png`（Apache-2.0）——「原版=草方块」 |
| Fabric | `fabric.png` | fabricmc.net/assets/favicon.png（官网，帆布/织纹标志） |
| Quilt | `quilt.svg` | quiltmc.org/assets/img/logo.svg（官网，紫拼布） |
| Forge | `../blocks/loader-forge.png` | PCL-CE `Blocks/Anvil.png`（Apache-2.0，**铁砧**） |
| NeoForge | `neoforge.svg` | github.com/neoforged/Documentation → static/img/logo.svg（官方仓库，橙色狐狸） |
| OptiFine | `optifine.png` | optifine.net（官网标志，红色 OF） |
| LiteLoader | `liteloader.png` | github.com/Mumfrey/LiteLoaderInstaller → liteloader_installer_icon_32.png（官方安装器） |
| Cleanroom | `../blocks/loader-cleanroom.png` | PCL-CE（Apache-2.0）——官方无独立彩色标志 |
| LabyMod | `../blocks/loader-litemod.png` | PCL-CE（Apache-2.0） |

### 为什么 Forge 用铁砧而不是官方 logo
Forge 官网只提供横向字标（`viewBox="0 0 316 66.7"`），塞进方形图标位会缩成一条细线。
PCL 的铁砧图标是方形的、彩色的，语义上也正好对应 Forge 的「锻造」，因此采用它。

### 未能拿到官方彩色标志的两项
- **LiteLoader**：官方仅提供 16/32px 安装器图标，已取 32px 版本。
- **OptiFine**：官网未公开 logo 文件，取官网标志的 64px 版本。
两者都没有更高质量的公开官方素材。
