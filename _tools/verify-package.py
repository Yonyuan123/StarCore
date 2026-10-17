#!/usr/bin/env python3
"""
校验发布压缩包的内容。CI 与本地共用同一份判定逻辑。

为什么要有这个脚本：

1) **它必须与打包器无关。** 最初的实现是 `7z l <zip> | grep 'wwwroot/index.html'`，
   在 CI 上必然失败 —— 7z 在 Windows 下列出的条目名用**反斜杠**
   （`wwwroot\\index.html`），正斜杠的 grep 永远匹配不上。
   现在改为直接用 zipfile 读条目名：标准 zip 的条目名恒用 `/`，
   与打包它的是 7z、Compress-Archive 还是别的工具无关。
   （保险起见仍会把 `\\` 归一化，容忍非标准打包器。）

2) **它要能挡住"发错版本"。** 自包含版和框架依赖版长得几乎一样，
   差别只在有没有那一百多兆运行时。发错了，用户下载解压后双击会弹
   「需要安装 .NET」—— 而这类错误只在"换了一台没装 SDK 的机器"时才暴露，
   非常容易漏。所以这里显式断言运行时特征文件的存在。

用法：
    python _tools/verify-package.py <zip>                            # 按自包含版校验
    python _tools/verify-package.py <zip> --framework-dependent      # 按框架依赖版校验
"""

import os
import sys
import zipfile

# 包内必须存在的条目。用"路径尾部"匹配，因为打包时可以加顶层目录前缀。
REQUIRED = [
    "StarCore.exe",
    "StarCore.Core.dll",
    "wwwroot/index.html",
    "wwwroot/scripts/app.js",
]

# 自包含版的判据：这些是 .NET 运行时本体，框架依赖版里不会有。任一命中即为自包含。
RUNTIME_MARKERS = ["coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll"]

# 一个能跑起来的启动器不可能只有这几个文件。
MIN_ENTRIES = 60


def normalize(name: str) -> str:
    """统一路径分隔符。标准 zip 用 /，但容忍打包器写成 \\。"""
    return name.replace("\\", "/").lstrip("./")


if hasattr(sys.stdout, "reconfigure"):
    # Windows 的 CI/终端默认编码不是 UTF-8，打印中文会直接 UnicodeEncodeError。
    # 这个脚本在中文环境下输出中文结论，输出失败比校验失败更冤枉。
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print("用法: verify-package.py <zip 路径> [--framework-dependent]", file=sys.stderr)
        return 2

    path = args[0]
    framework_dependent = "--framework-dependent" in sys.argv[2:]

    if not os.path.isfile(path):
        print(f"::error::找不到压缩包：{path}")
        return 1

    size_mb = os.path.getsize(path) / 1024 / 1024

    try:
        with zipfile.ZipFile(path) as z:
            names = [normalize(n) for n in z.namelist() if not n.endswith("/")]
    except zipfile.BadZipFile as ex:
        print(f"::error::不是有效的 zip：{path}（{ex}）")
        return 1

    def has(need: str) -> bool:
        """按路径尾部匹配 —— 包内可能有顶层目录前缀。"""
        tail = "/" + need
        return any(n == need or n.endswith(tail) for n in names)

    errors: list[str] = []

    print(f"压缩包   {os.path.basename(path)}")
    print(f"大小     {size_mb:.1f} MB")
    print(f"条目数   {len(names)}")

    # --- 必需文件 ---
    print("\n必需内容：")
    for need in REQUIRED:
        if has(need):
            print(f"  ✓ {need}")
        else:
            print(f"  ✗ {need}  ← 缺失")
            errors.append(f"缺少 {need}")

    # --- 分隔符健康检查：出现反斜杠说明打包器不给跨平台面子 ---
    bs = [n for n in names if "\\" in n]
    if bs:
        errors.append(f"有 {len(bs)} 个条目名带反斜杠，非标准 zip，macOS/Linux 解压会出错")

    # --- 体积与条目数下限 ---
    if len(names) < MIN_ENTRIES:
        errors.append(f"条目数只有 {len(names)}，少于 {MIN_ENTRIES}，包不完整")

    # --- 自包含 / 框架依赖 的判据 ---
    found_markers = [m for m in RUNTIME_MARKERS if has(m)]
    is_self_contained = len(found_markers) > 0

    print("\n运行时：")
    if is_self_contained:
        print(f"  ✓ 检测到运行时本体：{', '.join(found_markers[:3])}")
    else:
        print("  · 未检测到运行时本体（框架依赖版）")

    if framework_dependent:
        # 框架依赖版的期望是反过来的：本就不该带运行时，带了说明构建参数没生效。
        if is_self_contained:
            errors.append("声明为框架依赖版，却带上了运行时文件 —— 构建参数可能没生效")
        if size_mb > 30:
            errors.append(f"框架依赖版不该有 {size_mb:.1f} MB，体积异常")
    else:
        # 默认按自包含版校验：发给普通玩家的那一份必须开箱即用。
        if not is_self_contained:
            errors.append(
                "这是要发给用户的包，却没有 .NET 运行时 —— "
                "用户解压双击会弹「需要安装 .NET」。请确认 publish 带了 --self-contained true"
            )
        if size_mb < 30:
            errors.append(f"自包含版不可能只有 {size_mb:.1f} MB，打包步骤有问题")

    print()
    if errors:
        for e in errors:
            print(f"::error::{e}")
        print(f"校验失败：{len(errors)} 项问题")
        return 1

    print("校验通过：包内容完整，可直接分发。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
