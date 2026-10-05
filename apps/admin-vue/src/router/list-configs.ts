// 列表页配置。字段与格式都声明在这里，页面组件不出现任何格式化逻辑。
// method 多数后台列表是 POST；products/List 与 inventory/List 后端写的是 GET。
// response 形状不统一（有的分页信封、有的裸数组），由 ListView 兼容。

export const LISTS = {
  products: {
    title: '商品列表',
    desc: 'SPU 与价格区间，审核与上下架状态',
    endpoint: '/gateway/products/List',
    method: 'GET',
    tabs: [
      { label: '全部', value: 0 },
      { label: '待审核', value: 10 },
      { label: '已通过', value: 20 },
      { label: '已拒绝', value: 30 },
    ],
    byStatus: true,
    search: true,
    searchPlaceholder: '商品名 / 编码',
    columns: [
      { field: 'spuName', label: '商品', width: 220 },
      { field: 'brandName', label: '品牌', width: 110, format: 'text' },
      { field: 'categoryName', label: '分类', width: 130, format: 'text' },
      { field: 'minPrice', label: '最低价', width: 100, num: true, format: 'amount' },
      { field: 'maxPrice', label: '最高价', width: 100, num: true, format: 'amount' },
      { field: 'auditStatus', label: '审核', width: 100, dict: 'audit' },
      { field: 'status', label: '上下架', width: 100, dict: 'shelf' },
      { field: 'sales', label: '销量', width: 90, num: true, format: 'count' },
    ],
  },

  inventory: {
    title: '库存管理',
    desc: '可用 / 锁定 / 已扣，以及预警阈值',
    endpoint: '/gateway/inventory/List',
    method: 'GET',
    search: true,
    searchPlaceholder: '商品名 / 规格',
    columns: [
      { field: 'productName', label: '商品', width: 220 },
      { field: 'skuSpecText', label: '规格', width: 120, format: 'text' },
      { field: 'available', label: '可用', width: 90, num: true, format: 'count' },
      { field: 'locked', label: '锁定', width: 90, num: true, format: 'count' },
      { field: 'deducted', label: '已扣', width: 90, num: true, format: 'count' },
      { field: 'warnThreshold', label: '预警阈值', width: 110, num: true, format: 'count' },
      {
        field: 'isLowStock',
        label: '预警',
        width: 90,
        dict: 'lowStock',
      },
      { field: 'updatedAt', label: '更新时间', width: 150, format: 'time' },
    ],
  },

  customers: {
    title: '客户列表',
    desc: '按登录名 / 昵称 / 手机号搜索，可停用',
    endpoint: '/gateway/admin/customers/List',
    method: 'POST',
    byStatus: true,
    tabs: [
      { label: '全部', value: 0 },
      { label: '正常', value: 1 },
      { label: '已停用', value: 2 },
    ],
    search: true,
    searchPlaceholder: '登录名 / 昵称 / 手机号',
    columns: [
      { field: 'customerName', label: '登录名', width: 150 },
      { field: 'nickName', label: '昵称', width: 130, format: 'text' },
      // 后台要给客服完整手机号找人，脱敏了就等于没法用
      { field: 'phone', label: '手机号', width: 140, format: 'text' },
      { field: 'genderName', label: '性别', width: 80, format: 'text' },
      { field: 'status', label: '状态', width: 100, dict: 'customer' },
      { field: 'lastLoginAt', label: '最后登录', width: 150, format: 'time' },
      { field: 'createdAt', label: '注册时间', width: 150, format: 'time' },
    ],
    actions: [
      {
        label: '停用',
        endpoint: '/gateway/admin/customers/ChangeStatus',
        danger: true,
        okText: '已停用',
        build: (r: any) => ({ customerId: r.customerId, status: 2 }),
      },
    ],
    actionsWidth: 120,
  },

  brands: {
    title: '品牌管理',
    desc: '商品品牌，删除前需先解绑商品',
    endpoint: '/gateway/brands/List',
    method: 'GET',
    search: true,
    searchPlaceholder: '品牌名 / 编码',
    columns: [
      { field: 'brandName', label: '品牌名', width: 160 },
      { field: 'brandCode', label: '编码', width: 140, format: 'text' },
      { field: 'logo', label: 'Logo', width: 100, format: 'text' },
      { field: 'sort', label: '排序', width: 90, num: true, format: 'count' },
      { field: 'createdAt', label: '创建时间', width: 160, format: 'time' },
    ],
  },

  evaluates: {
    title: '评价管理',
    desc: 'SPU 级评价，可隐藏与回复',
    endpoint: '/gateway/evaluates/admin/List',
    method: 'POST',
    search: true,
    searchPlaceholder: '商品名 / 客户 / 内容',
    columns: [
      { field: 'spuName', label: '商品', width: 180 },
      { field: 'starScore', label: '评分', width: 90, num: true, format: 'score' },
      { field: 'content', label: '评价内容', width: 240, format: 'text' },
      { field: 'customerName', label: '客户', width: 120, format: 'text' },
      {
        field: 'isAnonymous',
        label: '匿名',
        width: 90,
        dict: 'anonymous',
      },
      {
        field: 'isHidden',
        label: '展示',
        width: 90,
        dict: 'hidden',
      },
      { field: 'createdAt', label: '评价时间', width: 150, format: 'time' },
    ],
  },

  operationLogs: {
    title: '操作日志',
    desc: '后台写操作留痕',
    endpoint: '/gateway/logs/Operation/List',
    method: 'POST',
    columns: [
      { field: 'occurredAt', label: '时间', width: 155, format: 'time' },
      { field: 'service', label: '服务', width: 150, format: 'text' },
      { field: 'operatorName', label: '操作人', width: 110, format: 'text' },
      { field: 'method', label: '方法', width: 80, format: 'text' },
      { field: 'path', label: '路径', width: 240, format: 'text' },
      { field: 'statusCode', label: '响应码', width: 90, num: true, format: 'count' },
      { field: 'elapsedMs', label: '耗时', width: 90, num: true, format: 'ms' },
      // 请求 Id 是编码类字段，等宽字体才能逐位比对
      { field: 'requestId', label: '请求 Id', width: 220, format: 'text', mono: true },
    ],
  },

  exceptionLogs: {
    title: '异常日志',
    desc: '未处理异常，点消息看完整堆栈',
    endpoint: '/gateway/logs/Exception/List',
    method: 'POST',
    columns: [
      { field: 'occurredAt', label: '时间', width: 155, format: 'time' },
      { field: 'service', label: '服务', width: 150, format: 'text' },
      { field: 'path', label: '路径', width: 220, format: 'text' },
      { field: 'message', label: '异常消息', width: 280, format: 'text' },
      { field: 'requestId', label: '请求 Id', width: 220, format: 'text', mono: true },
    ],
  },

  deadLetters: {
    title: '死信与重放',
    desc: '重试耗尽的消息，可按 EventId 精确重放',
    endpoint: '/gateway/logs/DeadLetter/List',
    method: 'POST',
    columns: [
      { field: 'failedAt', label: '失败时间', width: 155, format: 'time' },
      { field: 'eventType', label: '事件', width: 130, format: 'text' },
      { field: 'queueName', label: '队列', width: 150, format: 'text' },
      { field: 'errorMessage', label: '失败原因', width: 300, format: 'text' },
      { field: 'attempts', label: '尝试', width: 80, num: true, format: 'count' },
      { field: 'replayCount', label: '已重放', width: 90, num: true, format: 'count' },
    ],
    actions: [
      {
        label: '重放',
        endpoint: '/gateway/logs/DeadLetter/Replay',
        okText: '已重放',
        build: (r: any) => ({ eventId: r.eventId }),
      },
    ],
    actionsWidth: 100,
    emptyHint: '没有死信 —— 消费链路是健康的',
  },
};

export default LISTS;
