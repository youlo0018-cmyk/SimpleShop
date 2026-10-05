// 菜单点不到的页面（详情 / 编辑 / 场次商品 / 商户装修）。
//
// 为什么单独一个文件：主巡检脚本已经很长，而且这批页面的采集方式完全不同 ——
// 它们需要**真实 Id**，所以必须先调接口取，不能像菜单那样靠点。

const AUTH = process.env.AUTH_URL || 'http://127.0.0.1:5019';
const GATEWAY = process.env.GATEWAY_URL || 'http://127.0.0.1:5008';

const slug = (s) =>
  String(s)
    .trim()
    .toLowerCase()
    .replace(/[\\/:*?"<>|\s]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');

export async function apiToken(user, pass) {
  const body = new URLSearchParams({
    grant_type: 'password',
    client_id: 'admin-app',
    username: user,
    password: pass,
  });
  const r = await fetch(`${AUTH}/connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body,
  });
  const j = await r.json();
  if (!j.access_token) throw new Error(`取令牌失败：${JSON.stringify(j).slice(0, 200)}`);
  return j.access_token;
}

async function call(token, path, method, payload) {
  const init = { headers: { authorization: `Bearer ${token}` } };
  if (method === 'POST') {
    init.method = 'POST';
    init.headers['content-type'] = 'application/json';
    init.body = JSON.stringify(payload || {});
  }
  const r = await fetch(`${GATEWAY}${path}`, init);
  const j = await r.json().catch(() => null);

  // 取 id 的接口失败必须**报错而不是返回 null**。
  // 返回 null 会一路变成「SKIP：这页没数据」——报告照样全绿，
  // 而实际上这一页根本没被测过。动词写错（GET 端点用 POST）就是踩这个坑。
  if (!r.ok) {
    throw new Error(`取 id 失败 ${r.status} ${method} ${path}：${JSON.stringify(j || {}).slice(0, 160)}`);
  }
  if (!j || !j.success) {
    throw new Error(`取 id 失败 ${method} ${path}：${JSON.stringify(j || {}).slice(0, 160)}`);
  }

  return j;
}

export const PAGES = [
  { key: 'user', name: '账号', title: '编辑账号', route: '#/users/edit/{id}' },
  { key: 'platform', name: '平台', title: '编辑平台', route: '#/platforms/edit/{id}' },
  { key: 'platform', name: '平台', title: '小程序配置', route: '#/platforms/app-config/{id}' },
  { key: 'customer', name: '客户', title: '客户详情', route: '#/customers/detail/{id}' },
  { key: 'merchant', name: '商户', title: '编辑商户', route: '#/merchants/edit/{id}' },
  { key: 'product', name: '商品', title: '编辑商品', route: '#/products/edit/{id}' },
  { key: 'promotion', name: '营销活动', title: '编辑活动', route: '#/promotions/edit/{id}' },
  { key: 'session', name: '限时抢购', title: '编辑场次', route: '#/seckill/edit/{id}' },
  { key: 'session', name: '限时抢购', title: '场次商品', route: '#/seckill/items/{id}' },
  { key: 'refund', name: '退款', title: '退款详情', route: '#/refunds/detail/{id}' },
];

export async function collectIds(token) {
  const [users, platforms, customers, merchants, products, promotions, sessions, refunds] =
    await Promise.all([
      // users/List 是 [HttpGet]。这里原本写成 POST，于是 405 → 取不到数据 →
      // 「编辑账号」被标成 SKIP，报告全绿而这一页**根本没被测过**。
      // 教训：取 id 的接口用错动词不会让测试失败，只会让它悄悄少测一页。
      call(token, '/gateway/users/List?page=1&pageSize=1', 'GET'),
      call(token, '/gateway/platforms/List', 'POST', { page: 1, pageSize: 1 }),
      call(token, '/gateway/admin/customers/List', 'POST', { page: 1, pageSize: 1 }),
      call(token, '/gateway/merchants/List', 'POST', { page: 1, pageSize: 1 }),
      call(token, '/gateway/products/List?page=1&pageSize=1', 'GET'),
      call(token, '/gateway/marketing/activities/List', 'POST', { page: 1, pageSize: 1 }),
      call(token, '/gateway/marketing/seckill/sessions/List', 'POST', { page: 1, pageSize: 1 }),
      call(token, '/gateway/refunds/List', 'POST', { page: 1, pageSize: 1 }),
    ]);

  const first = (j) => {
    if (!j || !j.data) return null;
    const items = Array.isArray(j.data) ? j.data : j.data.items;
    return Array.isArray(items) && items.length ? items[0] : null;
  };

  return {
    user: first(users),
    platform: first(platforms),
    customer: first(customers),
    merchant: first(merchants),
    product: first(products),
    promotion: first(promotions),
    session: first(sessions),
    refund: first(refunds),
  };
}

export function idOf(row) {
  if (!row) return null;
  return (
    row.id ??
    row.userId ??
    row.platformId ??
    row.customerId ??
    row.merchantId ??
    row.productId ??
    row.activityId ??
    row.sessionId ??
    row.refundId ??
    null
  );
}

export { slug };
