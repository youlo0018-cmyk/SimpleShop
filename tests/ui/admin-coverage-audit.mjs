// 后台「每个页面每个组件每个功能」的覆盖清单生成器。
//
// 目的：深测（admin-deep-regression.mjs）是按页面手写用例的，
// 容易漏掉「新加的按钮 / 字段没人点」。这个脚本反过来做 ——
// 把每个页面上的**全部可交互控件**扫出来落成清单，
// 再与深测实际点过的动作对照，缺口就无处可藏。
//
// 用法：
//   node tests/ui/admin-coverage-audit.mjs            # 扫描并输出清单
//   node tests/ui/admin-coverage-audit.mjs --json     # 只输出 JSON

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { apiToken, collectIds, idOf, PAGES } from './non-menu-pages.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const OUT = path.join(__dirname, 'admin-coverage.json');

const BASE = process.env.ADMIN_URL || 'http://127.0.0.1:5173';
const USER = process.env.ADMIN_USER || 'codexadmin';
const PASS = process.env.ADMIN_PASS || 'Admin123456';
const jsonOnly = process.argv.includes('--json');

async function login(page) {
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForSelector('#username', { timeout: 20000 });
  await page.fill('#username', USER);
  await page.fill('#password', PASS);
  await page.click('.login__submit');
  await page.waitForSelector('.nav__link', { timeout: 25000 });
}

async function readMenu(page) {
  return page.evaluate(() => {
    const out = [];
    document.querySelectorAll('.nav__item').forEach((group) => {
      const sub = group.querySelectorAll('.nav__sublink');
      if (sub.length) {
        sub.forEach((a) => out.push({ title: a.textContent.trim(), route: a.getAttribute('href') || '' }));
      } else {
        const a = group.querySelector('.nav__link');
        if (a) out.push({ title: a.textContent.trim(), route: a.getAttribute('href') || '' });
      }
    });
    return out;
  });
}

/** 扫一页上所有可交互控件，按类型分组。 */
async function scanControls(page) {
  return page.evaluate(() => {
    const visible = (el) => {
      const r = el.getBoundingClientRect();
      const st = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none';
    };
    const label = (el) =>
      (el.textContent || '').trim() ||
      el.getAttribute('title') ||
      el.getAttribute('aria-label') ||
      el.getAttribute('placeholder') ||
      '';

    const buttons = [...document.querySelectorAll('button')]
      .filter(visible)
      .map(label)
      .filter(Boolean);

    const tabs = [...document.querySelectorAll('.el-tabs__item')].filter(visible).map(label);

    const formItems = [...document.querySelectorAll('.el-form-item')]
      .filter(visible)
      .map((item) => {
        const lb = item.querySelector('.el-form-item__label');
        const name = (lb?.textContent || '').trim();
        if (!name) return '';
        const kinds = [];
        if (item.querySelector('.el-select')) kinds.push('select');
        if (item.querySelector('textarea')) kinds.push('textarea');
        if (item.querySelector('.el-cascader')) kinds.push('cascader');
        if (item.querySelector('.el-switch')) kinds.push('switch');
        if (item.querySelector('.el-radio')) kinds.push('radio');
        if (item.querySelector('.el-checkbox')) kinds.push('checkbox');
        if (item.querySelector('.el-input-number')) kinds.push('number');
        if (item.querySelector('input:not([type=hidden])')) kinds.push('input');
        return `${name}(${kinds.join('+') || 'custom'})`;
      })
      .filter(Boolean);

    const segs = [...document.querySelectorAll('.el-segmented__item')].filter(visible).map(label);
    const tables = document.querySelectorAll('.el-table').length;
    const treeNodes = document.querySelectorAll('.el-tree-node').length;
    const uploads = document.querySelectorAll('.uploader, .image-uploader, .rich-editor').length;

    return {
      title: (document.querySelector('.head__title, .page-header__title')?.textContent || '').trim(),
      buttons: [...new Set(buttons)],
      tabs: [...new Set(tabs)],
      formItems: [...new Set(formItems)],
      segmented: [...new Set(segs)],
      tables,
      treeNodes,
      uploads,
    };
  });
}

async function main() {
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await login(page);

  const token = await apiToken(USER, PASS);
  const ids = await collectIds(token);

  const targets = [];
  for (const m of await readMenu(page)) targets.push({ name: m.title, route: m.route });
  for (const p of PAGES) {
    const id = p.key ? idOf(ids[p.key]) : null;
    if (p.route.includes('{id}') && !id) continue;
    targets.push({ name: p.title, route: p.route.replace('{id}', String(id)) });
  }

  const report = [];
  for (const t of targets) {
    const route = t.route.replace(/^#/, '');
    await page.goto(`${BASE}/#${route}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => document.querySelectorAll('.skel').length === 0, { timeout: 15000 })
      .catch(() => {});
    await page.waitForTimeout(300);
    const controls = await scanControls(page);
    report.push({ name: t.name, route, ...controls });
    if (!jsonOnly) {
      console.log(`\n${t.name}  ${route}`);
      console.log(`  标题: ${controls.title}`);
      if (controls.buttons.length) console.log(`  按钮: ${controls.buttons.join(' | ')}`);
      if (controls.tabs.length) console.log(`  页签: ${controls.tabs.join(' | ')}`);
      if (controls.segmented.length) console.log(`  分段: ${controls.segmented.join(' | ')}`);
      if (controls.formItems.length) console.log(`  字段: ${controls.formItems.join(' | ')}`);
      console.log(`  表格 ${controls.tables} / 树节点 ${controls.treeNodes} / 上传与富文本 ${controls.uploads}`);
    }
  }

  fs.writeFileSync(OUT, JSON.stringify({ base: BASE, at: new Date().toISOString(), pages: report }, null, 2));
  await browser.close();

  const totalControls = report.reduce(
    (n, p) => n + p.buttons.length + p.tabs.length + p.formItems.length + p.segmented.length, 0);
  console.log(`\n扫描 ${report.length} 个页面，可交互控件 ${totalControls} 个`);
  console.log(`清单：${path.relative(ROOT, OUT)}`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
