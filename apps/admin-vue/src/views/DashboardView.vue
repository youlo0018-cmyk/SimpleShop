<template>
  <div>
    <div class="page-header">
      <div>
        <h2 class="page-header__title">经营概览</h2>
        <p class="page-header__desc">{{ rangeName }} · 数据口径以支付时间为准</p>
      </div>
      <el-segmented v-model="range" :options="rangeOptions" />
    </div>

    <!-- 骨架屏而非整页遮罩（DESIGN_SPEC 5.2 / UI-TOKEN-009） -->
    <div v-if="loading" class="grid">
      <div v-for="i in 8" :key="i" class="card">
        <div class="skeleton-row" style="width: 60%" />
        <div class="skeleton-row" style="width: 40%; margin-top: var(--space-3)" />
      </div>
    </div>

    <template v-else>
      <div class="grid">
        <div class="card stat">
          <div class="stat__label">成交额</div>
          <div class="stat__value num">{{ formatAmount(report.gmv) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">客单价</div>
          <div class="stat__value num">{{ formatAmount(report.avgOrderValue) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">订单数</div>
          <div class="stat__value num">{{ formatCount(report.orderCount) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">支付订单数</div>
          <div class="stat__value num">{{ formatCount(report.paidOrderCount) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">完成订单数</div>
          <div class="stat__value num">{{ formatCount(report.completedOrderCount) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">退款金额</div>
          <div class="stat__value num">{{ formatAmount(report.refundAmount) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">退款率</div>
          <div class="stat__value num">{{ formatPercent(report.refundRate) }}</div>
        </div>
        <div class="card stat">
          <div class="stat__label">库存预警</div>
          <div class="stat__value num">
            {{ formatCount(report.lowStockCount) }}
            <span class="stat__unit">个 SKU</span>
          </div>
        </div>
      </div>

      <div class="card notes">
        <h3 class="card__title">口径说明</h3>
        <ul class="notes__list">
          <li>成交额按<b>支付时间</b>统计，不含已取消与已退款的订单。</li>
          <li>退款金额只算<b>审批通过</b>的退款单，按审批时间落在区间内。</li>
          <li>库存预警数为「阈值大于 0 且可用量低于阈值」的 SKU 数量。</li>
        </ul>
      </div>
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
.grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--space-4);
}

.stat {
  padding: var(--space-5);
}

.stat__label {
  font-size: var(--text-foot);
  color: var(--text-2);
  margin-bottom: var(--space-2);
}

.stat__value {
  font-size: var(--text-title-2);
  line-height: var(--lh-title-2);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.stat__unit {
  font-size: var(--text-foot);
  font-weight: 400;
  color: var(--text-3);
}

.notes {
  margin-top: var(--space-6);
}

.notes__list {
  margin: 0;
  padding-left: var(--space-5);
  font-size: var(--text-sub);
  line-height: var(--lh-sub);
  color: var(--text-2);
}

.notes__list li {
  margin-bottom: var(--space-1);
}

.notes__list b {
  color: var(--text-1);
  font-weight: 500;
}
</style>
