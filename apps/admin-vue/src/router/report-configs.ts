// 四张报表的配置（BUSINESS.md 17）。
// 抽成配置而不是写四个组件：四张表结构同构，复制四份之后改一次口径要改四处。
// `format` 决定用哪种格式化器，口径集中在 ReportView 里的 FORMATTERS。

export const REPORTS = {
  business: {
    title: '经营报表',
    desc: '按支付时间统计，含退款与库存预警',
    endpoint: '/gateway/reports/Report',
    byRange: true,
    note: '成交额按支付时间统计，不含已取消与已退款；退款金额只算审批通过的退款单。',
    metrics: [
      { field: 'gmv', label: '成交额', format: 'amount', strong: true },
      { field: 'avgOrderValue', label: '客单价', format: 'amount' },
      { field: 'orderCount', label: '订单数', format: 'count' },
      { field: 'paidOrderCount', label: '支付订单数', format: 'count' },
      { field: 'completedOrderCount', label: '完成订单数', format: 'count' },
      { field: 'refundAmount', label: '退款金额', format: 'amount' },
      { field: 'refundRate', label: '退款率', format: 'percent' },
      { field: 'lowStockCount', label: '库存预警 SKU 数', format: 'count' },
    ],
  },

  point: {
    title: '积分报表',
    desc: '发放 / 消耗 / 过期与当前余额',
    endpoint: '/gateway/reports/Point',
    byRange: true,
    note: '「冻结」不计入消耗：它只是把可用挪到冻结，钱还没付、可能还会解冻回去。',
    metrics: [
      { field: 'earnedTotal', label: '发放总额', format: 'count', strong: true },
      { field: 'consumedTotal', label: '消耗总额', format: 'count' },
      { field: 'expiredTotal', label: '过期总额', format: 'count' },
      { field: 'lockedTotal', label: '当前冻结', format: 'count' },
      { field: 'currentBalance', label: '当前总余额', format: 'count' },
    ],
  },

  marketing: {
    title: '营销效果报表',
    desc: '券的发放 / 领取 / 核销与折扣',
    endpoint: '/gateway/reports/Marketing',
    byRange: true,
    note: '发放与领取刻意分开看：运营常把「发了 1000 张」当业绩，只有被领走的才算触达。',
    metrics: [
      { field: 'issuedTotal', label: '发放总数', format: 'count', strong: true },
      { field: 'receivedTotal', label: '领取数', format: 'count' },
      { field: 'consumedTotal', label: '核销数', format: 'count' },
      { field: 'consumeRate', label: '核销率', format: 'percent' },
      { field: 'discountTotal', label: '折扣总额', format: 'amount' },
    ],
  },

  seckill: {
    title: '秒杀效果报表',
    desc: '逐场次的参与 / 成功 / 售罄',
    endpoint: '/gateway/reports/Seckill',
    byRange: true,
    note: '不含「场次 PV」：前台秒杀列表的 sessionId 在请求体里，pv 日志只记录 path 与 query，取不到。',
    metrics: [
      { field: 'totalParticipants', label: '参与人数合计', format: 'count', strong: true },
      { field: 'totalGrabSuccess', label: '抢购成功合计', format: 'count' },
      { field: 'totalGmv', label: '成交额合计', format: 'amount' },
    ],
    table: {
      title: '逐场次',
      rows: (d: any) => d.sessions || [],
      columns: [
        { field: 'sessionName', label: '场次', width: 180 },
        { field: 'participantCount', label: '参与', width: 90, num: true, format: 'count' },
        { field: 'grabSuccessCount', label: '成功', width: 90, num: true, format: 'count' },
        { field: 'stockTotal', label: '库存', width: 90, num: true, format: 'count' },
        { field: 'stockSold', label: '已抢', width: 90, num: true, format: 'count' },
        { field: 'sellOutRate', label: '售罄率', width: 100, num: true, format: 'percent' },
      ],
    },
  },
};

export default REPORTS;
