// 后台前端「链接 → 路由」静态检查。
//
// 背景：列表页的「新建 X / 编辑 / 详情」按钮都是配置里的字符串或模板函数，
// 拼错一个前缀**不会报错**，只会被 vue-router 的 catch-all 兜走 ——
// 点下去人就到了工作台，页面本身看起来完全正常。
// 物流公司就栽过这一次：按钮指向 `/orders/logistics-companies/*`，
// 而路由注册在 `/platforms/logistics-companies/*`。
//
// 这个脚本把两边的字符串都抽出来对一遍：
//   ① `modules.ts` 注册了哪些路由；
//   ② 前端源码里出现的所有 `router.push(...)` / `createRoute` / `editRoute` / `rowRoute` / `route` / `linkTo`；
// 对不上就非零退出。
//
// 用法：node tests/ui/check-route-links.mjs

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const ROUTER = path.join(ROOT, 'apps', 'admin-vue', 'src', 'router');

const FACTORY_CHILD = {
  listRoute: 0,
  treeRoute: 0,
  configRoute: 0,
  reportRoute: 0,
  formRoute: 0,
  detailRoute: 0,
};

/** 把 `:id` / `${...}` / `{id}` 统一成 `*`，并去掉查询串与尾部斜杠。 */
function normalize(p) {
  let s = String(p);
  // 拼接式跳转（`router.push('/orders/detail/' + row.orderId)`）在尾部留一个斜杠，
  // 必须在**去掉尾斜杠之前**记下来，否则看不出它后面还要接一段 Id。
  const openEnded = /\/$/.test(s);
  // 顺序很重要：先把模板占位换成 *，再切查询串。
  // 反过来的话 `${r.a ?? r.id}` 里的 `??` 会被当成查询串起点，
  // 路径被从中间截断，于是每一条带 `??` 的编辑链接都成了「假失败」。
  s = s
    .replace(/\$\{[^}]*\}/g, '*')
    .replace(/\{[^}]*\}/g, '*')
    .split('?')[0]
    .replace(/:[A-Za-z_][\w]*/g, '*')
    .replace(/\/+$/, '')
    .replace(/\/+/g, '/');
  if (openEnded) s += '/*';
  return s;
}

/** 解析 modules.ts，返回全部已注册路由（含参数占位）。 */
function readRoutes() {
  const src = fs.readFileSync(path.join(ROUTER, 'modules.ts'), 'utf8');
  const lines = src.split(/\r?\n/);
  const routes = new Set();
  let parent = null;

  for (const line of lines) {
    const indent = line.match(/^\s*/)[0].length;
    const trimmed = line.trim();

    // 顶层路由：缩进 4 的 path: 'xxx'
    const top = indent === 4 && trimmed.match(/^path:\s*'([^']+)'/);
    if (top) {
      parent = top[1];
      routes.add(normalize(`/${parent}`));
      continue;
    }

    if (!parent) continue;

    // 子路由：工厂函数的第一个参数，或缩进 8 的 path: 'xxx'
    const factory = trimmed.match(/^(listRoute|treeRoute|configRoute|reportRoute|formRoute|detailRoute)\('([^']*)'/);
    if (factory && indent === 6) {
      const child = factory[2];
      routes.add(normalize(child ? `/${parent}/${child}` : `/${parent}`));
      continue;
    }

    const child = indent === 8 && trimmed.match(/^path:\s*'([^']+)'/);
    if (child) {
      routes.add(normalize(child[1] ? `/${parent}/${child[1]}` : `/${parent}`));
    }
  }

  return routes;
}

/** 从源码里抽出所有会用于跳转的路径字面量。 */
function readLinks() {
  const files = [];
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else if (/\.(ts|vue)$/.test(e.name)) files.push(full);
    }
  };
  walk(path.join(ROOT, 'apps', 'admin-vue', 'src'));

  const links = [];
  const patterns = [
    /createRoute:\s*'([^']+)'/g,
    // 表单保存 / 取消之后回哪个列表页（FormView 里 router.push(config.listRoute)）
    /listRoute:\s*'([^']+)'/g,
    /cancelRoute:\s*'([^']+)'/g,
    /rowRoute:\s*\([^)]*\)\s*=>\s*`([^`]+)`/g,
    /editRoute:\s*\([^)]*\)\s*=>\s*`([^`]+)`/g,
    /\broute:\s*\([^)]*\)\s*=>\s*`([^`]+)`/g,
    /linkTo:\s*\([^)]*\)\s*=>\s*\n?\s*`([^`]+)`/g,
    /router\.push\(\s*'([^']+)'/g,
    /router\.push\(\s*`([^`]+)`/g,
  ];

  for (const file of files) {
    const src = fs.readFileSync(file, 'utf8');
    for (const re of patterns) {
      for (const m of src.matchAll(re)) {
        links.push({ file: path.relative(ROOT, file), raw: m[1] });
      }
    }
  }
  return links;
}

const routes = readRoutes();
const links = readLinks();
const bad = [];

for (const link of links) {
  const p = normalize(link.raw);
  // 只检查站内绝对路径；`/gateway/...` 是接口，不是路由
  if (!p.startsWith('/') || p.startsWith('/gateway')) continue;
  if (routes.has(p)) continue;
  bad.push({ ...link, normalized: p });
}

console.log(`==> 已注册路由 ${routes.size} 条，源码里的跳转路径 ${links.length} 处`);
if (bad.length === 0) {
  console.log('==> 全部跳转路径都能匹配到已注册路由');
  process.exit(0);
}

console.error(`==> 有 ${bad.length} 处跳转路径匹配不到路由（会被 catch-all 送去工作台）：`);
for (const b of bad) {
  console.error(`    ${b.file}: ${b.raw}  →  ${b.normalized}`);
}
process.exit(1);
