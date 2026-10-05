// 格式化工具唯一来源（DESIGN_SPEC.md 7.1）。
// 铁律：页面组件里禁止散写 toFixed / substring / new Date 拼接，
// 一切展示都走这里的函数——这是「界面不出现原始数据」的执行手段。

// 空值占位：所有空值统一显示破折号，不显示 null / undefined / 空串（6.1 第 5 类）。
export const DASH = '—';

// 判断是否为空值：null / undefined / 空串 / 纯空白。
export function isBlank(value) {
  if (value === null || value === undefined) return true;
  if (typeof value === 'string') return value.trim() === '';
  return false;
}

// 空值统一显示破折号。
export function emptyText(value) {
  return isBlank(value) ? DASH : String(value);
}

// 后端数字按字符串下发，比较与回填前先 Number()（ID 除外，见 DESIGN_SPEC 7.3）。
function toNumber(value, fallback = 0) {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

// 补零到两位。
function pad(n, len = 2) {
  return String(n).padStart(len, '0');
}

// 把后端下发的值解析成本地 Date。后端一律下发 UTC，这里转成浏览器本地时区显示。
function toDate(value) {
  if (isBlank(value)) return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

// 格式化日期时间：YYYY-MM-DD HH:mm（本地化）。空值返回破折号。
export function formatDateTime(value) {
  const d = toDate(value);
  if (!d) return DASH;
  const day = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  return `${day} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// 格式化日期：YYYY-MM-DD。空值返回破折号。
export function formatDate(value) {
  const d = toDate(value);
  if (!d) return DASH;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

// 相对时间：7 天内显示 HH:mm，超过 7 天只显示日期（6.1 第 3 类）。
// 列表页大量展示「什么时候发生的」，超过一周还带时分反而是噪音。
export function formatRelative(value) {
  const d = toDate(value);
  if (!d) return DASH;
  const diffDays = (Date.now() - d.getTime()) / 86400000;
  return diffDays > 7 ? formatDate(d) : `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// 金额：纯数字 + 两位小数，**不加货币符号与千分位**（6.1 第 4 类 / 6.4）。
export function formatAmount(value) {
  if (isBlank(value)) return DASH;
  return toNumber(value).toFixed(2);
}

// 百分比：一位小数，如 12.3%（6.4）。
// 后端按**小数**下发比率（0.1234），这里才转百分比——
// 服务端下发百分数会让「0.5%」和「50」两种口径混在一起，前端没法统一格式化。
export function formatPercent(value) {
  if (isBlank(value)) return DASH;
  return `${(toNumber(value) * 100).toFixed(1)}%`;
}

// 评分：最多一位小数（6.1 第 8 类）。无评分时按规格显示 5.0（配灰字「暂无评价」）。
export function formatScore(value, fallback = 5) {
  if (isBlank(value)) return fallback.toFixed(1);
  return String(Number(toNumber(value).toFixed(1)));
}

// 布尔转是否（6.1 第 6 类）。后端可能下发 true / 1 / "1"。
export function yesNo(value) {
  return value === true || value === 1 || value === '1' ? '是' : '否';
}

// 手机号脱敏：138****8000。
export function maskPhone(value) {
  if (isBlank(value)) return DASH;
  const s = String(value);
  return s.length >= 11 ? `${s.slice(0, 3)}****${s.slice(7)}` : s;
}

// 长文本截断：超过 50 字截断（6.1 第 9 类），hover 全文由组件用 el-tooltip 展示。
export function truncate(value, max = 50) {
  if (isBlank(value)) return DASH;
  const s = String(value);
  return s.length > max ? `${s.slice(0, max)}…` : s;
}

// 数量：后端按字符串下发，需 Number() 后显示（DESIGN_SPEC 7.3）。
export function formatCount(value) {
  if (isBlank(value)) return DASH;
  return String(toNumber(value));
}

// 复制到剪贴板（订单号 / 取货码 / SKU 编码用）。
export async function copyText(text) {
  if (isBlank(text)) return false;
  try {
    await navigator.clipboard.writeText(String(text));
    return true;
  } catch {
    return false;
  }
}

export default {
  DASH,
  isBlank,
  emptyText,
  formatDateTime,
  formatDate,
  formatRelative,
  formatAmount,
  formatPercent,
  formatScore,
  yesNo,
  maskPhone,
  truncate,
  formatCount,
  copyText,
};
