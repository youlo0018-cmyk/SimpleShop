<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ config.title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
      <el-button @click="back">返回列表</el-button>
    </div>

    <section v-if="loading" class="panel">
      <div class="skeleton-row" />
    </section>

    <section v-else-if="data" class="panel">
      <dl class="fields">
        <div v-for="f in config.fields" :key="f.field" class="fields__row">
          <dt class="fields__label">{{ f.label }}</dt>
          <dd class="fields__value">
            <span v-if="f.dict" class="pill" :class="'pill--' + statusColor(f.dict, data[f.field])">
              {{ data[f.field + 'Name'] || statusText(f.dict, data[f.field]) }}
            </span>
            <span v-else :class="[f.num ? 'num' : '', f.mono ? 'mono' : '']">
              {{ render(f, data[f.field]) }}
            </span>
          </dd>
        </div>
      </dl>

      <template v-if="config.list">
        <h3 class="sec">{{ config.list.title }}</h3>
        <el-table :data="rows" class="table">
          <el-table-column
            v-for="c in config.list.columns"
            :key="c.field"
            :label="c.label"
            :min-width="c.width || 120"
          >
            <template #default="{ row }">
              <span :class="c.num ? 'num' : c.mono ? 'mono' : ''">{{ render(c, row[c.field]) }}</span>
            </template>
          </el-table-column>
        </el-table>
        <p v-if="!rows.length" class="empty">{{ config.list.emptyHint || '没有明细行' }}</p>
      </template>
    </section>

    <section v-else class="panel">
      <p class="empty">{{ config.emptyHint || '未找到该记录' }}</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import { formatAmount, formatCount, formatDateTime, emptyText } from '@/utils/format';

const props = defineProps<{ config: any }>();
const route = useRoute();
const router = useRouter();
const config = computed(() => props.config);

const loading = ref(true);
const data = ref<any>(null);
const rows = ref<any[]>([]);

const entityId = computed(() => Number(route.params.id || 0));

const FORMATTERS: Record<string, (v: unknown) => string> = {
  amount: (v) => formatAmount(v),
  count: (v) => formatCount(v),
  time: (v) => formatDateTime(v),
  text: (v) => emptyText(v),
};

function render(def: any, value: unknown) {
  const fn = FORMATTERS[def.format];
  return fn ? fn(value) : emptyText(value);
}

async function load() {
  loading.value = true;
  try {
    data.value = await request(config.value.detailEndpoint, {
      method: config.value.detailMethod || 'POST',
      body: { [config.value.idField || 'id']: entityId.value },
      silent: true,
    });
    // 明细行有两种来源：详情响应里自带 items，或者再单独查一次。
    const inline = data.value?.[config.value.list?.key || 'items'];
    if (Array.isArray(inline)) {
      rows.value = inline;
    } else if (config.value.list?.endpoint) {
      rows.value =
        (await request(config.value.list.endpoint, {
          method: config.value.list.method || 'POST',
          body: { ...(config.value.list.body || {}), [config.value.list.idField || 'id']: entityId.value },
          silent: true,
        })) || [];
    } else {
      rows.value = [];
    }
  } catch {
    data.value = null;
  } finally {
    loading.value = false;
  }
}

function back() {
  router.push(config.value.listRoute);
}

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

.panel {
  padding: var(--space-6);
}

.fields {
  margin: 0;
}

.fields__row {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr);
  gap: var(--space-4);
  padding: var(--space-2) 0;
  border-bottom: 1px solid var(--hairline);
}

.fields__row:last-child {
  border-bottom: none;
}

.fields__label {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.fields__value {
  margin: 0;
  font-size: var(--text-sub);
  min-width: 0;
  overflow-wrap: anywhere;
}

.sec {
  margin: var(--space-6) 0 var(--space-3);
  font-size: var(--text-title-3);
  font-weight: 600;
}

.table {
  width: 100%;
}

.empty {
  margin: var(--space-4) 0 0;
  font-size: var(--text-foot);
  color: var(--text-3);
}
</style>
