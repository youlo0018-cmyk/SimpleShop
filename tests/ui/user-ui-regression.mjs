// 小程序 H5 端页面巡检。
//
// 为什么先测 H5：它是 uni-app 的同一套页面源码在浏览器里的运行结果，
// 能在没有微信开发者工具的环境里自动抓白屏、控制台错误与失败请求；
// 微信小程序构建由 `npm run build:mp-weixin` 单独把关。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..', '..');
const BASE = process.env.USER_URL || 'http://127.0.0.1:5175';
const GATEWAY = process.env.GATEWAY_URL || 'http://127.0.0.1:5008';
const SHOT_DIR = path.join(ROOT, 'tests', 'visual', 'user');
const IGNORED_CONSOLE = [
  /tongji-collector\.dcloud\.net\.cn/i,
  /cdn\.example\.com/i,
  /ERR_CONNECTION_CLOSED/i,
];

async function call(pathname, body, token = '') {
  const response = await fetch(`${GATEWAY}${pathname}`, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(body || {}),
  });
  const payload = await response.json();
  if (!response.ok || payload.success !== true) {
    throw new Error(`${pathname} 失败：${response.status} ${payload.message || ''}`);
  }
  return payload.data;
}

async function createCustomer() {
  const suffix = Date.now().toString();
  const name = `ui${suffix}`;
  const password = 'Test123456';
  const phone = `138${suffix.slice(-8)}`;
  await call('/gateway/customers/Register', {
    customerName: name,
    password,
    phone,
    nickName: '小程序回归',
  });
  return { name, password };
}

async function main() {
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const user = await createCustomer();
  const failures = [];
  const results = [];

  const attach = () => {
    const errors = [];
    const failed = [];
    const onConsole = (message) => {
      const text = message.text();
      if (message.type() === 'error' && !IGNORED_CONSOLE.some((pattern) => pattern.test(text))) {
        errors.push(text.slice(0, 300));
      }
    };
    const onResponse = (response) => {
      if (response.status() >= 400) {
        const url = response.url();
        // 默认店铺页在 merchantId=0 时返回 404 是预期行为，页面保留默认骨架。
        if (url.includes('/gateway/design/Store?merchantId=0')) return;
        failed.push(`${response.status()} ${response.request().method()} ${url.replace(BASE, '')}`);
      }
    };
    page.on('console', onConsole);
    page.on('response', onResponse);
    return () => {
      page.off('console', onConsole);
      page.off('response', onResponse);
      return { errors, failed };
    };
  };

  async function visit(name, route) {
    const detach = attach();
    await page.goto(`${BASE}/#${route}`, { waitUntil: 'networkidle' }).catch((error) => {
      failures.push(`${name}: ${error.message}`);
    });
    await page.waitForTimeout(500);
    const text = await page.locator('body').innerText().catch(() => '');
    if (!text.trim()) failures.push(`${name}: 页面空白`);
    await page.screenshot({ path: path.join(SHOT_DIR, `${name}.png`), fullPage: true });
    const { errors, failed } = detach();
    if (errors.length) failures.push(`${name}: 控制台 ${errors[0]}`);
    if (failed.length) failures.push(`${name}: 请求 ${failed[0]}`);
    results.push({ name, route, errors, failed, ok: !errors.length && !failed.length && Boolean(text.trim()) });
  }

  const products = await call('/gateway/shop/products/List', {
    customerId: '0',
    page: 1,
    pageSize: 1,
  });
  const productId = products.items?.[0]?.productId || '0';

  await visit('login-public', '/pages/login/index');
  await visit('register-public', '/pages/register/index');
  await page.goto(`${BASE}/#/pages/login/index`, { waitUntil: 'networkidle' });
  await page.locator('input').nth(0).fill(user.name);
  await page.locator('input').nth(1).fill(user.password);
  await page.getByText('登录', { exact: true }).click();
  await page.waitForTimeout(1200);
  await page.goto(`${BASE}/#/pages/product/detail?id=${productId}`, { waitUntil: 'networkidle' });
  await page.getByText('加入购物车', { exact: true }).click();
  await page.waitForTimeout(700);

  const pages = [
    ['home', '/pages/index/index'],
    ['category', '/pages/category/index'],
    ['mall', '/pages/mall/index'],
    ['seckill', '/pages/seckill/index'],
    ['search', '/pages/search/index'],
    ['product-detail', `/pages/product/detail?id=${productId}`],
    ['coupon-center', '/pages/coupon/center'],
    ['cart', '/pages/cart/index'],
    ['profile', '/pages/profile/index'],
    ['points', '/pages/points/index'],
    ['coupon-mine', '/pages/coupon/mine'],
    ['orders', '/pages/order/list'],
    ['evaluate', '/pages/evaluate/list'],
    ['shop-store', '/pages/shop/store?merchantId=0'],
  ];

  for (const [name, route] of pages) {
    await visit(name, route);
    if (name === 'product-detail') {
      await page.getByText('立即购买', { exact: true }).click();
      await page.waitForTimeout(800);
      await visit('checkout', '/pages/checkout/index');
    }
  }

  await browser.close();

  const failedPages = results.filter((result) => !result.ok);
  console.log(`小程序页面 ${results.length} 个，通过 ${results.length - failedPages.length}，失败 ${failedPages.length}`);
  for (const failure of failures) console.log(`  FAIL ${failure}`);
  console.log(`截图目录：${path.relative(ROOT, SHOT_DIR)}`);
  process.exit(failedPages.length > 0 ? 1 : 0);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
