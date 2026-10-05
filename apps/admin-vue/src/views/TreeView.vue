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
      />
    </div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 5" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <el-tree
        v-else-if="filtered.length"
        :data="filtered"
        :props="treeProps"
        node-key="id"
        default-expand-all
        :expand-on-click-node="false"
      >
        <template #default="{ data }">
          <span class="node">
            <span class="node__label">{{ label(data) }}</span>
            <span class="node__meta">
              <span
                v-if="config.statusField"
                class="pill"
                :class="statusClass(data)"
              >{{ statusTextOf(data) }}</span>
              <span v-if="config.codeField" class="node__code mono">{{ emptyText(data[config.codeField]) }}</span>
              <span v-if="config.sortField" class="node__sort">排序 {{ emptyText(data[config.sortField]) }}</span>
            </span>
          </span>
        </template>
      </el-tree>

      <div v-else class="empty">
        <div class="empty__title">{{ config.emptyHint || '暂无数据' }}</div>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import { emptyText } from '@/utils/format';

const props = defineProps<{ config: any }>();
// computed 而非快照：同一个组件服务多个树页面（分类 / 权限点），
// 快照会让切换后仍显示上一个树的数据。
const config = computed(() => props.config);

const loading = ref(true);
const keyword = ref('');
const nodes = ref<any[]>([]);

const treeProps = { children: 'children', label: 'name' };

async function load() {
  loading.value = true;
  try {
    const data = await request(config.value.endpoint, {
      method: config.value.method || 'GET',
      silent: true,
    });
    nodes.value = Array.isArray(data) ? data : (data?.items || []);
  } catch {
    nodes.value = [];
  } finally {
    loading.value = false;
  }
}

function label(data: any) {
  return emptyText(data[config.value.labelField || 'name']);
}

function statusClass(data: any) {
  return 'pill--' + statusColor(config.value.statusDict, data[config.value.statusField]);
}

function statusTextOf(data: any) {
  const v = data[config.value.statusField];
  return data[config.value.statusField + 'Name'] || statusText(config.value.statusDict, v);
}

// 关键词过滤：命中父节点时保留整棵子树。
// 直接丢弃未命中的父节点会让子节点也一起消失 —— 而分类名重���时
// 用户搜的往往正是叶子节点的名字。
function filterTree(list: any[], kw: string): any[] {
  const out = [];
  for (const n of list) {
    const children = n.children ? filterTree(n.children, kw) : [];
    const self = JSON.stringify(n).toLowerCase().includes(kw);
    if (self || children.length > 0) out.push({ ...n, children });
  }
  return out;
}

const filtered = computed(() => {
  const kw = keyword.value.trim().toLowerCase();
  if (!kw) return nodes.value;
  return filterTree(nodes.value, kw);
});

watch([config, keyword], () => {});
watch(config, load);
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

.node {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  width: 100%;
  padding-right: var(--space-2);
  font-size: var(--text-sub);
}

.node__meta {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.node__code {
  color: var(--text-2);
}

.node__sort {
  font-size: var(--text-foot);
  color: var(--text-3);
}

.empty {
  padding: var(--space-8) 0;
}

.empty__title {
  font-size: var(--text-sub);
  color: var(--text-2);
}

.skel {
  padding: var(--space-6);
}
</style>
