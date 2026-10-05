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
            <el-form-item label="轮播图">
              <ImageUploader v-model="m.images" multiple hint="可上传多张，按上传顺序展示" />
            </el-form-item>
          </div>
        </el-form>

        <h3 class="sec">规格项</h3>
        <p class="sec__hint">每个规格项至少填一个值，例如颜色：红、蓝；尺码：S、M。SKU 会按组合自动生成。</p>
        <div v-for="(sp, si) in specs" :key="si" class="spec">
          <div class="spec__head">
            <el-input v-model="sp.specName" class="spec__name" placeholder="规格项名，如 颜色" />
            <el-button text type="danger" @click="removeSpec(si)">删除</el-button>
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
            <el-input
              v-if="sp.adding"
              ref="valueInput"
              v-model="sp.draft"
              class="spec__add"
              placeholder="回车添加"
              @keyup.enter="addValue(si)"
              @blur="addValue(si)"
            />
            <el-button v-else text type="primary" @click="startAdd(si)">+ 添加值</el-button>
          </div>
        </div>
        <el-button v-if="specs.length < 5" text type="primary" @click="addSpec">+ 添加规格项</el-button>
        <p v-if="errors.specs" class="err">{{ errors.specs }}</p>

        <h3 class="sec">
          SKU（{{ skus.length }}）
          <span class="sec__hint">由规格项自动组合生成，改规格会重算</span>
        </h3>
        <el-table :data="skus" class="table">
          <el-table-column type="index" label="#" width="50" />
          <el-table-column v-for="(sp, si) in specs" :key="si" :label="sp.specName || '规格'" min-width="120">
            <template #default="{ row }">{{ row.specValues[si] }}</template>
          </el-table-column>
          <el-table-column label="SKU 编码" width="180">
            <template #default="{ row }">
              <el-input v-model="row.skuCode" size="small" placeholder="必填，唯一" />
            </template>
          </el-table-column>
          <el-table-column label="售价" width="130">
            <template #default="{ row }">
              <el-input v-model.number="row.price" type="number" size="small" />
            </template>
          </el-table-column>
          <el-table-column label="库存" width="110">
            <template #default="{ row }">
              <el-input v-model.number="row.stock" type="number" size="small" />
            </template>
          </el-table-column>
          <el-table-column label="SKU 图片" width="150">
            <template #default="{ row }">
              <ImageUploader v-model="row.image" hint="" />
            </template>
          </el-table-column>
          <el-table-column label="启用" width="90">
            <template #default="{ row }">
              <el-switch v-model="row.enabled" />
            </template>
          </el-table-column>
        </el-table>
        <p v-if="errors.skus" class="err">{{ errors.skus }}</p>

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

type Spec = { specName: string; specValues: string[]; adding?: boolean; draft?: string };
const specs = reactive<Spec[]>([{ specName: '颜色', specValues: [], adding: true, draft: '' }]);

type SkuRow = {
  specValues: string[];
  skuCode: string;
  price: number;
  stock: number;
  image: string;
  enabled: boolean;
};
const skus = ref<SkuRow[]>([]);

// 笛卡尔积：规格项组合出 SKU。规格是 0 项时给一行「无规格」，
// 否则规格全空的商品会一个 SKU 都没有，后端会直接拒掉。
function regenerate() {
  const active = specs.filter((s) => s.specName.trim() && s.specValues.length > 0);
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

function startAdd(si: number) {
  for (const s of specs) {
    s.adding = false;
    s.draft = '';
  }
  specs[si].adding = true;
  specs[si].draft = '';
}

function addValue(si: number) {
  const sp = specs[si];
  const v = (sp.draft || '').trim();
  sp.adding = false;
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

  const active = specs.filter((s) => s.specName.trim() && s.specValues.length > 0);
  if (!active.length) errors.specs = '至少要有一个规格项，且每个规格项至少有一个值';

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
      specs: specs
        .filter((s) => s.specName.trim() && s.specValues.length > 0)
        .map((s) => ({ specName: s.specName.trim(), specValues: s.specValues })),
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
  margin: var(--space-6) 0 var(--space-3);
  font-size: var(--text-title-3);
  font-weight: 600;
}

.sec:first-of-type {
  margin-top: 0;
}

.sec__hint {
  margin-left: var(--space-2);
  font-size: var(--text-foot);
  font-weight: 400;
  color: var(--text-2);
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

.control {
  width: 100%;
}

.spec {
  padding: var(--space-3) 0;
  border-bottom: 1px solid var(--hairline);
}

.spec__head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.spec__name {
  max-width: 260px;
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

.spec__add {
  width: 160px;
}

.table {
  width: 100%;
}

.err {
  margin: var(--space-2) 0 0;
  font-size: var(--text-foot);
  color: var(--danger-fg);
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
