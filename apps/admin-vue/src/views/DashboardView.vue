<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">经营概览</h2>
        <p class="head__desc">{{ rangeName }} · 按支付时间统计</p>
      </div>
      <el-segmented v-model="range" :options="rangeOptions" />
    </div>

    <!-- 骨架屏而非整页遮罩（DESIGN_SPEC 5.2 / UI-TOKEN-009） -->
    <div v-if="loading" class="panel skel">
      <div class="skel__hero">
        <div class="skeleton-row" style="width: 96px" />
        <div class="skeleton-row" style="width: 220px; height: 40px; margin-top: var(--space-4)" />
      </div>
      <div v-for="i in 7" :key="i" class="row">
        <div class="skeleton-row" style="width: 72px" />
        <div class="skeleton-row" style="width: 88px" />
      </div>
    </div>

    <template v-else>
      <!-- 成交额单独做主视觉：一个页面只有一个焦点，8 个同权重卡片会互相抢、
           结果谁也不突出，这正是模板感 / 古板感的来源。 -->
      <section class="panel hero">
        <div class="hero__label">成交额</div>
        <div class="hero__value">{{ formatAmount(report.gmv) }}</div>
        <div class="hero__meta">
          <span>客单价 {{ formatAmount(report.avgOrderValue) }}</span>
          <span class="hero__dot" />
          <span>支付 {{ formatCount(report.paidOrderCount) }} / 共 {{ formatCount(report.orderCount) }} 单</span>
        </div>
      </section>

      <section class="panel">
        <div class="row">
          <span class="row__label">完成订单数</span>
          <span class="row__value">{{ formatCount(report.completedOrderCount) }}</span>
        </div>
        <div class="row">
          <span class="row__label">退款金额</span>
          <span class="row__value">{{ formatAmount(report.refundAmount) }}</span>
        </div>
        <div class="row">
          <span class="row__label">退款率</span>
          <span class="row__value">{{ formatPercent(report.refundRate) }}</span>
        </div>
        <div class="row">
          <span class="row__label">库存预警</span>
          <span class="row__value">{{ formatCount(report.lowStockCount) }} <span class="row__unit">个 SKU</span></span>
        </div>
      </section>

      <!-- 口径说明降级成脚注：它是补充信息，抢页面的注意力反而是本末倒置 -->
      <p class="foot">
        成交额按支付时间统计，不含已取消与已退款；退款金额只算审批通过的退款单。
      </p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue';
import request from '@/api/request';
import { formatAmount, formatCount, formatPercent, emptyText } from '@/utils/format';

// 时间范围四档：值与后端 ReportRanges 一致（1 今日 / 2 昨日 / 3 近 7 天 / 4 近 30 天）
const rangeOptions = [
  { label: '今日', value: 1 },
  { label: '昨日', value: 2 },
  { label: '近 7 天', value: 3 },
  { label: '近 30 天', value: 4 },
];

const range = ref<number>(3);
const loading = ref(true);
const rangeName = ref('近 7 天');

// 后端没返回时用空串，格式化函数会统一显示破折号，不显示 null
const report = reactive({
  gmv: '' as any,
  orderCount: '' as any,
  paidOrderCount: '' as any,
  completedOrderCount: '' as any,
  avgOrderValue: '' as any,
  refundAmount: '' as any,
  refundRate: '' as any,
  lowStockCount: '' as any,
});

async function load() {
  loading.value = true;
  try {
    const data = await request('/gateway/reports/Report', {
      body: { range: range.value },
      silent: true,
    });
    Object.assign(report, data || {});
    rangeName.value = data?.rangeName || emptyText(data?.rangeName);
  } catch {
    Object.assign(report, {
      gmv: '', orderCount: '', paidOrderCount: '', completedOrderCount: '',
      avgOrderValue: '', refundAmount: '', refundRate: '', lowStockCount: '',
    });
  } finally {
    loading.value = false;
  }
}

watch(range, load);
onMounted(load);
</script>

<style scoped>
/* ---------- 页头：比之前更松，标题与副标题间距拉开 ---------- */
.head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  margin-bottom: var(--space-6);
}

.head__title {
  margin: 0;
  font-size: var(--text-display);
  line-height: var(--lh-display);
  /* 字阶里最大的一档配 600 字重就够，再重会变成海报而不是后台 */
  font-weight: 600;
  letter-spacing: -0.5px;
}

.head__desc {
  margin: var(--space-1) 0 0;
  font-size: var(--text-sub);
  color: var(--text-2);
}

/* ---------- 主视觉：成交额 ---------- */
.hero {
  padding: var(--space-7) var(--space-6);
  margin-bottom: var(--space-4);
}

.hero__label {
  font-size: var(--text-sub);
  color: var(--text-2);
}

.hero__value {
  /* 数字是这个页面唯一的主视觉，字号给到最大一档；
     tabular-nums 让刷新时数字不会左右跳动 */
  margin-top: var(--space-2);
  font-size: var(--text-display);
  line-height: var(--lh-display);
  font-weight: 600;
  letter-spacing: -1px;
  font-variant-numeric: tabular-nums;
}

.hero__meta {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin-top: var(--space-2);
  font-size: var(--text-foot);
  color: var(--text-2);
}

/* 分隔点用圆点而不是竖线，比 | 更轻 */
.hero__dot {
  width: 3px;
  height: 3px;
  border-radius: var(--radius-pill);
  background: var(--text-3);
}

.row__unit {
  font-size: var(--text-foot);
  font-weight: 400;
  color: var(--text-3);
}

.foot {
  margin: var(--space-4) 0 0;
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-3);
}

/* ---------- 骨架屏 ---------- */
.skel {
  padding: var(--space-7) 0 0;
}

.skel__hero {
  padding: 0 var(--space-6) var(--space-6);
}
</style>
