# 安卓版接入说明

这份文档写给将来的自己，或者说，写给要做 StarCore 安卓版的人。

**结论先说**：内核（`StarCore.Core`）已经与平台解耦，安卓版**不需要改动内核**，
只需要做三件事：实现一个 `IRuntimePlatform`、提供一个 UI 宿主、换掉进程启动方式。

---

## 一、现在的分层

```
StarCore.Core          内核：版本安装、加载器、账户、Modrinth、下载、Java 探测
                       不引用任何 UI 框架，只依赖一个平台接口
        ↑
StarCore.Desktop       桌面宿主：WPF 窗口 + WebView2 + 文件对话框 + 进程启动
```

通信是一条 JSON 消息桥，与传输方式无关：

```
JS → 宿主   { kind: "invoke", id, method, params }
宿主 → JS   { kind: "result", id, ok, data | error }
宿主 → JS   { kind: "event",  name, payload }
```

**这条协议不认识 WebView2。** 安卓版把 `postMessage` 换成
`WebView.evaluateJavascript` + `@JavascriptInterface` 即可复用整个界面。

---

## 二、要做的事

### 1. 实现 `IRuntimePlatform`

放在 `StarCore.Core/Platform/` 旁边，比如 `AndroidPlatform.cs`：

```csharp
internal sealed class AndroidPlatform : IRuntimePlatform
{
    public string Name => "android";
    public bool IsDesktop => false;          // 影响"能否开文件管理器""能否起外部进程"

    // 安卓上的 Java 运行时通常是随包分发的，没有 javaw.exe 这种东西
    public string JavaExecutable => "java";
    public string JavaExecutableFallback => "java";

    // 安卓没有"Program Files"这种约定。
    // 应该返回随包分发的运行时目录 —— 通常是应用的私有目录 + 内置的 JRE。
    public IEnumerable<(string Root, int Depth)> JavaProbeRoots()
    {
        yield return (Path.Combine(AppContext.BaseDirectory, "runtime"), 2);
    }

    // 安卓没有注册表
    public IEnumerable<string> JavaFromSystemDatabase() => Array.Empty<string>();

    public bool RevealInFileManager(string path) => false;   // 桌面概念，安卓上无意义
}
```

然后在入口注册：

```csharp
PlatformServices.Current = new AndroidPlatform();
StarCoreHost.Initialize();
```

`JavaService`、`MediaService` 等已经全部走接口，**不需要跟着改**。

### 2. 数据目录

`StarCorePaths` 默认用 `%APPDATA%`。安卓上要指到应用私有目录：

```csharp
StarCorePaths.OverrideDataRoot(context.FilesDir.AbsolutePath);
```

（这个方法已经存在，桌面版的「便携模式」就是用它实现的。）

### 3. 启动游戏的方式

这是**唯一需要真正动内核**的地方。现在 `LaunchService` 直接 `Process.Start` 一个
`javaw.exe`，安卓上完全不是这个模型：

| 桌面 | 安卓 |
|---|---|
| `Process.Start(javaw, args)` | `Runtime.exec` 或 `ProcessBuilder`（需要 JNI / 嵌入 JVM） |
| `-cp <jar 列表>` | 同样，但路径要用安卓的实际路径 |
| 子进程输出重定向到管道 | 需要自己接 `InputStream` |
| `Process.Kill()` | `Process.destroy()` |

建议的做法：把「启动进程」抽成第二个接口，比如：

```csharp
public interface IGameProcessHost
{
    Task<IGameProcess> StartAsync(string executable, IEnumerable<string> args,
                                  string workingDir, IDictionary<string,string> env,
                                  CancellationToken ct);
}
```

`LaunchService` 里的 `Process.Start` / 事件订阅 / `Kill` 三处换成这个接口的调用，
桌面实现包一层 `System.Diagnostics.Process`，安卓实现包一层 Java 的 `Process`。

**这是目前唯一还硬编码在 `LaunchService` 里的平台耦合**，做安卓版时优先处理它。

### 4. 资源访问

桌面版把 `wwwroot` 与数据目录映射到 `https://app.starcore.local/` 这个虚拟主机，
WebView2 才能用相对路径加载 HTML/CSS/JS 与背景图。

安卓上对应的是 `WebViewAssetLoader`（AndroidX）——`/app/` 映射到 assets，
`/data/` 映射到应用私有目录。`WebAssets.cs` 里的 URL 构造逻辑可以直接复用，
只要改 `VirtualHost` 常量的取值，或者干脆替换成 `file:///android_asset/`。

### 5. 界面适配

界面是为**鼠标 + 键盘 + 大屏**设计的。安卓上需要：

- 侧边导航栏 → 底部标签栏或抽屉
- `:hover` 状态没有意义，`pointerdown` 才是主路径
- 所有点击区域放大到至少 44×44 dp
- `--pad-page`、`--r-md` 这类间距令牌需要一套触屏档位
- 自绘窗口（无边框、标题栏拖拽）整块去掉，用系统状态栏

建议做法：加一个 `data-shell="mobile"` 属性挂在 `<html>` 上，
CSS 里用属性选择器覆盖布局，而不是复制一套样式。

### 6. 不要做的

- **不要**为安卓单独写一套界面。界面是零框架的原生 HTML/CSS/JS，
  设计上就是可移植的，重写等于放弃桌面版所有已修复的细节。
- **不要**分叉内核。一旦分叉，之后每个 bug 都要修两遍。
- **不要**指望安卓上能跑 Forge/NeoForge 的 installer ——
  那些 installer 是标准 Java 程序，在安卓的 JVM 上跑得起来与否取决于具体实现，
  但启动器本身不需要为此做特殊处理。

---

## 三、安卓上必然要面对的取舍

| 桌面能做 | 安卓上会怎样 | 建议 |
|---|---|---|
| 自动下载并解压 JDK | 包体巨大，且 JVM 未必能直接跑 | 随包分发一个精简 JRE，或明确不支持 |
| Forge installer 跑 Java 子进程 | 慢，且内存吃紧 | 优先支持 Fabric/Quilt（纯 profile json，不需要跑 installer） |
| 扫描全盘找 Java | 无意义 | 只用随包运行时 |
| 背景视频 | 耗电与发热 | 默认禁用 |
| 多线程下载 32 并发 | 移动网络下会被限速 | 默认降到 4 |
| 导出诊断报告到桌面 | 没有桌面 | 改成分享 Intent |

**最务实的路线**：安卓版先只支持 **Fabric / Quilt + 原版**，随包一个 JRE，
不做 Java 自动下载、不做 installer 类加载器。这样能覆盖绝大多数「在手机上玩轻量整合包」
的场景，而且完全复用现有内核。

---

## 四、当前的平台耦合清单（做安卓版前逐个确认）

| 位置 | 耦合内容 | 状态 |
|---|---|---|
| `Platform/IRuntimePlatform.cs` | Java 探测、可执行文件名、文件管理器 | ✅ 已抽象 |
| `StarCorePaths` | 数据目录位置 | ✅ 有 `OverrideDataRoot` |
| `WebAssets` | 虚拟主机名与 URL 构造 | ⚠️ 需要替换映射实现 |
| `LaunchService` | `Process.Start` 启动 Java | ❌ **唯一还需要动内核的地方** |
| `SystemActions`（Desktop） | 文件对话框、资源管理器、快捷方式 | ✅ 本来就在宿主层 |
| `RecycleBin` | Win32 `SHFileOperation` | ⚠️ 需按平台实现（当前未启用） |
| `Logger` | 文件路径 | ✅ 走 `StarCorePaths` |

---

_本文件最后更新：2026-10-16_
