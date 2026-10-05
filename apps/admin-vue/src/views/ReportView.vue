<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ config.title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
      <div class="head__tools">
        <!-- 只给需要时间档位的报表显示；秒杀报表按场次筛，不按时间 -->
        <el-segmented v-if="config.byRange" v-model="range" :options="rangeOptions" />
      </div>
    </div>

    <div v-if="loading" class="panel skel">
      <div class="skeleton-row" style="width: 100%; height: 20px" />
      <div v-for="i in config.metrics.length" :key="i" class="skeleton-row" style="margin-top: var(--space-3)" />
    </div>

    <template v-else>
      <section class="panel">
        <div v-for="m in config.metrics" :key="m.field" class="row">
          <span class="row__label">{{ m.label }}</span>
          <span class="row__value" :class="{ strong: m.strong }">{{ render(m, data) }}</span>
        </div>
      </section>

      <!-- 秒杀报表是「逐场次」的，用表；其余是「单个总数」，用上面的行 -->
      <section v-if="config.table" class="panel tablePanel">
        <h3 class="card__title">{{ config.table.title }}</h3>
        <el-table :data="config.table.rows(data)" class="table">
          <el-table-column
            v-for="col in config.table.columns"
            :key="col.field"
            :label="col.label"
            :min-width="col.width"
            :align="col.align || 'left'"
          >
            <template #default="{ row }">
              <span :class="col.num ? 'num' : ''">{{ render(col, row) }}</span>
            </template>
          </el-table-column>
        </el-table>
      </section>

      <p v-if="config.note" class="foot">{{ config.note }}</p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import request from '@/api/request';
import { formatAmount, formatCount, formatPercent, emptyText } from '@/utils/format';

// 格式化口径集中在这里（config 只声明用哪种），页面里不出现任何 toFixed。
// 之前分散到各页面各写一遍，改一次金额口径要改 N 处。
type Fmt = (v: unknown) => string;

const FORMATTERS: Record<string, Fmt> = {
  amount: (v) => formatAmount(v),
  count: (v) => formatCount(v),
  percent: (v) => formatPercent(v),
  text: (v) => emptyText(v),
  rate: (v) => formatPercent(v),
};

const props = defineProps<{ config: any }>();

// 🔴 必须用 computed，不能 `const config = props.config`。
// 四张报表复用同一个组件，vue-router 会**复用组件实例**（component 类型相同、
// 只是路由变了），于是 setup 只跑一次、props 被快照成第一次的值：
// 从「经营报表」切到「秒杀效果报表」，标题变了而数据还是经营报表的。
// 症状是「页面看起来像加载错了」，根因却在 setup 的取值方式上。
const config = computed(() => props.config);

const rangeOptions = [
  { label: '今日', value: 1 },
  { label: '昨日', value: 2 },
  { label: '近 7 天', value: 3 },
  { label: '近 30 天', value: 4 },
];

const range = ref(4);
const loading = ref(true);
const data = reactive<any>({});

// 按列声明的格式渲染；没声明 format 的当文本（走 emptyText，空值显破折号）
function render(def: any, source: any) {
  const raw = source?.[def.field];
  const fn = FORMATTERS[def.format];
  return fn ? fn(raw) : emptyText(raw);
}

async function load() {
  loading.value = true;
  try {
    const body: any = {};
    if (config.value.byRange) body.range = range.value;
    const result = await request(config.value.endpoint, { body, silent: true });
    Object.keys(data).forEach((k) => delete data[k]);
    Object.assign(data, result || {});
  } catch {
    Object.keys(data).forEach((k) => delete data[k]);
  } finally {
    loading.value = false;
  }
}

// 换报表 = 换 config，除了时间档位还要跟着换，所以两个都监听。
// 只监听 range 的话，从经营报表切到积分报表会一直显示上一张的数据。
watch([range, config], load);
onMounted(load);
</script>

<style scoped>
.head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  margin-bottom: var(--space-5);
}

.head__title {
  margin: 0;
  font-size: var(--text-display);
  line-height: var(--lh-display);
  font-weight: 600;
  letter-spacing: -0.5px;
}

.head__desc {
  margin: var(--space-1) 0 0;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.strong {
  font-weight: 600;
}

.tablePanel {
  margin-top: var(--space-4);
}

.skel {
  padding: var(--space-6);
}

.foot {
  margin: var(--space-4) 0 0;
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-3);
}
</style>
