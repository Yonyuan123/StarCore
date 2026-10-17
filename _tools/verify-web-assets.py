#!/usr/bin/env python3
"""
校验发布产物里的界面资源与源码完全一致。

为什么需要这个检查：

`dotnet publish` 走增量时，csproj 里 AfterTargets="Build" 的 robocopy 目标
**可能不执行**（已实测：改完 css 直接 publish，产物里还是昨天的旧文件）。
后果非常隐蔽 —— 包能启动、能显示界面，只是界面是旧版本的。
上一次发出去的 v1.0.0 就带着前一天的界面资源，人眼看不出，
只有拿两台机器对比才会发现"怎么我这边显示还是坏的"。

所以判据不能是"文件存在"，必须是**逐文件内容一致**。
这个脚本被 publish.sh 与 CI 共用，谁都不能跳过。

用法：
    python _tools/verify-web-assets.py <源码 wwwroot> <产物 wwwroot>
"""

import hashlib
import os
import sys


def digest(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def collect(root: str) -> dict[str, str]:
    """收集 root 下所有文件的相对路径（跳过 .map 源映射）。"""
    out: dict[str, str] = {}
    for dirpath, _, files in os.walk(root):
        for name in files:
            if name.endswith(".map"):
                continue
            full = os.path.join(dirpath, name)
            out[os.path.relpath(full, root).replace("\\", "/")] = full
    return out


def main() -> int:
    if len(sys.argv) < 3:
        print("用法: verify-web-assets.py <源码 wwwroot> <产物 wwwroot>", file=sys.stderr)
        return 2

    src_root, dst_root = sys.argv[1], sys.argv[2]

    if not os.path.isdir(src_root):
        print(f"::error::源码目录不存在：{src_root}")
        return 1
    if not os.path.isdir(dst_root):
        print(f"::error::产物里没有界面资源目录：{dst_root}")
        print("        （wwwroot 同步目标没执行 —— 发出去的会是个白屏空壳）")
        return 1

    src = collect(src_root)
    missing, mismatched = [], []

    for rel, path in sorted(src.items()):
        target = os.path.join(dst_root, rel)
        if not os.path.isfile(target):
            missing.append(rel)
        elif digest(path) != digest(target):
            mismatched.append(rel)

    total = len(src)
    print(f"界面资源校验：源码 {total} 个文件")
    print(f"  一致   {total - len(missing) - len(mismatched)}")
    print(f"  缺失   {len(missing)}")
    print(f"  内容旧 {len(mismatched)}")

    for rel in missing[:10]:
        print(f"    缺失  {rel}")
    for rel in mismatched[:10]:
        print(f"    旧版本 {rel}")

    if missing or mismatched:
        print()
        print("::error::产物里的界面资源与源码不一致，这份包不能发布。")
        print("        最常见原因：dotnet publish 走了增量，AfterTargets 的 robocopy 没执行。")
        print("        处理：删除输出目录后重新发布（或先跑一次 dotnet build 再 publish）。")
        return 1

    print("  ✓ 产物界面资源与源码逐文件一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
