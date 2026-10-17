#!/usr/bin/env bash
# StarCore 发布脚本 —— 产出可分发的构建，并连同 zip 包一起校验。
#
# 默认产出**自包含**版（dist/StarCore-win-x64）：双击即用，不需要装 .NET。
# 要产出体积小的框架依赖版加 --framework-dependent。
#
# 为什么单独一个脚本，而不是在 build.sh 里加个分支：
#   build.sh 服务于"改一行就重新跑一遍"的开发循环，不关心产物体积、
#   保留 pdb 与 xml 文档；publish.sh 服务于"发出去的这一份"，
#   要清掉调试文件、验证界面资源，并把结果打成包。
#
# ⚠️ 这个脚本里最关键的不是编译，是第 6 步。
#    实测过：改完 css 直接 dotnet publish，产物里的 wwwroot 还是前一天的文件
#    （csproj 里 AfterTargets="Build" 的 robocopy 目标在增量构建下没有执行）。
#    这种包能启动、界面也能显示，只是显示的是旧界面 —— 极难发现。
#    所以这里用逐文件哈希比对，而不是"文件存在就算过"。
#
# 用法：
#   ./publish.sh                          # 自包含版 -> dist/StarCore-win-x64
#   ./publish.sh --framework-dependent    # 框架依赖版 -> dist/StarCore-win-x64-framework-dependent
#   ./publish.sh Debug                    # 指定配置

set -u

SELF="$(cd "$(dirname "$0")" && pwd)"
cd "$SELF"

CONFIG="Release"
MODE="selfcontained"

for arg in "$@"; do
  case "$arg" in
    --framework-dependent) MODE="framework" ;;
    Debug|Release)         CONFIG="$arg" ;;
    *) echo "未知参数：$arg"; exit 2 ;;
  esac
done

if [ "$MODE" = "selfcontained" ]; then
  OUT="dist/StarCore-win-x64"
  RID_ARGS=(-r win-x64 --self-contained true)
else
  OUT="dist/StarCore-win-x64-framework-dependent"
  RID_ARGS=()
fi

echo "=== 发布 $CONFIG / $MODE -> $OUT ==="

# 1) 前端检查先行。前端错误不会让 C# 编译失败，只会让应用卡在启动画面。
if ! "$SELF/check-web.sh"; then
  echo "前端检查未通过，已中止发布。"
  exit 1
fi

# 2) 释放占用输出文件的进程，否则复制一定失败
taskkill //F //T //IM StarCore.exe > /dev/null 2>&1
taskkill //F //IM msedgewebview2.exe > /dev/null 2>&1
sleep 1

# 3) 清空输出目录。
#    必须清：残留的旧 wwwroot 会让增量复制"看起来"完成了，
#    而实际上那些文件从未被更新过。
rm -rf "$OUT"

# 4) 发布
dotnet publish src/StarCore.Desktop/StarCore.Desktop.csproj \
  -c "$CONFIG" \
  "${RID_ARGS[@]+"${RID_ARGS[@]}"}" \
  -o "$OUT" \
  -p:DebugType=none \
  -p:RunAnalyzers=false \
  -m:1
STATUS=$?

if [ $STATUS -ne 0 ]; then
  echo "发布失败（exit=$STATUS）"
  exit $STATUS
fi

# 5) 去掉不该随成品发出的文件。
#    pdb 是调试符号、xml 是 API 注释，对用户无价值。
#    注意 **不能** 删 StarCore.deps.json —— 运行时靠它解析依赖。
find "$OUT" -name '*.pdb' -delete 2>/dev/null
find "$OUT" -name '*.xml' -delete 2>/dev/null

# 6) 硬校验一：界面资源与源码逐文件一致。
#    这一步是整份脚本的重点，见文件头。
if ! python _tools/verify-web-assets.py src/StarCore.Desktop/wwwroot "$OUT/wwwroot"; then
  echo
  echo "✗ 产物里的界面资源不是最新的，已中止。"
  echo "  通常是 dotnet publish 走了增量导致 robocopy 目标没执行。"
  echo "  重跑本脚本即可（第 3 步会先清空输出目录）。"
  exit 1
fi

# 7) 硬校验二：必需文件在，且运行时形态符合预期。
if [ ! -f "$OUT/StarCore.exe" ]; then
  echo "✗ 未找到 StarCore.exe，已中止。"
  exit 1
fi

python - "$OUT" "$MODE" <<'PY'
import json, os, sys
out, mode = sys.argv[1], sys.argv[2]
cfg = json.load(open(os.path.join(out, "StarCore.runtimeconfig.json"), encoding="utf-8"))["runtimeOptions"]
is_sc = "includedFrameworks" in cfg
print(f"运行时形态：{'自包含（includedFrameworks）' if is_sc else '框架依赖（frameworks）'}")
if mode == "selfcontained" and not is_sc:
    print("::error::要求自包含，但产物是框架依赖版 —— 用户双击会弹“需要安装 .NET”")
    sys.exit(1)
if mode == "framework" and is_sc:
    print("::error::要求框架依赖版，但产物带了运行时")
    sys.exit(1)
PY
if [ $? -ne 0 ]; then
  echo "✗ 运行时形态不符合预期，已中止。"
  exit 1
fi

# 8) 打包成 zip 并校验包内容（与 CI 用的是同一份逻辑）
PKG="out/StarCore-win-x64-${MODE}.zip"
[ "$MODE" = "selfcontained" ] && PKG="out/StarCore-win-x64-selfcontained.zip" || PKG="out/StarCore-win-x64-framework-dependent.zip"

python _tools/make-package.py "$OUT" "$PKG" --prefix StarCore-win-x64 || exit 1

echo
if [ "$MODE" = "selfcontained" ]; then
  python _tools/verify-package.py "$PKG" || exit 1
else
  python _tools/verify-package.py "$PKG" --framework-dependent || exit 1
fi

echo
echo "发布完成"
echo "  目录     $OUT"
echo "  可执行   $(du -h "$OUT/StarCore.exe" | cut -f1)"
echo "  整包     $(du -sh "$OUT" | cut -f1)"
echo "  压缩包   $PKG（$(du -h "$PKG" | cut -f1)）"
echo "  界面资源 $(find "$OUT/wwwroot" -type f | wc -l) 个文件，与源码一致"
if [ "$MODE" = "selfcontained" ]; then
  echo
  echo "这一份可直接分发：解压后双击 StarCore.exe，不需要装 .NET。"
else
  echo
  echo "这一份是框架依赖版，目标机器需要 .NET 10 桌面运行时。"
fi
