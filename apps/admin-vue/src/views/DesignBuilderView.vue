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
        <!--
          商户装修**锁定在店铺页**（规格 16.3：商户装修只针对商户店铺页，
          与平台装修的首页 / 我的页互不干扰）。所以商户进来不显示这个下拉。
        -->
        <el-select v-if="!isMerchant" v-model="page" class="head__page" @change="load">
          <el-option value="index" label="首页" />
          <el-option value="profile" label="我的" />
        </el-select>
        <span v-else class="head__page head__page--fixed">店铺页</span>
        <!--
          必须写成 `saveDraft()` 而不是 `saveDraft`：直接绑函数名时 Vue 会把
          MouseEvent 当成第一个实参传进去，而 `saveDraft(silent = false)`
          只判真假 —— 事件对象恒为真，于是成功提示被当成「静默保存」吞掉，
          界面上点了「存草稿」什么反馈都没有（草稿其实已经存进去了）。
        -->
        <el-button :loading="saving" @click="saveDraft()">存草稿</el-button>
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

      <!--
        手机预览抽到 PhonePreview 组件：组件渲染规则与小程序端
        DesignComponent.vue 一一对应，写在一处才不会两边走偏。
      -->
      <section
        class="stage"
        @dragover.prevent="dragOver = true"
        @dragleave="dragOver = false"
        @drop.prevent="onDrop"
      >
        <PhonePreview
          :page="page"
          :components="components"
          :library="library"
          :selected-index="selectedIndex"
          @select="selectComponent"
          @reorder="onReorder"
          @move="move"
          @remove="remove"
        />
      </section>

      <!--
        右侧属性面板（DATA_SPEC 5.29「选中组件后编辑其私有配置」）。
        之前只有「组件库 + 手机预览」两栏：组件能拖进来、能排序、能删，
        但**改不了里面的字和图片** —— 用户明确要求「A3 可以改组件内的内容」
        「商户可以改自己店铺页的轮播图」，没有这一栏这两条就是空的。
        字段由后端下发的 props schema 驱动，前端不按组件类型写死分支。
      -->
      <aside class="props">
        <p class="props__title">组件属性</p>

        <p v-if="!selected" class="props__empty">
          在中间的手机预览里点一个组件，这里就能改它的内容。
        </p>

        <template v-else>
          <p class="props__name">{{ nameOf(selected.type) }}</p>

          <el-form label-position="top" class="props__form">
            <el-form-item label="宽度（栅格）">
              <el-select v-model="selected.span" class="props__control">
                <el-option v-for="s in SPANS" :key="s" :label="`${s} / 12`" :value="s" />
              </el-select>
            </el-form-item>
            <el-form-item label="高度（px，0 = 按组件默认）">
              <el-input v-model.number="selected.height" type="number" class="props__control" />
            </el-form-item>

            <el-form-item
              v-for="f in selectedProps"
              :key="f.key"
              :label="f.label"
            >
              <ImageUploader
                v-if="f.kind === 'image'"
                v-model="selected.props[f.key]"
                :hint="f.placeholder"
              />
              <ImageUploader
                v-else-if="f.kind === 'images'"
                v-model="selected.props[f.key]"
                multiple
                :max="f.max || 8"
                show-index
                sortable
                :hint="f.placeholder"
              />
              <el-input
                v-else-if="f.kind === 'textarea'"
                v-model="selected.props[f.key]"
                type="textarea"
                :rows="3"
                class="props__control"
                :placeholder="f.placeholder"
              />
              <el-input
                v-else-if="f.kind === 'number'"
                v-model.number="selected.props[f.key]"
                type="number"
                class="props__control"
                :placeholder="f.placeholder"
              />
              <el-input
                v-else
                v-model="selected.props[f.key]"
                class="props__control"
                :placeholder="f.placeholder"
              />
            </el-form-item>
          </el-form>

          <p v-if="!selectedProps.length" class="props__empty">
            这个组件没有可改的内容，只能调宽度、高度和顺序。
          </p>
        </template>
      </aside>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import PhonePreview from '@/components/PhonePreview.vue';
import ImageUploader from '@/components/ImageUploader.vue';

const props = defineProps<{ merchantId?: string }>();
const route = useRoute();

const isMerchant = computed(() => !!props.merchantId);
// 雪花 Id 全程按字符串传递；Number() 会丢末位精度。
const merchantId = computed(() => String(props.merchantId || route.params.id || ''));
const title = computed(() => (isMerchant.value ? '商户店铺装修' : '平台装修'));

// 商户装修只能建店铺页，平台装修默认首页
const page = ref(props.merchantId ? 'store' : 'index');
const platformId = ref('');
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

// 宽度只能是 12 栅格里的这几个值（与后端 DesignComponentRegistry.AllowedSpans 一致）。
// 前端给下拉而不是自由输入：填个 5 会被后端拒，运营还得回头看错误提示。
const SPANS = [1, 2, 3, 4, 6, 12];

/** 当前选中的组件下标；-1 = 没选中。 */
const selectedIndex = ref(-1);

const selected = computed(() =>
  selectedIndex.value >= 0 ? components.value[selectedIndex.value] : null);

/** 选中组件的可编辑属性 schema（来自后端组件库，按 type 取）。 */
const selectedProps = computed(() => {
  const type = selected.value?.type;
  if (!type) return [];
  return library.value.find((c: any) => c.type === type)?.props || [];
});

function selectComponent(i: number) {
  selectedIndex.value = i;
}

/** 选中项变化后把下标拉回合法范围，避免删除后属性面板指向不存在的组件。 */
function clampSelection() {
  if (selectedIndex.value >= components.value.length) {
    selectedIndex.value = components.value.length - 1;
  }
}

const grouped = computed(() => {
  const map = new Map<string, any[]>();
  for (const c of library.value) {
    const key = c.category || '其他';
    if (!map.has(key)) map.set(key, []);
    map.get(key)!.push(c);
  }
  return [...map.entries()].map(([name, items]) => ({ name, items }));
});

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
  // 拖进来就选中：运营的下一步一定是「把它的字和图片改成我要的」，
  // 让他在预览里再点一下是多余的一步。
  selectedIndex.value = components.value.length - 1;
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
  // 选中项跟着被拖动的那一个走。不跟的话属性面板会**悄悄切到邻居身上**：
  // 用户拖完组件接着改标题，改的却是另一个组件，而且界面上看不出任何异常。
  if (selectedIndex.value === from) selectedIndex.value = target;
}

function move(i: number, delta: number) {
  const to = i + delta;
  if (to < 0 || to >= components.value.length) return;
  onReorderTo(i, to);
  if (selectedIndex.value === i) selectedIndex.value = to;
}

function onReorderTo(from: number, to: number) {
  const list = [...components.value];
  const [item] = list.splice(from, 1);
  list.splice(to, 0, item);
  components.value = list;
}

function remove(i: number) {
  components.value.splice(i, 1);
  clampSelection();
}

/** 组件类型的中文名（组件库里有就显示中文，没有就退回 type）。 */
function nameOf(type: string) {
  return library.value.find((c: any) => c.type === type)?.name || type;
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
    const loaded = cfg?.pages?.[page.value]?.components || [];
    // props 缺失 / 不是对象时补空对象：属性面板直接 v-model 到 props[key]，
    // 没有这层兜底，一条历史配置就能让整个面板报错。
    components.value = loaded.map((c: any) => ({
      ...c,
      props: c?.props && typeof c.props === 'object' ? c.props : {},
    }));
    selectedIndex.value = -1;
  } catch {
    components.value = [];
    selectedIndex.value = -1;
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
      value: String(r.id ?? r.Id ?? ''),
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
  grid-template-columns: 200px minmax(0, 1fr) 300px;
  gap: var(--space-5);
  align-items: start;
}

@media (max-width: 1200px) {
  .workbench {
    grid-template-columns: 200px minmax(0, 1fr);
  }

  .props {
    grid-column: 1 / -1;
  }
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

/* 属性面板：与左侧组件库同一张卡片样式，保持「三栏一体」的观感 */
.props {
  padding: var(--space-4);
  border-radius: var(--radius-card);
  background: var(--bg-card);
  box-shadow: var(--shadow-card);
}

.props__title {
  margin: 0 0 var(--space-3);
  font-size: var(--text-sub);
  font-weight: 600;
}

.props__name {
  margin: 0 0 var(--space-3);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  background: var(--brand-soft, var(--neutral-bg));
  color: var(--brand);
  font-size: var(--text-note);
}

.props__empty {
  margin: 0;
  color: var(--text-3);
  font-size: var(--text-note);
  line-height: 1.6;
}

.props__control {
  width: 100%;
}

.props__form :deep(.el-form-item) {
  margin-bottom: var(--space-3);
}

/* 商户装修锁定在店铺页，这里用同样宽度的静态文字代替下拉，
   让「当前在搭哪一页」和平台装修的控件占位一致 */
.head__page--fixed {
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--text-2);
}

/* 手机预览居中：它是这一页的视觉主体，组件库在左边当「抽屉」用 */
.stage {
  display: flex;
  justify-content: center;
}

</style>
