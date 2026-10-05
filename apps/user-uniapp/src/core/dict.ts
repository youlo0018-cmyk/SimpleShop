export const ORDER_STATUS_NAMES: Record<number, string> = {
  10: '待付款',
  20: '待发货',
  30: '待收货',
  40: '待取货',
  50: '已完成',
  60: '已退款',
  91: '已取消',
};

export const DELIVERY_NAMES: Record<number, string> = {
  1: '快递',
  2: '虚拟商品',
  3: '到店自提',
};

export function displayName(value: unknown, map: Record<string, string>): string {
  return map[String(value)] || '—';
}
