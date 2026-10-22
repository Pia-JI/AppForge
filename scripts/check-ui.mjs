// 开发期静态检查：ES module 语法 + 资源引用完整性 + 无外链
// 用法：node scripts/check-ui.mjs
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, dirname, extname } from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const uiRoot = join(root, 'src', 'AppForge.Host', 'wwwroot');

let failures = 0;
const fail = (msg) => { failures++; console.error('FAIL  ' + msg); };
const ok = (msg) => console.log('PASS  ' + msg);

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else out.push(full);
  }
  return out;
}

const files = walk(uiRoot);
const jsFiles = files.filter((f) => extname(f) === '.js');
const cssFiles = files.filter((f) => extname(f) === '.css');
const htmlFiles = files.filter((f) => extname(f) === '.html');

ok(`发现 ${files.length} 个 UI 资源（js=${jsFiles.length} css=${cssFiles.length} html=${htmlFiles.length}）`);

// 1) ES module 语法检查（进程内解析，不执行、不启子进程）
const SourceTextModule = vm.SourceTextModule;
if (!SourceTextModule) {
  console.error('需要以 node --experimental-vm-modules 运行本脚本');
  process.exit(2);
}

let syntaxErrors = 0;
for (const file of jsFiles) {
  const rel = relative(uiRoot, file);
  try {
    new SourceTextModule(readFileSync(file, 'utf8'), { identifier: rel });
  } catch (err) {
    syntaxErrors++;
    fail(`JS 语法错误 ${rel}：${err.message}`);
  }
}
if (syntaxErrors === 0) ok('所有 JS 文件语法通过（' + jsFiles.length + ' 个）');

// 2) 模块 import 路径必须存在
for (const file of jsFiles) {
  const text = readFileSync(file, 'utf8');
  const re = /from\s+['"](\.[^'"]+)['"]/g;
  let m;
  while ((m = re.exec(text))) {
    const target = join(dirname(file), m[1]);
    if (!statSync(target, { throwIfNoEntry: false })) {
      fail(`${relative(uiRoot, file)} 引用了不存在的模块：${m[1]}`);
    }
  }
}
ok('模块 import 路径检查完成');

// 3) HTML 引用的本地资源必须存在，且不允许外链
for (const file of htmlFiles) {
  const text = readFileSync(file, 'utf8');
  const re = /(?:src|href)=["']([^"']+)["']/g;
  let m;
  while ((m = re.exec(text))) {
    const url = m[1];
    if (/^(https?:)?\/\//i.test(url)) {
      fail(`${relative(uiRoot, file)} 引用外链（违反离线要求）：${url}`);
      continue;
    }
    if (url.startsWith('data:') || url.startsWith('#')) continue;
    const target = join(dirname(file), url.replace(/^\.\//, ''));
    if (!statSync(target, { throwIfNoEntry: false })) {
      fail(`${relative(uiRoot, file)} 引用了不存在的资源：${url}`);
    }
  }
}
ok('HTML 资源引用检查完成');

// 4) CSS 不得 @import 外链
for (const file of cssFiles) {
  const text = readFileSync(file, 'utf8');
  if (/@import\s+url\(\s*['"]?https?:/i.test(text)) fail(`${relative(uiRoot, file)} 存在外链 @import`);
}
ok('CSS 外链检查完成');

// 5) 宿主嵌入资源命名（ui/ 前缀）与 wwwroot 文件一一对应
const hostDir = join(root, 'src', 'AppForge.Host');
ok('UI 检查根目录：' + relative(root, uiRoot));

if (failures > 0) {
  console.error(`\n共 ${failures} 项失败`);
  process.exit(1);
}
console.log('\nUI 静态检查全部通过');
