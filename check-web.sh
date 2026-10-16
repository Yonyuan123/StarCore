#!/usr/bin/env bash
# 前端资源检查。
#
# 为什么需要独立一个脚本，而不是靠"打开看看"：
#
#   前端是 HTML/JS，模块加载失败或语法错误不会让 C# 编译失败。表现是应用
#   一直停在启动画面（因为 app.js 是入口，它 import 的任何一个页面模块炸了，
#   整个 boot() 都不会执行），而日志里什么都没有 —— 极难排查。
#
#   ⚠️ 不能用 `node --check xxx.js`：对 .js 文件 Node 走的是 CommonJS 解析，
#   括号不匹配这类错误它能过。必须复制成 .mjs 再检查，才是真正的 ESM 语法校验。
#
# 检查内容：
#   1. 所有前端模块的 ESM 语法
#   2. import 的符号确实被目标模块导出（这是白屏的第二大原因）
#   3. 引用到的图标名都在 icons.js 里存在
#   4. CSS 里用到的自定义属性都有定义
#
# 用法：./check-web.sh

set -u

ROOT="$(cd "$(dirname "$0")" && pwd)"
WWW="${ROOT}/src/StarCore.Desktop/wwwroot"
NODE="${NODE:-node}"
# 临时目录放在项目内：Windows 上 mktemp 返回的 /tmp/... 路径在删除时
# 会被安全策略拦掉，用一个固定目录更稳。
TMP="${ROOT}/.webcheck"
rm -rf "$TMP" 2>/dev/null
mkdir -p "$TMP"

FAIL=0

echo "=== 1/4 ESM 语法 ==="
count=0
for f in "${WWW}"/scripts/*.js "${WWW}"/pages/*.js; do
  [ -f "$f" ] || continue
  rel="${f#"${WWW}/"}"
  dest="${TMP}/$(echo "$rel" | tr '/' '_').mjs"
  cp "$f" "$dest"
  if ! "$NODE" --check "$dest" 2>"${TMP}/err"; then
    echo "  ✗ ${rel}"
    head -5 "${TMP}/err" | sed 's/^/      /'
    FAIL=1
  fi
  count=$((count + 1))
done
[ $FAIL -eq 0 ] && echo "  ✓ ${count} 个模块语法通过"

echo "=== 2/4 导入符号解析 ==="
"${NODE}" - "$WWW" <<'JS'
const fs = require('fs'), path = require('path');
const www = process.argv[2];
const dirs = ['scripts', 'pages'];
let bad = [], n = 0;

function exportsOf(file) {
  const s = fs.readFileSync(file, 'utf8');
  const out = new Set();
  for (const m of s.matchAll(/^export\s+(?:async\s+)?(?:function|class|const|let|var)\s+([A-Za-z_$][\w$]*)/gm)) out.add(m[1]);
  for (const m of s.matchAll(/^export\s*\{([^}]*)\}/gm)) {
    for (const p of m[1].split(',')) if (p.trim()) out.add(p.trim().split(' as ').pop().trim());
  }
  return out;
}

for (const d of dirs) {
  const dir = path.join(www, d);
  if (!fs.existsSync(dir)) continue;
  for (const name of fs.readdirSync(dir).filter((f) => f.endsWith('.js'))) {
    const file = path.join(dir, name);
    const src = fs.readFileSync(file, 'utf8');
    for (const m of src.matchAll(/import\s*\{([^}]+)\}\s*from\s*['"](\.[^'"]+)['"]/g)) {
      n++;
      const target = path.resolve(path.dirname(file), m[2]);
      if (!fs.existsSync(target)) { bad.push(`${name}: 模块不存在 ${m[2]}`); continue; }
      const avail = exportsOf(target);
      for (const sym of m[1].split(',').map((x) => x.trim().split(' as ')[0].trim()).filter(Boolean)) {
        if (!avail.has(sym)) bad.push(`${name}: 从 ${m[2]} 导入的 ${sym} 未被导出`);
      }
    }
  }
}
if (bad.length) { bad.forEach((b) => console.log('  ✗ ' + b)); process.exit(1); }
console.log(`  ✓ ${n} 条 import 全部可解析`);
JS
[ $? -ne 0 ] && FAIL=1

echo "=== 3/4 图标引用 ==="
"${NODE}" - "$WWW" <<'JS'
const fs = require('fs'), path = require('path');
const www = process.argv[2];
const iconsJs = fs.readFileSync(path.join(www, 'scripts/icons.js'), 'utf8');

/* ⚠️ 解析 ICONS 表必须对行尾不敏感。
   踩过的坑：这里原本写死 `/…\};\n/`（只认 LF），本地是 LF 所以一直没问题，
   而 CI 上 git 会按平台的 core.autocrlf 把它们检出成 CRLF ——
   于是 `};\r\n` 匹配不上，每一个 PR 都在这一步失败，报"解析不了 ICONS 表"。
   现在两种行尾都接受，并且失败时把现场打出来（而不是只说"解析不了"）。 */
function parseIcons(src) {
  const m = src.match(/export const ICONS = (\{[\s\S]*?\});\s*(?:\r?\n|$)/);
  if (m) {
    try { return JSON.parse(m[1]); } catch { /* 落到下面的兜底 */ }
  }

  // 兜底：从标记处按花括号配平截取，不依赖任何行尾假设
  const marker = 'export const ICONS = ';
  const start = src.indexOf(marker);
  if (start < 0) return null;
  const from = start + marker.length;
  let depth = 0, inStr = false, esc = false, end = -1;
  for (let i = from; i < src.length; i++) {
    const c = src[i];
    if (inStr) {
      if (esc) esc = false;
      else if (c === '\\') esc = true;
      else if (c === '"') inStr = false;
      continue;
    }
    if (c === '"') inStr = true;
    else if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) { end = i + 1; break; } }
  }
  if (end < 0) return null;
  try { return JSON.parse(src.slice(from, end)); } catch { return null; }
}

const icons = parseIcons(iconsJs);
if (!icons) {
  console.log('  ✗ 解析不了 icons.js 的 ICONS 表');
  console.log('    （文件 ' + iconsJs.length + ' 字节；开头 80 字符：'
    + JSON.stringify(iconsJs.slice(0, 80)) + '）');
  process.exit(1);
}

/* 回归护栏：解析器必须对 CRLF 同样成立。
   加这条的唯一理由是它真实发生过 —— CI 上 git 按平台的 core.autocrlf
   把文件检出成 CRLF，而本地通常是 LF，于是"只在 CI 失败"的 bug 无法在本地发现。
   这里在同一份内容上就地换行尾再解析一次，等于把 CI 的条件搬到了本地。 */
const crlf = iconsJs.replace(/\r\n/g, '\n').replace(/\n/g, '\r\n');
if (!parseIcons(crlf)) {
  console.log('  ✗ 解析器对 CRLF 行尾失效（CI 上必然挂在这一步）');
  process.exit(1);
}

const known = new Set(Object.keys(icons));

const used = new Set();

// 加进来的每一项都要想清楚：它真的不是图标名，而不是"报错了先屏蔽"。
const KNOWN_NON_ICONS = new Set(['zh-CN', 'en-US']);

for (const d of ['scripts', 'pages']) {
  const dir = path.join(www, d);
  if (!fs.existsSync(dir)) continue;
  for (const name of fs.readdirSync(dir).filter((f) => f.endsWith('.js'))) {
    if (name === 'icons.js') continue;
    const src = fs.readFileSync(path.join(dir, name), 'utf8');

    // 第一轮：字面量写法 icon('x') / icon: 'x' / Icon='x'
    for (const re of [/icon(?:Button)?\(\s*['"]([a-z0-9-]+)['"]/g, /icon:\s*['"]([a-z0-9-]+)['"]/g, /Icon=['"]([a-z0-9-]+)['"]/g]) {
      for (const mm of src.matchAll(re)) used.add(mm[1]);
    }

    // 第二轮：icon 的值是表达式（三元、拼接）时，从 `icon:` 之后一直取到
    // 这个值结束（逗号或右花括号），再从这段里提取字面量。
    //
    // 为什么必须补这一轮：曾写过
    //     icon: v.type === 'release' ? 'circle-check' : 'flask-conical'
    // 而 flask-conical 在 icons.js 里并不存在。第一轮的正则要求引号紧跟
    // `icon:`，匹配不到三元表达式，于是漏检 —— 界面上就是一排空白方块。
    //
    // 只取到值结束（而不是整行）是关键：整行会把同一对象里的
    // variant: 'danger-ghost'、class: 'section-title' 之类也当成图标名。
    for (const mm of src.matchAll(/\bicon:\s*([^,}\n]+)/g)) {
      for (const lit of mm[1].matchAll(/['"]([a-z][a-z0-9]*-[a-z0-9-]+)['"]/g)) {
        used.add(lit[1]);
      }
    }
  }
}
const missing = [...used].filter((x) => !known.has(x) && !KNOWN_NON_ICONS.has(x)).sort();
if (missing.length) { console.log('  ✗ 未收录的图标：' + missing.join(', ')); process.exit(1); }
console.log(`  ✓ 引用 ${used.size} 个图标，全部存在`);
JS
[ $? -ne 0 ] && FAIL=1

echo "=== 4/4 CSS 变量 ==="
"${NODE}" - "$WWW" <<'JS'
const fs = require('fs'), path = require('path');
const www = process.argv[2];
const defined = new Set();
const files = [];
for (const d of ['styles', 'pages', '']) {
  const dir = path.join(www, d);
  if (!fs.existsSync(dir)) continue;
  for (const f of fs.readdirSync(dir)) if (f.endsWith('.css')) files.push(path.join(dir, f));
}
for (const f of files) for (const m of fs.readFileSync(f, 'utf8').matchAll(/(--[a-z0-9-]+)\s*:/g)) defined.add(m[1]);

const missing = new Map();
for (const f of files) {
  for (const m of fs.readFileSync(f, 'utf8').matchAll(/var\((--[a-z0-9-]+)\s*(,|\))/g)) {
    if (!defined.has(m[1]) && m[2] === ')') {
      if (!missing.has(m[1])) missing.set(m[1], new Set());
      missing.get(m[1]).add(path.basename(f));
    }
  }
}
if (missing.size) {
  for (const [k, v] of missing) console.log(`  ✗ ${k}（用于 ${[...v].join(', ')}，且没有 fallback）`);
  process.exit(1);
}
console.log(`  ✓ ${defined.size} 个自定义属性，无未定义引用`);
JS
[ $? -ne 0 ] && FAIL=1

echo
if [ $FAIL -ne 0 ]; then
  echo "前端检查未通过 —— 这些问题会让应用卡在启动画面，必须先修。"
  exit 1
fi
echo "前端检查全部通过。"
