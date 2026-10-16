#!/usr/bin/env bash
# 编译并运行无界面诊断工具。
#
# 为什么要删输出目录里的程序集：见 build.sh 顶部那段说明 ——
# MSBuild 的 Copy 任务在"先删后写"路径上会栈溢出崩溃
# （Interop+Kernel32.DeleteFilePrivate），改成"全新写入"就绕开了。
#
# 用法：
#   ./_tools/diag/run.sh              # 体检全部实例 + Java
#   ./_tools/diag/run.sh --java       # 只看 Java
#   ./_tools/diag/run.sh --id 9       # 只看实例 9

set -u

SELF="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="_tools/diag/bin/Debug/net10.0-windows"

cd "$SELF"

taskkill //F //T //IM StarCore.Diag.exe > /dev/null 2>&1
find "${OUT}" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.pdb' -o -name '*.exe' \) -delete 2>/dev/null

dotnet build _tools/diag/StarCore.Diag.csproj -c Debug -p:RunAnalyzers=false -m:1 > /tmp/sc-diag-build.log 2>&1
if [ $? -ne 0 ]; then
  echo "诊断工具编译失败，日志：/tmp/sc-diag-build.log"
  grep -E "error" /tmp/sc-diag-build.log | head -20
  exit 1
fi

dotnet run --project _tools/diag --no-build -- "$@"
