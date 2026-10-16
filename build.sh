#!/usr/bin/env bash
# StarCore 构建脚本。
#
# 为什么需要它而不是直接 dotnet build：
#
#   本机环境下 MSBuild 的 Copy 任务会在「删除已有目标文件」这一步栈溢出崩溃
#   （崩溃点是 Interop+Kernel32.DeleteFilePrivate，报错信息里看不出是哪个文件，
#   会以 exit 127 直接终止整个构建）。
#
#   经实测，**唯一可靠且必要**的规避手段是：构建前先删掉输出目录里的程序集。
#   这样 Copy 任务是「全新写入」而不是「先删后写」，压根不会走到那条崩溃路径。
#
#   （曾试过设 MSBUILDDISABLEFEATURESFROMVERSION=99.0 去关闭新版特性，
#    但 MSBuild 会警告该版本号超出轮换范围并回落到 17.14 —— 等于没设，
#    只是每次构建多刷三行警告。所以不要用那个办法。）
#
#   另外用 -m:1 单进程：本机并行 MSBuild 子节点偶发早退（MSB4166）。
#
# 用法：
#   ./build.sh              # Debug 构建（含前端检查）
#   ./build.sh Release      # Release 构建
#   ./build.sh Debug run    # 构建后直接运行
#   ./build.sh Debug skip   # 跳过前端检查（仅在前端确实无关时用）

set -u

CONFIG="${1:-Debug}"
SECOND="${2:-}"
OUT="src/StarCore.Desktop/bin/${CONFIG}/net10.0-windows"
SELF="$(cd "$(dirname "$0")" && pwd)"

# 1) 前端检查必须放在最前面。
#    前端语法错误不会让 C# 编译失败，而是让应用卡在启动画面（app.js 是入口，
#    它 import 的页面模块一旦解析失败，整个 boot() 都不会执行）。
#    那种症状极难排查，所以在编译前拦住。
if [ "$SECOND" != "skip" ]; then
  if ! "$SELF/check-web.sh"; then
    echo "前端检查未通过，已中止构建。"
    exit 1
  fi
fi

# 2) 释放可能占用输出文件的进程，否则复制一定失败
taskkill //F //T //IM StarCore.exe > /dev/null 2>&1
taskkill //F //IM msedgewebview2.exe > /dev/null 2>&1
sleep 1

# 3) 清掉程序集 —— 这一步就是上面说的崩溃规避手段，不能省
find "${OUT}" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.pdb' -o -name '*.exe' \) -delete 2>/dev/null

# 4) 编译：单进程最稳
cd "$SELF"
dotnet build StarCore.slnx -c "${CONFIG}" -p:RunAnalyzers=false -m:1
STATUS=$?

if [ $STATUS -ne 0 ]; then
  echo "构建失败（exit=$STATUS）"
  exit $STATUS
fi

echo "构建完成：${OUT}/StarCore.exe"

if [ "$SECOND" = "run" ]; then
  echo "启动 StarCore…"
  ( cd "${OUT}" && ./StarCore.exe )
fi
