<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ title }}</h2>
        <p class="head__desc">规格与 SKU 由规格项自动生成：改规格项会重算 SKU 组合</p>
      </div>
    </div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 4" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <template v-else>
        <h3 class="sec">基本信息</h3>
        <el-form label-position="top" class="form">
          <div class="grid">
            <el-form-item label="商品名" :error="errors.spuName">
              <el-input v-model="m.spuName" placeholder="2-128 个字符" />
            </el-form-item>
            <el-form-item label="副标题">
              <el-input v-model="m.subTitle" placeholder="选填" />
            </el-form-item>
            <el-form-item label="商品分类" :error="errors.categoryId">
              <el-cascader
                v-model="categoryPath"
                :options="categoryTree"
                :props="{ value: 'id', label: 'name', children: 'children', checkStrictly: true, emitPath: true }"
                class="control"
                placeholder="只能选第 3 级（叶子）分类"
                clearable
              />
            </el-form-item>
            <el-form-item label="品牌">
              <el-select v-model="m.brandId" class="control" clearable placeholder="选填">
                <el-option v-for="b in brands" :key="b.value" :label="b.label" :value="b.value" />
              </el-select>
            </el-form-item>
            <el-form-item label="配送方式" :error="errors.deliveryType">
              <el-select v-model="m.deliveryType" class="control">
                <el-option :value="1" label="实物快递" />
                <el-option :value="2" label="虚拟商品" />
                <el-option :value="3" label="实物自提" />
              </el-select>
            </el-form-item>
            <el-form-item label="划线原价" :error="errors.originalPrice">
              <el-input v-model.number="m.originalPrice" type="number" placeholder="0 表示不展示" />
            </el-form-item>
            <el-form-item label="商品主图" :error="errors.mainImage">
              <ImageUploader v-model="m.mainImage" hint="建议 1:1，支持 JPG / PNG / WebP" />
            </el-form-item>
            <el-form-item label="轮播图" class="form__item--wide">
              <!-- 最多 8 张、可拖动排序：轮播图的**顺序就是展示顺序**，
                   不给出排序入口的话运营只能靠反复删了重传来调位置。 -->
              <ImageUploader
                v-model="m.images"
                multiple
                :max="8"
                show-index
                sortable
                hint="最多 8 张，按住图块可拖动调整顺序，序号即展示顺序"
              />
            </el-form-item>
          </div>
        </el-form>

        <!--
          规格区拆成「规格项」与「SKU」两段，而不是揉成一坨。
          揉在一起时运营分不清「我改的是规格还是 SKU」——
          改一个规格值会重算整张 SKU 表，看着像把已录的价格弄丢了。
          两段分开的直接好处是：上面那段管「有哪些规格」，下面那段管「每个组合卖多少钱」。
        -->
        <section class="sec">
          <header class="sec__head">
            <h3 class="sec__title">规格项</h3>
            <span class="sec__count">{{ activeSpecs.length }} 项 · {{ comboCount }} 个组合</span>
          </header>
          <p class="sec__hint">
            每个规格项至少填一个值，例如「颜色：黑色、白色」「尺码：S、M」。
            SKU 会按所有规格项的<strong>组合</strong>自动生成：2 个颜色 × 2 个尺码 = 4 个 SKU。
          </p>

          <div v-for="(sp, si) in specs" :key="si" class="spec" :class="{ 'spec--empty': !sp.specName.trim() }">
            <div class="spec__row">
              <span class="spec__label">规格名</span>
              <el-input
                v-model="sp.specName"
                class="spec__name"
                placeholder="如 颜色"
                maxlength="16"
              />
              <span class="spec__label">可选值</span>
              <!--
                值输入框**常驻**，不再要点「+ 添加值」才出现。
                之前那个隐藏的输入框是最容易被卡住的地方：
                用户点了「添加值」但没看到输入框，以为按钮坏了。
              -->
              <el-input
                v-model="sp.draft"
                class="spec__draft"
                placeholder="输入后按回车添加"
                maxlength="32"
                @keyup.enter="addValue(si)"
              />
              <el-button
                v-if="specs.length > 1"
                type="danger"
                link
                @click="removeSpec(si)"
              >
                删除
              </el-button>
            </div>

            <div class="spec__values">
              <el-tag
                v-for="(v, vi) in sp.specValues"
                :key="vi"
                closable
                class="spec__tag"
                @close="removeValue(si, vi)"
              >
                {{ v }}
              </el-tag>
              <span v-if="!sp.specValues.length" class="spec__empty-hint">
                还没有值 —— 留空表示这个商品没有该规格
              </span>
            </div>
          </div>

          <el-button v-if="specs.length < 5" text type="primary" @click="addSpec">
            + 添加规格项
          </el-button>
          <p class="sec__note">最多 5 个规格项。没有多规格的商品可以删到只剩一个再清空它的值。</p>
          <p v-if="errors.specs" class="err">{{ errors.specs }}</p>
        </section>

        <section class="sec">
          <header class="sec__head">
            <h3 class="sec__title">SKU 清单</h3>
            <div class="sec__tools">
              <el-button size="small" @click="bulkEditVisible = true">批量设置</el-button>
            </div>
          </header>
          <p class="sec__hint">
            共 <strong>{{ skus.length }}</strong> 个 SKU，由上面的规格组合自动生成。
            修改规格值后组合会重算，<strong>已有组合的价格、库存、编码与图片会原样保留</strong>。
          </p>

          <el-table :data="skus" class="table">
          <el-table-column type="index" label="#" width="50" />
          <el-table-column v-for="(sp, si) in specs" :key="si" :label="sp.specName || '规格'" min-width="120">
            <template #default="{ row }">{{ row.specValues[si] }}</template>
          </el-table-column>
          <el-table-column label="SKU 编码" width="190">
            <template #header>
              <span>SKU 编码 <em class="req">必填</em></span>
            </template>
            <template #default="{ row }">
              <el-input v-model="row.skuCode" size="small" placeholder="唯一，如 K001" />
            </template>
          </el-table-column>
          <el-table-column label="售价" width="140">
            <template #header>
              <span>售价 <em class="req">必填</em></span>
            </template>
            <template #default="{ row }">
              <el-input v-model.number="row.price" type="number" size="small" placeholder="0.00" />
            </template>
          </el-table-column>
          <el-table-column label="库存" width="110">
            <template #default="{ row }">
              <el-input v-model.number="row.stock" type="number" size="small" />
            </template>
          </el-table-column>
          <el-table-column label="SKU 图片" width="150">
            <template #default="{ row }">
              <!-- compact：SKU 表格每行都有一张图，用默认尺寸会把价格挤到要横向滚动 -->
              <ImageUploader v-model="row.image" size="compact" hint="" />
            </template>
          </el-table-column>
          <el-table-column label="启用" width="90">
            <template #default="{ row }">
              <el-switch v-model="row.enabled" />
            </template>
          </el-table-column>
          </el-table>
          <p v-if="errors.skus" class="err">{{ errors.skus }}</p>
        </section>

        <!--
          批量设置：4 个规格组合出来的 16 个 SKU 一个个填价格，
          是最容易被抱怨「录不进去」的场景。这里一次填好套用到全部 SKU，
          需要区分价位的再单独改那几行。
        -->
        <el-dialog v-model="bulkEditVisible" title="批量设置 SKU" width="440" align-center>
          <el-form label-position="top">
            <el-form-item label="售价（留空则不改）">
              <el-input v-model="bulk.price" type="number" placeholder="0.00" />
            </el-form-item>
            <el-form-item label="库存（留空则不改）">
              <el-input v-model="bulk.stock" type="number" placeholder="0" />
            </el-form-item>
          </el-form>
          <p class="bulk__note">
            将套用到当前全部 {{ skus.length }} 个 SKU。已填好的值会被覆盖。
          </p>
          <template #footer>
            <el-button @click="bulkEditVisible = false">取消</el-button>
            <el-button type="primary" @click="applyBulk">应用</el-button>
          </template>
        </el-dialog>

        <h3 class="sec">商品描述</h3>
        <RichTextEditor v-model="m.description" placeholder="填写商品卖点、材质、使用说明等内容" />

        <div class="actions">
          <el-button @click="cancel">取消</el-button>
          <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
        </div>
      </template>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import ImageUploader from '@/components/ImageUploader.vue';
import RichTextEditor from '@/components/RichTextEditor.vue';

const route = useRoute();
const router = useRouter();

const loading = ref(true);
const saving = ref(false);
const errors = reactive<Record<string, string>>({});

const entityId = computed(() => Number(route.params.id || 0));
const isEdit = computed(() => entityId.value > 0);
const title = computed(() => (isEdit.value ? '编辑商品' : '新建商品'));

const m = reactive<any>({
  spuName: '',
  subTitle: '',
  categoryId: 0,
  brandId: 0,
  deliveryType: 1,
  originalPrice: 0,
  mainImage: '',
  images: [] as string[],
  description: '',
});

const categoryPath = ref<any[]>([]);
const categoryTree = ref<any[]>([]);
const brands = ref<any[]>([]);

type Spec = { specName: string; specValues: string[]; draft?: string };
const specs = reactive<Spec[]>([{ specName: '颜色', specValues: [], draft: '' }]);

type SkuRow = {
  specValues: string[];
  skuCode: string;
  price: number;
  stock: number;
  image: string;
  enabled: boolean;
};
const skus = ref<SkuRow[]>([]);

// 批量设置的临时值。留空表示「这一项不改」，而不是「改成 0」——
// 只想统一改库存时把售价填 0 是最常见的误操作。
const bulkEditVisible = ref(false);
const bulk = reactive({ price: '', stock: '' });

function applyBulk() {
  const price = Number(bulk.price);
  const stock = Number(bulk.stock);
  const hasPrice = bulk.price !== '' && Number.isFinite(price);
  const hasStock = bulk.stock !== '' && Number.isFinite(stock);

  if (!hasPrice && !hasStock) {
    ElMessage.warning('至少填写售价或库存其中一项');
    return;
  }

  for (const row of skus.value) {
    if (hasPrice) row.price = price;
    if (hasStock) row.stock = stock;
  }
  bulkEditVisible.value = false;
  ElMessage.success(`已应用到 ${skus.value.length} 个 SKU`);
}

// 真正参与组合的规格项：有名有值才算。
// 「填了名字但一个值都没填」的那一项**不参与**笛卡尔积，
// 否则会生成一堆规格文本为空的 SKU，用户看到的是「黑色 / （空）」这种行。
const activeSpecs = computed(() =>
  specs.filter((s) => s.specName.trim() && s.specValues.length > 0),
);

// 组合数：显示在标题旁边，运营加规格值之前就能看到 SKU 会变成多少个。
// 之前只有生成之后才知道结果，于是「不小心加了第 5 个值」已经来不及收回。
const comboCount = computed(() =>
  activeSpecs.value.reduce((count, sp) => count * sp.specValues.length, 1),
);

// 笛卡尔积：规格项组合出 SKU。规格是 0 项时给一行「无规格」，
// 否则规格全空的商品会一个 SKU 都没有，后端会直接拒掉。
function regenerate() {
  const active = activeSpecs.value;
  if (!active.length) {
    skus.value = [{ specValues: [], skuCode: '', price: 0, stock: 0, image: '', enabled: true }];
    return;
  }

  // 保留旧组合上的价格与库存：运营改了一个规格值，
  // 其余 SKU 的价格不该被清零（那等于要重新录一遍）。
  const previous = new Map<string, SkuRow>();
  for (const row of skus.value) previous.set(row.specValues.join('|'), row);

  let combos: string[][] = [[]];
  for (const sp of active) {
    const next: string[][] = [];
    for (const combo of combos) for (const v of sp.specValues) next.push([...combo, v]);
    combos = next;
  }

  skus.value = combos.map((values) => {
    const old = previous.get(values.join('|'));
    return {
      specValues: values,
      skuCode: old?.skuCode ?? '',
      price: old?.price ?? 0,
      stock: old?.stock ?? 0,
      image: old?.image ?? '',
      enabled: old?.enabled ?? true,
    };
  });
}

function addValue(si: number) {
  const sp = specs[si];
  const v = (sp.draft || '').trim();
  sp.draft = '';
  if (!v) return;
  if (sp.specValues.includes(v)) {
    ElMessage.warning(`「${v}」已在该规格项下`);
    return;
  }
  sp.specValues.push(v);
  regenerate();
}

function removeValue(si: number, vi: number) {
  specs[si].specValues.splice(vi, 1);
  regenerate();
}

function addSpec() {
  if (specs.length >= 5) return;
  specs.push({ specName: '', specValues: [] });
}

function removeSpec(si: number) {
  if (specs.length <= 1) return;
  specs.splice(si, 1);
  regenerate();
}

function flatten(node: any, path: number[] = []): any[] {
  const out: any[] = [];
  for (const c of node || []) {
    const p = [...path, c.id];
    if (c.children && c.children.length) out.push(...flatten(c.children, p));
    else out.push({ id: c.id, name: c.name, path: p });
  }
  return out;
}

function parseImages(value: unknown): string[] {
  if (Array.isArray(value)) return value.map(String).filter(Boolean);
  if (typeof value !== 'string' || !value.trim()) return [];
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.map(String).filter(Boolean) : [];
  } catch {
    return [];
  }
}

async function load() {
  loading.value = true;
  try {
    const [tree, brandRows] = await Promise.all([
      request('/gateway/categories/Tree', { method: 'GET', silent: true }),
      request('/gateway/brands/List', { method: 'GET', silent: true }),
    ]);
    const mapTree = (nodes: any[]): any[] => (nodes || []).map((node) => ({
      id: node.id,
      name: node.categoryName || node.name,
      children: mapTree(node.children || []),
    }));
    categoryTree.value = mapTree(Array.isArray(tree) ? tree : (tree?.items || []));
    brands.value = (Array.isArray(brandRows) ? brandRows : (brandRows?.items || []))
      .map((b: any) => ({ value: Number(b.id ?? b.Id), label: b.brandName ?? b.BrandName ?? '' }));

    if (!isEdit.value) {
      regenerate();
      return;
    }

    const d = await request(`/gateway/products/Detail?productId=${entityId.value}`, {
      method: 'GET',
      silent: true,
    });
    Object.assign(m, {
      spuName: d.spuName ?? '',
      subTitle: d.subTitle ?? '',
      categoryId: Number(d.categoryId ?? 0),
      brandId: Number(d.brandId ?? 0),
      deliveryType: Number(d.deliveryType ?? 1),
      originalPrice: Number(d.originalPrice ?? 0),
      mainImage: d.mainImage ?? '',
      images: parseImages(d.images),
      description: d.description ?? '',
    });

    specs.splice(0, specs.length);
    const incoming = (d.specs || []).map((s: any) => ({
      specName: s.specName ?? '',
      specValues: (s.values || s.specValues || []).map((v: any) => (typeof v === 'string' ? v : (v.value ?? v.name ?? ''))),
    }));
    specs.push(...(incoming.length ? incoming : [{ specName: '颜色', specValues: [] }]));

    const rows = (d.skus || []).map((s: any) => ({
      specValues: s.specValues || (s.specValueTexts || []),
      skuCode: s.skuCode ?? '',
      price: Number(s.price ?? 0),
      stock: Number(s.stock ?? 0),
      image: s.image ?? '',
      enabled: Number(s.status ?? 1) === 1,
    }));
    if (rows.length) skus.value = rows;
    else regenerate();
  } catch {
    // request 已弹提示
  } finally {
    loading.value = false;
  }
}

function validate() {
  for (const k of Object.keys(errors)) delete errors[k];

  if (!m.spuName || m.spuName.trim().length < 2) errors.spuName = '商品名至少 2 个字符';
  if (!m.categoryId) errors.categoryId = '请选择商品分类';
  if (!m.mainImage || !m.mainImage.trim()) errors.mainImage = '请填写主图地址';

  // 复用 activeSpecs：这里再写一遍筛选条件的话，两处很容易改得不一致，
  // 症状是「明明填了规格值却提示至少要有一个规格项」
  if (!activeSpecs.value.length) errors.specs = '至少要有一个规格项，且每个规格项至少有一个值';

  if (!skus.value.length) errors.skus = '至少要有一个 SKU';
  for (const row of skus.value) {
    if (!row.skuCode?.trim()) {
      errors.skus = '每个 SKU 都必须填写 SKU 编码';
      break;
    }
    if (!(Number(row.price) > 0)) {
      errors.skus = '每个 SKU 的售价都必须大于 0';
      break;
    }
  }

  return Object.keys(errors).length === 0;
}

async function submit() {
  if (!validate()) {
    ElMessage.warning('请先修正标红的字段');
    document.querySelector('.el-form-item.is-error')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    return;
  }

  saving.value = true;
  try {
    const body: Record<string, any> = {
      spuName: m.spuName.trim(),
      subTitle: m.subTitle || '',
      categoryId: m.categoryId,
      brandId: m.brandId || 0,
      deliveryType: m.deliveryType,
      mainImage: m.mainImage.trim(),
      originalPrice: m.originalPrice || 0,
      status: 2,
      description: m.description || '',
      images: JSON.stringify(m.images || []),
      // 同样复用 activeSpecs：提交与校验必须用**同一套**筛选，
      // 否则会出现「校验说有规格、提交却没带上规格」
      specs: activeSpecs.value.map((s) => ({
        specName: s.specName.trim(),
        specValues: s.specValues,
      })),
      skus: skus.value.map((row, i) => ({
        skuCode: row.skuCode.trim(),
        specValues: row.specValues,
        price: Number(row.price) || 0,
        stock: Number(row.stock) || 0,
        image: row.image || '',
        status: row.enabled ? 1 : 2,
      })),
    };
    if (isEdit.value) body.productId = entityId.value;

    await request(
      isEdit.value ? '/gateway/products/Save' : '/gateway/products/Create',
      { body },
    );
    ElMessage.success(isEdit.value ? '已保存' : '创建成功');
    router.push('/products');
  } catch {
    // request 已弹提示，保留表单
  } finally {
    saving.value = false;
  }
}

function cancel() {
  router.push('/products');
}

watch(categoryPath, (p) => {
  // 只认选到叶子（第 3 级）的分类：后端会拒绝挂到非叶子分类下，
  // 与其让用户提交后才报错，不如在选择时就拦。
  m.categoryId = p && p.length ? Number(p[p.length - 1]) : 0;
});

onMounted(load);
</script>

<style scoped>
.head {
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

.sec {
  margin-top: var(--space-7);
  padding-top: var(--space-5);
  border-top: 0.5px solid var(--hairline);
}

.sec:first-of-type {
  margin-top: 0;
  padding-top: 0;
  border-top: none;
}

/* 段标题：标题 + 右侧的计数 / 操作，各占一端。
   计数放右边是因为它回答的是「接下来会发生什么」，
   紧跟在标题后面会被读成标题的一部分。 */
.sec__head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-3);
  margin-bottom: var(--space-2);
}

.sec__title {
  margin: 0;
  font-size: var(--text-title-3);
  line-height: var(--lh-title-3);
  font-weight: 600;
}

.sec__count {
  font-size: var(--text-foot);
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

.sec__hint {
  margin: 0 0 var(--space-3);
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-2);
}

.sec__hint strong {
  color: var(--text-1);
  font-weight: 600;
}

.sec__note {
  margin: var(--space-2) 0 0;
  font-size: var(--text-note);
  color: var(--text-3);
}

/* 表头里的必填标记：小字红标，不抢标题的视觉重量 */
.req {
  margin-left: var(--space-1);
  padding: 1px 4px;
  border-radius: var(--radius-sm);
  background: var(--danger-bg);
  color: var(--danger-fg);
  font-size: var(--text-note);
  font-style: normal;
  font-weight: 400;
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  column-gap: var(--space-5);
}

@media (max-width: 1100px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

/* 轮播图占满整行：一行只放得下两块瓦片，挤在半栏里换行很别扭 */
.form__item--wide {
  grid-column: 1 / -1;
}

.control {
  width: 100%;
}

.spec {
  padding: var(--space-3) var(--space-4);
  margin-bottom: var(--space-2);
  border: 0.5px solid var(--hairline);
  border-radius: var(--radius-md);
  background: var(--bg-page);
}

/* 规格名还没填时给一层淡提示，提醒「这一项还没生效」 */
.spec--empty {
  border-style: dashed;
  border-color: var(--divider-soft);
}

.spec__row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.spec__label {
  flex-shrink: 0;
  font-size: var(--text-foot);
  color: var(--text-2);
}

.spec__name {
  width: 140px;
}

.spec__draft {
  width: 200px;
}

.spec__values {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-2);
  margin-top: var(--space-2);
}

.spec__tag {
  margin: 0;
}

.spec__empty-hint {
  font-size: var(--text-note);
  color: var(--text-3);
}

.table {
  width: 100%;
}

.err {
  margin: var(--space-2) 0 0;
  font-size: var(--text-foot);
  color: var(--danger-fg);
}

.bulk__note {
  margin: 0;
  font-size: var(--text-foot);
  color: var(--text-2);
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-5);
  padding-top: var(--space-4);
  border-top: 1px solid var(--hairline);
}

.skel {
  padding: var(--space-6);
}
</style>
