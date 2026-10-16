#!/usr/bin/env bash
# StarCore 发布脚本 —— 产出 dist/StarCore-win-x64（框架依赖版）。
#
# 为什么单独一个脚本，而不是在 build.sh 里加个分支：
#   两者目的完全不同。build.sh 服务于"改一行就重新跑一遍"的开发循环，
#   它不关心产物体积、会保留 pdb 与 xml 文档；publish.sh 服务于"发出去的这一份"，
#   要把调试文件清掉、把体积压下来，并且必须验证界面资源真的在包里。
#
# 用 ./build.sh Release run 可以得到能直接跑的产物，但那份是"开发视角的"；
# 要交给用户的那份请用本脚本。
#
# 用法：
#   ./publish.sh                 # 发布到 dist/StarCore-win-x64
#   ./publish.sh Release         # 同默认
#   ./publish.sh Debug out/tmp   # 指定配置与输出目录

set -u

CONFIG="${1:-Release}"
OUT="${2:-dist/StarCore-win-x64}"
SELF="$(cd "$(dirname "$0")" && pwd)"

cd "$SELF"

# 1) 前端检查先行。理由见 build.sh 顶部：前端错误不会让 C# 编译失败，
#    只会让应用卡在启动画面 —— 那种症状一旦发出去极难排查。
if ! "$SELF/check-web.sh"; then
  echo "前端检查未通过，已中止发布。"
  exit 1
fi

# 2) 释放占用输出文件的进程，否则复制一定失败
taskkill //F //T //IM StarCore.exe > /dev/null 2>&1
taskkill //F //IM msedgewebview2.exe > /dev/null 2>&1
sleep 1

rm -rf "$OUT"

# 3) 发布
dotnet publish src/StarCore.Desktop/StarCore.Desktop.csproj \
  -c "$CONFIG" \
  -o "$OUT" \
  -p:DebugType=none \
  -p:RunAnalyzers=false \
  -m:1
STATUS=$?

if [ $STATUS -ne 0 ]; then
  echo "发布失败（exit=$STATUS）"
  exit $STATUS
fi

# 4) 去掉不该随成品发出去的文件
#    pdb 是调试符号、xml 是 API 文档注释，对用户没有任何价值，只增加体积与噪音。
find "$OUT" -name '*.pdb' -delete 2>/dev/null
find "$OUT" -name '*.xml' -delete 2>/dev/null

# 5) 自检：界面资源必须真的在包里。
#    项目里 wwwroot 是靠 robocopy 目标同步的，一旦那个目标失效，
#    发出去的就会是一个"什么都没有的空壳"——而且它照样能启动，只是白屏。
if [ ! -f "$OUT/wwwroot/index.html" ]; then
  echo "✗ 界面资源缺失（$OUT/wwwroot/index.html 不存在），发布中止。"
  exit 1
fi
if [ ! -f "$OUT/StarCore.exe" ]; then
  echo "✗ 未找到 StarCore.exe，发布中止。"
  exit 1
fi

echo
echo "发布完成：$OUT"
echo "  可执行文件 $(du -h "$OUT/StarCore.exe" | cut -f1)"
echo "  整包大小   $(du -sh "$OUT" | cut -f1)"
echo "  界面资源   $(find "$OUT/wwwroot" -type f | wc -l) 个文件"
echo
echo "提示：这一份是**框架依赖**版本，目标机器需要安装 .NET 10 桌面运行时。"
echo "      要产出双击即用、不含运行时的自包含版本，见 .github/workflows/release.yml"
