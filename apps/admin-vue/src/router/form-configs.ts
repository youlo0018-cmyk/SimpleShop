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
    listSource: { url: '/gateway/users/List', method: 'POST' },
    listRoute: '/users',
    fields: [
      { field: 'userName', label: '登录名', required: true, pattern: '^[A-Za-z0-9_]{3,32}$', patternMessage: '登录名为 3-32 位字母、数字或下划线', readonlyInEdit: true, help: '创建后不可修改' },
      { field: 'password', label: '密码', required: true, secret: true, pattern: '^.{8,32}$', patternMessage: '密码长度 8-32 位' },
      { field: 'nickName', label: '昵称', required: true, pattern: '^.{2,32}$', patternMessage: '昵称 2-32 个字符' },
      { field: 'phone', label: '手机号', required: true, pattern: PHONE_PATTERN, patternMessage: '请填写正确的 11 位手机号' },
      { field: 'email', label: '邮箱', pattern: '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$', patternMessage: '邮箱格式不正确' },
      { field: 'tenantType', label: '租户类型', type: 'select', required: true, default: 1, static: [{ value: 1, label: '平台账号' }, { value: 2, label: '商户账号' }], readonlyInEdit: true },
      { field: 'platformId', label: '所属平台', type: 'select', options: 'platforms', default: 0 },
      { field: 'merchantId', label: '所属商户', type: 'select', options: 'merchants', default: 0 },
      { field: 'roleIds', label: '角色', type: 'select', options: 'roles', multiple: true, filterable: true },
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
