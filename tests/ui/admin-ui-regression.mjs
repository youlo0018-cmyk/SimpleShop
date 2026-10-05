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
import { apiToken, collectIds, idOf, PAGES, slug as nmSlug } from './non-menu-pages.mjs';

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
  {
    // 直接盯「点页签会不会 400」这个回归。
    // 商户 / 商品的查询命令里 status（启停，1/2）与 auditStatus（审核）是两个字段，
    // 而页签值是审核状态。修好之前，点「已通过」会发 status=20 被后端拒掉 ——
    // 而只加载首屏（status=0）时完全正常，所以单页巡检查不出来。
    name: '商户列表 -> 切页签 -> 审核状态筛选',
    start: '#/merchants',
    steps: [
      { desc: '进入商户列表', goto: '#/merchants', shot: 'flow-merchant-tab-1-list' },
      { desc: '切到「已通过」页签不报 400', clickTab: '已通过', shot: 'flow-merchant-tab-2-approved' },
      { desc: '切到「已拒绝」页签不报 400', clickTab: '已拒绝', shot: 'flow-merchant-tab-3-rejected' },
    ],
  },
  {
    // 危险动作的确认对话框：打开 -> 拒绝原因必填被拦 -> 填了再放行 -> 取消。
    // 最后一步**取消**是刻意的：UI 回归不该真的改业务数据，
    // 否则每跑一次就少一批待审核商户，审核队列会被测试清空。
    name: '商户审核 -> 点拒绝 -> 确认框与必填校验',
    start: '#/merchants/audit',
    steps: [
      { desc: '进入商户审核页（默认待审核）', goto: '#/merchants/audit', shot: 'flow-audit-1-list' },
      { desc: '点「拒绝」弹出确认框', clickText: '拒绝', clickScope: '.el-table', expect: '.el-dialog', shot: 'flow-audit-2-dialog' },
      { desc: '确认框里有「拒绝原因」输入框', expect: '.el-dialog textarea', shot: 'flow-audit-3-reason' },
      { desc: '填入原因', fill: '.el-dialog textarea', fillValue: '资料不完整，缺少营业执照', shot: 'flow-audit-4-filled' },
      { desc: '取消以免改动业务数据', clickText: '取消', clickScope: '.el-dialog', shot: 'flow-audit-5-cancelled' },
    ],
  },
  {
    // 「新建」必须是列表页右上角的按钮，点开**新页**：
    // 不能是侧边栏里的一个条目（那是业务地图，不是所有能点的地方），
    // 也不能是弹窗（账号表单十几个字段塞不进弹窗）。
    // 这条流程就是守住这个交互约定的。
    name: '账号列表 -> 点「新建账号」-> 开新页',
    start: '#/users',
    steps: [
      {
        desc: '侧边栏里没有「新建账号」条目',
        goto: '#/users',
        notExpectText: { sel: '.nav', text: '新建账号' },
        shot: 'flow-create-1-list',
      },
      { desc: '点右上角「新建账号」', clickText: '新建账号', clickScope: '.head__tools', shot: 'flow-create-2-form' },
      { desc: '停在新建页而不是弹窗', expect: '.form__grid, .form', shot: 'flow-create-3-page' },
    ],
  },
  {
    // 「编辑」要给一个**看得见**的按钮，不能只靠「整行可点」——
    // 不是所有人都知道整行能点。点开同样是新页。
    name: '账号列表 -> 点「编辑」-> 开新页',
    start: '#/users',
    steps: [
      { desc: '进入账号列表', goto: '#/users', shot: 'flow-edit-1-list' },
      { desc: '点该行「编辑」', clickText: '编辑', clickScope: '.el-table', shot: 'flow-edit-2-form' },
      { desc: '停在编辑页而不是弹窗', expect: '.form__grid, .form', shot: 'flow-edit-3-page' },
    ],
  },
  {
    // 退款审批是对外不可逆的钱操作，确认框里必须显示具体单号，
    // 拒绝还必须填原因。最后取消：UI 回归不该真的退钱。
    name: '退款列表 -> 点「拒绝」-> 确认框与必填原因',
    start: '#/refunds',
    steps: [
      { desc: '进入退款列表', goto: '#/refunds', shot: 'flow-refund-1-list' },
      { desc: '点「拒绝」弹出确认框', clickText: '拒绝', clickScope: '.el-table', shot: 'flow-refund-2-dialog' },
      { desc: '确认框里有「拒绝原因」输入框', expect: '.el-dialog textarea', shot: 'flow-refund-3-reason' },
      { desc: '取消以免真的退款', clickText: '取消', clickScope: '.el-dialog', shot: 'flow-refund-4-cancelled' },
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
  // 抓列表接口的响应体，用来和 DOM 交叉核对（见下面「裸枚举数字」的加强检查）。
  // 只记成功且带 data 的 JSON，避免把错误响应也当成数据。
  let lastListBody = null;
  const onConsole = (m) => {
    if (m.type() === 'error' && !ignored(m.text())) errors.push(m.text().slice(0, 300));
  };
  const onResponse = (r) => {
    const s = r.status();
    if (s >= 400 && !r.url().includes('/health')) {
      failed.push(`${s} ${r.request().method()} ${r.url().replace(BASE, '')}`);
      return;
    }
    if (s === 200 && r.url().includes('/gateway/')) {
      const ct = (r.headers()['content-type'] || '');
      if (!ct.includes('json')) return;
      r.json()
        .then((b) => {
          if (b && b.success && b.data) lastListBody = b.data;
        })
        .catch(() => {});
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

    // 加强：拿响应体与 DOM 交叉核对「后端给了中文孪生字段，前端却显示了裸数字」。
    //
    // 上面那条只查「色标签旁边多出一段裸数字」，查不到
    // 「这一列压根没标 dict，直接把 1 渲染出来」——而后者更常见：
    // 实测支付列表的「渠道」列就是这样，页面上出现一个孤零零的 1。
    //
    // 判据：凡是响应里同时存在 X 和非空的 X + 'Name'，说明 X 是个枚举、
    // 后端**已经把中文文案给过来了**。那么整张表里必须能看到那个文案。
    // 没看到 = 前端把 X 当普通数字渲染了。
    // 只断言「文案出现过」而不是「裸数字没出现过」：后者会误伤
    // 「数量 20」「金额 20.00」这类本来就该显示数字的列。
    if (lastListBody) {
      const rows = Array.isArray(lastListBody)
        ? lastListBody
        : Array.isArray(lastListBody.items)
          ? lastListBody.items
          : [];

      const missingNames = new Set();
      for (const row of rows.slice(0, 20)) {
        for (const [k, v] of Object.entries(row || {})) {
          if (typeof v !== 'number') continue;
          const nameKey = `${k}Name`;
          const nameVal = row[nameKey];
          if (typeof nameVal !== 'string' || nameVal.trim() === '') continue;
          // 文案与数字相同时说明它本来就不是枚举文案，跳过
          if (nameVal.trim() === String(v)) continue;
          missingNames.add(`${k}=${v} 应显示为「${nameVal.trim()}」`);
        }
      }

      if (missingNames.size > 0) {
        const tableText = await page.evaluate(() => {
          const t = document.querySelector('.el-table');
          return t ? t.innerText : '';
        });
        const absent = [...missingNames].filter((m) => {
          const label = m.match(/「(.+)」$/);
          if (!label || tableText.includes(label[1])) return false;
          // ⚠️ 光看「文案没出现」会误报：这一列可能**根本没渲染**（配置里没声明），
          // 那不是「渲染成了裸数字」，那是另一个问题（信息缺失）。
          // 加上「页面上确实有等于原始数字的单元格」这一条，
          // 才说明是**把枚举当数字渲染出来了**。
          const raw = m.match(/=(\S+) /);
          if (!raw) return false;
          const cells = [...String(tableText).matchAll(/(?:^|\n)([^\n]*)/g)].map((x) =>
            x[1].trim(),
          );
          return cells.includes(raw[1]);
        });
        if (absent.length > 0) {
          errors.push(
            `这些枚举字段渲染成了裸数字（后端已下发中文文案，页面上却没有）：${absent.slice(0, 5).join('；')}`,
          );
        }
      }
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
      } else if (step.clickText) {
        // 按可见文字点按钮。审核、通过、拒绝这些动作没有稳定的 class，
        // 用文字反而更贴近「用户看到什么就点什么」。
        // ⚠️ 匹配不到必须**报失败**而不是静默跳过：
        // 静默跳过的话，这个步骤的截图永远是上一页，
        // 报告还显示 ok，测试等于什么都没测。
        // ⚠️ 必须限定 scope。确认对话框没打开时它也在 DOM 里（el-dialog 只是隐藏），
        // 而对话框的确定按钮文字恰好也叫「拒绝」——不限定就会点到那个隐藏按钮上，
        // 表现为「元素存在但不可见，点了 30 秒超时」。
        // ⚠️ 还必须加 :visible。操作列是 fixed="right"，
        // Element Plus 会为固定列渲染一份**隐藏的副本**用于测量，
        // 它在 DOM 里排在真身前面 —— 不加 :visible 就会选中那份永远不可点的副本。
        // 这个坑的表现是「同一个按钮 sometimes 能点 sometimes 不能」，取决于列宽测量时机。
        const scope = step.clickScope ? step.clickScope : 'body';
        const btn = page.locator(`${scope} button:has-text("${step.clickText}"):visible`).first();
        if ((await btn.count()) === 0) {
          currentErrors.push(`流程步骤「${step.desc}」找不到按钮「${step.clickText}」`);
        } else {
          try {
            await btn.click({ timeout: 8000 });
          } catch {
            // 元素存在但不可见（例如只有隐藏的那一个）也算失败，
            // 不能当成「点过了」——否则这一步的截图会停在原地而报告显示成功。
            currentErrors.push(`流程步骤「${step.desc}」按钮「${step.clickText}」存在但不可点击`);
          }
        }
      } else if (step.fill) {
        const box = page.locator(step.fill).first();
        if ((await box.count()) === 0) {
          currentErrors.push(`流程步骤「${step.desc}」找不到输入框 ${step.fill}`);
        } else {
          await box.fill(step.fillValue || '');
        }
      } else if (step.clickTab) {
        // 页签不是 <button>，Element Plus 渲染成 role="tab" 的可点元素。
        // 用 button:has-text() 匹配不到，会被误判成「找不到按钮」。
        const tab = page.locator(`.el-tabs__item:has-text("${step.clickTab}")`).first();
        if ((await tab.count()) === 0) {
          currentErrors.push(`流程步骤「${step.desc}」找不到页签「${step.clickTab}」`);
        } else {
          await tab.click({ timeout: 8000 });
          // 等列表刷新完再截图，否则截到的是切换前那一帧
          await page.waitForTimeout(600);
        }
      }

      if (step.expect) {
        const ok = (await page.locator(step.expect).count()) > 0;
        if (!ok) currentErrors.push(`流程步骤「${step.desc}」期望出现 ${step.expect}`);
      }

      if (step.notExpect) {
        // 反向断言：用来守住「某样东西**不该**出现」。
        // 比如新建页不能以侧边栏条目的形式存在 —— 只靠截图去看，
        // 哪天有人把它挪回菜单，这一步会悄悄「通过」。
        const shown = (await page.locator(step.notExpect).count()) > 0;
        if (shown) currentErrors.push(`流程步骤「${step.desc}」不该出现 ${step.notExpect}`);
      }

      if (step.notExpectText) {
        // 「某个选择器下不该有某段文字」：CSS 选不出文字，只能在页面里数一遍。
        // 用来守「侧边栏里不能出现「新建账号」这类入口页条目」——
        // 只靠截图看，哪天它被挪回菜单，这一步会悄悄通过。
        const { sel, text } = step.notExpectText;
        const found = await page.evaluate(
          ([s, t]) =>
            [...document.querySelectorAll(s)].some((el) =>
              (el.textContent || '').trim().includes(t),
            ),
          [sel, text],
        );
        if (found) {
          currentErrors.push(`流程步骤「${step.desc}」${sel} 里不该出现「${text}」`);
        }
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

// 访问一个菜单点不到的页面（详情 / 编辑 / 场次商品）。
// 断言项与菜单页一致：白屏、控制台错误、失败请求，另外还要求**必须有标题** ——
// 编辑页最容易出的问题是组件没匹配上而渲染成一片空白，而那时「不白屏」也可能成立。
async function visitNonMenu(page, spec, id) {
  const href = spec.route.replace('{id}', id);
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

  page.on('console', onConsole);
  page.on('response', onResponse);

  try {
    await page.goto(`${BASE}/${href}`, { waitUntil: 'domcontentloaded' });
    await page
      .waitForFunction(() => document.querySelectorAll('.skel').length === 0, { timeout: 15000 })
      .catch(() => {});
    await page.waitForTimeout(350);

    const heading = await page.evaluate(() => {
      const el = document.querySelector('.head__title, .page-header__title');
      return (el?.textContent || '').trim();
    });
    if (!heading) errors.push('页面没有标题，可能是路由没匹配上组件');

    const blank = await page.evaluate(() => {
      const shell = document.querySelector('.shell__body');
      return !shell || shell.innerText.trim().length === 0;
    });

    const name = `adm-${nmSlug(spec.name)}-${nmSlug(spec.title)}-1440x900.png`;
    await page.screenshot({ path: path.join(SHOT_DIR, name), fullPage: true });

    return {
      group: spec.name,
      title: spec.title,
      route: href,
      heading,
      screenshot: name,
      blank,
      errors,
      failed,
      ok: !blank && errors.length === 0 && failed.length === 0,
    };
  } finally {
    page.off('console', onConsole);
    page.off('response', onResponse);
  }
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

  // ---- 菜单点不到的页面（详情 / 编辑 / 场次商品 / 商户装修）----
  // 它们需要真实 Id，所以先调接口取，而不是硬编码 —— 硬编码的 id 会在数据变了之后
  // 变成「404 也算通过」，那样清单是绿的而实际什么都没测。
  let nonMenu = [];
  if (!only) {
    console.log('\n── 菜单点不到的页面（先取真实 Id）──');
    try {
      const token = await apiToken(USER, PASS);
      const ids = await collectIds(token);

      for (const spec of PAGES) {
        // 路由里没有 {id} 的（新建页）不需要真实 Id，直接访问。
        // 之前对所有条目都去 ids 里查，key 为 null 时取到 undefined → 一律 SKIP，
        // 于是新建页刚被移出菜单就同时丢了测试覆盖。
        const needsId = spec.route.includes('{id}');
        const id = needsId ? idOf(ids[spec.key]) : 'new';
        if (!id) {
          nonMenu.push({ group: spec.name, title: spec.title, skipped: true, ok: true, errors: [], failed: [] });
          console.log(`  \x1b[33mSKIP\x1b[0m ${spec.name} / ${spec.title}  [列表里没有数据，取不到真实 Id]`);
          continue;
        }

        const r = await visitNonMenu(page, spec, id);
        nonMenu.push(r);
        const flags = [];
        if (r.blank) flags.push('白屏');
        if (r.errors?.length) flags.push(`控制台错误×${r.errors.length}`);
        if (r.failed?.length) flags.push(`失败请求×${r.failed.length}`);
        const mark = r.ok ? '\x1b[32mPASS\x1b[0m' : '\x1b[31mFAIL\x1b[0m';
        console.log(`  ${mark} ${r.group} / ${r.title}  -> ${r.route}${flags.length ? '  [' + flags.join(' ') + ']' : ''}`);
        if (!r.ok) {
          (r.errors || []).slice(0, 3).forEach((e) => console.log(`        控制台: ${e}`));
          (r.failed || []).slice(0, 3).forEach((e) => console.log(`        请求:   ${e}`));
        }
      }
    } catch (e) {
      console.log(`  非菜单页采集失败：${e.message}`);
      nonMenu.push({ group: '非菜单页', title: '采集', ok: false, errors: [e.message], failed: [] });
    }
  }

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
        nonMenu,
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
  const nmPass = nonMenu.filter((r) => r.ok).length;
  const nmFail = nonMenu.length - nmPass;
  const nmSkipped = nonMenu.filter((r) => r.skipped).length;
  console.log(`  总计 ${results.length} 个页面：可用 ${finalPass} / 有问题 ${finalFail}`);
  console.log(`  其中占位页（尚未实现）${placeholder} 个，真正已交付 ${real} 个`);
  console.log(`  非菜单页 ${nonMenu.length} 个：可用 ${nmPass} / 有问题 ${nmFail} / 无数据跳过 ${nmSkipped}`);
  console.log(`  流程 ${flowResults.length} 条：可用 ${flowPass} / 有问题 ${flowFail}`);
  console.log(`  截图目录：${path.relative(ROOT, SHOT_DIR)}`);
  console.log(`  明细报告：${path.relative(ROOT, REPORT_FILE)}`);
  process.exit(finalFail > 0 || nmFail > 0 || flowFail > 0 ? 1 : 0);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
