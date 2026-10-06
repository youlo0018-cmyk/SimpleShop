// 新建 / 编辑表单的配置。字段声明在这里，FormView 只负责渲染与提交。

// 下拉数据源。显示 name 不显示 id 是硬约束（DATA_SPEC 4.1）。
export const optionSources: Record<string, any> = {
  platforms: { url: '/gateway/platforms/Options', method: 'GET' },
  merchants: { url: '/gateway/merchants/Options', method: 'GET' },
  // 用 List 而不是 Options：DATA_SPEC 4.2 列了 roles/Options，但后端只实现了 List，
  // 而且 List 已经返回了下拉需要的 id + roleName。为此再加一个 Options 端点是纯重复。
  roles: { url: '/gateway/roles/List', method: 'GET' },
  // 券模板没有 Options 端点，只有按 Id 的 Get。用 List（POST，带分页）当数据源，
  // 第一页足够覆盖运营会选的模板数量。
  couponTemplates: { url: '/gateway/marketing/coupon-templates/List', method: 'POST', body: { page: 1, pageSize: 200 } },
  logisticsCompanies: { url: '/gateway/logistics-companies/Options', method: 'POST' },
};

// 本地时间 -> UTC。后端时间列都是 UTC，而 el-date-picker 给的是本地墙上时间。
// 不换算的话运营填 10:00，库里存 10:00 UTC = 本地 18:00，活动会「莫名其妙早了 8 小时」。
const toUtcIso = (v: unknown) => {
  if (!v) return '';
  const d = new Date(String(v));
  return Number.isNaN(d.getTime()) ? '' : d.toISOString();
};

// UTC -> 本地时间输入值，与 toUtcIso 互逆。
const toLocalInput = (v: unknown) => {
  if (!v) return '';
  const d = new Date(String(v));
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:00`;
};

const PHONE_PATTERN = '^1[3-9]\\d{9}$';

export const FORMS = {
  user: {
    title: '新建账号',
    desc: '租户身份创建后不可改，改归属请停用旧号重建',
    createEndpoint: '/gateway/users/Create',
    updateEndpoint: '/gateway/users/Update',
    idField: 'userId',
    // users/List 是 [HttpGet]。用 POST 会 405 —— 编辑账号页整个加载不出来，
    // 而症状只是控制台一条 405 + 一个空表单，很容易被当成「数据还没建」。
    listSource: { url: '/gateway/users/List', method: 'GET' },
    listRoute: '/users',
    fields: [
      { field: 'userName', label: '登录名', required: true, pattern: '^[A-Za-z0-9_]{3,32}$', patternMessage: '登录名为 3-32 位字母、数字或下划线', readonlyInEdit: true, help: '创建后不可修改' },
      { field: 'password', label: '密码', required: true, secret: true, pattern: '^.{8,32}$', patternMessage: '密码长度 8-32 位' },
      { field: 'nickName', label: '昵称', required: true, pattern: '^.{2,32}$', patternMessage: '昵称 2-32 个字符' },
      { field: 'phone', label: '手机号', required: true, pattern: PHONE_PATTERN, patternMessage: '请填写正确的 11 位手机号' },
      { field: 'email', label: '邮箱', pattern: '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$', patternMessage: '邮箱格式不正确' },
      { field: 'tenantType', label: '租户类型', type: 'select', required: true, default: 1, static: [{ value: 1, label: '平台账号' }, { value: 2, label: '商户账号' }], readonlyInEdit: true },
      {
        field: 'platformId',
        label: '所属平台',
        type: 'select',
        options: 'platforms',
        required: true,
        default: '',
        showWhen: (m: any) => Number(m.tenantType) === 1,
      },
      {
        field: 'merchantId',
        label: '所属商户',
        type: 'select',
        options: 'merchants',
        required: true,
        default: '',
        showWhen: (m: any) => Number(m.tenantType) === 2,
      },
      { field: 'roleIds', label: '角色', type: 'select', options: 'roles', multiple: true, filterable: true },
    ],
  },

  role: {
    title: '新建角色',
    desc: '角色决定账号能进哪些模块、能做哪些动作。内置角色不可编辑或删除',
    createEndpoint: '/gateway/roles/Create',
    updateEndpoint: '/gateway/roles/Update',
    idField: 'roleId',
    listSource: { url: '/gateway/roles/List', method: 'GET' },
    listRoute: '/roles',
    permissionTree: true,
    permissionDetailEndpoint: '/gateway/roles/Detail',
    permissionBindEndpoint: '/gateway/roles/BindPermissions',
    fields: [
      { field: 'roleName', label: '角色名', required: true, pattern: '^.{1,64}$', patternMessage: '角色名 1-64 个字符' },
      { field: 'code', label: '角色编码', required: true, pattern: '^[A-Za-z0-9_-]{1,64}$', patternMessage: '编码只能包含字母、数字、下划线或短横线', readonlyInEdit: true, help: '创建后不可修改，权限判断与审计会引用它' },
      {
        field: 'allowedScopes',
        label: '允许的账号范围',
        type: 'select',
        required: true,
        default: 1,
        static: [
          { value: 1, label: '平台账号' },
          { value: 2, label: '商户账号' },
          { value: 3, label: '平台与商户账号' },
        ],
      },
      {
        field: 'dataScope',
        label: '数据范围',
        type: 'select',
        required: true,
        default: 1,
        static: [
          { value: 1, label: '本级数据' },
          { value: 2, label: '本级及下级数据' },
        ],
      },
      { field: 'remark', label: '备注', type: 'textarea', rows: 3, help: '写清这个角色给哪类岗位使用，减少后续误绑' },
    ],
  },

  couponTemplate: {
    title: '新建券模板',
    desc: '改模板不影响已发出的券；已发出的券按发放时快照计算',
    createEndpoint: '/gateway/marketing/coupon-templates/Create',
    updateEndpoint: '/gateway/marketing/coupon-templates/Update',
    idField: 'templateId',
    listSource: { url: '/gateway/marketing/coupon-templates/List', method: 'POST' },
    listRoute: '/coupons/templates',
    fields: [
      { field: 'templateName', label: '模板名', required: true, pattern: '^.{2,128}$', patternMessage: '模板名 2-128 个字符' },
      {
        field: 'couponType',
        label: '券类型',
        type: 'select',
        required: true,
        default: 1,
        static: [
          { value: 1, label: '满减券' },
          { value: 2, label: '折扣券' },
          { value: 3, label: '代金券' },
          { value: 4, label: '满赠券' },
        ],
      },
      {
        field: 'thresholdAmount',
        label: '门槛金额',
        type: 'number',
        min: 0,
        default: 0,
        showWhen: (m: any) => Number(m.couponType) === 1 || Number(m.couponType) === 4,
        help: '0 表示无门槛，按适用商品行金额合计判定',
      },
      {
        field: 'discountAmount',
        label: '优惠金额',
        type: 'number',
        min: 0,
        default: 0,
        showWhen: (m: any) => Number(m.couponType) === 1 || Number(m.couponType) === 3,
        help: '满减券与代金券使用；金额保留两位小数',
      },
      {
        field: 'discountRate',
        label: '折扣率',
        type: 'number',
        min: 0.01,
        max: 10,
        default: 10,
        showWhen: (m: any) => Number(m.couponType) === 2,
        help: '10 表示不打折，8.5 表示 85 折',
      },
      {
        field: 'giftTemplateId',
        label: '赠送券模板',
        type: 'select',
        options: 'couponTemplates',
        default: 0,
        showWhen: (m: any) => Number(m.couponType) === 4,
        help: '满赠券生效时赠送的券模板',
      },
      { field: 'validDays', label: '领取后有效天数', type: 'number', required: true, min: 1, max: 3650, default: 30 },
      { field: 'totalQuantity', label: '总发行量', type: 'number', min: 0, default: 0, help: '0 表示不限量' },
      { field: 'perUserLimit', label: '每人限领', type: 'number', min: 1, max: 100, default: 1 },
    // 固定 1，不给改：订单一次只能使用一张券（BUSINESS.md 12.1），
    // 底层按订单号唯一占用天然保证。做成可编辑只会让人以为配大了能多减几张。
    { field: 'perOrderLimit', label: '每单限用', type: 'number', min: 1, max: 1, default: 1, readonlyInEdit: true, disabled: true, help: '固定 1 张，一笔订单只能用一张券' },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0, help: '数字越小越靠前' },
      // 归属平台创建后不可改：券模板一旦发出去，改平台等于把别人的券挪到另一个平台，
      // 而编辑接口本来就把它排除在更新列之外。做成可编辑的旋钮只会让人以为改生效了。
      { field: 'platformId', label: '归属平台', type: 'select', options: 'platforms', default: 0, readonlyInEdit: true, help: '券模板归属的平台；列表可按平台筛选，创建后不可改' },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
  },

  couponActivity: {
    title: '新建券活动',
    desc: '领券中心活动。发放量与券模板发行池是两个独立计数',
    createEndpoint: '/gateway/marketing/coupon-activities/Create',
    updateEndpoint: '/gateway/marketing/coupon-activities/Update',
    idField: 'activityId',
    listSource: { url: '/gateway/marketing/coupon-activities/List', method: 'POST' },
    listRoute: '/coupons/activities',
    fields: [
      { field: 'activityName', label: '活动名', required: true, pattern: '^.{2,128}$', patternMessage: '活动名 2-128 个字符' },
      { field: 'templateId', label: '券模板', type: 'select', options: 'couponTemplates', required: true },
      { field: 'claimStartTime', label: '领取开始时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'claimEndTime', label: '领取结束时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'claimQuantity', label: '本次发放量', type: 'number', required: true, min: 1, default: 1 },
      { field: 'perUserLimit', label: '每人限领', type: 'number', min: 1, default: 1 },
      {
        field: 'targetType',
        label: '适用范围',
        type: 'select',
        required: true,
        default: 1,
        static: [
          { value: 1, label: '全场' },
          { value: 2, label: '指定商品 SPU' },
          { value: 3, label: '指定规格 SKU' },
        ],
      },
      {
        field: 'targets',
        label: '目标 Id 列表',
        type: 'textarea',
        rows: 4,
        default: '[]',
        showWhen: (m: any) => Number(m.targetType) !== 1,
        help: 'JSON 数组字符串，例如 ["123","456"]；最多 200 个',
      },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0 },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
  },

  platform: {
    title: '新建平台',
    desc: '平台编码创建后不可改：小程序用 PLATFORM_CODE 锁死它',
    createEndpoint: '/gateway/platforms/Create',
    updateEndpoint: '/gateway/platforms/Update',
    idField: 'platformId',
    listSource: { url: '/gateway/platforms/List', method: 'POST' },
    listRoute: '/platforms',
    fields: [
      { field: 'platformName', label: '平台名称', required: true, pattern: '^.{2,128}$', patternMessage: '平台名称 2-128 个字符' },
      { field: 'platformCode', label: '平台编码', required: true, pattern: '^[A-Za-z]{6}$', patternMessage: '平台编码为 6 位字母', readonlyInEdit: true, help: '6 位字母，创建后不可修改' },
      { field: 'mallName', label: '商城名称', required: true, pattern: '^.{2,128}$', patternMessage: '商城名称 2-128 个字符' },
      { field: 'contactName', label: '联系人', required: true, pattern: '^.{2,32}$', patternMessage: '联系人 2-32 个字符' },
      { field: 'contactPhone', label: '联系电话', required: true, pattern: PHONE_PATTERN, patternMessage: '请填写正确的 11 位手机号' },
      { field: 'logo', label: '平台 Logo', help: '图片 URL，可先留空' },
      { field: 'notice', label: '首页公告', type: 'textarea', rows: 3 },
      { field: 'primaryColor', label: '主题色', type: 'color', default: '#0071e3' },
      { field: 'tabColor', label: 'TabBar 选中色', type: 'color', default: '#0071e3' },
      { field: 'backgroundColor', label: '页面背景色', type: 'color', default: '#f5f5f7' },
      { field: 'shippingFee', label: '运费', type: 'number', min: 0, default: 0, help: '仅实物快递收取，两位小数' },
      { field: 'freeShippingThreshold', label: '包邮门槛', type: 'number', min: 0, default: 0, help: '0 表示不启用包邮' },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
      { field: 'remark', label: '备注', type: 'textarea', rows: 2 },
    ],
  },

  merchant: {
    title: '新建商户',
    desc: '新建后为待审核，审核通过才会出现在小程序里',
    createEndpoint: '/gateway/merchants/Create',
    updateEndpoint: '/gateway/merchants/Update',
    idField: 'merchantId',
    listSource: { url: '/gateway/merchants/List', method: 'POST' },
    listRoute: '/merchants',
    fields: [
      { field: 'merchantName', label: '商户名称', required: true, pattern: '^.{2,64}$', patternMessage: '商户名称 2-64 个字符' },
      { field: 'platformId', label: '所属平台', type: 'select', options: 'platforms', required: true, readonlyInEdit: true, help: '创建后不可改' },
      { field: 'contactName', label: '联系人', required: true, pattern: '^.{2,32}$', patternMessage: '联系人 2-32 个字符' },
      { field: 'contactPhone', label: '联系电话', required: true, pattern: PHONE_PATTERN, patternMessage: '请填写正确的 11 位手机号' },
      { field: 'logo', label: '店铺 Logo' },
      { field: 'description', label: '店铺简介', type: 'textarea', rows: 3 },
      { field: 'status', label: '状态', type: 'select', required: true, default: 2, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
      { field: 'remark', label: '备注', type: 'textarea', rows: 2 },
    ],
  },

  promotion: {
    title: '新建活动',
    desc: '满减 / 满折 / 满赠。金额字段两位小数，保存时按 AwayFromZero 舍入',
    createEndpoint: '/gateway/marketing/activities/Create',
    updateEndpoint: '/gateway/marketing/activities/Update',
    idField: 'activityId',
    listRoute: '/promotions',
    listSource: { url: '/gateway/marketing/activities/List', method: 'POST' },
    fields: [
      { field: 'activityName', label: '活动名', required: true, pattern: '^.{2,128}$', patternMessage: '活动名 2-128 个字符' },
      { field: 'activityType', label: '活动类型', type: 'select', required: true, default: 1, static: [{ value: 1, label: '满减' }, { value: 2, label: '满折' }, { value: 3, label: '满赠' }] },
      { field: 'thresholdAmount', label: '门槛金额', type: 'number', min: 0, default: 0, help: '按适用行金额合计判定，0 表示无门槛' },
      { field: 'discountAmount', label: '优惠金额', type: 'number', min: 0, default: 0, help: '满减用' },
      { field: 'discountRate', label: '折扣率', type: 'number', min: 0, max: 10, default: 10, help: '满折用。10 表示不打折，8.5 表示 85 折' },
      { field: 'giftTemplateId', label: '赠送券模板', type: 'select', options: 'couponTemplates', default: 0, help: '满赠时必填' },
      { field: 'giftQuantity', label: '赠送张数', type: 'number', min: 1, max: 100, default: 1, help: '满赠每单赠送张数，1 ~ 100' },
      { field: 'startTime', label: '开始时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'endTime', label: '结束时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'perOrderLimit', label: '每单限用', type: 'number', min: 0, default: 0 },
      { field: 'totalQuantity', label: '总限量', type: 'number', min: 0, default: 0, help: '0 表示不限量' },
      { field: 'platformId', label: '归属平台', type: 'select', options: 'platforms', default: 0 },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0 },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
  },

  seckillSession: {
    title: '新建场次',
    desc: '场次本身不含库存。库存由「发布」动作单独划转',
    createEndpoint: '/gateway/marketing/seckill/sessions/Create',
    updateEndpoint: '/gateway/marketing/seckill/sessions/Update',
    idField: 'sessionId',
    listRoute: '/seckill',
    listSource: { url: '/gateway/marketing/seckill/sessions/List', method: 'POST' },
    fields: [
      { field: 'sessionName', label: '场次名', required: true, pattern: '^.{2,128}$', patternMessage: '场次名 2-128 个字符' },
      { field: 'startTime', label: '开始时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'endTime', label: '结束时间', type: 'datetime', required: true, toApi: toUtcIso, format: toLocalInput },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0 },
      { field: 'platformId', label: '归属平台', type: 'select', options: 'platforms', default: 0 },
    ],
  },

  brand: {
    title: '新建品牌',
    desc: '品牌下有商品时不能删除，只能停用',
    createEndpoint: '/gateway/brands/Create',
    updateEndpoint: '/gateway/brands/Update',
    idField: 'brandId',
    listRoute: '/brands',
    listSource: { url: '/gateway/brands/List', method: 'GET' },
    fields: [
      { field: 'brandName', label: '品牌名', required: true, pattern: '^.{1,64}$', patternMessage: '品牌名 1-64 个字符' },
      { field: 'brandCode', label: '品牌编码', pattern: '^.{0,64}$', patternMessage: '品牌编码不能超过 64 个字符', help: '选填，填写时全局唯一' },
      { field: 'logo', label: '品牌 Logo', help: '图片 URL' },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0, help: '小的在前' },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
  },

  logisticsCompany: {
    title: '新建物流公司',
    desc: '发货表单的下拉数据源。已发出的单存的是公司名快照，改这里不影响历史单',
    createEndpoint: '/gateway/logistics-companies/Create',
    updateEndpoint: '/gateway/logistics-companies/Update',
    idField: 'logisticsId',
    listRoute: '/orders/logistics-companies',
    listSource: { url: '/gateway/logistics-companies/List', method: 'POST' },
    fields: [
      { field: 'companyName', label: '公司名称', required: true, pattern: '^.{1,64}$', patternMessage: '公司名称 1-64 个字符' },
      { field: 'companyCode', label: '公司编码', help: '如 sf、yto。留空即可' },
      { field: 'logo', label: 'Logo', help: '图片 URL' },
      { field: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0, help: '小的在前，发货下拉按它排' },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
      { field: 'remark', label: '备注', type: 'textarea', rows: 2 },
    ],
  },

  // 小程序配置 = 平台在小程序里**看得见**的那部分：商城名、Logo、公告、三档主题色。
  // 刻意不把运费、联系人、编码放进来 —— 那些是「平台的资料」不是「小程序的配置」，
  // 放一起会让运营为了改个公告色去翻一屏不相关的字段。
  // 保存复用 platforms/Update（该命令对未传的字段会用原值，不存在「部分更新」问题）。
  appConfig: {
    title: '小程序配置',
    desc: '商城名称、Logo、公告与主题色。用户在小程序里看到的就是这些',
    createEndpoint: '/gateway/platforms/Update',
    updateEndpoint: '/gateway/platforms/Update',
    idField: 'platformId',
    listRoute: '/platforms',
    listSource: { url: '/gateway/platforms/List', method: 'POST' },
    // platforms/Update 的 platformName / platformCode / contactName / contactPhone
    // 都是必填，而这一页刻意不显示它们 —— 不带上就会被 400 挡下。
    carry: ['platformName', 'platformCode', 'contactName', 'contactPhone'],
    fields: [
      { field: 'mallName', label: '商城名称', required: true, pattern: '^.{2,128}$', patternMessage: '商城名称 2-128 个字符' },
      { field: 'logo', label: '平台 Logo', help: '图片 URL' },
      { field: 'notice', label: '首页公告', type: 'textarea', rows: 3, help: '最多 500 字' },
      { field: 'primaryColor', label: '主题色', type: 'color', default: '#0071e3' },
      { field: 'tabColor', label: 'TabBar 选中色', type: 'color', default: '#0071e3' },
      { field: 'backgroundColor', label: '页面背景色', type: 'color', default: '#f5f5f7' },
      { field: 'status', label: '状态', type: 'select', required: true, default: 1, static: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
  },
};

export default FORMS;
