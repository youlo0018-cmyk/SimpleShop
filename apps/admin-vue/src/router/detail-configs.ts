// 详情页配置（菜单点不到的那类：详情 / 编辑需要真实 Id）。
// 和 ListView / FormView 一样是「一个组件 + 各自的字段声明」。

export const DETAILS = {
  customer: {
    title: '客户详情',
    desc: '后台给客服找人用，手机号不脱敏',
    detailEndpoint: '/gateway/admin/customers/Detail',
    detailMethod: 'POST',
    idField: 'customerId',
    listRoute: '/customers',
    fields: [
      { field: 'customerId', label: '客户 Id', mono: true },
      { field: 'customerName', label: '登录名' },
      { field: 'nickName', label: '昵称', format: 'text' },
      // 后台要给客服完整手机号找人，脱敏了就等于没法用。
      { field: 'phone', label: '手机号', format: 'text' },
      { field: 'genderName', label: '性别', format: 'text' },
      { field: 'status', label: '状态', dict: 'customer' },
      { field: 'lastLoginAt', label: '最后登录', format: 'time' },
      { field: 'createdAt', label: '注册时间', format: 'time' },
    ],
  },

  refund: {
    title: '退款详情',
    desc: '审批通过才真正退钱',
    detailEndpoint: '/gateway/refunds/Detail',
    detailMethod: 'POST',
    idField: 'refundId',
    listRoute: '/refunds',
    fields: [
      { field: 'refundNo', label: '退款单号', mono: true },
      { field: 'orderNo', label: '订单号', mono: true },
      { field: 'amount', label: '退款金额', num: true, format: 'amount' },
      { field: 'refundType', label: '类型', dict: 'refundType' },
      { field: 'status', label: '状态', dict: 'refund' },
      { field: 'reason', label: '申请原因', format: 'text' },
      { field: 'rejectReason', label: '拒绝原因', format: 'text' },
      { field: 'approverName', label: '审批人', format: 'text' },
      { field: 'createdAt', label: '申请时间', format: 'time' },
    ],
    // 部分退款争议基本都发生在「退到哪一行」这一层，所以明细行是这页的主角
    list: {
      key: 'items',
      title: '退款明细',
      emptyHint: '整单退款，没有明细行',
      columns: [
        { field: 'productName', label: '商品', width: 200 },
        { field: 'skuSpecText', label: '规格', width: 140 },
        { field: 'quantity', label: '退货数量', width: 100, num: true, format: 'count' },
        { field: 'amount', label: '退款金额', width: 110, num: true, format: 'amount' },
      ],
    },
  },
};

export default DETAILS;
