import type { RouteRecordRaw } from 'vue-router';
import { REPORTS } from './report-configs';
import { LISTS } from './list-configs';
import { TREES } from './tree-configs';
import { CONFIGS } from './config-configs';
import { FORMS } from './form-configs';

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

// 报表页：四张表同构，用同一个组件 + 各自的配置驱动。
// 复制四个组件的话，改一次「金额怎么显示」要改四处。
// 配置键同样显式传入，不从 name 推导（理由同 listRoute）。
function reportRoute(path: string, name: string, title: string, perm: string, configKey: keyof typeof REPORTS) {
  return {
    path,
    name,
    component: () => import('@/views/ReportView.vue'),
    props: { config: REPORTS[configKey] },
    meta: { title, perm },
  };
}

// 列表页同构：一个 ListView + 各自的配置驱动。
// 配置键**显式传入**，不从 name 推导 —— 推导过的那版把
// customer-list -> customer（键其实叫 customers）、stock-list -> stock（键叫 inventory），
// 结果 config 为 undefined，页面直接抛 `Cannot read properties of undefined`。
// 名字与键对不上是常态，显式传才不会悄悄错位。
function listRoute(path: string, name: string, title: string, perm: string, configKey: keyof typeof LISTS) {
  return {
    path,
    name,
    component: () => import('@/views/ListView.vue'),
    props: { config: LISTS[configKey] },
    meta: { title, perm },
  };
}

// 树形页同构：一个 TreeView + 各自的字段映射
function treeRoute(path: string, name: string, title: string, perm: string, configKey: keyof typeof TREES) {
  return {
    path,
    name,
    component: () => import('@/views/TreeView.vue'),
    props: { config: TREES[configKey] },
    meta: { title, perm },
  };
}

// 配置页同构：读一份配置 → 改 → 存回去（地区地址 / 优惠优先级 / 积分规则）。
function configRoute(path: string, name: string, title: string, perm: string, configKey: keyof typeof CONFIGS) {
  return {
    path,
    name,
    component: () => import('@/views/ConfigView.vue'),
    props: { config: CONFIGS[configKey] },
    meta: { title, perm },
  };
}

// 新建 / 编辑表单同构：一个 FormView + 各自的字段声明。
// 编辑与新建共用一套字段，靠 route.params.id 是否存在区分。
function formRoute(path: string, name: string, title: string, perm: string, configKey: keyof typeof FORMS) {
  return {
    path,
    name,
    component: () => import('@/views/FormView.vue'),
    props: { config: FORMS[configKey] },
    meta: { title, perm },
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
      listRoute('', 'user-list', '账号列表', 'user:read', 'users'),
      formRoute('create', 'user-create', '新建账号', 'user:create', 'user'),
      formRoute('edit/:id', 'user-edit', '编辑账号', 'user:update', 'user'),
    ],
  },
  {
    path: 'customers',
    meta: { title: '客户', icon: 'UserFilled', perm: 'customer:read' },
    children: [
      listRoute('', 'customer-list', '客户列表', 'customer:read', 'customers'),
      { path: 'detail/:id', name: 'customer-detail', ...ph('detail/:id', '客户详情', 'customer:read') },
    ],
  },
  {
    path: 'roles',
    meta: { title: '角色权限', icon: 'Lock', perm: 'permission:read' },
    children: [
      listRoute('', 'role-list', '角色列表', 'permission:read', 'roles'),
      treeRoute('permissions', 'role-permissions', '权限点管理', 'permission:manage', 'permissions'),
    ],
  },

  // ---- 平台与商户 ----
  {
    path: 'platforms',
    meta: { title: '平台', icon: 'OfficeBuilding', perm: 'platform:read' },
    children: [
      listRoute('', 'platform-list', '平台列表', 'platform:read', 'platforms'),
      formRoute('create', 'platform-create', '新建平台', 'platform:create', 'platform'),
      formRoute('edit/:id', 'platform-edit', '编辑平台', 'platform:update', 'platform'),
      { path: 'app-config/:id', name: 'platform-app-config', ...ph('app-config/:id', '小程序配置', 'platform:update') },
      configRoute('regions', 'platform-regions', '地区地址配置', 'region:update', 'regions'),
    ],
  },
  {
    path: 'merchants',
    meta: { title: '商户', icon: 'Shop', perm: 'merchant:read' },
    children: [
      listRoute('', 'merchant-list', '商户列表', 'merchant:read', 'merchants'),
      formRoute('create', 'merchant-create', '新建商户', 'merchant:create', 'merchant'),
      formRoute('edit/:id', 'merchant-edit', '编辑商户', 'merchant:update', 'merchant'),
      listRoute('audit', 'merchant-audit', '商户审核', 'merchant:audit', 'merchantAudits'),
    ],
  },

  // ---- 商品 ----
  {
    path: 'categories',
    meta: { title: '分类', icon: 'Menu', perm: 'category:read' },
    children: [treeRoute('', 'category-list', '分类管理', 'category:read', 'categories')],
  },
  {
    path: 'brands',
    meta: { title: '品牌', icon: 'Star', perm: 'product:read' },
    children: [listRoute('', 'brand-list', '品牌管理', 'product:read', 'brands')],
  },
  {
    path: 'products',
    meta: { title: '商品', icon: 'Goods', perm: 'product:read' },
    children: [
      listRoute('', 'product-list', '商品列表', 'product:read', 'products'),
      { path: 'create', name: 'product-create', ...ph('create', '新建商品', 'product:create') },
      { path: 'edit/:id', name: 'product-edit', ...ph('edit/:id', '编辑商品', 'product:update') },
      listRoute('audit', 'product-audit', '商品审核', 'product:audit', 'productAudits'),
    ],
  },
  {
    path: 'inventory',
    meta: { title: '库存', icon: 'Coin', perm: 'inventory:read' },
    children: [listRoute('', 'stock-list', '库存管理', 'inventory:read', 'inventory')],
  },
  {
    path: 'search-index',
    meta: { title: '搜索索引', icon: 'Search', perm: 'product:read' },
    children: [
      {
        path: '',
        name: 'search-index-list',
        component: () => import('@/views/IndexReconcileView.vue'),
        meta: { title: '索引对账', perm: 'search:reindex' },
      },
    ],
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
      {
        path: 'pickup-verify',
        name: 'order-pickup-verify',
        component: () => import('@/views/PickupVerifyView.vue'),
        meta: { title: '取货码核销', perm: 'order:pickup' },
      },
      // 物流公司字典归在「订单」模块下（BUSINESS.md 5.2 的 logistics:manage 挂在订单组），
      // 而不是单开一个一级菜单：它是发货表单的下拉数据源，不是一个独立业务域。
      listRoute('logistics-companies', 'logistics-company-list', '物流公司', 'logistics:manage', 'logisticsCompanies'),
    ],
  },
  {
    path: 'payments',
    meta: { title: '支付', icon: 'Wallet', perm: 'payment:read' },
    children: [listRoute('', 'payment-list', '支付列表', 'payment:read', 'payments')],
  },
  {
    path: 'refunds',
    meta: { title: '退款', icon: 'RefreshLeft', perm: 'refund:read' },
    children: [
      listRoute('', 'refund-list', '退款列表', 'refund:read', 'refunds'),
      { path: 'detail/:id', name: 'refund-detail', ...ph('detail/:id', '退款详情', 'refund:read') },
    ],
  },

  // ---- 营销 ----
  {
    path: 'promotions',
    meta: { title: '营销活动', icon: 'Present', perm: 'marketing:read' },
    children: [
      listRoute('', 'promotion-list', '活动列表', 'marketing:read', 'promotions'),
      formRoute('create', 'promotion-create', '新建活动', 'marketing:create', 'promotion'),
      formRoute('edit/:id', 'promotion-edit', '编辑活动', 'marketing:update', 'promotion'),
    ],
  },
  {
    path: 'coupons',
    meta: { title: '券', icon: 'Ticket', perm: 'coupon-template:read' },
    children: [
      listRoute('templates', 'coupon-template-list', '券模板', 'coupon-template:read', 'couponTemplates'),
      listRoute('activities', 'coupon-activity-list', '券活动', 'coupon-activity:read', 'couponActivities'),
      configRoute('config', 'coupon-config', '优惠优先级配置', 'marketing-config:update', 'promotionPriority'),
    ],
  },
  {
    path: 'seckill',
    meta: { title: '限时抢购', icon: 'Timer', perm: 'seckill:read' },
    children: [
      listRoute('', 'seckill-list', '场次列表', 'seckill:read', 'seckillSessions'),
      formRoute('create', 'seckill-create', '新建场次', 'seckill:create', 'seckillSession'),
      formRoute('edit/:id', 'seckill-edit', '编辑场次', 'seckill:update', 'seckillSession'),
      { path: 'items/:id', name: 'seckill-items', ...ph('items/:id', '场次商品', 'seckill:update') },
    ],
  },

  // ---- 其他 ----
  {
    path: 'points',
    meta: { title: '积分', icon: 'Medal', perm: 'point:read' },
    children: [
      listRoute('', 'point-list', '积分流水', 'point:read', 'pointRecords'),
      configRoute('rules', 'point-rules', '积分规则', 'point:rule-update', 'pointRules'),
    ],
  },
  {
    path: 'evaluates',
    meta: { title: '评价', icon: 'ChatDotSquare', perm: 'evaluate:read' },
    children: [listRoute('', 'evaluate-list', '评价管理', 'evaluate:read', 'evaluates')],
  },
  {
    path: 'design',
    meta: { title: '装修', icon: 'Brush', perm: 'design:read' },
    children: [
      { path: 'platform', name: 'design-platform', ...ph('platform', '平台装修', 'design:read') },
      // 商户店铺装修用**独立**的 design:merchant，不能跟着平台装修走 design:read：
      // 用户明确要求商户只能改自己的店铺装修，改不到平台装修。
      { path: 'merchant/:id', name: 'design-merchant', ...ph('merchant/:id', '商户店铺装修', 'design:merchant') },
    ],
  },
  {
    path: 'reports',
    meta: { title: '报表', icon: 'DataLine', perm: 'report:view' },
    children: [
      reportRoute('business', 'report-business', '经营报表', 'report:view', 'business'),
      reportRoute('marketing', 'report-marketing', '营销效果报表', 'report:marketing', 'marketing'),
      reportRoute('seckill', 'report-seckill', '秒杀效果报表', 'report:seckill', 'seckill'),
      reportRoute('point', 'report-point', '积分报表', 'report:view', 'point'),
    ],
  },
  {
    path: 'files',
    meta: { title: '文件', icon: 'FolderOpened', perm: 'file:upload' },
    children: [listRoute('', 'file-list', '文件管理', 'file:upload', 'files')],
  },
  {
    path: 'logs',
    meta: { title: '日志', icon: 'Document', perm: 'log:read' },
    children: [
      listRoute('operation', 'log-operation', '操作日志', 'log:read', 'operationLogs'),
      listRoute('exception', 'log-exception', '异常日志', 'log:read', 'exceptionLogs'),
      listRoute('dead-letter', 'log-dead-letter', '死信与重放', 'log:read', 'deadLetters'),
    ],
  },
];

export default adminRoutes;
