// 后台 UI 回归：登录后逐个点击侧边栏，每个页面截图 + 收集控制台错误与失败请求。
//
// 为什么**驱动侧边栏**而不是硬编码路由表：
//   1. 它走的正是运营真实会走的路径，菜单点不开本身就是要查的故障；
//   2. 新增页面后不需要改这里，自动就覆盖到了——
//      硬编码的清单一定会和实际路由不同步，然后「全绿」其实是没测。
//
// 用法：
//   node tests/ui/admin-ui-regression.mjs
//   node tests/ui/admin-ui-regression.mjs --only orders
//   node tests/ui/admin-ui-regression.mjs --headed
//
// 退出码非 0 即视为 UI 回归失败（任何页面有控制台错误 / 失败请求 / 白屏）。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const SHOT_DIR = path.join(ROOT, 'tests', 'visual', 'current');
const REPORT_FILE = path.join(__dirname, 'ui-report.json');

const args = process.argv.slice(2);
const only = args.includes('--only') ? args[args.indexOf('--only') + 1] : '';
const headed = args.includes('--headed');

const BASE = process.env.ADMIN_URL || 'http://localhost:5174';
const USER = process.env.ADMIN_USER || 'codexadmin';
const PASS = process.env.ADMIN_PASS || 'Admin123456';
const VIEWPORT = { width: 1440, height: 900 };

// 明显不是「页面加载失败」的错误：网络/资源类噪音，不算回归失败
const IGNORED_ERROR = [/favicon/i, /Download the Vue Devtools/i, /\[vite\] connect/i];

const ignored = (t) => IGNORED_ERROR.some((re) => re.test(t));

// 菜单点到不了的页面（详情 / 编辑需要真实 Id），用「流程」覆盖：
// 从列表页出发，按真实操作路径一步步走，每步都截图。
// 这也是「每一个测试都要一步步截图验证」的落点——
// 只截最终页会漏掉「点第一行就报错」「加载中白屏」这类中间态问题。
const FLOWS = [
  {
    name: '订单列表 -> 点行 -> 订单详情',
    start: '#/orders',
    steps: [
      { desc: '进入订单列表', goto: '#/orders', shot: 'flow-orders-list' },
      { desc: '点击第一行进入详情', clickRow: '.el-table__row', shot: 'flow-order-detail' },
      { desc: '详情内金额构成可见', expect: '.card__title', shot: 'flow-order-detail-money' },
    ],
  },
];

const slug = (s) =>
  String(s).trim().toLowerCase()
    .replace(/[\\/:*?"<>|\s]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');

async function login(page) {
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForSelector('#username', { timeout: 15000 });
  await page.fill('#username', USER);
  await page.fill('#password', PASS);
  await page.click('.login__submit');
  // 等侧边栏出现 = 登录成功且布局已挂载
  await page.waitForSelector('.nav__link', { timeout: 20000 });
}

// 从侧边栏读出所有叶子菜单项（分组标题只是结构，不算页面）
async function readMenu(page) {
  return page.evaluate(() => {
    const out = [];
    const groups = [...document.querySelectorAll('.nav__item')];
    for (const g of groups) {
      const title = g.querySelector('.nav__label')?.textContent?.trim() || '';
      const subs = [...g.querySelectorAll('.nav__sublink')].map((a) => ({
        title: a.textContent.trim(),
        href: a.getAttribute('href'),
      }));
      if (subs.length > 0) {
        for (const s of subs) out.push({ group: title, ...s });
      } else {
        const link = g.querySelector('.nav__link');
        if (link) out.push({ group: title, title, href: link.getAttribute('href') });
      }
    }
    return out;
  });
}

async function visit(page, item) {
  const errors = [];
  const failed = [];
  const onConsole = (m) => {
    if (m.type() === 'error' && !ignored(m.text())) errors.push(m.text().slice(0, 300));
  };
  const onResponse = (r) => {
    const s = r.status();
    if (s >= 400 && !r.url().includes('/health')) {
      failed.push(`${s} ${r.request().method()} ${r.url().replace(BASE, '')}`);
    }
  };
  const onError = (e) => {
    const t = String(e.message?.split('\n')[0] || e);
    if (!ignored(t)) failed.push(`NET ${t.slice(0, 200)}`);
  };

  page.on('console', onConsole);
  page.on('response', onResponse);
  page.on('requestfailed', onError);

  try {
    await page.goto(`${BASE}/#${item.href.replace('#', '')}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(350);

    // 白屏检测：内容区既没有骨架也没有任何文字
    const blank = await page.evaluate(() => {
      const shell = document.querySelector('.shell__body');
      return !shell || shell.innerText.trim().length === 0;
    });

    // 「尚未实现」页不算故障，但必须单独统计，
    // 否则清单会被当成已交付
    const placeholder = await page.locator('.ph__title').count();

    const name = `${slug(item.group)}-${slug(item.title)}`;
    await page.screenshot({
      path: path.join(SHOT_DIR, `adm-${name}-1440x900.png`),
      fullPage: true,
    });

    return {
      group: item.group,
      title: item.title,
      href: item.href,
      screenshot: `adm-${name}-1440x900.png`,
      blank,
      placeholder: placeholder > 0,
      errors,
      failed,
      ok: !blank && errors.length === 0 && failed.length === 0,
    };
  } finally {
    page.off('console', onConsole);
    page.off('response', onResponse);
    page.off('requestfailed', onError);
  }
}

// 跑一条流程，逐步截图并记录每步的错误
async function runFlow(page, flow) {
  const steps = [];
  let currentErrors = [];
  let currentFailed = [];

  const onConsole = (m) => {
    if (m.type() === 'error' && !ignored(m.text())) currentErrors.push(m.text().slice(0, 300));
  };
  const onResponse = (r) => {
    const s = r.status();
    if (s >= 400 && !r.url().includes('/health')) {
      currentFailed.push(`${s} ${r.request().method()} ${r.url().replace(BASE, '')}`);
    }
  };

  page.on('console', onConsole);
  page.on('response', onResponse);

  try {
    await page.goto(`${BASE}/${flow.start}`, { waitUntil: 'networkidle' });

    for (const step of flow.steps) {
      currentErrors = [];
      currentFailed = [];

      if (step.goto) {
        await page.goto(`${BASE}/${step.goto}`, { waitUntil: 'networkidle' });
      } else if (step.clickRow) {
        const row = page.locator(step.clickRow).first();
        const has = (await row.count()) > 0;
        if (has) {
          await row.click();
        } else {
          // 没有行数据时不算失败：空列表本来就点不出详情
          currentErrors.push(`流程步骤「${step.desc}」找不到可点击的行（列表为空）`);
        }
      }

      if (step.expect) {
        const ok = (await page.locator(step.expect).count()) > 0;
        if (!ok) currentErrors.push(`流程步骤「${step.desc}」期望出现 ${step.expect}`);
      }

      await page.waitForTimeout(400);
      await page.screenshot({
        path: path.join(SHOT_DIR, `adm-${slug(flow.name)}-${step.shot}-1440x900.png`),
        fullPage: true,
      });

      steps.push({
        desc: step.desc,
        shot: `adm-${slug(flow.name)}-${step.shot}-1440x900.png`,
        url: page.url().replace(BASE, ''),
        errors: [...currentErrors],
        failed: [...currentFailed],
        ok: currentErrors.length === 0 && currentFailed.length === 0,
      });
    }
  } finally {
    page.off('console', onConsole);
    page.off('response', onResponse);
  }

  return { name: flow.name, steps, ok: steps.every((s) => s.ok) };
}

async function main() {
  fs.mkdirSync(SHOT_DIR, { recursive: true });

  const browser = await chromium.launch({ headless: !headed });
  const page = await browser.newPage({ viewport: VIEWPORT });

  try {
    await login(page);
  } catch (e) {
    console.error(`登录失败：${e.message}`);
    await browser.close();
    process.exit(1);
  }

  const menu = await readMenu(page);
  const targets = only
    ? menu.filter((m) => (m.title + m.href).toLowerCase().includes(only.toLowerCase()))
    : menu;

  console.log(`侧边栏共 ${menu.length} 个页面，本轮测 ${targets.length} 个\n`);

  const results = [];
  for (const item of targets) {
    const r = await visit(page, item);
    results.push(r);
    const flags = [];
    if (r.blank) flags.push('白屏');
    if (r.placeholder) flags.push('占位页');
    if (r.errors.length) flags.push(`控制台错误×${r.errors.length}`);
    if (r.failed.length) flags.push(`失败请求×${r.failed.length}`);
    const mark = r.ok ? '\x1b[32mPASS\x1b[0m' : '\x1b[31mFAIL\x1b[0m';
    console.log(`  ${mark} ${r.group} / ${r.title}${flags.length ? '  [' + flags.join(' ') + ']' : ''}`);
    if (!r.ok) {
      r.errors.slice(0, 3).forEach((e) => console.log(`        控制台: ${e}`));
      r.failed.slice(0, 3).forEach((e) => console.log(`        请求:   ${e}`));
    }
  }

  const pass = results.filter((r) => r.ok).length;
  const fail = results.length - pass;
  const placeholder = results.filter((r) => r.placeholder).length;
  const real = results.filter((r) => !r.placeholder).length;

  console.log('\n  ── 流程（菜单点到不了的页面）──');
  const flowResults = [];
  for (const flow of FLOWS) {
    const fr = await runFlow(page, flow);
    flowResults.push(fr);
    const mark = fr.ok ? '\x1b[32mPASS\x1b[0m' : '\x1b[31mFAIL\x1b[0m';
    console.log(`  ${mark} ${fr.name}`);
    for (const s of fr.steps) {
      const sm = s.ok ? '\x1b[32m✓\x1b[0m' : '\x1b[31m✗\x1b[0m';
      console.log(`      ${sm} ${s.desc}  -> ${s.url}`);
      s.errors.slice(0, 2).forEach((e) => console.log(`          ${e}`));
      s.failed.slice(0, 2).forEach((e) => console.log(`          ${e}`));
    }
  }

  await browser.close();

  const flowPass = flowResults.filter((f) => f.ok).length;
  const flowFail = flowResults.length - flowPass;

  fs.writeFileSync(
    REPORT_FILE,
    JSON.stringify(
      {
        base: BASE,
        user: USER,
        at: new Date().toISOString(),
        total: results.length,
        pass,
        fail,
        placeholder,
        results,
        flows: flowResults,
        flowFail,
      },
      null,
      2,
    ),
  );

  console.log(`\n${'='.repeat(64)}`);
  console.log(`  总计 ${results.length} 个页面：可用 ${pass} / 有问题 ${fail}`);
  console.log(`  其中占位页（尚未实现）${placeholder} 个，真正已交付 ${real} 个`);
  console.log(`  流程 ${flowResults.length} 条：可用 ${flowPass} / 有问题 ${flowFail}`);
  console.log(`  截图目录：${path.relative(ROOT, SHOT_DIR)}`);
  console.log(`  明细报告：${path.relative(ROOT, REPORT_FILE)}`);
  process.exit(fail > 0 || flowFail > 0 ? 1 : 0);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
