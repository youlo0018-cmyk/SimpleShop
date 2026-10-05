<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">索引对账</h2>
        <p class="head__desc">比对商品库与搜索索引，找出不一致的条目</p>
      </div>
      <el-button type="primary" :loading="loading" @click="load">重新对账</el-button>
    </div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <template v-else>
        <div class="stats">
          <div class="stat">
            <div class="stat__value">{{ count(data.dbProducts) }}</div>
            <div class="stat__label">库内商品</div>
          </div>
          <div class="stat">
            <div class="stat__value">{{ count(data.indexedCount) }}</div>
            <div class="stat__label">索引内商品</div>
          </div>
          <div class="stat">
            <div class="stat__value" :class="{ 'stat__value--bad': data.missingInIndex > 0 }">
              {{ count(data.missingInIndex) }}
            </div>
            <div class="stat__label">库有索引无</div>
          </div>
          <div class="stat">
            <div class="stat__value" :class="{ 'stat__value--bad': data.orphanInIndex > 0 }">
              {{ count(data.orphanInIndex) }}
            </div>
            <div class="stat__label">索引有库无</div>
          </div>
        </div>

        <p class="verdict" :class="verdictClass">
          {{ verdictText }}
        </p>

        <el-alert
          v-if="data.truncated"
          type="warning"
          :closable="false"
          show-icon
          title="本次只扫描了部分商品，结论不完整"
          description="商品量大时扫库有上限。宁可说「不知道」，也不要报一个假的「已一致」。"
        />

        <div v-if="samples.length" class="samples">
          <p class="samples__title">{{ samplesTitle }}（最多 20 个）</p>
          <div class="samples__ids">
            <span v-for="id in samples" :key="id" class="samples__id">{{ id }}</span>
          </div>
        </div>

        <div class="actions">
          <el-button :loading="rebuilding" @click="rebuild">补写并清理</el-button>
          <span class="actions__hint">{{ rebuildHint }}</span>
        </div>
      </template>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { formatCount, emptyText } from '@/utils/format';

const loading = ref(true);
const rebuilding = ref(false);
const data = reactive<Record<string, any>>({});

const count = (v: unknown) => formatCount(v);

const verdictText = computed(() => {
  if (data.truncated) return '只扫描了部分商品，结论不完整';
  if (data.consistent) return '索引与数据库一致';
  return `不一致：${count(data.missingInIndex)} 个库有索引无，${count(data.orphanInIndex)} 个索引有库无`;
});

const verdictClass = computed(() => {
  if (data.truncated) return 'verdict--warn';
  return data.consistent ? 'verdict--ok' : 'verdict--bad';
});

const samples = computed(() => data.missingInIndex > 0 ? (data.missingSampleIds || []) : (data.orphanSampleIds || []));
const samplesTitle = computed(() => (data.missingInIndex > 0 ? '待补写的商品 Id' : '待清理的孤儿 Id'));

async function load() {
  loading.value = true;
  try {
    const res = await request('/gateway/products/SearchIndex/Reconcile', {
      body: { pageSize: 500 },
      silent: true,
    });
    Object.assign(data, res || {});
  } catch {
    // request 已弹提示
  } finally {
    loading.value = false;
  }
}

// 「补写并清理」是**写操作**，所以要二次确认并说清后果：
// 它会删掉索引里的孤儿文档（用户可能正搜得到一个点进去 404 的商品）。
const rebuildHint = '按差集补写缺失的、清理孤儿的，不重建索引（重建期间搜索会拿到零结果）';

async function rebuild() {
  rebuilding.value = true;
  try {
    const res = await request('/gateway/products/SearchIndex/Reindex', {
      body: { deleteOrphans: true, pageSize: 200 },
    });
    ElMessage.success(emptyText(res && res.message) || '重建完成');
    await load();
  } catch {
    // request 已弹提示
  } finally {
    rebuilding.value = false;
  }
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

.stats {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-4);
}

@media (max-width: 900px) {
  .stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.stat {
  padding: var(--space-4);
  border-radius: var(--radius-card);
  background: var(--bg-page);
}

.stat__value {
  font-size: var(--text-title-1);
  line-height: var(--lh-title-1);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.stat__value--bad {
  color: var(--danger);
}

.stat__label {
  margin-top: var(--space-1);
  font-size: var(--text-foot);
  color: var(--text-2);
}

.verdict {
  margin: var(--space-4) 0;
  font-size: var(--text-sub);
  font-weight: 500;
}

.verdict--ok {
  color: var(--success-fg);
}

.verdict--bad {
  color: var(--danger-fg);
}

.verdict--warn {
  color: var(--warning-fg);
}

.samples {
  margin-top: var(--space-4);
}

.samples__title {
  margin: 0 0 var(--space-2);
  font-size: var(--text-foot);
  color: var(--text-2);
}

.samples__ids {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1);
}

.samples__id {
  padding: 2px var(--space-2);
  border-radius: var(--radius-sm);
  background: var(--neutral-bg);
  font-family: var(--font-mono);
  font-size: var(--text-note);
  color: var(--neutral-fg);
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin-top: var(--space-5);
  padding-top: var(--space-4);
  border-top: 1px solid var(--hairline);
}

.actions__hint {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.skel {
  padding: var(--space-6);
}
</style>
