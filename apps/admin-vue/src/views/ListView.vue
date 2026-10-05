<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ config.title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
      <el-input
        v-if="config.search"
        v-model="keyword"
        class="head__search"
        :placeholder="config.searchPlaceholder || '搜索'"
        clearable
        @keyup.enter="reload"
        @clear="reload"
      />
    </div>

    <el-tabs v-if="tabs.length" v-model="status" class="tabs" @tab-change="reload">
      <el-tab-pane v-for="t in tabs" :key="t.value" :label="t.label" :name="t.value" />
    </el-tabs>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 5" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <el-table v-else-if="rows.length" :data="rows" class="table" @row-click="onRow">
        <el-table-column
          v-for="c in config.columns"
          :key="c.field"
          :label="c.label"
          :min-width="c.width"
          :align="c.align || 'left'"
        >
          <template #default="{ row }">
            <span v-if="c.dict" class="pill" :class="'pill--' + statusColor(c.dict, row[c.field])">
              {{ row[c.field + 'Name'] || statusText(c.dict, row[c.field]) }}
            </span>
            <!-- 🔴 v-else 不能少。之前给它加 mono 选项时把 v-else 弄丢了，
                 两个 span 就都渲染了：字典列会变成「色标签 + 裸枚举值」并排，
                 比如「已通过 20」——直接违反「界面不出现枚举数字」这条硬约束。
                 普通列看不出来（两行渲染的是同一段文字），只有状态列才暴露。 -->
            <span v-else :class="c.num ? 'num' : c.mono ? 'mono' : ''">{{ render(c, row) }}</span>
          </template>
        </el-table-column>

        <!-- 🔴 操作列必须直接放在 el-table 里，**不能**用
             `<template #default>` 包起来再加 v-if。
             #default 是「整行的作用域插槽」，v-if 一旦为真，
             这个插槽就**顶替**了默认插槽的全部内容 ——
             外面那些 v-for 生成的列会被整个丢弃。
             症状是表格只剩「操作」一列、数据列全没了，
             而控制台一行错都没有，纯看代码很难发现。
             v-if 放在 el-table-column 上是没问题的（它是普通组件）。 -->
        <el-table-column
          v-if="config.actions"
          label="操作"
          :width="config.actionsWidth || 160"
          fixed="right"
          align="center"
        >
          <template #default="{ row }">
            <el-button
              v-for="a in config.actions"
              :key="a.label"
              :type="a.type || 'text'"
              :danger="a.danger"
              @click.stop="runAction(a, row)"
            >
              {{ a.label }}
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-else class="empty">
        <div class="empty__title">没有符合条件的数据</div>
        <div class="empty__desc">{{ config.emptyHint || '换个筛选条件或关键词试试' }}</div>
      </div>

      <div v-if="paged && rows.length" class="pager">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          background
          @current-change="load"
          @size-change="reload"
        />
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import {
  formatAmount,
  formatCount,
  formatDateTime,
  formatScore,
  emptyText,
} from '@/utils/format';

const props = defineProps<{ config: any }>();
// 🔴 computed 而非快照：同一个组件服务多个列表页，
// 快照会让切换后仍显示上一个列表的数据（见 CODING_STANDARD 68）
const config = computed(() => props.config);

type Fmt = (v: unknown) => string;

const FORMATTERS: Record<string, Fmt> = {
  amount: (v: unknown) => formatAmount(v),
  count: (v: unknown) => formatCount(v),
  time: (v: unknown) => formatDateTime(v),
  text: (v: unknown) => emptyText(v),
  // 评分最多一位小数，无数据显示 5.0（规格 6.1 第 8 类）
  score: (v: unknown) => formatScore(v),
  // 耗时带单位，毫秒。空值走「—」而不是 0ms —— 分不清「没记录」和「瞬时完成」
  ms: (v: unknown) => (v === '' || v === null || v === undefined ? '—' : `${v} ms`),
};

function render(def: any, row: any) {
  const fn = FORMATTERS[def.format];
  return fn ? fn(row?.[def.field]) : emptyText(row?.[def.field]);
}

const router = useRouter();
const loading = ref(true);
const rows = ref<any[]>([]);
const total = ref(0);
const paged = ref(false);

// 用独立 ref 而不是塞进一个 reactive 对象：模板里要直接绑定 v-model，
// reactive 的字段在模板作用域里取不到（会报 Property does not exist），
// 为此还得额外定义一堆 computed 转发，纯属绕路。
const status = ref(0);
const keyword = ref('');
const page = ref(1);
const pageSize = ref(20);

const tabs = computed(() => config.value.tabs || []);

async function load() {
  loading.value = true;
  try {
    const body: any = {};
    if (config.value.byStatus && status.value) body.status = status.value;
    if (config.value.search && keyword.value.trim()) body.keyword = keyword.value.trim();
    body.page = page.value;
    body.pageSize = pageSize.value;

    const result = await request(config.value.endpoint, {
      method: config.value.method || 'POST',
      body: config.value.method === 'GET' ? undefined : body,
      params: config.value.method === 'GET' ? body : undefined,
      silent: true,
    });

    // 后端返回结构不统一：有的接口回 { items, total }，有的（products/List、
    // inventory/List）回**裸数组**。这里两种都兼容，
    // 免得为了一个接口的形状差异就得改组件。
    if (Array.isArray(result)) {
      rows.value = result;
      total.value = result.length;
      paged.value = false;
    } else {
      rows.value = result?.items || [];
      total.value = Number(result?.total || 0);
      paged.value = true;
    }
  } catch {
    rows.value = [];
    total.value = 0;
  } finally {
    loading.value = false;
  }
}

function reload() {
  page.value = 1;
  load();
}

function onRow(row: any) {
  if (config.value.rowClick) config.value.rowClick(row, router);
}

async function runAction(action: any, row: any) {
  try {
    await request(action.endpoint, { body: { ...action.build(row) } });
    ElMessage.success(action.okText || '操作成功');
    await load();
  } catch {
    // request 已经弹过提示，这里不重复弹
  }
}

watch([status, config], () => reload());
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

.head__search {
  width: 280px;
}

.tabs {
  margin-bottom: var(--space-4);
}

.table {
  cursor: pointer;
}

.pager {
  padding: 0 var(--space-6);
}

.empty {
  padding: var(--space-8) 0;
}

.empty__title {
  font-size: var(--text-sub);
  color: var(--text-2);
}

.empty__desc {
  margin-top: var(--space-1);
  font-size: var(--text-foot);
  color: var(--text-3);
}

.skel {
  padding: var(--space-6);
}
</style>
