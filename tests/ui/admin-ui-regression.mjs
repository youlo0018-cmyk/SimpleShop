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
    // 用 domcontentloaded + 显式等骨架消失，而不是 networkidle：
    // networkidle 要等 500ms 无网络活动才返回，页面里有慢查询时会白耗超时预算。
    // 真正要等的是「骨架屏消失」，那个有明确信号。
    await page.goto(`${BASE}/#${item.href.replace('#', '')}`, { waitUntil: 'domcontentloaded' });
    await page
      .waitForFunction(() => document.querySelectorAll('.skel').length === 0, { timeout: 15000 })
      .catch(() => {});
    await page.waitForTimeout(250);

    // 白屏检测：内容区既没有骨架也没有任何文字
    const blank = await page.evaluate(() => {
      const shell = document.querySelector('.shell__body');
      return !shell || shell.innerText.trim().length === 0;
    });

    // 「尚未实现」页不算故障，但必须单独统计，
    // 否则清单会被当成已交付
    const placeholder = await page.locator('.ph__title').count();

    // 页面内标题。
    //
    // ⚠️ 必须用 evaluate 而不是 locator().textContent()：
    // locator 在选择器匹配不到时会**自动等待到默认超时（30 秒）**再抛错。
    // 占位页没有 .head__title，于是每页白等 30 秒 —— 41 页就是 20 分钟，
    // 而 `.catch(() => '')` 把异常吃掉后，症状只剩「工具慢」，看不出是哪一行。
    // evaluate 是直接读 DOM，不存在自动等待。
    //
    // 标题也**不与菜单项比对**：菜单叫「工作台」、页面标题叫「经营概览」是正常的，
    // 那样断言会一直误报，而一直误报的检查项等于没有（会被直接忽略）。
    const heading = await page.evaluate(() => {
      const el = document.querySelector('.head__title, .page-header__title');
      return (el?.textContent || '').trim();
    });

    // 表格渲染完整性：配置声明了几列，表头就该有几列。
    // 这条是被真实 bug 逼出来的：操作列被 `<template #default>` + v-if 包住，
    // 顶替了整个默认插槽，于是**所有数据列消失、只剩操作列**——
    // 而控制台一行错都没有、页面也不算白屏，纯靠「有没有报错」判定会全绿。
    // 「渲染出来的结构对不对」必须单独断言，不能指望异常来报。
    const tableCols = await page.evaluate(() => {
      const ths = document.querySelectorAll('.el-table__header th');
      const names = [];
      ths.forEach((th) => {
        const t = (th.textContent || '').trim();
        if (t) names.push(t);
      });
      return names;
    });
    // 任何真实的列表都不可能只有 1 列。出现 1 列几乎必然是渲染结构出了问题
    //（而不报错）。不依赖配置、纯靠结构就能判，所以对所有页面都成立。
    if (tableCols.length === 1) {
      errors.push(`表格只渲染出 1 列「${tableCols[0]}」，数据列很可能被插槽顶替丢弃了`);
    }

    // 规格 UI-RAW-003：界面不出现枚举数字。
    // 具体形态：状态色标签（.pill）**旁边**又多出一段裸数字，
    // 渲染成「已通过 20」。这是模板里 v-if 少了配套的 v-else 时才会出现的，
    // 而且普通列看不出来（两处渲染的是同一段文字），只有状态列才暴露。
    const rawEnum = await page.evaluate(() => {
      const bad = [];
      document.querySelectorAll('.el-table__row td .cell').forEach((cell) => {
        const pill = cell.querySelector('.pill');
        if (!pill) return;
        // 去掉色标签自身，剩下的纯文本若含裸数字就是漏出来的枚举值
        const rest = cell.cloneNode(true);
        rest.querySelectorAll('.pill').forEach((p) => p.remove());
        const text = (rest.textContent || '').trim();
        if (/^\d+$/.test(text)) bad.push(text);
      });
      return bad.slice(0, 5);
    });
    if (rawEnum.length > 0) {
      errors.push(`状态标签旁出现裸枚举数字：${rawEnum.join('、')}（应为纯中文状态）`);
    }

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
      heading,
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

  // 同一分组下的兄弟页面**不允许渲染出完全一样的标题**。
  // 这条是被真实 bug 逼出来的：一个组件服务多个路由时，vue-router 会复用组件实例，
  // setup 里 `const x = props.x` 快照成第一次的值 —— 结果「秒杀效果报表」的顶栏写着秒杀，
  // 内容却是经营报表的。按「同组同名」来判，既能抓住它，又不会像「标题必须等于菜单文字」
  // 那样一直误报（「工作台」vs「经营概览」本来就是合理的差异）。
  const dupInGroup = [];
  const byGroup = new Map();
  for (const r of results) {
    if (!r.heading) continue;
    const list = byGroup.get(r.group) || [];
    list.push(r);
    byGroup.set(r.group, list);
  }
  for (const [group, list] of byGroup) {
    const seen = new Map();
    for (const r of list) {
      const prev = seen.get(r.heading);
      if (prev) {
        const msg = `分组「${group}」下「${prev.title}」与「${r.title}」渲染出相同标题「${r.heading}」，疑似组件复用导致 props 快照`;
        r.errors.push(msg);
        r.ok = false;
        dupInGroup.push(msg);
      } else {
        seen.set(r.heading, r);
      }
    }
  }
  if (dupInGroup.length > 0) {
    console.log('\n  ── 同组标题重复（组件复用嫌疑）──');
    dupInGroup.forEach((m) => console.log(`  FAIL ${m}`));
  }

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
  // 汇总必须在同组标题检查**之后**算，否则那道检查新判红的问题不会计入「有问题」
  const finalPass = results.filter((r) => r.ok).length;
  const finalFail = results.length - finalPass;
  console.log(`  总计 ${results.length} 个页面：可用 ${finalPass} / 有问题 ${finalFail}`);
  console.log(`  其中占位页（尚未实现）${placeholder} 个，真正已交付 ${real} 个`);
  console.log(`  流程 ${flowResults.length} 条：可用 ${flowPass} / 有问题 ${flowFail}`);
  console.log(`  截图目录：${path.relative(ROOT, SHOT_DIR)}`);
  console.log(`  明细报告：${path.relative(ROOT, REPORT_FILE)}`);
  process.exit(finalFail > 0 || flowFail > 0 ? 1 : 0);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
