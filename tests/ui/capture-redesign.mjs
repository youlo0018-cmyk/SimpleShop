// 单独给「本轮重做的页面」补一组截图。
// admin-ui-regression.mjs 已经在跑全量回归，但它只对**路由可达**的页面截图；
// 本轮改动里有几个交互态（发货弹窗、部分退款弹窗、装修手机预览）
// 不在任何一条既有流程里，必须单独打，否则改没改坏只能靠肉眼看代码。
import { chromium } from 'playwright';
import { mkdirSync } from 'fs';

const BASE = process.env.ADMIN_URL || 'http://localhost:5174';
const USER = process.env.ADMIN_USER || 'codexadmin';
const PASS = process.env.ADMIN_PASS || 'Admin123456';
const OUT = 'tests/visual/redesign';

mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

async function login() {
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForSelector('#username', { timeout: 15000 });
  await page.fill('#username', USER);
  await page.fill('#password', PASS);
  await page.click('.login__submit');
  await page.waitForTimeout(1500);
}

async function shot(name) {
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(`  ok ${name}`);
}

async function go(hash) {
  await page.goto(`${BASE}/${hash}`, { waitUntil: 'networkidle' });
  // 光靠 goto 不够：导航到**同一个**地址（含 hash）时 Playwright 认为是同文档导航，
  // SPA 根本不重新执行 onMounted —— 于是接口改了配置、页面却还停在上一帧的空画布，
  // 看起来就像「预览坏了」。这里显式 reload 才拿得到新配置。
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForTimeout(900);
}

await login();

// 1. 订单列表：发货要选物流公司 + 运单号
await go('#/orders');
await shot('01-orders-list');

// 找一张待发货的订单点「发货」。没有就只截列表 —— 测试库里未必有待发货的单。
const shipBtn = page.locator('button:has-text("发货")').first();
if (await shipBtn.count()) {
  await shipBtn.click();
  await page.waitForTimeout(700);
  await shot('02-ship-dialog-logistics');
  // 空着提交，验证「提交时才校验」且错误提示落在字段下方
  const ok = page.locator('.el-dialog button:has-text("快递发货")').first();
  if (await ok.count()) {
    await ok.click();
    await page.waitForTimeout(600);
    await shot('03-ship-validation-errors');
  }
  await page.keyboard.press('Escape');
  await page.waitForTimeout(400);
}

// 2. 部分退款弹窗：逐行勾选 + 金额
const refundBtn = page.locator('button:has-text("退款")').first();
if (await refundBtn.count()) {
  await refundBtn.click();
  await page.waitForTimeout(900);
  await shot('04-refund-dialog-whole');
  const partial = page.locator('.el-radio-button:has-text("只退指定商品")').first();
  if (await partial.count()) {
    await partial.click();
    await page.waitForTimeout(500);
    await shot('05-refund-dialog-partial');
  }
  await page.keyboard.press('Escape');
  await page.waitForTimeout(400);
}

// 3. 订单详情：分段 + 表格
await go('#/orders');
const detailBtn = page.locator('button:has-text("详情")').first();
if (await detailBtn.count()) {
  await detailBtn.click();
  await page.waitForTimeout(1400);
  await shot('06-order-detail');
}

// 4. 商品新建：图片瓦片 + 规格分区
await go('#/products/create');
await shot('07-product-create-images');
await page.mouse.wheel(0, 900);
await shot('08-product-create-specs');
await page.mouse.wheel(0, 900);
await shot('09-product-create-sku-matrix');

// 5. 装修：手机模拟预览
await go('#/design/platform');
await shot('10-design-phone-preview');

// 空画布只能看出「手机框对不对」，看不出**组件渲染对不对**。
// 直接用接口塞一份配置进去再截图 —— 拖拽的中间态没法稳定复现，
// 而组件渲染才是这块最容易出错的地方（与小程序端对不上）。
//
// ⚠️ 必须同时给 index 与 profile 两页：后端校验「缺少某页画布」会整单拒收，
// 只塞 index 的话存草稿直接失败，页面读回空配置 —— 看起来像预览坏了。
const seeded = await page.evaluate(async () => {
  const token = localStorage.getItem('simpleshop_admin_token') || '';
  const auth = { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` };

  const platforms = await fetch('/gateway/platforms/Options', { headers: auth })
    .then((x) => x.json()).catch(() => null);
  const platformId = platforms?.data?.[0]?.id;
  if (!platformId) return { ok: false, why: '没有可用平台' };

  const config = {
    pages: {
      index: {
        components: [
          { id: 'p1', type: 'searchBar', span: 12, height: 40, props: {} },
          { id: 'p2', type: 'banner', span: 12, height: 160, props: { title: '本季新品' } },
          { id: 'p3', type: 'title', span: 12, height: 40, props: { title: '为你推荐', subtitle: '今日热卖' } },
          { id: 'p4', type: 'notice', span: 12, height: 40, props: { text: '全场满 100 减 10' } },
          { id: 'p5', type: 'kingKong', span: 12, height: 120, props: { title: '分类导航' } },
          { id: 'p6', type: 'productGrid', span: 12, height: 200, props: { title: '热卖商品' } },
          { id: 'p7', type: 'seckillZone', span: 12, height: 80, props: {} },
          { id: 'p8', type: 'couponZone', span: 12, height: 100, props: {} },
        ],
      },
      profile: {
        components: [
          { id: 'q1', type: 'memberCard', span: 12, height: 90, props: {} },
          { id: 'q2', type: 'benefits', span: 12, height: 120, props: {} },
          { id: 'q3', type: 'serviceGrid', span: 12, height: 120, props: {} },
        ],
      },
    },
  };

  const res = await fetch('/gateway/design/SavePlatformDraft', {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ platformId, configJson: JSON.stringify(config) }),
  }).then((x) => x.json());

  return { ok: !!res?.success, why: res?.message || '', platformId };
});

if (!seeded.ok) {
  console.log(`  ! 装修配置种入失败：${seeded.why}（预览截图会拍到空画布）`);
} else {
  await go('#/design/platform');
  await shot('10b-design-phone-with-components');

  // 我的页走另一套组件（会员卡 / 权益行 / 服务宫格），单独拍一张
  await page.selectOption('.head__ops select >> nth=1', 'profile').catch(() => {});
  await page.waitForTimeout(1200);
  await shot('10c-design-phone-profile');
}

// 6. 商户列表：启用/停用按钮
await go('#/merchants');
await shot('11-merchants-status-actions');

// 7. 商品审核：合并成一个「审核」对话框，结论用分段控件选
//
// 包 try/catch：测试库里未必总有「待审核」的商品（审核队列常是空的），
// 找不到按钮是**正常情况**，不该让整轮截图在这里崩掉。
try {
  await go('#/products/audit');
  // 限定在**表格操作列**里：页签上也有「审核」两个字，
  // 用 `button:has-text("审核")` 会先匹配到那个不可点的页签，click 直接超时。
  const auditBtn = page.locator('.el-table__body button:has-text("审核")').first();
  if (await auditBtn.count()) {
    await auditBtn.click();
    await page.waitForTimeout(700);
    await shot('12-product-audit-dialog-pass');

    // 切到「驳回」：原因输入框应当**这时才出现**，而且必填
    const reject = page.locator('.el-radio-button__inner:has-text("驳回")').first();
    if (await reject.count()) {
      await reject.click();
      await page.waitForTimeout(500);
      await shot('13-product-audit-dialog-reject');
    }
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
  } else {
    console.log('  - 商品审核队列为空，跳过审核对话框截图');
  }
} catch (e) {
  console.log(`  ! 商品审核截图跳过：${String(e).split('\n')[0]}`);
}

await browser.close();
console.log(`截图目录：${OUT}`);
