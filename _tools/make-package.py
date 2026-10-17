#!/usr/bin/env python3
"""
把发布目录打成可分发的 zip。CI 与本地共用。

为什么不用 7z / Compress-Archive：

1) **分隔符。** 7z 在 Windows 上写 zip 条目名会用**反斜杠**
   （`StarCore-win-x64\\wwwroot\\index.html`）。这种包在 Windows 上解压看着正常，
   但在 macOS / Linux、以及不少解压工具里会被当成一个带怪名字的单文件。
   `zipfile` 写出的条目名恒用 `/`，是标准形态。

2) **顶层目录。** 加了 `--prefix` 之后，用户解压到"下载"文件夹里不会散出一堆 dll，
   而是得到一个干净的文件夹。这一条纯粹是为对方省事。

用法：
    python _tools/make-package.py <源目录> <输出 zip> [--prefix StarCore-win-x64]
"""

import os
import sys
import zipfile

# 这些是调试符号与文档，分发出去只是徒增体积。
EXCLUDE_SUFFIX = (".pdb", ".xml")
EXCLUDE_NAME = (".DS_Store", "Thumbs.db")


if hasattr(sys.stdout, "reconfigure"):
    # Windows 的 CI/终端默认编码不是 UTF-8，打印中文会直接 UnicodeEncodeError。
    # 这个脚本在中文环境下输出中文结论，输出失败比校验失败更冤枉。
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) < 2:
        print("用法: make-package.py <源目录> <输出 zip> [--prefix 名称]", file=sys.stderr)
        return 2

    src, dest = args[0], args[1]

    prefix = ""
    for i, a in enumerate(sys.argv):
        if a == "--prefix" and i + 1 < len(sys.argv):
            prefix = sys.argv[i + 1].strip("/")

    if not os.path.isdir(src):
        print(f"::error::源目录不存在：{src}")
        return 1

    entries = []
    for root, _, files in os.walk(src):
        for f in files:
            if f.endswith(EXCLUDE_SUFFIX) or f in EXCLUDE_NAME:
                continue
            full = os.path.join(root, f)
            rel = os.path.relpath(full, src).replace("\\", "/")
            entries.append((full, f"{prefix}/{rel}" if prefix else rel))

    if not entries:
        print(f"::error::源目录是空的：{src}")
        return 1

    os.makedirs(os.path.dirname(os.path.abspath(dest)) or ".", exist_ok=True)

    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for full, arc in sorted(entries):
            z.write(full, arc)

    size = os.path.getsize(dest)
    print(f"已打包 {len(entries)} 个文件 -> {dest}")
    print(f"  压缩包大小 {size / 1024 / 1024:.1f} MB")
    if prefix:
        print(f"  包内顶层目录 {prefix}/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
