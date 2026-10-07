// 后台前端深度回归：不只看页面能不能打开，还逐个点列表页的页签 / 筛选 / 操作按钮，
// 逐个打开表单、配置、树、报表、装修和特殊页面，并验证组件本身。
//
// 与 admin-ui-regression.mjs 的分工：
//   admin-ui-regression：菜单页 + 非菜单页 + 关键流程的「页面级」巡检；
//   admin-deep-regression：页面内的「功能级」巡检，尽量把每个可见按钮、字段、
//   页签、筛选器、对话框都真实点一遍，产出逐项报告与截图。
//
// 用法：
//   node tests/ui/admin-deep-regression.mjs
//   node tests/ui/admin-deep-regression.mjs --headed
//   node tests/ui/admin-deep-regression.mjs --only users

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { apiToken, collectIds, idOf } from './non-menu-pages.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const SHOT_DIR = path.join(ROOT, 'tests', 'visual', 'deep');
const REPORT_FILE = path.join(__dirname, 'admin-deep-report.json');

const args = process.argv.slice(2);
const only = args.includes('--only') ? args[args.indexOf('--only') + 1] : '';
const headed = args.includes('--headed');

const BASE = process.env.ADMIN_URL || 'http://127.0.0.1:5173';
const GATEWAY = process.env.GATEWAY_URL || 'http://127.0.0.1:5008';
const PRODUCT = process.env.PRODUCT_URL || 'http://127.0.0.1:5058';
const ORDER = process.env.ORDER_URL || 'http://127.0.0.1:5064';
const INVENTORY = process.env.INVENTORY_URL || 'http://127.0.0.1:5062';
const USER = process.env.ADMIN_USER || 'codexadmin';
const PASS = process.env.ADMIN_PASS || 'Admin123456';
const VIEWPORT = { width: 1440, height: 900 };

const IGNORED_ERROR = [
  /favicon/i,
  /Download the Vue Devtools/i,
  /\[vite\] connect/i,
  /cdn\.example\.com/i,
  /net::ERR_CONNECTION_CLOSED/i,
];

const slug = (s) =>
  String(s).trim().toLowerCase()
    .replace(/[\\/:*?"<>|\s]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');

const ignored = (t) => IGNORED_ERROR.some((re) => re.test(t));

const results = [];

// opts.allow4xx：允许列表里的正则命中 URL 时，4xx 不计为失败。
// 只给「故意提交非法输入验证提示」的用例用 —— 否则一个 404 会把
// 「页面确实给了反馈」这条正确行为判成红的。
async function testCase(page, id, name, action, opts = {}) {
  const allow4xx = opts.allow4xx || [];
  const allowConsole = opts.allowConsole || [];
  const errors = [];
  const failed = [];
  const onConsole = (m) => {
    const text = m.text();
    if (m.type() !== 'error' || ignored(text)) return;
    // 故意提交非法输入时浏览器也会往控制台写一条 "Failed to load resource: 400"，
    // 那是**预期行为**的一部分（我们要的就是「被拒且有提示」），
    // 不豁免的话用例会因为「页面正确拒绝了请求」而判红。
    if (allowConsole.some((re) => re.test(text))) return;
    errors.push(text.slice(0, 400));
  };
  const onResponse = (r) => {
    if (r.status() >= 400 && !r.url().includes('/health')) {
      if (r.status() < 500 && allow4xx.some((re) => re.test(r.url()))) return;
      failed.push(`${r.status()} ${r.request().method()} ${r.url().replace(BASE, '')}`);
    }
  };
  page.on('console', onConsole);
  page.on('response', onResponse);

  let detail = '';
  let ok = false;
  try {
    detail = (await action()) || '';
    ok = errors.length === 0 && failed.length === 0;
  } catch (e) {
    errors.push(e.message || String(e));
  } finally {
    page.off('console', onConsole);
    page.off('response', onResponse);
  }

  const shot = `deep-${slug(id)}-${slug(name)}-1440x900.png`;
  await page.screenshot({ path: path.join(SHOT_DIR, shot), fullPage: true }).catch(() => {});

  const record = { id, name, ok, detail, errors, failed, shot };
  results.push(record);
  const mark = ok ? '\x1b[32mPASS\x1b[0m' : '\x1b[31mFAIL\x1b[0m';
  console.log(`  ${mark} ${id}  ${name}${detail ? `  ${detail}` : ''}`);
  errors.slice(0, 3).forEach((e) => console.log(`        ${e}`));
  failed.slice(0, 3).forEach((e) => console.log(`        ${e}`));
  return record;
}

async function login(page) {
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForSelector('#username', { timeout: 15000 });
  await page.fill('#username', USER);
  await page.fill('#password', PASS);
  await page.click('.login__submit');
  await page.waitForSelector('.nav__link', { timeout: 20000 });
}

async function goto(page, route) {
  const target = `${BASE}/#${route}`;
  // 目标与当前 URL 完全相同时，`page.goto` 不会重新挂载组件：
  // 上一条用例留下的**校验错误与表单值会原样留在页面上**，
  // 于是「新建商品」用例一进去就顶着上一条的空提交错误跑，
  // 保存时被前端校验挡住，报的却是「商品名至少 2 个字符」这种对不上号的错。
  if (page.url() === target) {
    await page.reload({ waitUntil: 'domcontentloaded' });
  } else {
    await page.goto(target, { waitUntil: 'domcontentloaded' });
  }
  await page.waitForFunction(() => document.querySelectorAll('.skel').length === 0, { timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(250);
}

async function gatewayCall(token, route, method = 'POST', payload = {}) {
  const response = await fetch(`${GATEWAY}${route}`, {
    method,
    headers: {
      authorization: `Bearer ${token}`,
      ...(method === 'GET' ? {} : { 'content-type': 'application/json' }),
    },
    body: method === 'GET' ? undefined : JSON.stringify(payload),
  });
  const body = await response.json().catch(() => null);
  if (!response.ok || !body?.success) {
    throw new Error(`网关调用失败 ${method} ${route}：${JSON.stringify(body || {}).slice(0, 200)}`);
  }
  return body.data;
}

/**
 * 创建一张真实待支付订单，用于验证订单列表里的「模拟支付」按钮。
 *
 * 前端 UI 回归不能只断言按钮存在：没有待支付订单时按钮根本不会渲染，
 * 之前那条用例就会退化成「跳过」。这里先通过后端接口造一张测试单，
 * 测完再用「取消」把它清掉。
 */
async function createPendingOrder(token, deliveryTypeOverride = 0, fixture = null, quantity = 1) {
  let detail = fixture?.detail ?? null;
  let sku = fixture?.sku ?? null;

  if (!detail || !sku) {
    // 用 C 端商品接口找「审核通过 + 已上架」的商品，再查库存。
    // 后台商品列表不注入公开可见性过滤，拿到的商品可能未审核，下单会被拒。
    const list = await gatewayCall(token, '/gateway/shop/products/List', 'POST', {
      customerId: '0',
      page: 1,
      pageSize: 20,
    });
    for (const item of list?.items || []) {
      const candidate = await gatewayCall(token, '/gateway/shop/products/Detail', 'POST', {
        customerId: '0',
        productId: item.productId,
      }).catch(() => null);
      if (!candidate?.skus?.length) continue;

      for (const candidateSku of candidate.skus) {
        const snapshotResponse = await fetch(
          `${INVENTORY}/internal/inventory/Snapshot?skuIds=${candidateSku.skuId}`,
        );
        const snapshotBody = await snapshotResponse.json().catch(() => null);
        const available = Number(snapshotBody?.data?.[0]?.available || 0);
        if (available > 0) {
          detail = candidate;
          sku = candidateSku;
          break;
        }
      }
      if (sku) break;
    }
  }
  if (!detail || !sku) throw new Error('没有可售且仍有库存的商品，无法构造待支付订单');
  const product = { id: detail.productId };

  const suffix = `${Date.now()}${Math.floor(Math.random() * 1000)}`.slice(-12);
  const customerId = `98${suffix}`;
  // 下单是 C 端路由，网关对后台令牌按 fail-closed 拒绝未映射路径；
  // 测试单直接打订单服务，与 order-regression 的做法一致。
  const createResponse = await fetch(`${ORDER}/orders/Create`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
    customerId,
    platformId: 0,
    merchantId: 0,
    idempotencyKey: `UI-DEEP-${suffix}`,
    receiverName: 'UI 深测',
    receiverPhone: '13800000000',
    receiverAddress: '深测地址 1 号',
    lines: [{
      spuId: String(product.id),
      skuId: String(sku.skuId),
      quantity,
      unitPrice: Number(sku.price ?? sku.finalPrice ?? 1),
      productName: sku.skuName || detail.spuName || '深测商品',
      skuSpecText: sku.skuSpecText || '',
      // 覆盖配送方式：自提核销要一条「自提」单，而随便挑到的商品多半是快递。
      // 订单行上的 deliveryType 决定后续走哪条履约链路（发货 / 备货 / 核销）。
      deliveryType: deliveryTypeOverride || Number(detail.deliveryType || 1),
    }],
    couponId: 0,
    pointsToUse: 0,
    freight: 0,
    }),
  });
  const createBody = await createResponse.json();
  if (!createResponse.ok || !createBody?.success) {
    throw new Error(`创建测试订单失败：${JSON.stringify(createBody || {}).slice(0, 200)}`);
  }
  const created = createBody.data;
  return { orderNo: created.orderNo, orderId: created.orderId, customerId };
}

/**
 * 造一个「自提 + 已审核 + 已上架 + 有库存」的商品，返回它的 SKU。
 *
 * 为什么必须造：配送方式是 **SPU 级**的，服务端下单时以商品为准
 * （BUSINESS.md 6.1），拿快递商品在下单请求里改 deliveryType 是改不动的 ——
 * 实测会得到「该订单没有自提商品，不需要备货取货」。
 * 所以自提核销这条链路要有自己的商品。
 */
/** 取一个可用的三级（叶子）分类 Id。 */
async function pickLeafCategoryId(token) {
  const tree = await gatewayCall(token, '/gateway/categories/Tree?includeDisabled=false', 'GET');
  const flat = [];
  const walk = (nodes) => (nodes || []).forEach((n) => { flat.push(n); walk(n.children); });
  walk(Array.isArray(tree) ? tree : tree?.items);
  const leaf = flat.find((n) => Number(n.level) === 3 && Number(n.status) === 1);
  if (!leaf) throw new Error('找不到可用的三级分类');
  return leaf.id;
}

/**
 * 取一张**真实存在**的图片 URL（商品主图必须能打开，否则列表渲染会 404，
 * 深测会把这条控制台错误记成失败）。优先复用文件库里已有的图片。
 */
async function pickImageUrl(token) {
  const files = await gatewayCall(token, '/gateway/files/List', 'POST', { page: 1, pageSize: 20 })
    .catch(() => null);
  const existing = (files?.items || [])
    .find((f) => f.publicUrl && ['png', 'jpg', 'jpeg', 'webp'].includes(String(f.extension)));
  if (existing) return existing.publicUrl;

  const png = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
    'base64',
  );
  const form = new FormData();
  form.append('file', new Blob([png], { type: 'image/png' }), 'deep-fixture.png');
  const resp = await fetch(`${GATEWAY}/gateway/files/Upload`, {
    method: 'POST',
    headers: { authorization: `Bearer ${token}` },
    body: form,
  });
  const uploaded = await resp.json().catch(() => null);
  if (!resp.ok || !uploaded?.success) {
    throw new Error(`上传深测主图失败：${JSON.stringify(uploaded || {}).slice(0, 150)}`);
  }
  return uploaded.data.publicUrl;
}

/** 建一个「待审核」的商品（用于审核流程用例），返回商品 Id。 */
async function createDraftProduct(token, name, deliveryType = 1) {
  const stamp = `${Date.now()}`.slice(-9);
  return gatewayCall(token, '/gateway/products/Create', 'POST', {
    spuName: name,
    subTitle: '深度回归',
    categoryId: await pickLeafCategoryId(token),
    brandId: 0,
    deliveryType,
    mainImage: await pickImageUrl(token),
    originalPrice: 30,
    status: 2,
    description: '',
    images: '[]',
    specs: [{ specName: '规格', specValues: ['默认'] }],
    skus: [{
      skuCode: `DEEP-${stamp}`,
      specValues: ['默认'],
      price: 30,
      stock: 0,
      image: '',
      status: 1,
    }],
  });
}

/**
 * 取一个「指定配送方式 + 已审核 + 已上架 + 有库存」的商品；没有就自己造一个。
 *
 * 为什么必须按配送方式找：配送方式是 **SPU 级**的，服务端下单时以商品为准
 * （BUSINESS.md 6.1）—— 拿快递商品在请求里改 deliveryType 是改不动的，
 * 实测会得到「该订单没有自提商品，不需要备货取货」这类对不上号的错误。
 *
 * @param deliveryType 1 快递 / 2 虚拟 / 3 自提
 */
async function ensureSellableProduct(token, deliveryType) {
  // 先看有没有现成的（别的回归可能刚建过）
  const list = await gatewayCall(token, '/gateway/shop/products/List', 'POST', {
    customerId: '0', page: 1, pageSize: 50,
  });
  for (const item of list?.items || []) {
    const detail = await gatewayCall(token, '/gateway/shop/products/Detail', 'POST', {
      customerId: '0', productId: item.productId,
    }).catch(() => null);
    if (Number(detail?.deliveryType) === deliveryType && detail?.skus?.length) {
      const sku = detail.skus[0];
      const snap = await fetch(`${INVENTORY}/internal/inventory/Snapshot?skuIds=${sku.skuId}`)
        .then((r) => r.json()).catch(() => null);
      if (Number(snap?.data?.[0]?.available || 0) > 0) return { detail, sku };
    }
  }

  // 没有就自己造：建商品（待审核）→ 审核通过 → 上架 → 入库
  const label = deliveryType === 3 ? '自提' : deliveryType === 2 ? '虚拟' : '快递';
  const productId = await createDraftProduct(
    token, `深测${label}商品${`${Date.now()}`.slice(-9)}`, deliveryType);
  // 新建的商品直接就是「待审核」，不需要再 SubmitAudit
  // （那是「已驳回」的商品重新提交时才调的，多调一次会 400「该商品已在审核队列中」）
  await gatewayCall(token, '/gateway/products/Audit', 'POST', {
    productId, auditStatus: 20, remark: '深度回归自动审核通过',
  });
  await gatewayCall(token, '/gateway/products/ChangeListing', 'POST', { productId, status: 1 });

  const detail = await gatewayCall(token, '/gateway/shop/products/Detail', 'POST', {
    customerId: '0', productId,
  });
  const sku = detail?.skus?.[0];
  if (!sku) throw new Error(`${label}商品建好后取不到 SKU`);

  await gatewayCall(token, '/gateway/inventory/Adjust', 'POST', {
    skuId: sku.skuId, availableAdjust: 10, remark: `深度回归${label}入库`, warnThreshold: 0,
  });
  return { detail, sku, createdProductId: productId };
}

async function visibleText(page, selector) {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    return (el?.textContent || '').trim();
  }, selector);
}

async function clickVisibleText(page, text, scope = 'body') {
  const btn = page.locator(`${scope} button:has-text("${text}"):visible`).first();
  if ((await btn.count()) === 0) throw new Error(`找不到按钮「${text}」（scope=${scope}）`);
  await btn.click({ timeout: 8000 });
}

async function cancelDialog(page) {
  const dialog = page.locator('.el-dialog:visible, .el-message-box:visible').last();
  if ((await dialog.count()) === 0) throw new Error('没有打开的对话框');
  const cancel = dialog.locator('button:has-text("取消"), button:has-text("关闭")').first();
  if ((await cancel.count()) > 0) {
    await cancel.click({ timeout: 8000 });
  } else {
    // 没有文字按钮时退回右上角的 ×（Element Plus 的 headerbtn）。
    // 之前直接判失败，报的是「对话框没有取消按钮」——
    // 那是**界面缺一个取消按钮**的问题，不该让测试脚本在这里卡住。
    const close = dialog.locator('.el-dialog__headerbtn').first();
    if ((await close.count()) === 0) throw new Error('对话框既没有取消按钮也没有关闭图标');
    await close.click({ timeout: 8000 });
  }
  await page.waitForTimeout(200);
}

async function dialogSnapshot(page) {
  const dialog = page.locator('.el-dialog:visible, .el-message-box:visible').last();
  if ((await dialog.count()) === 0) return null;
  return {
    title:
      (await dialog.locator('.el-dialog__title, .el-message-box__title').first().textContent().catch(() => ''))?.trim() ||
      '',
    inputs: await dialog.locator('input, textarea').count(),
    buttons: await dialog.locator('button:visible').count(),
  };
}

/**
 * 展开 el-tree 的全部节点。
 * 权限树的第一层是**虚拟根「全部权限」**，默认收起 —— 不展开的话
 * 新建的一级权限点（虚拟根的另一个子节点）根本不在 DOM 里，
 * 「新增后找不到」会被误判成创建失败。
 * 一次只点一个箭头：点完 DOM 会重建，拿着旧列表批量点会漏节点。
 */
async function expandTree(page, guard = 200) {
  for (let i = 0; i < guard; i++) {
    const next = page.locator('.el-tree-node__expand-icon:not(.is-leaf):not(.expanded)').first();
    if ((await next.count()) === 0) break;
    await next.click({ timeout: 5000 }).catch(() => {});
    await page.waitForTimeout(60);
  }
}

/** 按字段标签填一个文本框（label 用「包含」匹配，兼容必填星号与后缀说明）。 */
async function fillField(page, label, value) {
  const item = page.locator(`.el-form-item:has(.el-form-item__label:has-text("${label}"))`).first();
  if ((await item.count()) === 0) throw new Error(`表单里没有字段「${label}」`);
  const input = item.locator('input:not([type=hidden]), textarea').first();
  if ((await input.count()) === 0) throw new Error(`字段「${label}」没有输入框`);
  // 只读 / 禁用字段（如券模板的「每单限用」，值由券类型决定）直接跳过：
  // 它的值本来就是对的，硬填只会超时。
  if (await input.isDisabled().catch(() => false)) return;
  await input.fill(String(value));
}

/** 按字段标签选下拉的第一项（或按文本匹配）。 */
async function pickSelect(page, label, optionText = '') {
  const item = page.locator(`.el-form-item:has(.el-form-item__label:has-text("${label}"))`).first();
  if ((await item.count()) === 0) throw new Error(`表单里没有字段「${label}」`);
  await item.locator('.el-select').first().click({ timeout: 8000 });
  await page.waitForTimeout(350);
  const dropdown = page.locator('.el-select-dropdown:visible .el-select-dropdown__item:not(.is-disabled)');
  const option = optionText ? dropdown.filter({ hasText: optionText }).first() : dropdown.first();
  if ((await option.count()) === 0) throw new Error(`字段「${label}」的下拉没有可选项`);
  await option.click({ timeout: 8000 });
  await page.waitForTimeout(250);
  // 多选下拉点完不会自动收起，留着会把后面的点击挡住
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
}

/** 按字段标签填日期时间（Element Plus 的 datetime 输入框）。 */
async function fillDateTime(page, label, localValue) {
  const item = page.locator(`.el-form-item:has(.el-form-item__label:has-text("${label}"))`).first();
  if ((await item.count()) === 0) throw new Error(`表单里没有字段「${label}」`);
  const input = item.locator('input').first();
  await input.click({ timeout: 8000 });
  await input.fill(localValue);
  await input.press('Enter');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(250);
}

/**
 * 按字段标签设置颜色（Element Plus 的 el-color-picker）。
 * 颜色选择器没有可填的可见输入框，必须先点开面板，再往面板里的色值输入框写。
 */
async function fillColor(page, label, hex) {
  const item = page.locator(`.el-form-item:has(.el-form-item__label:has-text("${label}"))`).first();
  if ((await item.count()) === 0) throw new Error(`表单里没有字段「${label}」`);
  await item.locator('.el-color-picker').first().click({ timeout: 8000 });
  await page.waitForTimeout(350);
  // 🔴 Element Plus 新版面板的类名是 `.el-color-picker__panel`（旧版是 `.el-color-dropdown`），
  // 色值输入框在 footer 里。只认旧类名会报「面板没有色值输入框」，
  // 而面板其实开得好好的。两个类名都兜住。
  const panel = page.locator('.el-color-picker__panel:visible, .el-color-dropdown:visible').first();
  if ((await panel.count()) === 0) throw new Error(`「${label}」的颜色面板没有打开`);
  const input = panel.locator('.el-color-picker-panel__footer input, .el-color-dropdown__value input, input').first();
  if ((await input.count()) === 0) throw new Error(`「${label}」的颜色面板没有色值输入框`);
  await input.fill(hex);
  await input.press('Enter');
  await page.waitForTimeout(250);
  // 面板上还有个「确定」按钮，点了才真正写回 v-model（不同版本行为不一致，都点一次）
  const confirm = panel.locator('button:has-text("确定"), .el-color-dropdown__btn').first();
  if ((await confirm.count()) > 0) await confirm.click({ timeout: 5000 }).catch(() => {});
  await page.keyboard.press('Escape');
  await page.waitForTimeout(250);
}

/** 本地时间字符串（表单用的格式：YYYY-MM-DDTHH:mm:ss）。 */
function localTime(offsetHours) {
  const d = new Date(Date.now() + offsetHours * 3600 * 1000);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:00`;
}

/** 读表单里某个字段当前显示的值（文本框 / 数字框 / 多行文本）。 */
async function readField(page, label) {
  const item = page.locator(`.el-form-item:has(.el-form-item__label:has-text("${label}"))`).first();
  if ((await item.count()) === 0) throw new Error(`表单里没有字段「${label}」`);
  const input = item.locator('input:not([type=hidden]), textarea').first();
  if ((await input.count()) === 0) return '';
  return (await input.inputValue()).trim();
}

/**
 * 点「保存」并抓新建接口的响应，返回新记录 Id。
 * 直接抓响应而不是「去列表里找名字」：既省一次往返，也能拿到 Id 做收尾清理。
 */
async function submitCreate(page, urlPart) {
  const [resp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes(urlPart) && r.request().method() === 'POST',
      { timeout: 20000 },
    ),
    clickVisibleText(page, '保存'),
  ]);
  // 先取原始文本再解析：直接 resp.json() 解析失败时只能拿到 null，
  // 报错会变成「HTTP 200 {}」——看不出到底是空响应、非 JSON 还是被拦截了。
  const text = await resp.text().catch((e) => `<读取响应失败：${e.message}>`);
  let body = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    body = null;
  }
  return { status: resp.status(), body, text };
}

// 列表页：页签 / 搜索 / 筛选 / 分页 / 行操作逐个点。
const LIST_PAGES = [
  ['/users', '账号列表'],
  ['/customers', '客户列表'],
  ['/roles', '角色列表'],
  ['/platforms', '平台列表'],
  ['/platforms/logistics-companies', '物流公司'],
  ['/merchants', '商户列表'],
  ['/merchants/audit', '商户审核'],
  ['/brands', '品牌管理'],
  ['/products', '商品列表'],
  ['/products/audit', '商品审核'],
  ['/orders', '订单列表'],
  ['/refunds', '退款列表'],
  ['/promotions', '营销活动'],
  ['/coupons/templates', '券模板'],
  ['/coupons/activities', '券活动'],
  ['/coupons/records', '券核销记录'],
  ['/coupons/activity-records', '活动参与记录'],
  ['/seckill', '场次列表'],
  ['/points', '积分流水'],
  ['/evaluates', '评价管理'],
  ['/files', '文件管理'],
  ['/logs/operation', '操作日志'],
  ['/logs/exception', '异常日志'],
  ['/logs/dead-letter', '死信与重放'],
];

const NAV_ACTIONS = [
  '详情', '编辑', '库存', '场次商品', '小程序配置', '分配权限', '退款详情', '查看堆栈',
  // 客户列表的三个跳转入口：订单 / 积分 / 卡券（带 customerId 查询参数跳过去）
  '订单', '积分', '卡券',
];

// 列表页右上角的「新建 X」按钮 → 目标新建页。
// 这条链路以前完全没测过：新建页本身有覆盖（直接访问路由），
// 但「点按钮能不能到那一页」没人验，于是物流公司的新建 / 编辑按钮
// 一直指向不存在的 `/orders/logistics-companies/*`，点了只会被 catch-all 送去工作台。
const CREATE_BUTTON_TARGETS = [
  ['/users', '新建账号', '/users/create'],
  ['/roles', '新建角色', '/roles/create'],
  ['/platforms', '新建平台', '/platforms/create'],
  ['/platforms/logistics-companies', '新建物流公司', '/platforms/logistics-companies/create'],
  ['/merchants', '新建商户', '/merchants/create'],
  ['/brands', '新建品牌', '/brands/create'],
  ['/products', '新建商品', '/products/create'],
  ['/promotions', '新建活动', '/promotions/create'],
  ['/seckill', '新建场次', '/seckill/create'],
  ['/coupons/templates', '新建券模板', '/coupons/templates/create'],
  ['/coupons/activities', '新建券活动', '/coupons/activities/create'],
];
const DIALOG_ACTIONS = [
  '重置密码', '停用', '启用', '删除', '审核', '通过', '拒绝',
  '上架', '下架', '重新提交审核', '调整库存', '回复', '隐藏', '恢复', '重放', '发布', '结束',
  '模拟成功', '模拟失败', '取消', '发货', '核销', '退款',
  // 文件管理：只清列表记录，不删存储内容
  '移除',
];

async function runListPage(page, route, name) {
  await testCase(page, `list-${route}`, `${name}：页面加载`, async () => {
    await goto(page, route);
    const heading = await visibleText(page, '.head__title, .page-header__title');
    if (!heading) throw new Error('页面没有标题');
    return heading;
  });

  // 页签
  const tabs = await page.locator('.el-tabs__item:visible').allTextContents().catch(() => []);
  for (const tab of tabs.map((t) => t.trim()).filter(Boolean)) {
    await testCase(page, `list-${route}-tab-${tab}`, `${name}：页签「${tab}」`, async () => {
      const el = page.locator(`.el-tabs__item:has-text("${tab}")`).first();
      if ((await el.count()) === 0) throw new Error('页签不存在');
      await el.click({ timeout: 8000 });
      await page.waitForTimeout(450);
      return '已切换';
    });
  }

  // 搜索
  if (await page.locator('.head__search input').count()) {
    await testCase(page, `list-${route}-search`, `${name}：搜索框`, async () => {
      const input = page.locator('.head__search input').first();
      const rowsBefore = await page.locator('.el-table__row').count();
      // 用一个绝对搜不到的关键词：**必须把行筛掉**。
      // 只验「输入后能回车」的话，后端忽略 keyword 的搜索框（曾经有三处）
      // 会一直显示全部数据，而用例照样通过 —— 搜索框成了纯装饰。
      await input.fill('zzz-no-such-keyword-zzz');
      await input.press('Enter');
      await page.waitForTimeout(700);
      const rowsAfter = await page.locator('.el-table__row').count();
      if (rowsBefore > 0 && rowsAfter > 0) {
        throw new Error(`搜索没有过滤掉任何行（${rowsBefore} → ${rowsAfter}）`);
      }
      await input.fill('');
      await input.press('Enter');
      await page.waitForTimeout(450);
      const restored = await page.locator('.el-table__row').count();
      if (rowsBefore > 0 && restored === 0) throw new Error('清空关键词后没有恢复列表');
      return `${rowsBefore} → ${rowsAfter} → ${restored}（搜索真的生效）`;
    });
  }

  // 筛选器
  const filterCount = await page.locator('.head__filter').count();
  for (let i = 0; i < filterCount; i++) {
    await testCase(page, `list-${route}-filter-${i}`, `${name}：筛选器 #${i + 1}`, async () => {
      const select = page.locator('.head__filter').nth(i);
      await select.click({ timeout: 8000 });
      // Element Plus 的下拉是 teleport + 过渡动画，点击后立刻查 DOM 会看不到选项；
      // 之前把这种时序问题误报成「筛选器没有可选项」。
      await page.waitForTimeout(350);
      const option = page.locator('.el-select-dropdown:visible .el-select-dropdown__item').first();
      if ((await option.count()) === 0) throw new Error('筛选器没有可选项');
      await option.click({ timeout: 8000 });
      await page.waitForTimeout(450);
      return '已选择第一项';
    });
  }

  // 分页
  if (await page.locator('.el-pagination').count()) {
    await testCase(page, `list-${route}-pagination`, `${name}：分页`, async () => {
      const next = page.locator('.el-pagination .btn-next').first();
      const disabled = await next.isDisabled().catch(() => true);
      if (disabled) return '只有一页，跳过翻页';
      await next.click({ timeout: 8000 });
      await page.waitForTimeout(500);
      return '已翻到下一页';
    });

    // 上一页：翻过去之后必须能翻回来。只测「下一页」的话，
    // 把 prev 按钮绑错事件、或者页码状态没同步，都看不出来。
    await testCase(page, `list-${route}-pagination-prev`, `${name}：分页上一页`, async () => {
      await goto(page, route);
      const next = page.locator('.el-pagination .btn-next').first();
      if (await next.isDisabled().catch(() => true)) return '只有一页，跳过';
      await next.click({ timeout: 8000 });
      await page.waitForTimeout(500);
      const prev = page.locator('.el-pagination .btn-prev').first();
      if (await prev.isDisabled().catch(() => true)) throw new Error('翻到第 2 页后「上一页」仍是禁用');
      await prev.click({ timeout: 8000 });
      await page.waitForTimeout(500);
      const active = (await page.locator('.el-pagination .el-pager li.is-active').first().textContent().catch(() => ''))?.trim();
      if (active !== '1') throw new Error(`上一页之后当前页是「${active}」，预期 1`);
      return '第 2 页 → 第 1 页';
    });
  }

  // 行操作文本必须**跨全部页签 + 回到第一页**收集。
  // 只在默认页签收集会漏掉「待发货才有发货」「待取货才有核销」这类按状态出现的按钮；
  // 而分页之后停在最后一页收集更糟 —— 那一页没数据时按钮一个都扫不到，
  // 覆盖会**静默消失**（物流公司的编辑 / 删除就这么漏掉过，报告里看不出来）。
  const actionTexts = [];
  const collectActions = async () => {
    const raw = await page
      .locator('.el-table button:visible, .el-table .el-button:visible')
      .allTextContents()
      .catch(() => []);
    for (const t of raw.map((x) => x.trim()).filter(Boolean)) {
      if (!actionTexts.includes(t)) actionTexts.push(t);
    }
  };
  await goto(page, route);
  await collectActions();
  const actionTabs = await page.locator('.el-tabs__item:visible').allTextContents().catch(() => []);
  for (const tab of actionTabs.map((t) => t.trim()).filter(Boolean)) {
    await page.locator(`.el-tabs__item:has-text("${tab}")`).first().click({ timeout: 8000 }).catch(() => {});
    await page.waitForTimeout(450);
    await collectActions();
  }

  for (const action of actionTexts) {
    const known = NAV_ACTIONS.includes(action) || DIALOG_ACTIONS.includes(action);
    if (!known) continue;

    await testCase(page, `list-${route}-action-${action}`, `${name}：操作「${action}」`, async () => {
      await goto(page, route);
      // 行操作大多带状态条件（待发货才有「发货」、待取货才有「核销」、已上架才有「下架」…），
      // 而 goto 之后默认停在「全部」页签。只在默认页签找按钮会把「该状态下没有数据」
      // 误报成「按钮不存在」，所以这里逐个页签找，找到哪个用哪个。
      const btn = page.locator(`.el-table button:has-text("${action}"):visible`).first();
      let found = (await btn.count()) > 0;
      if (!found) {
        const tabs = await page.locator('.el-tabs__item:visible').allTextContents().catch(() => []);
        for (const tab of tabs.map((t) => t.trim()).filter(Boolean)) {
          await page.locator(`.el-tabs__item:has-text("${tab}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(500);
          if ((await btn.count()) > 0) {
            found = true;
            break;
          }
        }
      }
      if (!found) throw new Error('按钮不存在（所有页签下都没有该状态的数据）');
      const before = page.url();
      await btn.click({ timeout: 8000 });
      await page.waitForTimeout(500);

      if (NAV_ACTIONS.includes(action)) {
        const dialog = await dialogSnapshot(page);
        if (dialog) {
          await cancelDialog(page);
          return `打开对话框「${dialog.title || action}」`;
        }
        if (page.url() === before) throw new Error('既没有跳转也没有打开对话框');
        // 落到工作台 = 路由没匹配上被 catch-all 兜走了。
        // 只断言「URL 变了」是抓不到这个的：坏路由同样会变，
        // 而且是变成 /#/dashboard —— 物流公司的「新建 / 编辑」就这么错过一次。
        if (page.url().replace(BASE, '').startsWith('/#/dashboard')) {
          throw new Error(`跳转落到了工作台（路由未匹配）：${page.url()}`);
        }
        return `跳转到 ${page.url().replace(BASE, '')}`;
      }

      const dialog = await dialogSnapshot(page);
      if (!dialog) throw new Error('危险操作没有确认对话框');
      if (dialog.inputs === 0 && dialog.buttons < 2) throw new Error('对话框没有可操作内容');
      await cancelDialog(page);
      return `确认框「${dialog.title || action}」已打开并取消`;
    });
  }
}

// 表单页：字段渲染 + 空提交校验 + 动态字段联动。
const FORM_PAGES = [
  ['/users/create', '新建账号'],
  ['/roles/create', '新建角色'],
  ['/coupons/templates/create', '新建券模板'],
  ['/coupons/activities/create', '新建券活动'],
  ['/platforms/create', '新建平台'],
  ['/merchants/create', '新建商户'],
  ['/products/create', '新建商品'],
  ['/promotions/create', '新建活动'],
  ['/seckill/create', '新建场次'],
  ['/brands/create', '新建品牌'],
  ['/platforms/logistics-companies/create', '新建物流公司'],
];

async function runFormPage(page, route, name) {
  await testCase(page, `form-${route}`, `${name}：字段渲染`, async () => {
    await goto(page, route);
    const fields = await page.locator('.el-form-item').count();
    if (fields === 0) throw new Error('表单没有任何字段');
    const controls = await page.locator('.el-form-item input:visible, .el-form-item textarea:visible, .el-form-item .el-select:visible').count();
    if (controls === 0) throw new Error('表单字段没有可交互控件');
    return `${fields} 个字段 / ${controls} 个控件`;
  });

  await testCase(page, `form-${route}-required`, `${name}：空提交校验`, async () => {
    await goto(page, route);
    await clickVisibleText(page, '保存');
    await page.waitForTimeout(350);
    const errors = await page.locator('.el-form-item__error:visible').count();
    if (errors === 0) throw new Error('空提交没有出现字段级错误提示');
    return `${errors} 条字段错误`;
  });

  if (route === '/users/create') {
    await testCase(page, `form-${route}-tenant-switch`, `${name}：租户类型联动`, async () => {
      await goto(page, route);
      await page.locator('.el-form-item:has-text("租户类型") .el-select').click();
      await page.locator('.el-select-dropdown:visible .el-select-dropdown__item:has-text("商户账号")').click();
      if ((await page.locator('.el-form-item:has-text("所属商户")').count()) === 0) {
        throw new Error('切到商户账号后没有「所属商户」字段');
      }
      await page.locator('.el-form-item:has-text("租户类型") .el-select').click();
      await page.locator('.el-select-dropdown:visible .el-select-dropdown__item:has-text("平台账号")').click();
      if ((await page.locator('.el-form-item:has-text("所属平台")').count()) === 0) {
        throw new Error('切回平台账号后没有「所属平台」字段');
      }
      return '平台 / 商户切换正常';
    });
  }

  if (route === '/coupons/templates/create') {
    await testCase(page, `form-${route}-coupon-type`, `${name}：券类型动态字段`, async () => {
      await goto(page, route);
      const cases = [
        ['满减券', '门槛金额'],
        ['折扣券', '折扣率'],
        ['代金券', '优惠金额'],
        ['满赠券', '赠送券模板'],
      ];
      for (const [type, field] of cases) {
        await page.locator('.el-form-item:has-text("券类型") .el-select').click();
        await page.locator(`.el-select-dropdown:visible .el-select-dropdown__item:has-text("${type}")`).click();
        await page.waitForTimeout(200);
        if ((await page.locator(`.el-form-item:has-text("${field}")`).count()) === 0) {
          throw new Error(`券类型「${type}」没有显示「${field}」`);
        }
      }
      return '四种券类型字段联动正常';
    });
  }

  if (route === '/promotions/create') {
    await testCase(page, `form-${route}-activity-type`, `${name}：活动类型动态字段`, async () => {
      await goto(page, route);
      const cases = [
        ['满减', '优惠金额'],
        ['满折', '折扣率'],
        ['满赠', '赠送券模板'],
      ];
      for (const [type, field] of cases) {
        await page.locator('.el-form-item:has-text("活动类型") .el-select').click();
        await page.locator(`.el-select-dropdown:visible .el-select-dropdown__item:has-text("${type}")`).click();
        await page.waitForTimeout(200);
        if ((await page.locator(`.el-form-item:has-text("${field}")`).count()) === 0) {
          throw new Error(`活动类型「${type}」没有显示「${field}」`);
        }
      }
      return '三种活动类型字段联动正常';
    });
  }
}

async function main() {
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  const browser = await chromium.launch({ headless: !headed });
  const page = await browser.newPage({ viewport: VIEWPORT });

  try {
    await login(page);

    // ---- 布局 ----
    await testCase(page, 'layout-menu', '布局：菜单项数量', async () => {
      const count = await page.evaluate(() => {
        let total = 0;
        document.querySelectorAll('.nav__item').forEach((group) => {
          const subs = group.querySelectorAll('.nav__sublink');
          if (subs.length > 0) total += subs.length;
          else if (group.querySelector('.nav__link')) total += 1;
        });
        return total;
      });
      if (count < 31) throw new Error(`菜单项只有 ${count} 个，预期至少 31 个`);
      return `${count} 个菜单项`;
    });
    await testCase(page, 'layout-collapse', '布局：侧栏折叠 / 展开', async () => {
      const side = page.locator('.shell__side').first();
      const before = (await side.boundingBox())?.width || 0;
      await page.locator('.collapse').click();
      await page.waitForTimeout(350);
      const collapsed = (await side.boundingBox())?.width || 0;
      await page.locator('.collapse').click();
      await page.waitForTimeout(350);
      const expanded = (await side.boundingBox())?.width || 0;
      if (!(collapsed < before && expanded > collapsed)) throw new Error(`宽度变化异常：${before}/${collapsed}/${expanded}`);
      return `${before} → ${collapsed} → ${expanded}`;
    });

    // ---- 列表页 ----
    for (const [route, name] of LIST_PAGES) {
      if (only && !(route + name).toLowerCase().includes(only.toLowerCase())) continue;
      await runListPage(page, route, name);
    }

    // ---- 列表页的「新建 X」按钮 → 新建页 ----
    for (const [route, label, target] of CREATE_BUTTON_TARGETS) {
      if (only && !(route + label + target).toLowerCase().includes(only.toLowerCase())) continue;
      await testCase(page, `create-button-${route}`, `${label}：列表按钮进入新建页`, async () => {
        await goto(page, route);
        const btn = page.locator(`.head button:has-text("${label}"):visible`).first();
        if ((await btn.count()) === 0) throw new Error(`列表上没有「${label}」按钮`);
        await btn.click({ timeout: 8000 });
        await page.waitForTimeout(500);
        const url = page.url().replace(BASE, '');
        if (!url.startsWith(`/#${target}`)) {
          throw new Error(`「${label}」应当进入 ${target}，实际 ${url}`);
        }
        const title = await visibleText(page, '.head__title, .page-header__title');
        if (!title) throw new Error('新建页没有标题（可能是空白页）');
        return `进入 ${target}（${title}）`;
      });
    }

    // ---- 已确认的合并入口：支付 / 库存不再单独占菜单 ----
    if (!only || ['products', 'product', 'inventory', '库存', '商品'].includes(only.toLowerCase())) {
      await testCase(page, 'merged-product-inventory', '商品列表：库存操作进入 SKU 调整页', async () => {
        await goto(page, '/products');
        const btn = page.locator('.el-table button:has-text("库存"):visible').first();
        if ((await btn.count()) === 0) throw new Error('商品列表没有「库存」操作按钮');
        await btn.click({ timeout: 8000 });
        await page.waitForTimeout(650);
        if (!page.url().includes('/products/inventory/')) {
          throw new Error(`库存操作没有进入调整页：${page.url()}`);
        }
        const title = await visibleText(page, '.page-header__title');
        if (title !== '调整库存') throw new Error(`调整页标题是「${title}」`);
        const rows = await page.locator('.el-table__row').count();
        if (rows === 0) throw new Error('调整页没有 SKU 行');
        return `${rows} 个 SKU 可调整`;
      });
    }

    if (!only || ['orders', 'order', 'payment', '支付', '订单'].includes(only.toLowerCase())) {
      await testCase(page, 'merged-order-simulate-payment', '订单列表：模拟支付失败并取消测试单', async () => {
        const token = await apiToken(USER, PASS);
        const { orderNo } = await createPendingOrder(token);

        await goto(page, '/orders');
        await page.locator('.el-tabs__item:has-text("待支付")').click();
        await page.waitForTimeout(550);

        const row = page.locator('.el-table__row', { hasText: orderNo }).first();
        if ((await row.count()) === 0) throw new Error(`新建待支付订单 ${orderNo} 没有出现在列表`);

        // 模拟失败：确认后订单必须仍是待支付（失败不能把订单推进到已支付）
        await row.locator('button:has-text("模拟失败")').click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const simulateBox = page.locator('.el-message-box:visible');
        if ((await simulateBox.count()) === 0) throw new Error('模拟失败没有确认框');
        await simulateBox.locator('button:has-text("确认")').first().click({ timeout: 8000 });
        await page.waitForTimeout(800);

        await page.locator('.el-tabs__item:has-text("待支付")').click();
        await page.waitForTimeout(550);
        const stillPending = page.locator('.el-table__row', { hasText: orderNo }).first();
        if ((await stillPending.count()) === 0) throw new Error('模拟失败后订单没有保持待支付');

        // 清理：后台取消测试单，避免污染后续订单列表
        await stillPending.locator('button:has-text("取消")').click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const cancelBox = page.locator('.el-message-box:visible');
        if ((await cancelBox.count()) === 0) throw new Error('取消订单没有确认框');
        await cancelBox.locator('button:has-text("确认取消")').first().click({ timeout: 8000 });
        await page.waitForTimeout(800);

        return `订单 ${orderNo} 模拟失败保持待支付，已取消清理`;
      });
    }

    // ---- 券活动启停：走专用接口，不能复用整行编辑 ----
    if (!only || ['coupons', 'coupon', '券'].includes(only.toLowerCase())) {
      await testCase(page, 'merged-coupon-activity-status', '券活动：停用 / 启用走专用接口（含确认框）', async () => {
        const token = await apiToken(USER, PASS);
        const suffix = Date.now().toString().slice(-6);
        const activityName = `深度回归券活动${suffix}`;

        const templateId = await gatewayCall(token, '/gateway/marketing/coupon-templates/Create', 'POST', {
          templateName: `深度回归券模板${suffix}`, couponType: 1,
          thresholdAmount: 10, discountAmount: 1, validDays: 30,
          totalQuantity: 20, perUserLimit: 1, perOrderLimit: 1, platformId: 0, status: 1,
        });
        const activityId = await gatewayCall(token, '/gateway/marketing/coupon-activities/Create', 'POST', {
          activityName, templateId,
          claimStartTime: new Date(Date.now() - 5 * 60 * 1000).toISOString(),
          claimEndTime: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
          claimQuantity: 5, perUserLimit: 1, targetType: 1, targets: '[]', platformId: 0, status: 1,
        });

        const searchFor = async () => {
          await goto(page, '/coupons/activities');
          const input = page.locator('.head__search input').first();
          await input.fill(activityName);
          await input.press('Enter');
          await page.waitForTimeout(650);
          const row = page.locator('.el-table__row', { hasText: activityName }).first();
          if ((await row.count()) === 0) throw new Error(`券活动 ${activityName} 不在列表里`);
          return row;
        };

        try {
          // 停用：确认框 → 提交 → 行上应当只剩「启用」
          let row = await searchFor();
          await row.locator('button:has-text("停用")').first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          const offBox = page.locator('.el-dialog:visible');
          if ((await offBox.count()) === 0) throw new Error('停用没有确认框');
          await offBox.locator('button:has-text("停用")').last().click({ timeout: 8000 });
          await page.waitForTimeout(900);

          row = await searchFor();
          if ((await row.locator('button:has-text("启用")').count()) === 0) {
            throw new Error('停用后行上没有出现「启用」按钮');
          }

          // 启用：同样要确认框，提交后行上应当恢复「停用」
          await row.locator('button:has-text("启用")').first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          const onBox = page.locator('.el-dialog:visible');
          if ((await onBox.count()) === 0) throw new Error('启用没有确认框');
          await onBox.locator('button:has-text("启用")').last().click({ timeout: 8000 });
          await page.waitForTimeout(900);

          row = await searchFor();
          if ((await row.locator('button:has-text("停用")').count()) === 0) {
            throw new Error('启用后行上没有恢复「停用」按钮');
          }

          return `券活动 ${activityId} 停用 → 启用，全程未走 Update`;
        } finally {
          // 收尾：停用后删模板。券活动没有删除接口（领过的券要能查到来源），只停用。
          await gatewayCall(token, '/gateway/marketing/coupon-activities/SetStatus', 'POST', {
            activityId, status: 2,
          }).catch(() => {});
          await gatewayCall(token, '/gateway/marketing/coupon-templates/Delete', 'POST', {
            templateId,
          }).catch(() => {});
        }
      });
    }

    // ---- 功能页：工作台 / 树 / 配置 / 报表 / 对账 / 核销 / 装修 ----
    const want = (keys) => !only || keys.includes(only.toLowerCase());

    if (want(['dashboard', '工作台'])) {
      await testCase(page, 'page-dashboard', '工作台：指标卡与时间区间切换', async () => {
        await goto(page, '/dashboard');
        const gmv = await visibleText(page, '.hero__value');
        if (!gmv) throw new Error('工作台没有成交额主视觉');
        const rows = await page.locator('.panel .row').count();
        if (rows < 4) throw new Error(`指标行只有 ${rows} 行`);

        const segs = await page.locator('.el-segmented__item').allTextContents();
        if (segs.length < 4) throw new Error(`时间档位只有 ${segs.length} 个`);
        for (const label of segs.map((s) => s.trim())) {
          await page.locator(`.el-segmented__item:has-text("${label}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(700);
          const value = await visibleText(page, '.hero__value');
          if (!value) throw new Error(`切到「${label}」后成交额为空`);
        }
        return `${segs.length} 个档位 / ${rows} 行指标`;
      });
    }

    if (want(['permissions', '权限点'])) {
      await testCase(page, 'page-permission-tree', '权限点管理：树 / 选中 / 修改弹窗 / 新增弹窗', async () => {
        await goto(page, '/roles/permissions');
        const roots = await page.locator('.el-tree-node').count();
        if (roots === 0) throw new Error('权限树没有节点');

        await expandTree(page);
        const nodes = await page.locator('.el-tree-node').count();
        if (nodes < 50) throw new Error(`展开后只有 ${nodes} 个节点，权限树可能没读全`);

        // 选一个真叶子（有 code 的节点），右侧详情才该出现「修改」
        // ⚠️ 不能用 `:has(.node__code)` —— 父节点因为含叶子后代也会命中，
        // 于是「选中的是叶子」这条断言会变成选中根节点还能通过。
        const leaf = page.locator('.el-tree-node:not(:has(.el-tree-node)):has(.node__code)').first();
        if ((await leaf.count()) === 0) throw new Error('树里没有带 code 的叶子权限点');
        await leaf.locator('.node__main, .el-tree-node__label').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const detailTitle = await visibleText(page, '.detail-title h3');
        if (!detailTitle) throw new Error('点节点后右侧详情没有内容');

        await clickVisibleText(page, '修改');
        await page.waitForTimeout(350);
        const edit = await dialogSnapshot(page);
        if (!edit) throw new Error('「修改」没有打开弹窗');
        if (edit.inputs === 0) throw new Error('修改弹窗没有输入框');
        await cancelDialog(page);

        await clickVisibleText(page, '新增一级权限');
        await page.waitForTimeout(350);
        const create = await dialogSnapshot(page);
        if (!create) throw new Error('「新增一级权限」没有打开弹窗');
        await cancelDialog(page);
        return `${nodes} 个树节点，选中「${detailTitle}」`;
      });

      await testCase(page, 'page-permission-crud', '权限点管理：新增 → 修改 → 停用 → 启用 → 删除', async () => {
        const token = await apiToken(USER, PASS);
        const stamp = Date.now().toString().slice(-6);
        const name = `深度回归权限${stamp}`;
        const renamed = `${name}已改`;
        const code = `deepreg${stamp}:read`;

        // 权限点比分类更危险：它带着 api_path，网关 30 秒内就会用它做 RBAC。
        // 所以 apiPath 只填一个**没有任何真实端点**的路径（撞不到别的权限点，
        // 也不会把某个真接口的所需权限换掉），并且无论成败都在 finally 里删干净 ——
        // 留一条悬空 api_path 会让 scripts/check-permission-paths.ps1 在下次运行时红掉。
        const cleanup = async () => {
          const tree = await gatewayCall(token, '/gateway/permissions/Tree?includeDisabled=true', 'GET').catch(() => null);
          const flat = [];
          const walk = (list) => (list || []).forEach((n) => { flat.push(n); walk(n.children); });
          walk(Array.isArray(tree) ? tree : tree?.items);
          for (const n of flat.filter((x) => String(x.code || '').startsWith('deepreg'))) {
            await gatewayCall(token, '/gateway/permissions/Delete', 'POST', { permissionId: n.id }).catch(() => {});
          }
        };

        try {
          await goto(page, '/roles/permissions');
          const search = page.locator('.head__search input').first();
          // 每次 load() 之后树都会回到「只展开虚拟根」的状态，
          // 所以定位节点前都要先展开一次。
          // 搜索词与「显示文本」是两回事：权限点的搜索命中靠 JSON（含 code），
          // 但节点上渲染出来的是 **中文名 + apiPath**（codeField = apiPath），
          // 拿 code 去 has-text 永远匹配不到。
          const locate = async (searchText, displayText = searchText) => {
            // 顺序：**先搜索、再展开**。搜索会把树数据换成过滤后的副本，
            // el-tree 随之重渲染，之前展开的层级会回到收起状态 ——
            // 反过来做的话，节点虽然在 DOM 里（locator 能解析到），
            // 但被收起的祖先挡住，click 会一直等「visible」直到超时。
            await search.fill(searchText);
            await page.waitForTimeout(500);
            await expandTree(page);
            const n = page.locator(`.node__main:has-text("${displayText}")`).first();
            if ((await n.count()) === 0) {
              throw new Error(`权限树里找不到「${displayText}」（搜索词 ${searchText}）`);
            }
            return n;
          };

          // 1) 新增一级权限
          await clickVisibleText(page, '新增一级权限');
          await page.waitForTimeout(350);
          let dlg = page.locator('.el-dialog:visible').last();
          await dlg.locator('.el-form-item:has-text("中文名称") input').first().fill(name);
          await dlg.locator('.el-form-item:has-text("权限编码") input').first().fill(code);
          await dlg.locator('.el-form-item:has-text("接口路径") input').first().fill(`/gateway/permissions/DeepProbe${stamp}`);
          await dlg.locator('button:has-text("保存")').first().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('新增权限点没有成功提示');
          }

          // 2) 搜索定位 → 修改（这条路径曾经发的是 { id }，后端一律 400）
          const node = await locate(code, name);
          await node.click({ timeout: 8000 });
          await page.waitForTimeout(300);

          await clickVisibleText(page, '修改');
          await page.waitForTimeout(350);
          dlg = page.locator('.el-dialog:visible').last();
          await dlg.locator('.el-form-item:has-text("中文名称") input').first().fill(renamed);
          await dlg.locator('button:has-text("保存")').first().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('修改权限点没有成功提示（很可能是 Id 字段名对不上）');
          }

          // 3) 停用 → 启用（走 /gateway/permissions/Status 专用接口）
          await (await locate(code, renamed)).click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '停用');
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('停用权限点没有成功提示');
          }
          await (await locate(code, renamed)).click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '启用');
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('启用权限点没有成功提示');
          }

          // 4) 删除
          await (await locate(code, renamed)).click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '删除');
          await page.waitForTimeout(350);
          const confirmBox = page.locator('.el-dialog:visible').last();
          await confirmBox.locator('button:has-text("删除")').last().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('删除权限点没有成功提示');
          }
          await search.fill(code);
          await page.waitForTimeout(500);
          if ((await page.locator(`.node__main:has-text("${renamed}")`).count()) > 0) {
            throw new Error('删除后权限点还在树里');
          }
          return `${code} 新增 → 改名 → 停用 → 启用 → 删除 全部生效`;
        } finally {
          await cleanup();
        }
      });
    }

    if (want(['role-permissions', '分配权限', 'roles'])) {
      await testCase(page, 'page-role-permissions', '分配权限：全部权限 / 保存 / 清空（只落库叶子）', async () => {
        const token = await apiToken(USER, PASS);
        const code = `deepperm${Date.now().toString().slice(-8)}`;
        const roleId = await gatewayCall(token, '/gateway/roles/Create', 'POST', {
          roleName: `深度回归角色${code}`, code, allowedScopes: 1, dataScope: 1, remark: '深度回归临时角色',
        });

        try {
          await goto(page, `/roles/permissions/${roleId}`);
          await page.waitForTimeout(700);
          if ((await page.locator('.el-tree-node').count()) === 0) throw new Error('权限树没有节点');

          // 全部权限：容器节点会被自动勾上，但**落库只能有叶子**
          await clickVisibleText(page, '全部权限');
          await page.waitForTimeout(400);
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('保存全部权限没有成功提示');
          }

          const detail = await gatewayCall(token, `/gateway/roles/Detail?roleId=${roleId}`, 'GET');
          const ids = (detail?.permissionIds || []).map(String);
          if (ids.length < 50) throw new Error(`只存下 ${ids.length} 个权限点，远少于 77`);
          // 0 = 虚拟根，2101 = 业务大类下的模块，2108 = 没有叶子的「品牌」模块
          const containers = ['0', '2101', '2108'].filter((id) => ids.includes(id));
          if (containers.length) throw new Error(`容器 Id 被当成权限点落库：${containers.join(',')}`);

          // 清空：必须真的清干净
          await goto(page, `/roles/permissions/${roleId}`);
          await page.waitForTimeout(700);
          await clickVisibleText(page, '清空');
          await page.waitForTimeout(300);
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          const after = await gatewayCall(token, `/gateway/roles/Detail?roleId=${roleId}`, 'GET');
          if ((after?.permissionIds || []).length !== 0) {
            throw new Error(`清空后还剩 ${(after?.permissionIds || []).length} 个权限点`);
          }
          return `${ids.length} 个叶子权限点已保存，容器 Id 未落库；清空生效`;
        } finally {
          await gatewayCall(token, '/gateway/roles/Delete', 'POST', { roleId }).catch(() => {});
        }
      });
    }

    if (want(['categories', '分类'])) {
      await testCase(page, 'page-category-tree', '分类管理：树 / 选中 / 新增下级弹窗', async () => {
        await goto(page, '/categories');
        const nodes = await page.locator('.el-tree-node').count();
        if (nodes === 0) throw new Error('分类树没有节点');

        await page.locator('.el-tree-node__label, .node__main').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        if (!(await visibleText(page, '.detail-title h3'))) throw new Error('右侧详情没有内容');

        await clickVisibleText(page, '新增下级');
        await page.waitForTimeout(350);
        const dlg = await dialogSnapshot(page);
        if (!dlg) throw new Error('「新增下级」没有打开弹窗');
        await cancelDialog(page);
        return `${nodes} 个分类节点`;
      });

      await testCase(page, 'page-category-crud', '分类管理：新增 → 修改 → 停用 → 启用 → 删除', async () => {
        const token = await apiToken(USER, PASS);
        const stamp = Date.now().toString().slice(-6);
        const name = `深度回归分类${stamp}`;
        const renamed = `${name}已改`;

        // 按名字前缀清场：用例中途失败也不会在分类树里留下垃圾节点
        const cleanup = async () => {
          const tree = await gatewayCall(token, '/gateway/categories/Tree?includeDisabled=true', 'GET').catch(() => null);
          const flat = [];
          const walk = (list) => (list || []).forEach((n) => { flat.push(n); walk(n.children); });
          walk(Array.isArray(tree) ? tree : tree?.items);
          for (const n of flat.filter((x) => String(x.categoryName || '').startsWith('深度回归分类'))) {
            await gatewayCall(token, '/gateway/categories/Delete', 'POST', { categoryId: n.id }).catch(() => {});
          }
        };

        try {
          await goto(page, '/categories');
          const search = page.locator('.head__search input').first();

          // 1) 新增一级分类
          await clickVisibleText(page, '新增一级分类');
          await page.waitForTimeout(350);
          let dlg = page.locator('.el-dialog:visible').last();
          await dlg.locator('.el-form-item:has-text("分类名称") input').first().fill(name);
          await dlg.locator('.el-form-item:has-text("分类编码") input').first().fill(`deep${stamp}`);
          await dlg.locator('button:has-text("保存")').first().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('新增分类没有成功提示');
          }

          // 2) 搜索定位新节点 → 选中 → 修改
          await search.fill(name);
          await page.waitForTimeout(500);
          const node = page.locator(`.node__main:has-text("${name}")`).first();
          if ((await node.count()) === 0) throw new Error('新增的分类没有出现在树里');
          await node.click({ timeout: 8000 });
          await page.waitForTimeout(300);

          await clickVisibleText(page, '修改');
          await page.waitForTimeout(350);
          dlg = page.locator('.el-dialog:visible').last();
          const nameInput = dlg.locator('.el-form-item:has-text("分类名称") input').first();
          await nameInput.fill(renamed);
          await dlg.locator('button:has-text("保存")').first().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('修改分类没有成功提示');
          }

          // 3) 停用 → 启用（分类走 Update，没有独立的状态接口）
          await search.fill(renamed);
          await page.waitForTimeout(500);
          await page.locator(`.node__main:has-text("${renamed}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '停用');
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('停用分类没有成功提示');
          }
          await search.fill(renamed);
          await page.waitForTimeout(500);
          await page.locator(`.node__main:has-text("${renamed}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '启用');
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('启用分类没有成功提示');
          }

          // 4) 删除
          await search.fill(renamed);
          await page.waitForTimeout(500);
          await page.locator(`.node__main:has-text("${renamed}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await clickVisibleText(page, '删除');
          await page.waitForTimeout(350);
          const confirmBox = page.locator('.el-dialog:visible').last();
          if (!(await confirmBox.innerText()).includes('删除')) throw new Error('删除没有确认框');
          await confirmBox.locator('button:has-text("删除")').last().click({ timeout: 8000 });
          await page.waitForTimeout(900);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('删除分类没有成功提示');
          }
          await search.fill(renamed);
          await page.waitForTimeout(500);
          if ((await page.locator(`.node__main:has-text("${renamed}")`).count()) > 0) {
            throw new Error('删除后节点还在树里');
          }
          return `${name} 新增 → 改名 → 停用 → 启用 → 删除 全部生效`;
        } finally {
          await cleanup();
        }
      });
    }

    if (want(['config', '优惠优先级', 'coupons'])) {
      await testCase(page, 'page-coupon-config', '优惠优先级配置：平台下拉 / 切换 / 保存', async () => {
        await goto(page, '/coupons/config');
        const select = page.locator('.head__platform').first();
        if ((await select.count()) === 0) throw new Error('没有平台下拉');
        await select.click({ timeout: 8000 });
        await page.waitForTimeout(350);
        const option = page.locator('.el-select-dropdown:visible .el-select-dropdown__item').first();
        if ((await option.count()) === 0) throw new Error('平台下拉没有选项');
        await option.click({ timeout: 8000 });
        await page.waitForTimeout(650);

        const field = page.locator('.el-form-item .el-select').first();
        await field.click({ timeout: 8000 });
        await page.waitForTimeout(350);
        const items = page.locator('.el-select-dropdown:visible .el-select-dropdown__item');
        if ((await items.count()) < 2) throw new Error('优先级只有不到 2 个选项');
        await items.nth(0).click({ timeout: 8000 });
        await page.waitForTimeout(250);

        await clickVisibleText(page, '保存');
        await page.waitForTimeout(800);
        const ok = page.locator('.el-message--success:visible');
        if ((await ok.count()) === 0) throw new Error('保存后没有成功提示');

        // 恢复默认值：把表单拉回「当前生效值」，只是填回来、不落库
        await clickVisibleText(page, '恢复默认值');
        await page.waitForTimeout(500);
        if ((await page.locator('.el-message--info:visible').count()) === 0) {
          throw new Error('恢复默认值没有提示');
        }
        return '平台切换 + 优先级保存 + 恢复默认值';
      });
    }

    if (want(['point', '积分规则', 'points'])) {
      await testCase(page, 'page-point-rules', '积分规则：字段渲染 / 恢复默认值 / 保存', async () => {
        await goto(page, '/points/rules');
        const fields = await page.locator('.el-form-item').count();
        if (fields === 0) throw new Error('积分规则没有字段');
        const inputs = await page.locator('.el-form-item input:visible').count();
        if (inputs === 0) throw new Error('积分规则没有可编辑控件');

        await clickVisibleText(page, '保存');
        await page.waitForTimeout(800);
        const ok = page.locator('.el-message--success:visible');
        if ((await ok.count()) === 0) throw new Error('保存后没有成功提示');

        await clickVisibleText(page, '恢复默认值');
        await page.waitForTimeout(500);
        if ((await page.locator('.el-message--info:visible').count()) === 0) {
          throw new Error('恢复默认值没有提示');
        }
        return `${fields} 个字段 / 保存 + 恢复默认值`;
      });
    }

    if (want(['regions', '地区'])) {
      await testCase(page, 'page-regions', '地区地址配置：省份 / 城市 / 区县三级编辑', async () => {
        await goto(page, '/platforms/regions');
        const provinces = await page.locator('.province, .region').count();
        const addButtons = await page.locator('button:has-text("新增省份"):visible').count();
        if (addButtons === 0) throw new Error('没有「新增省份」按钮');
        const saveButtons = await page.locator('button:has-text("保存"):visible').count();
        if (saveButtons === 0) throw new Error('没有「保存」按钮');
        return `${provinces} 个省份节点`;
      });

      await testCase(page, 'page-regions-crud', '地区地址：新增省 / 市 / 区 → 保存 → 删除 → 保存', async () => {
        const stamp = Date.now().toString().slice(-6);
        const provinceName = `深度回归省${stamp}`;
        const cityName = `深度回归市${stamp}`;
        const districtName = `深度回归区${stamp}`;

        await goto(page, '/platforms/regions');
        const before = await page.locator('.province').count();

        // 按输入框的**值**找省份：v-model 只更新 DOM 属性，不写 value 特性，
        // 所以 `input[value="x"]` 这种选择器永远匹配不到（会误判成「保存丢了」）。
        const findProvince = async (name) => {
          const total = await page.locator('.province').count();
          for (let i = 0; i < total; i++) {
            const p = page.locator('.province').nth(i);
            const v = await p.locator('.name-input input').first().inputValue().catch(() => '');
            if (v === name) return p;
          }
          return null;
        };

        // 新增省份 → 填名 → 新增城市 → 新增区县
        await clickVisibleText(page, '新增省份');
        await page.waitForTimeout(300);
        const province = page.locator('.province').last();
        await province.locator('.name-input input').first().fill(provinceName);
        await province.locator('.code-input input').first().fill(`DR${stamp}`);

        await province.locator('button:has-text("新增城市")').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const city = province.locator('.city').last();
        await city.locator('.name-input input').first().fill(cityName);
        await city.locator('.code-input input').first().fill(`DRC${stamp}`);

        await city.locator('button:has-text("新增区县")').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const district = city.locator('.district').last();
        await district.locator('.name-input input').first().fill(districtName);
        await district.locator('.code-input input').first().fill(`DRD${stamp}`);

        if ((await page.locator('.province').count()) !== before + 1) {
          throw new Error('新增省份后数量没有增加');
        }

        // 再建一个城市与区县，然后删掉 —— 覆盖「删除城市 / 删除区县」两个按钮
        await province.locator('button:has-text("新增城市")').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const extraCity = province.locator('.city').last();
        await extraCity.locator('.name-input input').first().fill(`${cityName}待删`);
        await extraCity.locator('button:has-text("新增区县")').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        const extraDistrict = extraCity.locator('.district').last();
        await extraDistrict.locator('.name-input input').first().fill(`${districtName}待删`);

        const cityCount = await province.locator('.city').count();
        await extraDistrict.locator('button:has-text("删除")').first().click({ timeout: 8000 });
        await page.waitForTimeout(250);
        if ((await extraCity.locator('.district').count()) !== 0) throw new Error('删除区县没有生效');
        await extraCity.locator('button:has-text("删除城市")').first().click({ timeout: 8000 });
        await page.waitForTimeout(250);
        if ((await province.locator('.city').count()) !== cityCount - 1) {
          throw new Error('删除城市没有生效');
        }

        // 保存 → 重新加载 → 数据必须还在（证明真的落库了）
        await clickVisibleText(page, '保存');
        await page.waitForTimeout(1200);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('保存地区配置没有成功提示');
        }
        await goto(page, '/platforms/regions');
        const saved = await findProvince(provinceName);
        if (!saved) throw new Error('保存后重新加载，新增的省份不见了');

        // 删除 → 再保存 → 数据回到原样（不给小程序地址簿留测试数据）
        await saved.locator('button:has-text("删除省份")').first().click({ timeout: 8000 });
        await page.waitForTimeout(300);
        await clickVisibleText(page, '保存');
        await page.waitForTimeout(1200);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('删除后保存没有成功提示');
        }
        await goto(page, '/platforms/regions');
        if (await findProvince(provinceName)) throw new Error('删除后重新加载，省份还在');
        if ((await page.locator('.province').count()) !== before) {
          throw new Error(`省份数量没有回到原值：${await page.locator('.province').count()} ≠ ${before}`);
        }
        return `${provinceName} 省 / 市 / 区 新增 → 保存 → 删除 → 保存，数量回到 ${before}`;
      });
    }

    if (want(['reports', '报表'])) {
      await testCase(page, 'page-reports', '报表：四张表 + 时间区间 + 营销下钻', async () => {
        const routes = [
          ['/reports/business', '经营报表'],
          ['/reports/marketing', '营销效果报表'],
          ['/reports/seckill', '秒杀效果报表'],
          ['/reports/point', '积分报表'],
        ];
        const titles = [];
        for (const [route, expect] of routes) {
          await goto(page, route);
          const title = await visibleText(page, '.head__title');
          if (title !== expect) throw new Error(`${route} 标题是「${title}」，预期「${expect}」`);
          const rows = await page.locator('.panel .row').count();
          const tables = await page.locator('.panel .el-table').count();
          if (rows === 0 && tables === 0) throw new Error(`${route} 没有任何指标或表格`);
          titles.push(title);
        }

        // 时间区间：经营报表的四个档位逐个切
        await goto(page, '/reports/business');
        const segs = await page.locator('.el-segmented__item').allTextContents();
        if (segs.length < 4) throw new Error(`时间档位只有 ${segs.length} 个`);
        for (const label of segs.map((s) => s.trim())) {
          await page.locator(`.el-segmented__item:has-text("${label}")`).first().click({ timeout: 8000 });
          await page.waitForTimeout(600);
        }

        // 营销下钻：逐活动表的「明细」按钮必须能进参与记录页
        await goto(page, '/reports/marketing');
        const drill = page.locator('.el-table button:has-text("明细"):visible').first();
        if ((await drill.count()) === 0) throw new Error('营销报表没有下钻按钮（逐活动表为空）');
        await drill.click({ timeout: 8000 });
        await page.waitForTimeout(700);
        if (!page.url().includes('/coupons/activity-records')) {
          throw new Error(`下钻没有进参与记录页：${page.url()}`);
        }
        return titles.join(' / ');
      });
    }

    if (want(['search-index', '对账', '索引'])) {
      await testCase(page, 'page-search-index', '索引对账：重新对账 + 补写并清理', async () => {
        await goto(page, '/search-index');
        const stats = await page.locator('.stat').count();
        if (stats < 4) throw new Error(`对账统计只有 ${stats} 项`);
        const verdict = await visibleText(page, '.verdict');
        if (!verdict) throw new Error('没有对账结论');

        await clickVisibleText(page, '重新对账');
        await page.waitForTimeout(900);
        await clickVisibleText(page, '补写并清理');
        await page.waitForTimeout(1200);
        return `结论：${verdict}`;
      });
    }

    if (want(['pickup', '核销', 'orders'])) {
      await testCase(page, 'page-pickup-verify', '取货码核销：空提交拦截 + 错误码提示', async () => {
        await goto(page, '/orders/pickup-verify');
        const input = page.locator('.form__control input').first();
        if ((await input.count()) === 0) throw new Error('没有取货码输入框');

        await clickVisibleText(page, '核销');
        await page.waitForTimeout(400);
        const warn = page.locator('.el-message--warning:visible');
        if ((await warn.count()) === 0) throw new Error('空提交没有提示');

        // 非法码：必须有可见反馈，不能静默失败。
        // 后端对查不到的取货码返回 404，前端统一由 request 弹错误 tip 并清空上一次结果；
        // 只有「码能解出来但订单状态不对」才会走到页面内的 result__msg--warn 分支。
        await input.fill('NOT-A-REAL-PICKUP-CODE');
        await clickVisibleText(page, '核销');
        await page.waitForTimeout(900);
        const msg = await visibleText(page, '.result__msg');
        const toast = await page.locator('.el-message--error:visible').count();
        if (!msg && toast === 0) throw new Error('非法取货码没有结果反馈');
        return msg ? msg.slice(0, 30) : '错误 tip 已提示';
      }, { allow4xx: [/VerifyPickupCode/] });

      await testCase(page, 'page-pickup-verify-success', '取货码核销：真实自提单核销成功（40 → 50）', async () => {
        const token = await apiToken(USER, PASS);
        const fixture = await ensureSellableProduct(token, 3);

        try {
          const { orderNo, orderId } = await createPendingOrder(token, 0, fixture);

          // 支付 → 备货完成（服务端用 RSA 把订单号加密成取货码）
          await gatewayCall(token, '/gateway/admin/orders/SimulatePayment', 'POST', {
            orderNo, succeed: true, remark: '深度回归自提',
          });
          const ready = await gatewayCall(token, '/gateway/admin/orders/SelfPickupReady', 'POST', {
            orderNo, remark: '深度回归备货',
          });
          const pickupCode = ready?.pickupCode;
          if (!pickupCode) throw new Error('备货完成没有返回取货码');

          await goto(page, '/orders/pickup-verify');
          const input = page.locator('.form__control input').first();
          await input.fill(pickupCode);
          await clickVisibleText(page, '核销');
          await page.waitForTimeout(1200);

          const msg = await visibleText(page, '.result__msg');
          if (!msg.includes('核销成功')) throw new Error(`核销结果不是成功：${msg || '(空)'}`);
          // 详情接口的入参是 orderId（不是 orderNo），传错会得到「订单信息不正确」
          const order = await gatewayCall(token, '/gateway/admin/orders/Detail', 'POST', { orderId });
          if (Number(order?.status) !== 50) throw new Error(`核销后订单状态是 ${order?.status}，预期 50`);
          return `订单 ${orderNo} 取货码核销成功，状态 40 → 50`;
        } finally {
          // 自建的自提商品用完就删，不给商品列表留测试数据
          if (fixture?.createdProductId) {
            await gatewayCall(token, '/gateway/products/Delete', 'POST', {
              productId: fixture.createdProductId,
            }).catch(() => {});
          }
        }
      });
    }

    if (want(['inventory', '库存', 'products'])) {
      await testCase(page, 'page-inventory-adjust', '库存调整：SKU 行 + 调整量 / 原因校验 + 提交', async () => {
        const token = await apiToken(USER, PASS);
        const ids = await collectIds(token);
        const productId = idOf(ids.product);
        if (!productId) throw new Error('取不到商品 Id');

        await goto(page, `/products/inventory/${productId}`);
        const rows = await page.locator('.el-table__row').count();
        if (rows === 0) throw new Error('调整页没有 SKU 行');

        // 空提交：后端校验必须给出提示，不能静默吞掉
        await page.locator('.el-table__row button:has-text("提交"):visible').first().click({ timeout: 8000 });
        await page.waitForTimeout(600);
        const bad = page.locator('.el-message--error:visible, .el-message--warning:visible');
        if ((await bad.count()) === 0) throw new Error('调整量与原因都为空却没有提示');

        // 正常调整：+1 后应当出现成功提示
        const row = page.locator('.el-table__row').first();
        await row.locator('input').first().fill('1');
        await row.locator('input').nth(1).fill('深度回归库存调整');
        await row.locator('button:has-text("提交")').first().click({ timeout: 8000 });
        await page.waitForTimeout(900);
        const ok = page.locator('.el-message--success:visible');
        if ((await ok.count()) === 0) throw new Error('合法调整没有成功提示');
        // 还原：再 -1 把库存放回去。不回滚的话每跑一次回归这个 SKU 就多一件，
        // 几轮之后「当前可用」跟别的用例里的期望值就对不上了。
        await goto(page, `/products/inventory/${productId}`);
        const back = page.locator('.el-table__row').first();
        await back.locator('input').first().fill('-1');
        await back.locator('input').nth(1).fill('深度回归库存还原');
        await back.locator('button:has-text("提交")').first().click({ timeout: 8000 });
        await page.waitForTimeout(900);
        const restored = await page.locator('.el-message--success:visible').count();
        if (restored === 0) throw new Error('库存还原提交没有成功提示');
        return `${rows} 个 SKU，+1 → -1 已还原`;
      }, { allow4xx: [/inventory\/Adjust/] });
    }

    if (want(['product-form', '商品编辑', '规格', '商品审核', '批量'])) {
      await testCase(page, 'page-product-form', '商品编辑：规格项 + SKU 矩阵', async () => {
        const token = await apiToken(USER, PASS);
        const ids = await collectIds(token);
        const productId = idOf(ids.product);
        if (!productId) throw new Error('取不到商品 Id');

        await goto(page, `/products/edit/${productId}`);
        const secs = await page.locator('.sec__title').allTextContents();
        if (!secs.some((s) => s.includes('规格项'))) throw new Error('没有规格项分区');
        if (!secs.some((s) => s.includes('SKU'))) throw new Error('没有 SKU 分区');
        const specRows = await page.locator('.spec').count();
        const skuRows = await page.locator('.el-table__row').count();
        if (skuRows === 0) throw new Error('SKU 矩阵没有行');

        // 富文本详情：工具栏按钮 + contenteditable 都要真的能用
        const editor = page.locator('.rich-editor__body').first();
        if ((await editor.count()) === 0) throw new Error('商品详情没有富文本编辑器');
        await editor.click({ timeout: 8000 });
        await page.keyboard.type('深度回归详情');
        await page.keyboard.press('Control+a');

        // 逐个点工具栏按钮：加粗 / 斜体 / 标题 / 列表，每个都要在 HTML 上留下痕迹
        const toolbarChecks = [
          ['加粗', /<(b|strong)>/i],
          ['斜体', /<(i|em)>/i],
          ['标题', /<h3>/i],
          ['列表', /<ul>/i],
        ];
        for (const [title, pattern] of toolbarChecks) {
          await page.locator(`.rich-editor__toolbar button[title="${title}"]`).click({ timeout: 8000 });
          await page.waitForTimeout(250);
          const html = await editor.innerHTML();
          if (!pattern.test(html)) throw new Error(`富文本「${title}」没有生效：${html.slice(0, 120)}`);
        }

        // 插入图片：走真实上传（工具栏「图」→ 隐藏 file input）
        const png = Buffer.from(
          'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
          'base64',
        );
        await page.locator('.rich-editor__toolbar button[title="插入图片"]').click({ timeout: 8000 });
        await page.locator('.rich-editor input[type=file]').setInputFiles(
          { name: 'rich.png', mimeType: 'image/png', buffer: png });
        await page.waitForTimeout(1800);
        if (!(await editor.innerHTML()).includes('<img')) throw new Error('富文本插入图片没有生效');

        return `${specRows} 个规格项 / ${skuRows} 个 SKU / 富文本 4 个格式 + 插图可用`;
      });

      await testCase(page, 'page-product-bulk-edit', '新建商品：批量设置 SKU（空提交拦截 → 应用）', async () => {
        await goto(page, '/products/create');

        // 先造一个规格值，让 SKU 表出现一行
        await clickVisibleText(page, '+ 添加规格项');
        await page.waitForTimeout(300);
        const spec = page.locator('.spec').first();
        await spec.locator('.spec__name input').first().fill('颜色');
        const draft = spec.locator('.spec__draft input').first();
        await draft.fill('黑色');
        await draft.press('Enter');
        await page.waitForTimeout(500);

        // 规格值是 el-tag，带一个「关闭此标签」×：点它要真的把值删掉
        // （删除会连带重算 SKU 组合，是最容易「点了没反应」的一个按钮）
        if ((await spec.locator('.el-tag').count()) !== 1) throw new Error('规格值没有落成标签');
        await spec.locator('.el-tag__close').first().click({ timeout: 8000 });
        await page.waitForTimeout(400);
        if ((await spec.locator('.el-tag').count()) !== 0) throw new Error('删除规格值没有生效');
        // 再填回来，后面的批量设置需要至少一个规格值
        await draft.fill('黑色');
        await draft.press('Enter');
        await page.waitForTimeout(400);

        await clickVisibleText(page, '批量设置');
        await page.waitForTimeout(400);
        const dlg = page.locator('.el-dialog:visible').last();
        if (!(await dlg.innerText()).includes('批量设置 SKU')) throw new Error('批量设置弹窗没打开');

        // 两项都留空 → 必须拦住（否则会「应用」出一堆 0）
        await dlg.locator('button:has-text("应用")').click({ timeout: 8000 });
        await page.waitForTimeout(400);
        if ((await page.locator('.el-message--warning:visible').count()) === 0) {
          throw new Error('售价与库存都留空时没有提示');
        }

        await dlg.locator('.el-form-item:has-text("售价") input').first().fill('88');
        await dlg.locator('.el-form-item:has-text("库存") input').first().fill('7');
        await dlg.locator('button:has-text("应用")').click({ timeout: 8000 });
        await page.waitForTimeout(500);

        const row = page.locator('.el-table__row').first();
        const price = await row.locator('input[type=number]').nth(0).inputValue();
        const stock = await row.locator('input[type=number]').nth(1).inputValue();
        if (Number(price) !== 88 || Number(stock) !== 7) {
          throw new Error(`批量应用后 SKU 价格/库存是 ${price}/${stock}，预期 88/7`);
        }
        return '空提交被拦，售价 88 / 库存 7 已套用到 1 个 SKU';
      });

      await testCase(page, 'page-product-audit', '商品审核：结论切换 + 驳回原因必填 + 通过落库', async () => {
        const token = await apiToken(USER, PASS);
        const name = `深测审核商品${`${Date.now()}`.slice(-8)}`;
        const productId = await createDraftProduct(token, name, 1);

        try {
          await goto(page, '/products/audit');
          const search = page.locator('.head__search input').first();
          await search.fill(name);
          await page.waitForTimeout(700);
          const row = page.locator('.el-table__row', { hasText: name }).first();
          if ((await row.count()) === 0) throw new Error('新建的待审核商品没有出现在审核列表');

          await row.locator('button:has-text("审核")').first().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          const dlg = page.locator('.el-dialog:visible').last();
          if (!(await dlg.innerText()).includes('商品审核')) throw new Error('审核弹窗没打开');

          // 默认结论是「通过」：驳回原因不该出现（visibleWhen）
          const reasonItem = dlg.locator('.el-form-item:has-text("驳回原因")');
          if (await reasonItem.isVisible().catch(() => false)) {
            throw new Error('结论为「通过」时不应显示驳回原因');
          }

          // 切到「驳回」：原因出现且必填 —— 空提交必须被拦
          // 结论用 el-radio-button（分段控件）渲染，不是 el-radio
          await dlg.locator('.el-radio-button:has-text("驳回")').first().click({ timeout: 8000 });
          await page.waitForTimeout(350);
          if (!(await reasonItem.isVisible().catch(() => false))) {
            throw new Error('切到「驳回」后没有出现驳回原因');
          }
          await dlg.locator('button:has-text("提交审核结果")').click({ timeout: 8000 });
          await page.waitForTimeout(450);
          if ((await dlg.locator('.el-form-item__error:visible').count()) === 0) {
            throw new Error('驳回不填原因也能提交');
          }

          // 切回「通过」并提交
          await dlg.locator('.el-radio-button:has-text("通过")').first().click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await dlg.locator('button:has-text("提交审核结果")').click({ timeout: 8000 });
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('提交审核结果没有成功提示');
          }

          const detail = await gatewayCall(token, `/gateway/products/Detail?productId=${productId}`, 'GET');
          if (Number(detail?.auditStatus) !== 20) {
            throw new Error(`审核通过后 auditStatus 是 ${detail?.auditStatus}，预期 20`);
          }
          return `${name} 驳回原因必填 + 通过后 auditStatus=20`;
        } finally {
          await gatewayCall(token, '/gateway/products/Delete', 'POST', { productId }).catch(() => {});
        }
      });
    }

    if (want(['seckill', '秒杀', '场次商品'])) {
      await testCase(page, 'page-seckill-items', '场次商品：SKU 校验提示 + 添加 / 删除', async () => {
        const token = await apiToken(USER, PASS);
        const ids = await collectIds(token);
        const sessionId = idOf(ids.session);
        if (!sessionId) throw new Error('取不到场次 Id');

        await goto(page, `/seckill/items/${sessionId}`);
        const heading = await visibleText(page, '.page-header__title, .head__title');
        if (!heading) throw new Error('场次商品页没有标题');

        const skuInput = page.locator('.el-form-item:has-text("SKU") input').first();
        if ((await skuInput.count()) === 0) throw new Error('没有 SKU 输入项');
        await skuInput.fill('');
        await clickVisibleText(page, '添加');
        await page.waitForTimeout(500);
        const err = page.locator('.el-form-item__error:visible');
        if ((await err.count()) === 0) throw new Error('SKU 为空提交后输入框下方没有报错');
        return `${await page.locator('.el-table__row').count()} 行场次商品`;
      });

      await testCase(page, 'page-seckill-items-crud', '场次商品：添加 → 落库 → 删除', async () => {
        const token = await apiToken(USER, PASS);

        // 找一个真实有 SKU 的商品：场次商品必须挂到真实 SKU 上
        const list = await gatewayCall(token, '/gateway/products/List?page=1&pageSize=20', 'GET');
        const products = Array.isArray(list) ? list : (list?.items || []);
        let skuId = null;
        for (const p of products) {
          const pid = p.productId ?? p.id;
          // 参数名是 spuId（不是 productId）：写成 productId 会 400「商品 Id 不合法」，
          // 而被 catch 吞掉之后表现成「找不到带 SKU 的商品」，排查方向完全错。
          const skus = await gatewayCall(token, `/gateway/products/Skus?spuId=${pid}`, 'GET').catch(() => null);
          const arr = Array.isArray(skus) ? skus : (skus?.items || []);
          const hit = arr.find((s) => s.id ?? s.skuId);
          if (hit) { skuId = String(hit.id ?? hit.skuId); break; }
        }
        if (!skuId) throw new Error('找不到带 SKU 的商品，无法测场次商品');

        const sessionId = await gatewayCall(token, '/gateway/marketing/seckill/sessions/Create', 'POST', {
          sessionName: `深度回归场次商品${Date.now().toString().slice(-6)}`,
          startTime: new Date(Date.now() + 6 * 3600 * 1000).toISOString(),
          endTime: new Date(Date.now() + 8 * 3600 * 1000).toISOString(),
          sortOrder: 0,
          platformId: 0,
        });

        try {
          await goto(page, `/seckill/items/${sessionId}`);
          await fillField(page, 'SKU Id', skuId);
          await fillField(page, '秒杀价', '9.9');
          await fillField(page, '秒杀库存', '2');
          await clickVisibleText(page, '添加');
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('添加场次商品没有成功提示');
          }
          const rows = await page.locator('.el-table__row').count();
          if (rows === 0) throw new Error('添加后表格里没有行');

          // 删除：ElMessageBox 确认框 → 删除
          await page.locator('.el-table__row button:has-text("删除")').first().click({ timeout: 8000 });
          await page.waitForTimeout(400);
          const box = page.locator('.el-message-box:visible');
          if ((await box.count()) === 0) throw new Error('删除场次商品没有确认框');
          await box.locator('button:has-text("删除")').last().click({ timeout: 8000 });
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('删除场次商品没有成功提示');
          }
          if ((await page.locator('.el-table__row').count()) !== 0) {
            throw new Error('删除后表格里还有行');
          }

          // 返回场次列表：这一页的出口按钮
          await clickVisibleText(page, '返回场次列表');
          await page.waitForTimeout(600);
          if (!page.url().includes('/seckill') || page.url().includes('/items/')) {
            throw new Error(`返回场次列表没有回到列表页：${page.url()}`);
          }
          return `SKU ${skuId} 添加 → 删除 → 返回列表 全部生效`;
        } finally {
          // 场次没有删除接口，收尾用「取消」把它挪出未开始列表
          await gatewayCall(token, '/gateway/marketing/seckill/sessions/Finish', 'POST', {
            sessionId, cancel: true,
          }).catch(() => {});
        }
      });
    }

    if (want(['order-detail', '订单详情', 'orders', '退款'])) {
      await testCase(page, 'page-order-detail', '订单详情：金额 / 物流 / 支付记录 / 商品明细', async () => {
        const token = await apiToken(USER, PASS);
        const list = await gatewayCall(token, '/gateway/admin/orders/List', 'POST', { page: 1, pageSize: 1 });
        const first = list?.items?.[0];
        if (!first) throw new Error('订单列表为空，取不到订单详情 Id');

        await goto(page, `/orders/detail/${first.orderId}`);
        const sections = await page.locator('.section__title, .card__title').allTextContents();
        const need = ['金额', '支付记录', '商品明细'];
        const missing = need.filter((n) => !sections.some((s) => s.includes(n)));
        if (missing.length) throw new Error(`订单详情缺少分区：${missing.join('/')}`);
        const amount = await visibleText(page, '.amount__value');
        if (!amount) throw new Error('订单详情没有实付金额');
        return `${sections.length} 个分区 / 实付 ${amount}`;
      });

      await testCase(page, 'page-order-detail-ship', '订单详情：快递发货（物流公司与运单号必填）→ 状态 20→30', async () => {
        const token = await apiToken(USER, PASS);
        const fixture = await ensureSellableProduct(token, 1);
        const { orderNo, orderId } = await createPendingOrder(token, 0, fixture);
        await gatewayCall(token, '/gateway/admin/orders/SimulatePayment', 'POST', {
          orderNo, succeed: true, remark: '深度回归快递发货',
        });

        await goto(page, `/orders/detail/${orderId}`);
        await clickVisibleText(page, '快递发货');
        await page.waitForTimeout(500);
        const dlg = page.locator('.el-dialog:visible').last();
        if (!(await dlg.innerText()).includes('订单发货')) throw new Error('发货弹窗没打开');

        // 空提交：物流公司与运单号都要报错（用户要求提交时校验）
        await dlg.locator('button:has-text("快递发货")').first().click({ timeout: 8000 });
        await page.waitForTimeout(450);
        const errs = await dlg.locator('.el-form-item__error:visible').allTextContents();
        if (errs.length < 2) throw new Error(`空提交只报了 ${errs.length} 条错误：${errs.join('/')}`);

        // 选物流公司 + 填运单号 → 提交
        await dlg.locator('.el-form-item:has-text("物流公司") .el-select').first().click({ timeout: 8000 });
        await page.waitForTimeout(400);
        const option = page.locator('.el-select-dropdown:visible .el-select-dropdown__item:not(.is-disabled)').first();
        if ((await option.count()) === 0) throw new Error('物流公司下拉没有可选项');
        await option.click({ timeout: 8000 });
        await page.waitForTimeout(250);
        await dlg.locator('.el-form-item:has-text("运单号") input').first().fill(`SF${Date.now()}`);
        await dlg.locator('button:has-text("快递发货")').first().click({ timeout: 8000 });
        await page.waitForTimeout(1200);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('快递发货没有成功提示');
        }

        const order = await gatewayCall(token, '/gateway/admin/orders/Detail', 'POST', { orderId });
        if (Number(order?.status) !== 30) throw new Error(`发货后订单状态是 ${order?.status}，预期 30`);
        return `订单 ${orderNo} 快递发货成功，状态 20 → 30`;
      });

      await testCase(page, 'page-order-detail-virtual', '订单详情：虚拟发货（发货内容必填）→ 状态 20→30', async () => {
        const token = await apiToken(USER, PASS);
        const fixture = await ensureSellableProduct(token, 2);
        const { orderNo, orderId } = await createPendingOrder(token, 0, fixture);
        await gatewayCall(token, '/gateway/admin/orders/SimulatePayment', 'POST', {
          orderNo, succeed: true, remark: '深度回归虚拟发货',
        });

        try {
          await goto(page, `/orders/detail/${orderId}`);
          await clickVisibleText(page, '虚拟发货');
          await page.waitForTimeout(500);
          const dlg = page.locator('.el-dialog:visible').last();

          // 内容空着不许发：顾客拿不到卡号等于付了钱什么都没得到
          await dlg.locator('button:has-text("虚拟发货")').first().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          if ((await dlg.locator('.el-form-item__error:visible').count()) === 0) {
            throw new Error('虚拟发货不填内容也能提交');
          }

          await dlg.locator('textarea').first().fill(`卡号 DEEP-${Date.now().toString().slice(-6)}`);
          await dlg.locator('button:has-text("虚拟发货")').first().click({ timeout: 8000 });
          await page.waitForTimeout(1200);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('虚拟发货没有成功提示');
          }

          const order = await gatewayCall(token, '/gateway/admin/orders/Detail', 'POST', { orderId });
          if (Number(order?.status) !== 30) throw new Error(`虚拟发货后状态是 ${order?.status}，预期 30`);
          return `订单 ${orderNo} 虚拟发货成功，状态 20 → 30`;
        } finally {
          if (fixture?.createdProductId) {
            await gatewayCall(token, '/gateway/products/Delete', 'POST', {
              productId: fixture.createdProductId,
            }).catch(() => {});
          }
        }
      });

      await testCase(page, 'page-refund-dialog-whole', '订单详情：整单退款（原因必填）→ 退款单落库', async () => {
        const token = await apiToken(USER, PASS);
        const fixture = await ensureSellableProduct(token, 1);
        const { orderNo, orderId } = await createPendingOrder(token, 0, fixture);
        await gatewayCall(token, '/gateway/admin/orders/SimulatePayment', 'POST', {
          orderNo, succeed: true, remark: '深度回归退款',
        });

        await goto(page, `/orders/detail/${orderId}`);
        await clickVisibleText(page, '代客退款');
        await page.waitForTimeout(500);
        const dlg = page.locator('.el-dialog:visible').last();
        if (!(await dlg.innerText()).includes('发起退款')) throw new Error('退款弹窗没打开');

        // 原因空着不许提交（会记录到审计日志）
        await dlg.locator('button:has-text("确认退款")').click({ timeout: 8000 });
        await page.waitForTimeout(450);
        if ((await dlg.locator('.el-form-item__error:visible').count()) === 0) {
          throw new Error('不填退款原因也能提交');
        }

        await dlg.locator('textarea').first().fill('深度回归整单退款');
        await dlg.locator('button:has-text("确认退款")').click({ timeout: 8000 });
        await page.waitForTimeout(1300);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('整单退款没有成功提示');
        }

        const refunds = await gatewayCall(token, '/gateway/admin/orders/Refunds', 'POST', { orderId });
        // 注意：这个接口返回的 DTO **没有 orderNo**（退款单按 refundNo 标识，
        // 因为它是按 orderId 查出来的），拿 orderNo 去比会永远匹配不到。
        if (!refunds?.length) throw new Error('退款单没有落库');
        const hit = refunds[refunds.length - 1];
        return `订单 ${orderNo} 整单退款单已创建（${hit.refundNo}）`;
      });

      await testCase(page, 'page-refund-dialog-partial', '订单详情：部分退款（勾选商品行 + 金额）→ 退款单落库', async () => {
        const token = await apiToken(USER, PASS);
        const fixture = await ensureSellableProduct(token, 1);
        // 数量 2 才谈得上「只退一件」
        const { orderNo, orderId } = await createPendingOrder(token, 0, fixture, 2);
        await gatewayCall(token, '/gateway/admin/orders/SimulatePayment', 'POST', {
          orderNo, succeed: true, remark: '深度回归部分退款',
        });

        await goto(page, `/orders/detail/${orderId}`);
        await clickVisibleText(page, '代客退款');
        await page.waitForTimeout(500);
        const dlg = page.locator('.el-dialog:visible').last();

        // 切到「只退指定商品」→ 勾一行 → 填金额与原因
        await dlg.locator('.el-radio-button:has-text("只退指定商品")').first().click({ timeout: 8000 });
        await page.waitForTimeout(400);

        // 未勾选就提交：必须拦住
        await dlg.locator('textarea').first().fill('深度回归部分退款');
        await dlg.locator('button:has-text("确认退款")').click({ timeout: 8000 });
        await page.waitForTimeout(450);
        if ((await page.locator('.el-message--warning:visible').count()) === 0) {
          throw new Error('没勾选商品行也能提交');
        }

        await dlg.locator('.el-table__row .el-checkbox').first().click({ timeout: 8000 });
        await page.waitForTimeout(350);
        const amountInput = dlg.locator('.el-table__row input[type=number]').last();
        await amountInput.fill('5');
        await page.waitForTimeout(250);
        await dlg.locator('button:has-text("确认退款")').click({ timeout: 8000 });
        await page.waitForTimeout(1300);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('部分退款没有成功提示');
        }

        const refunds = await gatewayCall(token, '/gateway/admin/orders/Refunds', 'POST', { orderId });
        const hit = (refunds || []).find((r) => Number(r.amount) === 5);
        if (!hit) throw new Error(`部分退款单没有落库：${JSON.stringify(refunds || []).slice(0, 200)}`);
        if (Number(hit.amount) !== 5) throw new Error(`退款金额是 ${hit.amount}，预期 5`);

        // 再补一条 **C 端申请**（payment 侧 refund_order）并在退款列表里拒绝：
        // 代客退款走的是订单侧 order_refund（立即生效、不进审批队列），
        // 而退款列表读的是 payment 侧 refund_order —— 两者是两套记录，
        // 想测「拒绝 + 必填原因」必须有一条真正的申请单。
        const orderDetail = await gatewayCall(token, '/gateway/admin/orders/Detail', 'POST', { orderId });
        const firstItem = (orderDetail?.items || [])[0];
        if (!firstItem) throw new Error('订单详情没有商品行，无法发起退款申请');
        const refundId = await gatewayCall(token, '/gateway/refunds/Apply', 'POST', {
          orderId,
          orderNo,
          items: [{ orderItemId: firstItem.orderItemId, amount: 1 }],
          reason: '深度回归：申请部分退款',
        });

        await goto(page, '/refunds');
        const search = page.locator('.head__search input').first();
        await search.fill(orderNo);
        await page.waitForTimeout(800);
        const row = page.locator('.el-table__row', { hasText: orderNo }).first();
        if ((await row.count()) === 0) throw new Error('申请单没有出现在退款列表（keyword 过滤失效？）');
        await row.locator('button:has-text("拒绝")').first().click({ timeout: 8000 });
        await page.waitForTimeout(450);
        const rejectBox = page.locator('.el-dialog:visible').last();
        const reason = rejectBox.locator('textarea').first();
        if ((await reason.count()) === 0) throw new Error('拒绝退款没有原因输入框');
        await reason.fill('深度回归：不同意本次退款');
        await rejectBox.locator('button:has-text("拒绝")').last().click({ timeout: 8000 });
        await page.waitForTimeout(1200);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('拒绝退款没有成功提示');
        }
        const detail = await gatewayCall(token, '/gateway/refunds/Detail', 'POST', { refundId });
        if (Number(detail?.status) !== 90) {
          throw new Error(`拒绝后退款单状态是 ${detail?.status}，预期 90（已拒绝）`);
        }
        return `订单 ${orderNo} 代客部分退款 5.00 元 + 申请单被拒绝（原因必填）`;
      });
    }

    if (want(['design', '装修'])) {
      await testCase(page, 'page-design-platform', '平台装修：组件库 / 手机预览 / 存草稿', async () => {
        await goto(page, '/design/platform');
        const palette = await page.locator('.palette__item').count();
        if (palette === 0) throw new Error('组件库为空');
        const preview = await page.locator('.stage').count();
        if (preview === 0) throw new Error('没有手机预览区');
        const pages = await page.locator('.head__page').count();
        if (pages === 0) throw new Error('没有页面（首页 / 我的）选择');

        // 属性面板：选中一个组件 → 面板出现它的字段 → 改内容 → 存草稿
        const slot = page.locator('.slot').first();
        if ((await slot.count()) === 0) throw new Error('画布上一个组件都没有');
        await slot.click({ timeout: 8000 });
        await page.waitForTimeout(300);
        if ((await page.locator('.slot--selected').count()) === 0) throw new Error('点组件后没有选中态');
        const panelFields = await page.locator('.props .el-form-item').count();
        if (panelFields === 0) throw new Error('属性面板没有字段');

        const titleInput = page.locator('.props .el-form-item:has-text("标题") input').first();
        let typed = '';
        if ((await titleInput.count()) > 0) {
          typed = `回归标题${Date.now().toString().slice(-5)}`;
          await titleInput.fill(typed);
        }

        await clickVisibleText(page, '存草稿');
        await page.waitForTimeout(1000);
        const ok = page.locator('.el-message--success:visible');
        if ((await ok.count()) === 0) throw new Error('存草稿没有成功提示');

        // 存完再读一次：改过的标题必须落库（只改前端不算数）
        if (typed) {
          await goto(page, '/design/platform');
          await page.waitForTimeout(600);
          const back = await page.locator('.slot').first().click({ timeout: 8000 }).then(() => page
            .locator('.props .el-form-item:has-text("标题") input').first().inputValue());
          if (back !== typed) throw new Error(`标题没有存下来：期望「${typed}」实际「${back}」`);
        }
        return `${palette} 个组件 / 属性面板 ${panelFields} 个字段`;
      });

      await testCase(page, 'page-design-merchant', '商户装修：锁定店铺页 + 组件库独立', async () => {
        const token = await apiToken(USER, PASS);
        const ids = await collectIds(token);
        const merchantId = idOf(ids.merchant);
        if (!merchantId) throw new Error('取不到商户 Id');

        await goto(page, `/design/merchant/${merchantId}`);
        const palette = await page.locator('.palette__item').count();
        if (palette === 0) throw new Error('商户组件库为空');
        const fixed = await visibleText(page, '.head__page--fixed');
        if (!fixed.includes('店铺页')) throw new Error(`商户装修页面标识是「${fixed}」`);
        return `店铺页锁定 / ${palette} 个组件`;
      });

      await testCase(page, 'page-design-ops', '平台装修：拖入组件 → 上移 → 删除 → 存草稿 → 发布', async () => {
        await goto(page, '/design/platform');
        const before = await page.locator('.slot').count();
        const palette = page.locator('.palette__item').first();
        if ((await palette.count()) === 0) throw new Error('组件库为空');

        // 原生 HTML5 拖放。这里用**手动派发 DragEvent** 而不是 locator.dragTo()：
        // Playwright 的 dragTo 走的是鼠标序列，对 `draggable` + `@dragstart/@drop`
        // 这套原生拖放不可靠（实测点完组件数不变，看起来像「拖拽功能坏了」）。
        await page.evaluate(() => {
          const src = document.querySelector('.palette__item');
          const stage = document.querySelector('.stage');
          if (!src || !stage) throw new Error('找不到组件库或画布');
          const dt = new DataTransfer();
          src.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
          stage.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
          stage.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
        });
        await page.waitForTimeout(600);
        const added = await page.locator('.slot').count();
        if (added !== before + 1) throw new Error(`拖入组件后数量是 ${added}，预期 ${before + 1}`);

        // 新组件会被自动选中（onDrop 里做了），选中项会跟着它移动 ——
        // 所以上移 / 下移 / 删除都按「当前选中的那个槽位」来点，
        // 而不是按「最后一个按钮」（最后一个槽位的「下移」是禁用的）。
        await page.locator('.slot--selected .slot__op[title="上移"]').click({ timeout: 8000 });
        await page.waitForTimeout(300);
        await page.locator('.slot--selected .slot__op[title="下移"]').click({ timeout: 8000 });
        await page.waitForTimeout(300);
        if ((await page.locator('.slot').count()) !== before + 1) throw new Error('上移 / 下移把组件弄丢了');

        // 删除刚加的那个（保证不改动原有排版）
        await page.locator('.slot--selected .slot__op--danger').click({ timeout: 8000 });
        await page.waitForTimeout(400);
        if ((await page.locator('.slot').count()) !== before) throw new Error('删除后组件数没回到原值');

        await clickVisibleText(page, '存草稿');
        await page.waitForTimeout(1000);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('存草稿没有成功提示');
        }

        const [publishResp] = await Promise.all([
          page.waitForResponse((r) => r.url().includes('/design/PublishPlatform'), { timeout: 20000 }),
          clickVisibleText(page, '发布'),
        ]);
        const body = await publishResp.json().catch(() => null);
        if (publishResp.status() !== 200 || !body?.success) {
          throw new Error(`发布被拒：HTTP ${publishResp.status()} ${JSON.stringify(body?.message || {})}`);
        }
        // 发布接口返回的是 ApiResponse<int>：data 就是版本号本身（不是 { version }）
        if (!(Number(body.data) > 0)) throw new Error(`发布后版本号异常：${JSON.stringify(body.data)}`);
        await page.waitForTimeout(600);
        return `${before} → ${before + 1} → ${before} 个组件，发布版本 v${body.data}`;
      });

      await testCase(page, 'page-design-image-prop', '平台装修：属性面板上传图片 → 存草稿 → 草稿里带上图片地址', async () => {
        const token = await apiToken(USER, PASS);
        await goto(page, '/design/platform');

        // 找一个「有图片属性」的组件：逐个点开槽位，直到属性面板出现上传控件
        const slots = await page.locator('.slot').count();
        let picked = -1;
        for (let i = 0; i < slots; i++) {
          await page.locator('.slot').nth(i).click({ timeout: 8000 });
          await page.waitForTimeout(250);
          if ((await page.locator('.props .uploader').count()) > 0) {
            picked = i;
            break;
          }
        }
        if (picked < 0) throw new Error('画布里没有任何「可传图」的组件，无法验证属性面板上传');

        const png = Buffer.from(
          'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
          'base64',
        );
        const before = await page.locator('.props .uploader img').count();
        await page.locator('.props input[type=file]').first()
          .setInputFiles({ name: 'prop.png', mimeType: 'image/png', buffer: png });
        await page.waitForTimeout(1800);
        const after = await page.locator('.props .uploader img').count();
        if (after <= before) throw new Error('属性面板上传后没有回显图片');

        await clickVisibleText(page, '存草稿');
        await page.waitForTimeout(1200);
        if ((await page.locator('.el-message--success:visible').count()) === 0) {
          throw new Error('存草稿没有成功提示');
        }

        // 草稿里必须真的带上文件服务返回的地址（而不是只改了界面）
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        const design = await gatewayCall(token, `/gateway/design/Platform?platformId=${platformId}`, 'GET');
        if (!String(design?.configJson || '').includes('/gateway/files/Content/')) {
          throw new Error('存下来的草稿里没有图片地址');
        }

        // 收尾：把刚加的图片移除再存一次，别给草稿留测试图
        await page.locator('.props .uploader .tile--remove, .props .uploader button[title="移除"]')
          .first().click({ timeout: 8000 }).catch(() => {});
        await page.waitForTimeout(400);
        await clickVisibleText(page, '存草稿');
        await page.waitForTimeout(1000);
        return `第 ${picked + 1} 个组件的图片属性已上传并落进草稿`;
      });
    }

    // ---- 表单页 ----
    for (const [route, name] of FORM_PAGES) {
      if (only && !(route + name).toLowerCase().includes(only.toLowerCase())) continue;
      await runFormPage(page, route, name);
    }

    // ---- 新建表单的真实落库：填 → 保存 → 回列表 → 列表可见 ----
    // 前面那组只验「字段渲染 + 空提交校验」；真正容易错的是**表单值 → 请求体**这一段
    // （字段名对不上、空值没转换、下拉没选、日期格式错…），
    // 树页的 id/categoryId 就是栽在这里：接口全绿、界面点了必报错。
    const letters6 = () =>
      Array.from({ length: 6 }, () => String.fromCharCode(97 + Math.floor(Math.random() * 26))).join('');

    const CREATE_FLOWS = [
      {
        key: 'brand', name: '新建品牌', route: '/brands/create', listRoute: '/brands',
        endpoint: '/gateway/brands/Create',
        fill: async (page, ctx) => {
          await fillField(page, '品牌名', ctx.name);
          await fillField(page, '品牌编码', ctx.code);
          // 选填字段也要填：它们最容易「界面上填了、请求体里没带」
          await fillField(page, '品牌 Logo', 'https://cdn.example.com/deep-brand.png');
          await fillField(page, '排序', '7');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/brands/List?page=1&pageSize=50', 'GET');
          const row = (Array.isArray(list) ? list : list?.items || []).find((b) => String(b.id) === String(id));
          if (!row) throw new Error('列表里找不到刚建的品牌');
          if (row.logo !== 'https://cdn.example.com/deep-brand.png') throw new Error(`品牌 Logo 没保存：${row.logo}`);
          if (Number(row.sortOrder) !== 7) throw new Error(`品牌排序没保存：${row.sortOrder}`);
        },
        cleanup: { url: '/gateway/brands/Delete', body: (id) => ({ brandId: id }) },
      },
      {
        key: 'logistics', name: '新建物流公司', route: '/platforms/logistics-companies/create',
        listRoute: '/platforms/logistics-companies',
        endpoint: '/gateway/logistics-companies/Create',
        fill: async (page, ctx) => {
          await fillField(page, '公司名称', ctx.name);
          await fillField(page, '公司编码', ctx.code);
          await fillField(page, 'Logo', 'https://cdn.example.com/deep-logistics.png');
          await fillField(page, '排序', '3');
          await fillField(page, '备注', '深度回归物流公司');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/logistics-companies/List', 'POST', {
            page: 1, pageSize: 50,
          });
          const row = (list?.items || []).find((x) => String(x.logisticsId ?? x.id) === String(id));
          if (!row) throw new Error('列表里找不到刚建的物流公司');
          if (row.logo !== 'https://cdn.example.com/deep-logistics.png') throw new Error(`物流 Logo 没保存：${row.logo}`);
          if (Number(row.sortOrder) !== 3) throw new Error(`物流排序没保存：${row.sortOrder}`);
          if (row.remark !== '深度回归物流公司') throw new Error(`物流备注没保存：${row.remark}`);
        },
        cleanup: { url: '/gateway/logistics-companies/Delete', body: (id) => ({ logisticsId: id }) },
      },
      {
        key: 'role', name: '新建角色', route: '/roles/create', listRoute: '/roles',
        endpoint: '/gateway/roles/Create',
        fill: async (page, ctx) => {
          await fillField(page, '角色名', ctx.name);
          await fillField(page, '角色编码', ctx.code);
          await pickSelect(page, '允许的账号范围', '商户');
          await pickSelect(page, '数据范围', '本级及下级');
          await fillField(page, '备注', '深度回归角色');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/roles/List?page=1&pageSize=100', 'GET');
          const row = (list?.items || list || []).find((r) => String(r.id ?? r.Id) === String(id));
          if (!row) throw new Error('列表里找不到刚建的角色');
          // AllowedScopes：1 平台 / 2 商户 / 3 平台与商户；DataScope：1 本级 / 2 本级及下级
          if (Number(row.allowedScopes ?? row.AllowedScopes) !== 2) {
            throw new Error(`角色账号范围没保存：${row.allowedScopes ?? row.AllowedScopes}`);
          }
          if (Number(row.dataScope ?? row.DataScope) !== 2) {
            throw new Error(`角色数据范围没保存：${row.dataScope ?? row.DataScope}`);
          }
          if ((row.remark ?? row.Remark) !== '深度回归角色') {
            throw new Error(`角色备注没保存：${row.remark ?? row.Remark}`);
          }
        },
        cleanup: { url: '/gateway/roles/Delete', body: (id) => ({ roleId: id }) },
      },
      {
        key: 'user', name: '新建账号', route: '/users/create', listRoute: '/users',
        endpoint: '/gateway/users/Create',
        fill: async (page, ctx) => {
          await fillField(page, '登录名', ctx.code);
          await fillField(page, '密码', 'DeepReg123456');
          await fillField(page, '昵称', ctx.name);
          // 手机号必须**每次都不一样**：账号没有删除接口（只能停用），
          // 写死一个号码的话第二轮就会撞「手机号已注册」，
          // 而且报错是 HTTP 200 + success:false，看起来完全不像重复数据。
          await fillField(page, '手机号', `139${String(Math.floor(Math.random() * 1e8)).padStart(8, '0')}`);
          await fillField(page, '邮箱', `${ctx.code}@example.com`);
          await pickSelect(page, '所属平台');
          // 后端要求账号至少有一个角色（没有角色的账号登进去什么都做不了，fail-closed）
          await pickSelect(page, '角色');
        },
        // 账号没有删除接口（停用即可），收尾走启停
        cleanup: { url: '/gateway/users/UpdateStatus', body: (id) => ({ userId: id, status: 2 }) },
      },
      {
        key: 'platform', name: '新建平台', route: '/platforms/create', listRoute: '/platforms',
        endpoint: '/gateway/platforms/Create',
        fill: async (page, ctx) => {
          await fillField(page, '平台名称', ctx.name);
          await fillField(page, '平台编码', letters6());
          await fillField(page, '商城名称', `${ctx.name}商城`);
          await fillField(page, '联系人', '深度回归');
          await fillField(page, '联系电话', '13800000000');
          await fillField(page, '平台 Logo', 'https://cdn.example.com/deep-platform.png');
          await fillField(page, '首页公告', '深度回归公告：本店测试数据');
          await fillColor(page, '主题色', '#123456');
          await fillColor(page, 'TabBar 选中色', '#654321');
          await fillColor(page, '页面背景色', '#f0f0f0');
          await fillField(page, '运费', '12.5');
          await fillField(page, '包邮门槛', '99');
          await fillField(page, '备注', '深度回归平台备注');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/platforms/List', 'POST', {
            page: 1, pageSize: 50, keyword: '深度回归platform',
          });
          const row = (list?.items || []).find((p) => String(p.id ?? p.platformId) === String(id));
          if (!row) throw new Error('平台列表里找不到刚建的平台');
          if (row.logo !== 'https://cdn.example.com/deep-platform.png') throw new Error(`平台 Logo 没保存：${row.logo}`);
          if (row.notice !== '深度回归公告：本店测试数据') throw new Error(`首页公告没保存：${row.notice}`);
          if (String(row.primaryColor).toLowerCase() !== '#123456') throw new Error(`主题色没保存：${row.primaryColor}`);
          if (String(row.tabColor).toLowerCase() !== '#654321') throw new Error(`TabBar 选中色没保存：${row.tabColor}`);
          if (String(row.backgroundColor).toLowerCase() !== '#f0f0f0') throw new Error(`页面背景色没保存：${row.backgroundColor}`);
          if (Number(row.shippingFee) !== 12.5) throw new Error(`运费没保存：${row.shippingFee}`);
          if (Number(row.freeShippingThreshold) !== 99) throw new Error(`包邮门槛没保存：${row.freeShippingThreshold}`);
          if (row.remark !== '深度回归平台备注') throw new Error(`平台备注没保存：${row.remark}`);
        },
        cleanup: { url: '/gateway/platforms/Delete', body: (id) => ({ platformId: id }) },
      },
      {
        key: 'merchant', name: '新建商户', route: '/merchants/create', listRoute: '/merchants',
        endpoint: '/gateway/merchants/Create',
        fill: async (page, ctx) => {
          await fillField(page, '商户名称', ctx.name);
          await pickSelect(page, '所属平台');
          await fillField(page, '联系人', '深度回归');
          await fillField(page, '联系电话', '13800000000');
          await fillField(page, '店铺 Logo', 'https://cdn.example.com/deep-merchant.png');
          await fillField(page, '店铺简介', '深度回归店铺简介');
          await fillField(page, '备注', '深度回归商户备注');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/merchants/List', 'POST', {
            page: 1, pageSize: 50,
          });
          const row = (list?.items || []).find((m) => String(m.id ?? m.merchantId) === String(id));
          if (!row) throw new Error('列表里找不到刚建的商户');
          if (row.logo !== 'https://cdn.example.com/deep-merchant.png') throw new Error(`商户 Logo 没保存：${row.logo}`);
          if (row.description !== '深度回归店铺简介') throw new Error(`商户简介没保存：${row.description}`);
          if (row.remark !== '深度回归商户备注') throw new Error(`商户备注没保存：${row.remark}`);
        },
        cleanup: { url: '/gateway/merchants/Delete', body: (id) => ({ merchantId: id }) },
      },
      {
        key: 'couponTemplate', name: '新建券模板', route: '/coupons/templates/create',
        listRoute: '/coupons/templates',
        endpoint: '/gateway/marketing/coupon-templates/Create',
        fill: async (page, ctx) => {
          await fillField(page, '模板名', ctx.name);
          await fillField(page, '门槛金额', '10');
          await fillField(page, '优惠金额', '1');
          await fillField(page, '领取后有效天数', '30');
          await fillField(page, '总发行量', '100');
          await fillField(page, '每人限领', '1');
          await fillField(page, '每单限用', '1');
          await fillField(page, '排序', '5');
          await pickSelect(page, '归属平台');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/marketing/coupon-templates/List', 'POST', {
            page: 1, pageSize: 50, keyword: '深度回归couponTemplate',
          });
          const row = (list?.items || []).find((t) => String(t.templateId) === String(id));
          if (!row) throw new Error('券模板列表里找不到刚建的模板');
          if (Number(row.sortOrder) !== 5) throw new Error(`券模板排序没保存：${row.sortOrder}`);
          if (!(Number(row.platformId) > 0)) throw new Error(`券模板归属平台没保存：${row.platformId}`);
        },
        cleanup: { url: '/gateway/marketing/coupon-templates/Delete', body: (id) => ({ templateId: id }) },
      },
      {
        key: 'couponActivity', name: '新建券活动', route: '/coupons/activities/create',
        listRoute: '/coupons/activities',
        endpoint: '/gateway/marketing/coupon-activities/Create',
        fill: async (page, ctx) => {
          await fillField(page, '活动名', ctx.name);
          await pickSelect(page, '券模板');
          await fillDateTime(page, '领取开始时间', localTime(1));
          await fillDateTime(page, '领取结束时间', localTime(3));
          await fillField(page, '本次发放量', '1');
          await pickSelect(page, '适用范围', '指定商品 SPU');
          await fillField(page, '目标 Id 列表', '[100]');
          await fillField(page, '排序', '4');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/marketing/coupon-activities/List', 'POST', {
            page: 1, pageSize: 50, keyword: '深度回归couponActivity',
          });
          const row = (list?.items || []).find((a) => String(a.activityId) === String(id));
          if (!row) throw new Error('券活动列表里找不到刚建的活动');
          if (Number(row.targetType) !== 2) throw new Error(`券活动适用范围没保存：${row.targetType}`);
          if (Number(row.sortOrder) !== 4) throw new Error(`券活动排序没保存：${row.sortOrder}`);
        },
        // 券活动没有删除接口（领过的券要能查到来源），只能停用
        cleanup: {
          url: '/gateway/marketing/coupon-activities/SetStatus',
          body: (id) => ({ activityId: id, status: 2 }),
        },
      },
      {
        key: 'promotion', name: '新建活动', route: '/promotions/create', listRoute: '/promotions',
        endpoint: '/gateway/marketing/activities/Create',
        fill: async (page, ctx) => {
          await fillField(page, '活动名', ctx.name);
          await fillField(page, '门槛金额', '10');
          await fillField(page, '优惠金额', '1');
          await fillDateTime(page, '开始时间', localTime(1));
          await fillDateTime(page, '结束时间', localTime(5));
          await fillField(page, '赠送张数', '2');
          await pickSelect(page, '归属平台');
          await fillField(page, '排序', '6');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/marketing/activities/List', 'POST', {
            page: 1, pageSize: 50, keyword: '深度回归promotion',
          });
          // 🔴 营销活动列表 DTO 的 Id 字段是 `id`（券活动才是 `activityId`）——
          // 之前按 activityId 找，find 永远 undefined，报「找不到刚建的活动」，
          // 而活动其实就躺在返回结果里。失败时把实际 Id 打出来便于下次一眼定位。
          const row = (list?.items || []).find((a) => String(a.id ?? a.activityId) === String(id));
          if (!row) {
            console.log(`        查询 total=${list?.total} items=${(list?.items || []).length} ` +
              `ids=${(list?.items || []).slice(0, 3).map((a) => a.id ?? a.activityId).join(',')} 目标=${id}`);
            throw new Error('活动列表里找不到刚建的活动');
          }
          if (Number(row.giftQuantity) !== 2) throw new Error(`赠送张数没保存：${row.giftQuantity}`);
          if (Number(row.sortOrder) !== 6) throw new Error(`活动排序没保存：${row.sortOrder}`);
        },
        cleanup: { url: '/gateway/marketing/activities/Delete', body: (id) => ({ activityId: id }) },
      },
      {
        key: 'seckill', name: '新建场次', route: '/seckill/create', listRoute: '/seckill',
        endpoint: '/gateway/marketing/seckill/sessions/Create',
        fill: async (page, ctx) => {
          await fillField(page, '场次名', ctx.name);
          await fillDateTime(page, '开始时间', localTime(1));
          await fillDateTime(page, '结束时间', localTime(4));
          await fillField(page, '排序', '2');
          await pickSelect(page, '归属平台');
        },
        verify: async (token, id) => {
          const list = await gatewayCall(token, '/gateway/marketing/seckill/sessions/List', 'POST', {
            page: 1, pageSize: 50,
          });
          const row = (list?.items || []).find((s) => String(s.sessionId) === String(id));
          if (!row) throw new Error('场次列表里找不到刚建的场次');
          if (Number(row.sortOrder) !== 2) throw new Error(`场次排序没保存：${row.sortOrder}`);
          if (!(Number(row.platformId) > 0)) throw new Error(`场次归属平台没保存：${row.platformId}`);
        },
        // 场次没有删除接口，收尾用「取消」把它挪出未开始列表
        cleanup: {
          url: '/gateway/marketing/seckill/sessions/Finish',
          body: (id) => ({ sessionId: id, cancel: true }),
        },
      },
      {
        key: 'product', name: '新建商品', route: '/products/create', listRoute: '/products',
        endpoint: '/gateway/products/Create',
        fill: async (page, ctx) => {
          await fillField(page, '商品名', ctx.name);
          await fillField(page, '副标题', '深度回归');
          await fillField(page, '划线原价', '199');

          // 分类：三级级联，必须选到第 3 级（叶子）
          await page.locator('.el-form-item:has(.el-form-item__label:has-text("商品分类")) .el-cascader')
            .first().click({ timeout: 8000 });
          await page.waitForTimeout(500);
          for (let level = 0; level < 3; level++) {
            const node = page.locator('.el-cascader-menu').nth(level)
              .locator('.el-cascader-node:not(.is-disabled)').first();
            if ((await node.count()) === 0) throw new Error(`分类级联第 ${level + 1} 级没有可选项`);
            await node.click({ timeout: 8000 });
            await page.waitForTimeout(350);
          }
          await page.keyboard.press('Escape');
          await page.waitForTimeout(250);

          // 主图：真实走一次上传（1×1 PNG），把「上传 → 回填 URL」这条链路也覆盖掉
          const png = Buffer.from(
            'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
            'base64',
          );
          await page.setInputFiles('input[type=file]', { name: 'deep.png', mimeType: 'image/png', buffer: png });
          await page.waitForTimeout(1500);
          const uploaded = await page.locator('.el-form-item:has(.el-form-item__label:has-text("商品主图")) img').count();
          if (uploaded === 0) throw new Error('主图上传后没有回显');

          // 「替换图片」：单图上传器在已有图时显示这个按钮，替换后仍然只有一张
          const mainImg = page.locator('.el-form-item:has(.el-form-item__label:has-text("商品主图")) img').first();
          const firstSrc = await mainImg.getAttribute('src');
          const replaceBtn = page.locator('.el-form-item:has(.el-form-item__label:has-text("商品主图")) button[title="替换图片"]').first();
          if ((await replaceBtn.count()) === 0) throw new Error('已有主图时没有「替换图片」按钮');
          await replaceBtn.click({ timeout: 8000 });
          await page.waitForTimeout(300);
          await page.setInputFiles('input[type=file]', { name: 'deep2.png', mimeType: 'image/png', buffer: png });
          await page.waitForTimeout(1500);
          const afterReplace = page.locator('.el-form-item:has(.el-form-item__label:has-text("商品主图")) img');
          if ((await afterReplace.count()) !== 1) throw new Error('替换图片后变成了多张');
          if ((await afterReplace.first().getAttribute('src')) === firstSrc) {
            throw new Error('替换图片后地址没有变化');
          }

          // 规格项：名称 + 可选值（回车落成标签）
          await clickVisibleText(page, '+ 添加规格项');
          await page.waitForTimeout(300);
          const spec = page.locator('.spec').first();
          await spec.locator('.spec__name input').first().fill('颜色');
          const draft = spec.locator('.spec__draft input').first();
          await draft.fill('黑色');
          await draft.press('Enter');
          await page.waitForTimeout(500);

          // SKU 行：编码 / 售价 / 库存
          const row = page.locator('.el-table__row').first();
          if ((await row.count()) === 0) throw new Error('规格组合没有生成 SKU 行');
          // 行里的输入顺序：SKU 编码（text）→ 售价（number）→ 库存（number）→ 图片上传（file，隐藏）。
          // 不按 placeholder 定位：SKU 编码那格的占位符在窄列里会被裁掉，
          // 用文本匹配会变成一个「看起来莫名其妙」的定位失败。
          await row.locator('input:not([type=number]):not([type=file])').first().fill(ctx.code);
          await row.locator('input[type=number]').nth(0).fill('99');
          await row.locator('input[type=number]').nth(1).fill('10');
        },
        cleanup: { url: '/gateway/products/Delete', body: (id) => ({ productId: id }) },
      },
    ];

    for (const flow of CREATE_FLOWS) {
      if (only && !(flow.route + flow.name).toLowerCase().includes(only.toLowerCase())) continue;
      await testCase(page, `create-flow-${flow.key}`, `${flow.name}：填表 → 保存 → 落库 → 列表可见`, async () => {
        const token = await apiToken(USER, PASS);
        const stamp = `${Date.now().toString().slice(-8)}${Math.floor(Math.random() * 90 + 10)}`;
        const ctx = { name: `深度回归${flow.key}${stamp}`, code: `dr${stamp}` };
        let newId = null;

        try {
          await goto(page, flow.route);
          await flow.fill(page, ctx);
          const { status, body, text } = await submitCreate(page, flow.endpoint);
          if (status !== 200 || !body?.success) {
            // message 优先：业务失败（如「手机号已注册」）走的是 success:false + message，
            // errors 是字段级校验错误。两个都不带才回退到原始响应文本。
            const detail = body?.message || JSON.stringify(body?.errors || {}) || text || '';
            throw new Error(
              `保存被拒：HTTP ${status} ${detail}`.slice(0, 300));
          }
          newId = body.data;

          await page.waitForTimeout(700);
          if (!page.url().includes(`#${flow.listRoute}`)) {
            throw new Error(`保存后应当回到 ${flow.listRoute}，实际 ${page.url().replace(BASE, '')}`);
          }

          const search = page.locator('.head__search input').first();
          if (await search.count()) {
            await search.fill(ctx.name);
            // 🔴 搜索框只在**回车 / 清空**时触发 reload（ListView 的 @keyup.enter）。
            // 只 fill 不回车，列表不会过滤——而列表按 sortOrder 排序，
            // 脚本自己填了「排序=5」的新行排在默认 0 的行后面、第一页看不见，
            // 断言就会失败，看起来像「后端没存进去」。
            await search.press('Enter');
            await page.waitForTimeout(650);
          }
          if ((await page.locator('.el-table__row', { hasText: ctx.name }).count()) === 0) {
            throw new Error(`新建的「${ctx.name}」没有出现在列表里`);
          }

          // 选填字段的回读校验：只看列表不够，「填了但没进请求体」的字段
          // 在列表上往往也看不出差别（比如 Logo、备注、排序）。
          if (flow.verify) await flow.verify(token, newId);

          return `${ctx.name} 已落库（Id ${newId}）并出现在列表${flow.verify ? '，选填字段回读一致' : ''}`;
        } finally {
          if (newId && flow.cleanup) {
            await gatewayCall(token, flow.cleanup.url, 'POST', flow.cleanup.body(newId)).catch(() => {});
          }
        }
      });
    }
    // ---- 死信重放：造一条真死信（RabbitMQ DLQ 消息 + ES 记录）→ 界面重放 ----
    if (want(['dead', '死信', '重放', 'logs'])) {
      await testCase(page, 'page-dead-letter-replay', '死信与重放：列表显示 → 确认框 → 重放成功 → 计数 +1', async () => {
        const eventId = `deep-replay-${Date.now().toString().slice(-8)}`;
        // 事件类型故意取一个**没有消费方绑定**的值：重放会把它投回主交换机，
        // 但没有队列订阅这个 routing key，消息落地即被丢弃 —— 不会误触发业务。
        const eventType = 'deep.regression.probe';
        const envelope = JSON.stringify({
          eventId, eventType, occurredAt: new Date().toISOString(), schemaVersion: 1, payload: '{}',
        });

        const rabbitAuth = 'Basic ' + Buffer.from('simpleshop:simpleshop_dev_2026').toString('base64');
        const esUrl = 'http://127.0.0.1:9200/simpleshop_log_deadletter/_doc/';

        const publish = await fetch('http://127.0.0.1:15672/api/exchanges/%2F/amq.default/publish', {
          method: 'POST',
          headers: { authorization: rabbitAuth, 'content-type': 'application/json' },
          body: JSON.stringify({
            properties: {}, routing_key: 'simpleshop.log.dlq',
            payload: envelope, payload_encoding: 'string',
          }),
        });
        const publishBody = await publish.json().catch(() => null);
        if (!publish.ok || publishBody?.routed !== true) {
          throw new Error(`往 DLQ 投消息失败：${JSON.stringify(publishBody || {}).slice(0, 150)}`);
        }

        const now = new Date().toISOString();
        // `refresh=wait_for`：ES 默认 1 秒才刷新，不加这个参数的话
        // 紧接着打开列表页会**查不到刚写进去的记录**，看起来像「死信没记上」。
        const doc = await fetch(`${esUrl}${encodeURIComponent(eventId)}?refresh=wait_for`, {
          method: 'PUT',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            eventId, eventType, occurredAt: now, queueName: 'simpleshop.log',
            errorMessage: '深度回归：模拟一条重试耗尽的死信',
            errorType: 'DeepRegressionProbe', stackTrace: '',
            failedAt: now, attempts: 3, payloadPreview: '{}',
            replayCount: 0, lastReplayAt: null,
          }),
        });
        if (!doc.ok) throw new Error(`写死信记录失败：HTTP ${doc.status}`);

        try {
          await goto(page, '/logs/dead-letter');
          await page.waitForTimeout(900);
          const row = page.locator('.el-table__row', { hasText: eventType }).first();
          if ((await row.count()) === 0) throw new Error('死信列表里没有刚造的那条');

          await row.locator('button:has-text("重放")').first().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          const dlg = page.locator('.el-dialog:visible').last();
          if (!(await dlg.innerText()).includes('重放死信')) throw new Error('重放没有确认框');
          await dlg.locator('button:has-text("重放")').last().click({ timeout: 8000 });
          await page.waitForTimeout(1200);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('重放没有成功提示');
          }

          // 重放计数要落库。**必须重新打开一次列表**再读：
          // 后端更新 ES 文档后要等一次 refresh（约 1 秒）才对查询可见，
          // 而列表页只在动作成功后刷新一次 —— 那一刻读到的还是旧值。
          // 这是 ES 的最终一致特性，不是界面缺陷（成功提示已经给了）。
          await page.waitForTimeout(1300);
          await goto(page, '/logs/dead-letter');
          await page.waitForTimeout(800);
          const after = page.locator('.el-table__row', { hasText: eventType }).first();
          if ((await after.count()) === 0) throw new Error('重放后记录不见了');
          const replayCell = (await after.locator('td').nth(5).innerText()).trim();
          if (replayCell !== '1') throw new Error(`重放后「已重放」列是「${replayCell}」，预期 1`);
          return `${eventId} 已重放（DLQ 消息被消费 + ES 计数 +1）`;
        } finally {
          // 收尾：删掉 ES 里的探针记录（DLQ 里的消息已被重放器 ack 掉）
          await fetch(esUrl + encodeURIComponent(eventId), { method: 'DELETE' }).catch(() => {});
        }
      });
    }

    // ---- 编辑页回填：保存一次（什么都不改）不能把字段清空 ----
    // 编辑页的数据源就是列表 DTO，列表少一个字段 → 输入框永远空着 → 保存时把空值写回去。
    // 平台（Logo / 公告 / 备注）与商户（备注）都栽过这一条，属于**静默丢数据**。
    if (want(['edit', '编辑', '回填'])) {
      await testCase(page, 'page-edit-roundtrip-platform', '编辑平台：字段回填完整，直接保存不丢数据', async () => {
        const token = await apiToken(USER, PASS);
        const stamp = `${Date.now()}`.slice(-8);
        const letters = Array.from({ length: 6 }, () => String.fromCharCode(97 + Math.floor(Math.random() * 26))).join('');
        const platformId = await gatewayCall(token, '/gateway/platforms/Create', 'POST', {
          platformName: `回填平台${stamp}`, platformCode: letters,
          contactName: '回填', contactPhone: '13800000000',
          logo: 'https://cdn.example.com/rt-platform.png',
          mallName: `回填商城${stamp}`,
          notice: '回填公告内容',
          primaryColor: '#112233', tabColor: '#445566', backgroundColor: '#f1f1f1',
          shippingFee: 8.5, freeShippingThreshold: 66,
          status: 1, remark: '回填平台备注',
        });

        try {
          await goto(page, `/platforms/edit/${platformId}`);
          const expect = {
            '平台 Logo': 'https://cdn.example.com/rt-platform.png',
            '首页公告': '回填公告内容',
            '运费': '8.5',
            '包邮门槛': '66',
            '备注': '回填平台备注',
          };
          for (const [label, want] of Object.entries(expect)) {
            const got = await readField(page, label);
            if (got !== want) throw new Error(`「${label}」回填成「${got}」，期望「${want}」`);
          }

          // 什么都不改直接保存：字段必须原样保留
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('保存没有成功提示');
          }
          const list = await gatewayCall(token, '/gateway/platforms/List', 'POST', {
            page: 1, pageSize: 50, keyword: `回填平台${stamp}`,
          });
          const row = (list?.items || []).find((p) => String(p.id) === String(platformId));
          if (!row) throw new Error('保存后找不到平台');
          if (row.logo !== expect['平台 Logo']) throw new Error(`保存后 Logo 被清空：${row.logo}`);
          if (row.notice !== expect['首页公告']) throw new Error(`保存后公告被清空：${row.notice}`);
          if (row.remark !== expect['备注']) throw new Error(`保存后备注被清空：${row.remark}`);
          return '平台编辑页 5 个字段回填一致，原样保存后未被清空';
        } finally {
          await gatewayCall(token, '/gateway/platforms/Delete', 'POST', { platformId }).catch(() => {});
        }
      });

      await testCase(page, 'page-edit-roundtrip-merchant', '编辑商户：字段回填完整，直接保存不丢数据', async () => {
        const token = await apiToken(USER, PASS);
        const stamp = `${Date.now()}`.slice(-8);
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        const merchantId = await gatewayCall(token, '/gateway/merchants/Create', 'POST', {
          merchantName: `回填商户${stamp}`, platformId,
          contactName: '回填', contactPhone: '13800000000',
          logo: 'https://cdn.example.com/rt-merchant.png',
          description: '回填店铺简介', status: 2, remark: '回填商户备注',
        });

        try {
          await goto(page, `/merchants/edit/${merchantId}`);
          const expect = {
            '店铺 Logo': 'https://cdn.example.com/rt-merchant.png',
            '店铺简介': '回填店铺简介',
            '备注': '回填商户备注',
          };
          for (const [label, want] of Object.entries(expect)) {
            const got = await readField(page, label);
            if (got !== want) throw new Error(`「${label}」回填成「${got}」，期望「${want}」`);
          }

          await clickVisibleText(page, '保存');
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('保存没有成功提示');
          }
          // 商户列表 DTO 现在带 Remark，直接用它回读
          const list = await gatewayCall(token, '/gateway/merchants/List', 'POST', {
            page: 1, pageSize: 50, keyword: `回填商户${stamp}`,
          });
          const row = (list?.items || []).find((m) => String(m.id) === String(merchantId));
          if (!row) throw new Error('保存后找不到商户');
          if (row.logo !== expect['店铺 Logo']) throw new Error(`保存后 Logo 被清空：${row.logo}`);
          if (row.description !== expect['店铺简介']) throw new Error(`保存后简介被清空：${row.description}`);
          if (row.remark !== expect['备注']) throw new Error(`保存后备注被清空：${row.remark}`);
          return '商户编辑页 3 个字段回填一致，原样保存后未被清空';
        } finally {
          await gatewayCall(token, '/gateway/merchants/Delete', 'POST', { merchantId }).catch(() => {});
        }
      });

      await testCase(page, 'page-edit-time-roundtrip', '编辑页时间回填：按本地时区显示，保存不改时间（不偏移 8 小时）', async () => {
        const token = await apiToken(USER, PASS);
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        const stamp = `${Date.now()}`.slice(-8);

        // 本地时间字符串（表单用的格式）
        // 注意用空格而不是 'T'：el-date-picker 渲染时会把它规范成 "YYYY-MM-DD HH:mm:ss"，
        // 拿 'T' 形式去比会永远不相等（而值其实是对的）。
        const local = (d) => {
          const pad = (n) => String(n).padStart(2, '0');
          return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:00`;
        };
        const startAt = new Date(Date.now() + 3 * 3600 * 1000);
        const endAt = new Date(startAt.getTime() + 2 * 3600 * 1000);
        let sessionId = 0;

        try {
          // ① 秒杀场次：列表 DTO 不带时区标记时，页面会把 UTC 当本地显示，
          //    保存再按本地转 UTC —— 每存一次时间整体偏移一个时区。
          sessionId = await gatewayCall(token, '/gateway/marketing/seckill/sessions/Create', 'POST', {
            sessionName: `时间回填场次${stamp}`,
            startTime: startAt.toISOString(), endTime: endAt.toISOString(),
            sortOrder: 3, platformId,
          });
          await goto(page, `/seckill/edit/${sessionId}`);
          const shownStart = await readField(page, '开始时间');
          if (shownStart !== local(startAt)) {
            throw new Error(`场次开始时间显示成「${shownStart}」，按本地时区应为「${local(startAt)}」`);
          }
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);

          const sessions = await gatewayCall(token, '/gateway/marketing/seckill/sessions/List', 'POST', {
            page: 1, pageSize: 50,
          });
          const row = (sessions?.items || []).find((s) => String(s.sessionId) === String(sessionId));
          if (!row) throw new Error('保存后场次不在列表第一页（时间可能被改到很远）');
          const savedStart = new Date(row.startTime).getTime();
          if (Math.abs(savedStart - startAt.getTime()) > 60_000) {
            throw new Error(
              `保存后开始时间偏移了 ${Math.round((savedStart - startAt.getTime()) / 60000)} 分钟：`
              + `${row.startTime} vs ${startAt.toISOString()}`);
          }

          // ② 营销活动：同一类字段，同样必须按本地显示
          const activityId = await gatewayCall(token, '/gateway/marketing/activities/Create', 'POST', {
            activityName: `时间回填活动${stamp}`, activityType: 1, thresholdAmount: 10, discountAmount: 1,
            startTime: startAt.toISOString(), endTime: endAt.toISOString(),
            platformId, sortOrder: 0, status: 1,
          });
          await goto(page, `/promotions/edit/${activityId}`);
          const shownActivity = await readField(page, '开始时间');
          if (shownActivity !== local(startAt)) {
            throw new Error(`活动开始时间显示成「${shownActivity}」，应为「${local(startAt)}」`);
          }
          await gatewayCall(token, '/gateway/marketing/activities/Delete', 'POST', { activityId }).catch(() => {});

          return `场次 / 活动的时间都按本地时区回显，保存后 UTC 时间未偏移（${row.startTime}）`;
        } finally {
          if (sessionId) {
            await gatewayCall(token, '/gateway/marketing/seckill/sessions/Finish', 'POST', {
              sessionId, cancel: true,
            }).catch(() => {});
          }
        }
      });

      await testCase(page, 'page-edit-roundtrip-marketing', '编辑页回填：物流 / 券模板 / 活动 / 场次（保存不能重置排序或清空字段）', async () => {
        const token = await apiToken(USER, PASS);
        const stamp = `${Date.now()}`.slice(-8);
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        const created = [];
        let sessionId = 0;

        try {
          // ① 物流公司：备注 / Logo / 排序
          const logisticsId = await gatewayCall(token, '/gateway/logistics-companies/Create', 'POST', {
            companyName: `回填物流${stamp}`, companyCode: `rt${stamp}`,
            logo: 'https://cdn.example.com/rt-logistics.png', sortOrder: 8, status: 1, remark: '回填物流备注',
          });
          created.push(['/gateway/logistics-companies/Delete', { logisticsId }]);
          await goto(page, `/platforms/logistics-companies/edit/${logisticsId}`);
          for (const [label, want] of Object.entries({
            Logo: 'https://cdn.example.com/rt-logistics.png', '排序': '8', '备注': '回填物流备注',
          })) {
            const got = await readField(page, label);
            if (got !== want) throw new Error(`物流「${label}」回填成「${got}」，期望「${want}」`);
          }
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          const logistics = await gatewayCall(token, '/gateway/logistics-companies/List', 'POST', { page: 1, pageSize: 50 });
          const lgRow = (logistics?.items || []).find((x) => String(x.logisticsId) === String(logisticsId));
          if (Number(lgRow?.sortOrder) !== 8) throw new Error(`保存后物流排序变成 ${lgRow?.sortOrder}`);
          if (lgRow?.remark !== '回填物流备注') throw new Error(`保存后物流备注被清空：${lgRow?.remark}`);

          // ② 券模板：排序（列表 DTO 少了它就会被重置成 0）
          const templateId = await gatewayCall(token, '/gateway/marketing/coupon-templates/Create', 'POST', {
            templateName: `回填券模板${stamp}`, couponType: 1, thresholdAmount: 10, discountAmount: 1,
            validDays: 30, totalQuantity: 100, perUserLimit: 1, perOrderLimit: 1,
            sortOrder: 7, platformId, status: 1,
          });
          created.push(['/gateway/marketing/coupon-templates/Delete', { templateId }]);
          await goto(page, `/coupons/templates/edit/${templateId}`);
          if ((await readField(page, '模板名')) !== `回填券模板${stamp}`) throw new Error('券模板名没有回填');
          if ((await readField(page, '排序')) !== '7') throw new Error('券模板排序没有回填');
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          // 用 Get 回读，别用列表搜索：列表是分页的，新行不一定在第一页
          const tplRow = await gatewayCall(token, `/gateway/marketing/coupon-templates/Get?templateId=${templateId}`, 'GET');
          if (Number(tplRow?.sortOrder) !== 7) throw new Error(`保存后券模板排序变成 ${tplRow?.sortOrder}`);

          // ③ 营销活动：排序 + 定向范围不能被洗成全场
          const activityId = await gatewayCall(token, '/gateway/marketing/activities/Create', 'POST', {
            activityName: `回填活动${stamp}`, activityType: 1, thresholdAmount: 10, discountAmount: 1,
            targetType: 2, targets: '[100]',
            startTime: new Date(Date.now() + 3600e3).toISOString(),
            endTime: new Date(Date.now() + 7200e3).toISOString(),
            platformId, sortOrder: 6, status: 1,
          });
          created.push(['/gateway/marketing/activities/Delete', { activityId }]);
          await goto(page, `/promotions/edit/${activityId}`);
          if ((await readField(page, '排序')) !== '6') throw new Error('活动排序没有回填');
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          const actRow = await gatewayCall(token, `/gateway/marketing/activities/Get?activityId=${activityId}`, 'GET');
          if (Number(actRow?.sortOrder) !== 6) throw new Error(`保存后活动排序变成 ${actRow?.sortOrder}`);
          if (Number(actRow?.targetType) !== 2) throw new Error(`保存后活动范围被洗成 ${actRow?.targetType}（应为 2 指定商品）`);

          // ④ 秒杀场次：排序
          sessionId = await gatewayCall(token, '/gateway/marketing/seckill/sessions/Create', 'POST', {
            sessionName: `回填场次${stamp}`,
            startTime: new Date(Date.now() + 3600e3).toISOString(),
            endTime: new Date(Date.now() + 7200e3).toISOString(),
            sortOrder: 5, platformId,
          });
          await goto(page, `/seckill/edit/${sessionId}`);
          if ((await readField(page, '场次名')) !== `回填场次${stamp}`) throw new Error('场次名没有回填');
          if ((await readField(page, '排序')) !== '5') throw new Error('场次排序没有回填');
          await clickVisibleText(page, '保存');
          await page.waitForTimeout(900);
          // 场次没有 Get 端点，直接重开编辑页读回来 —— 这也正是用户核对时走的路
          await goto(page, `/seckill/edit/${sessionId}`);
          const sortBack = await readField(page, '排序');
          if (sortBack !== '5') throw new Error(`保存后场次排序变成 ${sortBack}`);

          return '物流 / 券模板 / 活动 / 场次 四个编辑页回填一致，保存后排序与范围都未丢';
        } finally {
          for (const [url, body] of created.reverse()) {
            await gatewayCall(token, url, 'POST', body).catch(() => {});
          }
          // 场次没有删除接口，收尾用「取消」把它挪出未开始列表
          if (sessionId) {
            await gatewayCall(token, '/gateway/marketing/seckill/sessions/Finish', 'POST', {
              sessionId, cancel: true,
            }).catch(() => {});
          }
        }
      });
    }

    // ---- 商户审核：通过（含影响面确认）→ 落库 ----
    if (want(['merchants', '商户审核', '商户'])) {
      await testCase(page, 'page-merchant-audit', '商户审核：通过确认框（带影响面）→ 审核状态落库', async () => {
        const token = await apiToken(USER, PASS);
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        if (!platformId) throw new Error('取不到平台，无法建待审核商户');

        const name = `深测审核商户${`${Date.now()}`.slice(-8)}`;
        const merchantId = await gatewayCall(token, '/gateway/merchants/Create', 'POST', {
          merchantName: name,
          platformId,
          contactName: '深度回归',
          contactPhone: '13800000000',
          logo: '',
          description: '深度回归自动创建',
          status: 2,
          remark: '深度回归',
        });

        try {
          await goto(page, '/merchants/audit');
          const search = page.locator('.head__search input').first();
          await search.fill(name);
          await page.waitForTimeout(700);
          const row = page.locator('.el-table__row', { hasText: name }).first();
          if ((await row.count()) === 0) throw new Error('新建的待审核商户没有出现在审核列表');

          await row.locator('button:has-text("通过")').first().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          const dlg = page.locator('.el-dialog:visible').last();
          const text = await dlg.innerText();
          if (!text.includes('通过商户审核')) throw new Error('通过确认框没打开');
          // 确认框必须写清影响面：通过后小程序就能看到这家店
          if (!text.includes('出现在小程序')) throw new Error('确认框没有说明通过后的影响');

          await dlg.locator('button:has-text("通过")').last().click({ timeout: 8000 });
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('通过审核没有成功提示');
          }

          const list = await gatewayCall(token, '/gateway/merchants/List', 'POST', {
            page: 1, pageSize: 50, keyword: name,
          });
          const saved = (list?.items || []).find((m) => m.merchantName === name);
          if (Number(saved?.auditStatus) !== 20) {
            throw new Error(`审核通过后 auditStatus 是 ${saved?.auditStatus}，预期 20`);
          }
          return `${name} 通过审核，auditStatus=20`;
        } finally {
          await gatewayCall(token, '/gateway/merchants/Delete', 'POST', { merchantId }).catch(() => {});
        }
      });
    }

      await testCase(page, 'page-merchant-audit-reject', '商户审核：拒绝必须填原因 → auditStatus=90', async () => {
        const token = await apiToken(USER, PASS);
        const platforms = await gatewayCall(token, '/gateway/platforms/Options', 'GET');
        const platformId = platforms?.[0]?.id;
        if (!platformId) throw new Error('取不到平台，无法建待审核商户');

        const name = `深测拒绝商户${`${Date.now()}`.slice(-8)}`;
        const merchantId = await gatewayCall(token, '/gateway/merchants/Create', 'POST', {
          merchantName: name,
          platformId,
          contactName: '深度回归',
          contactPhone: '13800000000',
          logo: '',
          description: '深度回归自动创建',
          status: 2,
          remark: '深度回归',
        });

        try {
          await goto(page, '/merchants/audit');
          const search = page.locator('.head__search input').first();
          await search.fill(name);
          await page.waitForTimeout(700);
          const row = page.locator('.el-table__row', { hasText: name }).first();
          if ((await row.count()) === 0) throw new Error('待审核商户不在列表里');

          await row.locator('button:has-text("拒绝")').first().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          const dlg = page.locator('.el-dialog:visible').last();
          // 原因字段是 textarea（withReason 生成的就是多行输入）。
          // 用 `textarea, input` 取 .first() 有风险：拿到别的 input 时
          // 填进去的值不会进原因字段，提交后 400「拒绝原因必填」，
          // 而界面上看起来「明明填了」。
          const reason = dlg.locator('textarea').first();
          if ((await reason.count()) === 0) throw new Error('拒绝确认框里没有原因输入框');

          // 空原因必须被拦（商户要知道为什么被拒，否则只能反复提交碰运气）
          await dlg.locator('button:has-text("拒绝")').last().click({ timeout: 8000 });
          await page.waitForTimeout(450);
          if ((await dlg.locator('.el-form-item__error:visible').count()) === 0) {
            throw new Error('拒绝不填原因也能提交');
          }

          await reason.fill('深度回归：资料不完整');
          await dlg.locator('button:has-text("拒绝")').last().click({ timeout: 8000 });
          await page.waitForTimeout(1000);
          if ((await page.locator('.el-message--success:visible').count()) === 0) {
            throw new Error('拒绝审核没有成功提示');
          }

          const list = await gatewayCall(token, '/gateway/merchants/List', 'POST', {
            page: 1, pageSize: 50, keyword: name,
          });
          const saved = (list?.items || []).find((m) => m.merchantName === name);
          if (Number(saved?.auditStatus) !== 90) {
            throw new Error(`拒绝后 auditStatus 是 ${saved?.auditStatus}，预期 90`);
          }
          return `${name} 拒绝原因必填 + auditStatus=90`;
        } finally {
          await gatewayCall(token, '/gateway/merchants/Delete', 'POST', { merchantId }).catch(() => {});
        }
      });

    // ---- 客户详情：返回列表 ----
    if (want(['customers', '客户详情', '客户'])) {
      await testCase(page, 'page-customer-detail', '客户详情：字段渲染 / 返回列表', async () => {
        const token = await apiToken(USER, PASS);
        const ids = await collectIds(token);
        const customerId = idOf(ids.customer);
        if (!customerId) throw new Error('取不到客户 Id');

        await goto(page, `/customers/detail/${customerId}`);
        const title = await visibleText(page, '.head__title, .page-header__title');
        if (title !== '客户详情') throw new Error(`客户详情标题是「${title}」`);
        const rows = await page.locator('.fields__row').count();
        if (rows === 0) throw new Error('客户详情没有渲染任何字段');
        const labels = (await page.locator('.fields__label').allTextContents()).map((s) => s.trim());
        for (const need of ['登录名', '手机号', '状态']) {
          if (!labels.includes(need)) throw new Error(`客户详情缺少字段「${need}」`);
        }

        await clickVisibleText(page, '返回列表');
        await page.waitForTimeout(500);
        if (!page.url().includes('/customers') || page.url().includes('/detail/')) {
          throw new Error(`返回列表没有回到客户列表：${page.url()}`);
        }
        return `${rows} 个详情字段，已返回列表`;
      });
    }

    // ---- 登录 / 退出：放在最后跑，因为退出会清掉当前会话 ----
    if (want(['auth', '登录', 'logout', '退出'])) {
      await testCase(page, 'auth-logout-login', '登录页：退出登录 / 空提交校验 / 错误密码被拒 / 重新登录', async () => {
        await goto(page, '/dashboard');
        await page.locator('button:has-text("退出登录")').first().click({ timeout: 8000 });
        await page.waitForTimeout(700);
        if ((await page.locator('#username').count()) === 0) throw new Error('退出登录后没有回到登录页');

        // 空提交：字段级报错（与后台表单「提交时验证」同一口径）
        await page.click('.login__submit');
        await page.waitForTimeout(400);
        if ((await page.locator('.field__error:visible').count()) === 0) {
          throw new Error('空提交没有字段级错误');
        }

        // 错误密码：必须提示，而且不能进后台
        await page.fill('#username', USER);
        await page.fill('#password', 'WrongPassword123');
        await page.click('.login__submit');
        await page.waitForTimeout(1500);
        if ((await page.locator('.el-message--error:visible').count()) === 0) {
          throw new Error('错误密码没有错误提示');
        }
        if ((await page.locator('.nav__link').count()) > 0) throw new Error('错误密码竟然登录成功');

        await page.fill('#password', PASS);
        await page.click('.login__submit');
        await page.waitForSelector('.nav__link', { timeout: 25000 });
        return '退出 → 空提交校验 → 错误密码被拒 → 重新登录成功';
      }, { allow4xx: [/auth\/token/], allowConsole: [/Failed to load resource.*400/] });
    }

    await browser.close();
  } catch (e) {
    console.error(e);
    await browser.close().catch(() => {});
    process.exit(1);
  }

  fs.writeFileSync(REPORT_FILE, JSON.stringify({ base: BASE, at: new Date().toISOString(), results }, null, 2));
  const pass = results.filter((r) => r.ok).length;
  const fail = results.length - pass;
  console.log(`\n${'='.repeat(64)}`);
  console.log(`深度回归：通过 ${pass} / 失败 ${fail} / 共 ${results.length}`);
  console.log(`截图目录：${path.relative(ROOT, SHOT_DIR)}`);
  console.log(`明细报告：${path.relative(ROOT, REPORT_FILE)}`);
  process.exit(fail > 0 ? 1 : 0);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
