<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ title }}</h2>
        <p class="head__desc">从左侧拖组件到手机预览里，可拖动排序</p>
      </div>
      <div class="head__ops">
        <el-select
          v-if="!isMerchant"
          v-model="platformId"
          class="head__page"
          placeholder="选择平台"
          filterable
          :loading="platformsLoading"
          @change="load"
        >
          <el-option v-for="p in platforms" :key="p.value" :label="p.label" :value="p.value" />
        </el-select>
        <el-select v-model="page" class="head__page" @change="load">
          <el-option value="index" label="首页" />
          <el-option value="profile" label="我的" />
        </el-select>
        <el-button :loading="saving" @click="saveDraft">存草稿</el-button>
        <el-button type="primary" :loading="publishing" @click="publish">发布</el-button>
      </div>
    </div>

    <p v-if="warnings.length" class="warn">
      <span v-for="w in warnings" :key="w" class="warn__item">{{ w }}</span>
    </p>

    <div class="workbench">
      <aside class="palette">
        <p class="palette__title">组件库</p>
        <template v-for="cat in grouped" :key="cat.name">
          <p class="palette__cat">{{ cat.name }}</p>
          <div
            v-for="c in cat.items"
            :key="c.type"
            class="palette__item"
            draggable="true"
            @dragstart="onDragStart(c)"
          >
            {{ c.name }}
          </div>
        </template>
      </aside>

      <section class="stage">
        <div
          class="phone"
          :class="{ 'phone--over': dragOver }"
          @dragover.prevent="dragOver = true"
          @dragleave="dragOver = false"
          @drop.prevent="onDrop"
        >
          <div class="phone__bar">{{ page === 'index' ? '首页' : '我的' }}</div>
          <div class="phone__body">
            <div
              v-for="(c, i) in components"
              :key="c.id"
              class="comp"
              :class="{ 'comp--dragging': dragIndex === i }"
              draggable="true"
              @dragstart="dragIndex = i"
              @dragover.prevent
              @drop.prevent.stop="onReorder(i)"
            >
              <div class="comp__head">
                <span class="comp__name">{{ nameOf(c.type) }}</span>
                <span class="comp__ops">
                  <el-button text @click="move(i, -1)">上移</el-button>
                  <el-button text @click="move(i, 1)">下移</el-button>
                  <el-button text type="danger" @click="remove(i)">删除</el-button>
                </span>
              </div>
              <div class="comp__body">{{ c.type }}</div>
            </div>
            <p v-if="!components.length" class="phone__empty">把左侧组件拖到这里</p>
          </div>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';

const props = defineProps<{ merchantId?: string }>();
const route = useRoute();

const isMerchant = computed(() => !!props.merchantId);
const merchantId = computed(() => Number(props.merchantId || route.params.id || 0));
const title = computed(() => (isMerchant.value ? '商户店铺装修' : '平台装修'));

const page = ref('index');
const platformId = ref(0);
const platforms = ref<any[]>([]);
const platformsLoading = ref(false);
const saving = ref(false);
const publishing = ref(false);
const loading = ref(false);
const dragOver = ref(false);
const dragIndex = ref(-1);
const dragging = ref<any>(null);
const library = ref<any[]>([]);
const warnings = ref<string[]>([]);
const components = ref<any[]>([]);

const grouped = computed(() => {
  const map = new Map<string, any[]>();
  for (const c of library.value) {
    const key = c.category || '其他';
    if (!map.has(key)) map.set(key, []);
    map.get(key)!.push(c);
  }
  return [...map.entries()].map(([name, items]) => ({ name, items }));
});

const nameOf = (type: string) => library.value.find((c) => c.type === type)?.name || type;

let seq = 0;
const nextId = () => `c${Date.now().toString(36)}${(seq += 1)}`;

function onDragStart(c: any) {
  dragging.value = c;
}

function onDrop() {
  dragOver.value = false;
  const c = dragging.value;
  if (!c) return;
  components.value.push({ id: nextId(), type: c.type, span: 12, height: 80, props: {} });
  dragging.value = null;
}

function onReorder(target: number) {
  const from = dragIndex.value;
  dragIndex.value = -1;
  if (from < 0 || from === target) return;
  const list = [...components.value];
  const [item] = list.splice(from, 1);
  list.splice(target, 0, item);
  components.value = list;
}

function move(i: number, delta: number) {
  const to = i + delta;
  if (to < 0 || to >= components.value.length) return;
  onReorderTo(i, to);
}

function onReorderTo(from: number, to: number) {
  const list = [...components.value];
  const [item] = list.splice(from, 1);
  list.splice(to, 0, item);
  components.value = list;
}

function remove(i: number) {
  components.value.splice(i, 1);
}

function buildConfig(): string {
  // 只提交当前编辑的这一页，后端会把其它页原样保留 ——
  // 整份提交的话，切到「我的」页存草稿会把「首页」的排版覆盖掉。
  return JSON.stringify({ pages: { [page.value]: { components: components.value } } });
}

function parseConfig(json?: string): any {
  if (!json || !json.trim()) return {};
  try {
    return JSON.parse(json);
  } catch {
    // 配置坏掉时不能让页面整块崩掉：按空配置继续编辑，
    // 否则运营看不到自己已有的排版，只能找开发。
    ElMessage.warning('装修配置不是合法 JSON，已按空配置显示');
    return {};
  }
}

async function load() {
  loading.value = true;
  try {
    const defs = await request(
      `/gateway/design/Components?forMerchant=${isMerchant.value}&page=${page.value}`,
      { method: 'GET', silent: true },
    );
    library.value = Array.isArray(defs) ? defs : [];

    // 平台装修是**每平台一份**（design/Platform 要 platformId），所以必须先有平台。
    if (!isMerchant.value) {
      await loadPlatforms();
      if (!platformId.value) {
        components.value = [];
        return;
      }
    }

    const url = isMerchant.value
      ? `/gateway/design/Merchant?merchantId=${merchantId.value}`
      : `/gateway/design/Platform?platformId=${platformId.value}`;
    const res = await request(url, { method: 'GET', silent: true });

    warnings.value = res?.warnings || [];
    const cfg = parseConfig(res?.configJson);
    components.value = cfg?.pages?.[page.value]?.components || [];
  } catch {
    components.value = [];
  } finally {
    loading.value = false;
  }
}

async function loadPlatforms() {
  if (platforms.value.length) return;
  platformsLoading.value = true;
  try {
    const rows = await request('/gateway/platforms/Options', { method: 'GET', silent: true });
    platforms.value = (Array.isArray(rows) ? rows : []).map((r: any) => ({
      value: Number(r.id ?? r.Id ?? 0),
      label: String(r.name ?? r.platformName ?? ''),
    }));
    if (platforms.value.length) platformId.value = platforms.value[0].value;
  } catch {
    platforms.value = [];
  } finally {
    platformsLoading.value = false;
  }
}

async function saveDraft(silent = false) {
  saving.value = true;
  try {
    const path = isMerchant.value
      ? '/gateway/design/SaveMerchantDraft'
      : '/gateway/design/SavePlatformDraft';
    const body = isMerchant.value
      ? { merchantId: merchantId.value, configJson: buildConfig() }
      : { platformId: platformId.value, configJson: buildConfig() };
    const res = await request(path, { body });
    warnings.value = res?.warnings || warnings.value;
    if (!silent) ElMessage.success('草稿已保存，发布后才对用户生效');
  } catch {
    // request 已弹提示
  } finally {
    saving.value = false;
  }
}

async function publish() {
  // 发布转的是**已存的草稿**，命令里不带配置内容 —— 所以没存草稿就发布等于发布上一版，
  // 运营会以为改上去了。这里先强制存一次草稿。
  if (!components.value.length) {
    ElMessage.warning('页面是空的，先添加组件再发布');
    return;
  }

  publishing.value = true;
  try {
    // silent：发布流程里的那次存草稿不该再弹一次「草稿已保存」，
    // 否则用户会以为是两次独立操作。
    await saveDraft(true);
    const path = isMerchant.value ? '/gateway/design/PublishMerchant' : '/gateway/design/PublishPlatform';
    const body = isMerchant.value
      ? { merchantId: merchantId.value }
      : { platformId: platformId.value };
    await request(path, { body });
    ElMessage.success('已发布');
  } catch {
    // request 已弹提示
  } finally {
    publishing.value = false;
  }
}

onMounted(load);
</script>

<style scoped>
.head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--space-4);
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

.head__ops {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.head__page {
  width: 120px;
}

.warn {
  margin: 0 0 var(--space-4);
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  background: var(--warning-bg);
  color: var(--warning-fg);
  font-size: var(--text-foot);
}

.warn__item {
  display: block;
}

.workbench {
  display: grid;
  grid-template-columns: 200px minmax(0, 1fr);
  gap: var(--space-5);
  align-items: start;
}

@media (max-width: 900px) {
  .workbench {
    grid-template-columns: minmax(0, 1fr);
  }
}

.palette {
  padding: var(--space-4);
  border-radius: var(--radius-card);
  background: var(--bg-card);
  box-shadow: var(--shadow-card);
}

.palette__title {
  margin: 0 0 var(--space-3);
  font-size: var(--text-sub);
  font-weight: 600;
}

.palette__cat {
  margin: var(--space-3) 0 var(--space-1);
  font-size: var(--text-note);
  color: var(--text-3);
}

.palette__item {
  padding: var(--space-2) var(--space-3);
  margin-bottom: var(--space-1);
  border-radius: var(--radius-sm);
  background: var(--bg-page);
  font-size: var(--text-sub);
  cursor: grab;
  user-select: none;
}

.palette__item:hover {
  background: var(--neutral-bg);
}

.stage {
  display: flex;
  justify-content: center;
}

/* 手机外框：宽度与真实小程序同量级，让人对成品高度有直觉 */
.phone {
  width: 320px;
  border-radius: 28px;
  border: 1px solid var(--divider-soft);
  background: var(--bg-card);
  overflow: hidden;
  transition: border-color var(--duration-fast) var(--ease);
}

.phone--over {
  border-color: var(--brand);
}

.phone__bar {
  padding: var(--space-2);
  text-align: center;
  font-size: var(--text-foot);
  color: var(--text-2);
  background: var(--bg-page);
}

.phone__body {
  min-height: 420px;
  max-height: 70vh;
  overflow-y: auto;
  padding: var(--space-2);
}

.phone__empty {
  padding: var(--space-8) 0;
  text-align: center;
  font-size: var(--text-foot);
  color: var(--text-3);
}

.comp {
  padding: var(--space-2);
  margin-bottom: var(--space-2);
  border: 1px solid var(--hairline);
  border-radius: var(--radius-sm);
  background: var(--bg-card);
  cursor: grab;
}

.comp--dragging {
  opacity: 0.5;
}

.comp__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
}

.comp__name {
  font-size: var(--text-foot);
  font-weight: 600;
}

.comp__body {
  margin-top: var(--space-1);
  font-family: var(--font-mono);
  font-size: var(--text-note);
  color: var(--text-3);
}
</style>
