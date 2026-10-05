// 状态字典（DESIGN_SPEC 5.3 / 7.1）。
//
// ⚠️ 职责边界：**本表只负责颜色，不负责文案**（DESIGN_SPEC 7.3）。
// 枚举文案由后端下发的 XxxText 字段提供；只有后端没给 XxxText 时
// 才回退到本表的 text，避免界面上出现裸数字或英文枚举。
// 两边都写一份文案的话，后端改了枚举名而前端没跟上，就会出现
// 「接口返回新状态、页面显示旧文案」这种极难排查的问题。

// 各业务的状态码 → { 文案兜底, 语义色档位 }
const D = {
  // 订单状态（OrderStatuses）
  order: {
    10: { text: '待支付', color: 'warning' },
    20: { text: '待发货', color: 'info' },
    30: { text: '待收货', color: 'info' },
    40: { text: '待取货', color: 'info' },
    50: { text: '已完成', color: 'success' },
    60: { text: '已退款', color: 'danger' },
    91: { text: '已取消', color: 'neutral' },
  },

  // 支付单状态（PaymentStatuses）
  payment: {
    1: { text: '待支付', color: 'warning' },
    20: { text: '已支付', color: 'success' },
    30: { text: '已关闭', color: 'neutral' },
  },

  // 退款单状态（RefundStatuses）
  refund: {
    10: { text: '待审批', color: 'warning' },
    20: { text: '已退款', color: 'success' },
    90: { text: '已拒绝', color: 'danger' },
  },

  // 审核状态（AuditStatuses）
  audit: {
    10: { text: '待审核', color: 'warning' },
    20: { text: '已通过', color: 'success' },
    30: { text: '已拒绝', color: 'danger' },
  },

  // 上下架（ShelfStatuses）
  shelf: {
    1: { text: '已上架', color: 'success' },
    2: { text: '已下架', color: 'neutral' },
  },

  // 券状态（CouponStatuses）
  coupon: {
    1: { text: '未使用', color: 'info' },
    2: { text: '已占用', color: 'warning' },
    3: { text: '已核销', color: 'success' },
    4: { text: '已过期', color: 'neutral' },
  },

  // 券占用状态（OccupancyStatuses）
  occupancy: {
    1: { text: '已占用', color: 'warning' },
    2: { text: '已核销', color: 'success' },
    3: { text: '已回退', color: 'neutral' },
  },

  // 秒杀场次状态（SeckillSessionStatuses）
  seckill: {
    10: { text: '未开始', color: 'warning' },
    20: { text: '进行中', color: 'info' },
    30: { text: '已结束', color: 'neutral' },
    40: { text: '已取消', color: 'danger' },
  },

  // 抢购结果（SeckillGrabResults）
  grab: {
    0: { text: '处理中', color: 'info' },
    1: { text: '抢购成功', color: 'success' },
    2: { text: '已抢完', color: 'neutral' },
    3: { text: '未开抢', color: 'neutral' },
    4: { text: '超出限购', color: 'warning' },
    5: { text: '下单失败', color: 'danger' },
  },

  // 配送方式（BUSINESS.md 6.1）
  delivery: {
    1: { text: '快递', color: 'info' },
    2: { text: '虚拟商品', color: 'neutral' },
    3: { text: '自提', color: 'info' },
  },

  // 订单行来源（OrderSourceTypes）
  orderSource: {
    1: { text: '普通下单', color: 'neutral' },
    2: { text: '秒杀', color: 'info' },
  },

  // 优惠类型（PromotionTypes）
  promotionType: {
    1: { text: '满减', color: 'info' },
    2: { text: '满折', color: 'info' },
    3: { text: '满赠', color: 'success' },
  },

  // 优惠券类型
  couponType: {
    1: { text: '满减券', color: 'info' },
    2: { text: '折扣券', color: 'info' },
  },

  // 退款类型（RefundTypes）
  refundType: {
    1: { text: '整单退款', color: 'info' },
    2: { text: '部分退款', color: 'warning' },
  },

  // 账号启用状态
  userStatus: {
    1: { text: '启用', color: 'success' },
    2: { text: '停用', color: 'neutral' },
  },

  // 客户账号状态（与 userStatus 数值相同但语义不同，分开写以免将来改一边忘了另一边）
  customer: {
    1: { text: '正常', color: 'success' },
    2: { text: '已停用', color: 'neutral' },
  },

  // 库存是否预警。这个字段本来就是布尔，字典表照样管颜色，
  // 免得页面里为它单写一套 if。
  lowStock: {
    true: { text: '需补货', color: 'warning' },
    false: { text: '充足', color: 'neutral' },
  },

  // 评价是否匿名
  anonymous: {
    true: { text: '匿名', color: 'neutral' },
    false: { text: '实名', color: 'info' },
  },

  // 评价是否被后台隐藏。隐藏是「打标记不删数据」，
  // 客户在自己的评价列表里仍能看到「已被隐藏」，所以这里要显示出来而不是当成不存在。
  hidden: {
    true: { text: '已隐藏', color: 'danger' },
    false: { text: '展示中', color: 'success' },
  },
};

// 取状态对应的语义色档位。
// 状态码按字符串下发（DESIGN_SPEC 7.3），所以统一 Number() 一次再查表。
export function statusColor(kind, value) {
  const map = D[kind];
  if (!map) return 'neutral';
  // 布尔字段（如 isLowStock）的字典键就是 true/false，
  // 直接 Number() 会把它们变成 1/0 然后匹配不上。
  // 所以先按原值试一次，再退回数字键。
  const hit = map[value] || map[Number(value)];
  return hit ? hit.color : 'neutral';
}

// 取状态文案（后端未下发 XxxText 时的兜底）。页面应优先用后端的 XxxText。
export function statusText(kind, value) {
  const map = D[kind];
  if (!map) return '';
  const hit = map[value] || map[Number(value)];
  return hit ? hit.text : '';
}

// 渲染状态标签所需的完整信息：文案优先用后端下发的 XxxText。
export function statusPill(kind, value, backendText) {
  return {
    text: backendText || statusText(kind, value),
    color: statusColor(kind, value),
  };
}

export default { statusColor, statusText, statusPill };
