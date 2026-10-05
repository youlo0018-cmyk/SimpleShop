import type { RouteRecordRaw } from 'vue-router';

// 菜单与路由的单一来源（BUSINESS.md 5.2 的 23 组功能模块）。
// meta.title 同时用于侧边栏文字与浏览器标题，meta.icon 是 Element Plus 图标名。
//
// ⚠️ 路径必须由调用方通过第一个参数传入，工厂函数内部**不再返回 path**。
// 之前写成 `{ path: 'x', ...placeholder('y') }` 而工厂也返回 path，
// TypeScript 直接报 TS2783（后者覆盖前者）——而且它是**静默生效**的：
// 编译能过但菜单指向错误路径，属于最难发现的那一类错误。

// 待实现页面的统一承接页：明确写清「尚未实现 + 对应后端权限点」，
// 比菜单项点开一片空白诚实，接手的人也能一眼看出前后端边界。
function ph(path: string, title: string, perm: string) {
  return {
    component: () => import('@/views/PlaceholderView.vue'),
    props: { title, perm },
    meta: { title, perm, icon: 'Document' },
  };
}

export const adminRoutes: RouteRecordRaw[] = [
  {
    path: 'dashboard',
    name: 'dashboard',
    component: () => import('@/views/DashboardView.vue'),
    meta: { title: '工作台', icon: 'HomeFilled', perm: 'dashboard:view' },
  },

  // ---- 账号与客户 ----
  {
    path: 'users',
    meta: { title: '账号', icon: 'User', perm: 'user:read' },
    children: [
      { path: '', name: 'user-list', ...ph('', '账号列表', 'user:read') },
      { path: 'create', name: 'user-create', ...ph('create', '新建账号', 'user:create') },
    ],
  },
  {
    path: 'customers',
    meta: { title: '客户', icon: 'UserFilled', perm: 'customer:read' },
    children: [
      { path: '', name: 'customer-list', ...ph('', '客户列表', 'customer:read') },
      { path: 'detail/:id', name: 'customer-detail', ...ph('detail/:id', '客户详情', 'customer:read') },
    ],
  },
  {
    path: 'roles',
    meta: { title: '角色权限', icon: 'Lock', perm: 'role:read' },
    children: [
      { path: '', name: 'role-list', ...ph('', '角色列表', 'role:read') },
      { path: 'permissions', name: 'role-permissions', ...ph('permissions', '权限点管理', 'permission:manage') },
    ],
  },

  // ---- 平台与商户 ----
  {
    path: 'platforms',
    meta: { title: '平台', icon: 'OfficeBuilding', perm: 'platform:read' },
    children: [
      { path: '', name: 'platform-list', ...ph('', '平台列表', 'platform:read') },
      { path: 'create', name: 'platform-create', ...ph('create', '新建平台', 'platform:create') },
      { path: 'edit/:id', name: 'platform-edit', ...ph('edit/:id', '编辑平台', 'platform:update') },
      { path: 'app-config/:id', name: 'platform-app-config', ...ph('app-config/:id', '小程序配置', 'platform:update') },
      { path: 'regions', name: 'platform-regions', ...ph('regions', '地区地址配置', 'region:manage') },
    ],
  },
  {
    path: 'merchants',
    meta: { title: '商户', icon: 'Shop', perm: 'merchant:read' },
    children: [
      { path: '', name: 'merchant-list', ...ph('', '商户列表', 'merchant:read') },
      { path: 'create', name: 'merchant-create', ...ph('create', '新建商户', 'merchant:create') },
      { path: 'edit/:id', name: 'merchant-edit', ...ph('edit/:id', '编辑商户', 'merchant:update') },
      { path: 'audit', name: 'merchant-audit', ...ph('audit', '商户审核', 'merchant:audit') },
    ],
  },

  // ---- 商品 ----
  {
    path: 'categories',
    meta: { title: '分类', icon: 'Menu', perm: 'category:read' },
    children: [{ path: '', name: 'category-list', ...ph('', '分类管理', 'category:read') }],
  },
  {
    path: 'brands',
    meta: { title: '品牌', icon: 'Star', perm: 'brand:read' },
    children: [{ path: '', name: 'brand-list', ...ph('', '品牌管理', 'brand:read') }],
  },
  {
    path: 'products',
    meta: { title: '商品', icon: 'Goods', perm: 'product:read' },
    children: [
      { path: '', name: 'product-list', ...ph('', '商品列表', 'product:read') },
      { path: 'create', name: 'product-create', ...ph('create', '新建商品', 'product:create') },
      { path: 'edit/:id', name: 'product-edit', ...ph('edit/:id', '编辑商品', 'product:update') },
      { path: 'audit', name: 'product-audit', ...ph('audit', '商品审核', 'product:audit') },
    ],
  },
  {
    path: 'inventory',
    meta: { title: '库存', icon: 'Coin', perm: 'stock:read' },
    children: [{ path: '', name: 'stock-list', ...ph('', '库存管理', 'stock:read') }],
  },
  {
    path: 'search-index',
    meta: { title: '搜索索引', icon: 'Search', perm: 'product:read' },
    children: [{ path: '', name: 'search-index-list', ...ph('', '索引对账', 'product:read') }],
  },

  // ---- 交易 ----
  {
    path: 'orders',
    meta: { title: '订单', icon: 'List', perm: 'order:read' },
    children: [
      {
        path: '',
        name: 'order-list',
        component: () => import('@/views/OrderListView.vue'),
        meta: { title: '订单列表', perm: 'order:read' },
      },
      {
        path: 'detail/:id',
        name: 'order-detail',
        component: () => import('@/views/OrderDetailView.vue'),
        meta: { title: '订单详情', perm: 'order:read' },
      },
      { path: 'pickup-verify', name: 'order-pickup-verify', ...ph('pickup-verify', '取货码核销', 'order:verify') },
    ],
  },
  {
    path: 'payments',
    meta: { title: '支付', icon: 'Wallet', perm: 'payment:read' },
    children: [{ path: '', name: 'payment-list', ...ph('', '支付列表', 'payment:read') }],
  },
  {
    path: 'refunds',
    meta: { title: '退款', icon: 'RefreshLeft', perm: 'refund:read' },
    children: [
      { path: '', name: 'refund-list', ...ph('', '退款列表', 'refund:read') },
      { path: 'detail/:id', name: 'refund-detail', ...ph('detail/:id', '退款详情', 'refund:read') },
    ],
  },

  // ---- 营销 ----
  {
    path: 'promotions',
    meta: { title: '营销活动', icon: 'Present', perm: 'promotion:read' },
    children: [
      { path: '', name: 'promotion-list', ...ph('', '活动列表', 'promotion:read') },
      { path: 'create', name: 'promotion-create', ...ph('create', '新建活动', 'promotion:create') },
      { path: 'edit/:id', name: 'promotion-edit', ...ph('edit/:id', '编辑活动', 'promotion:update') },
    ],
  },
  {
    path: 'coupons',
    meta: { title: '券', icon: 'Ticket', perm: 'coupon:read' },
    children: [
      { path: 'templates', name: 'coupon-template-list', ...ph('templates', '券模板', 'coupon:read') },
      { path: 'activities', name: 'coupon-activity-list', ...ph('activities', '券活动', 'coupon:read') },
      { path: 'config', name: 'coupon-config', ...ph('config', '优惠优先级配置', 'coupon:manage') },
    ],
  },
  {
    path: 'seckill',
    meta: { title: '限时抢购', icon: 'Timer', perm: 'seckill:read' },
    children: [
      { path: '', name: 'seckill-list', ...ph('', '场次列表', 'seckill:read') },
      { path: 'create', name: 'seckill-create', ...ph('create', '新建场次', 'seckill:create') },
      { path: 'edit/:id', name: 'seckill-edit', ...ph('edit/:id', '编辑场次', 'seckill:update') },
      { path: 'items/:id', name: 'seckill-items', ...ph('items/:id', '场次商品', 'seckill:update') },
    ],
  },

  // ---- 其他 ----
  {
    path: 'points',
    meta: { title: '积分', icon: 'Medal', perm: 'point:read' },
    children: [{ path: '', name: 'point-list', ...ph('', '积分流水', 'point:read') }],
  },
  {
    path: 'evaluates',
    meta: { title: '评价', icon: 'ChatDotSquare', perm: 'evaluate:read' },
    children: [{ path: '', name: 'evaluate-list', ...ph('', '评价管理', 'evaluate:read') }],
  },
  {
    path: 'design',
    meta: { title: '装修', icon: 'Brush', perm: 'design:manage' },
    children: [
      { path: 'platform', name: 'design-platform', ...ph('platform', '平台装修', 'design:manage') },
      { path: 'merchant/:id', name: 'design-merchant', ...ph('merchant/:id', '商户店铺装修', 'design:manage') },
    ],
  },
  {
    path: 'reports',
    meta: { title: '报表', icon: 'DataLine', perm: 'report:view' },
    children: [
      { path: 'business', name: 'report-business', ...ph('business', '经营报表', 'report:view') },
      { path: 'marketing', name: 'report-marketing', ...ph('marketing', '营销效果报表', 'report:marketing') },
      { path: 'seckill', name: 'report-seckill', ...ph('seckill', '秒杀效果报表', 'report:seckill') },
      { path: 'point', name: 'report-point', ...ph('point', '积分报表', 'report:view') },
    ],
  },
  {
    path: 'files',
    meta: { title: '文件', icon: 'FolderOpened', perm: 'file:upload' },
    children: [{ path: '', name: 'file-list', ...ph('', '文件管理', 'file:upload') }],
  },
  {
    path: 'logs',
    meta: { title: '日志', icon: 'Document', perm: 'log:read' },
    children: [
      { path: 'operation', name: 'log-operation', ...ph('operation', '操作日志', 'log:read') },
      { path: 'exception', name: 'log-exception', ...ph('exception', '异常日志', 'log:read') },
      { path: 'dead-letter', name: 'log-dead-letter', ...ph('dead-letter', '死信与重放', 'log:read') },
    ],
  },
];

export default adminRoutes;
