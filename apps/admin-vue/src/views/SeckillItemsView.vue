<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">场次商品</h2>
        <p class="head__desc">添加秒杀商品。场次发布后不能再改</p>
      </div>
      <el-button @click="back">返回场次列表</el-button>
    </div>

    <section class="panel">
      <el-form label-position="top" class="form">
        <div class="form__grid">
          <el-form-item label="SKU Id">
            <el-input v-model="form.skuId" placeholder="商品 SKU 的 Id" />
          </el-form-item>
          <el-form-item label="秒杀价" :error="errors.price">
            <el-input v-model.number="form.seckillPrice" type="number" placeholder="两位小数" />
          </el-form-item>
          <el-form-item label="秒杀库存" :error="errors.stock">
            <el-input v-model.number="form.seckillStock" type="number" placeholder="该场次单独划出的库存" />
          </el-form-item>
          <el-form-item label="每人限购">
            <el-input v-model.number="form.perUserLimit" type="number" />
          </el-form-item>
          <el-form-item label="排序">
            <el-input v-model.number="form.sortOrder" type="number" />
          </el-form-item>
          <el-form-item label=" ">
            <el-button type="primary" :loading="adding" @click="add">添加</el-button>
          </el-form-item>
        </div>
      </el-form>
    </section>

    <section class="panel">
      <el-table v-if="rows.length" :data="rows" class="table">
        <el-table-column prop="productName" label="商品" min-width="180" />
        <el-table-column prop="skuSpecText" label="规格" min-width="130" />
        <el-table-column label="秒杀价" width="110">
          <template #default="{ row }"><span class="num">{{ amount(row.seckillPrice) }}</span></template>
        </el-table-column>
        <el-table-column label="秒杀库存" width="100">
          <template #default="{ row }"><span class="num">{{ count(row.seckillStock) }}</span></template>
        </el-table-column>
        <el-table-column label="已售" width="90">
          <template #default="{ row }"><span class="num">{{ count(row.soldCount) }}</span></template>
        </el-table-column>
        <el-table-column label="剩余" width="90">
          <template #default="{ row }"><span class="num">{{ count(row.remaining) }}</span></template>
        </el-table-column>
        <el-table-column label="操作" width="100" align="center">
          <template #default="{ row }">
            <el-button text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <p v-else class="empty">这个场次还没有添加商品</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import request from '@/api/request';
import { formatAmount, formatCount } from '@/utils/format';

const route = useRoute();
const router = useRouter();

const sessionId = computed(() => Number(route.params.id || 0));
const rows = ref<any[]>([]);
const adding = ref(false);
const errors = reactive<Record<string, string>>({});

const form = reactive({
  skuId: '',
  seckillPrice: 0,
  seckillStock: 0,
  perUserLimit: 1,
  sortOrder: 0,
});

const amount = (v: unknown) => formatAmount(v);
const count = (v: unknown) => formatCount(v);

async function load() {
  try {
    const res = await request('/gateway/marketing/seckill/sessions/Items/List', {
      body: { sessionId: sessionId.value },
      silent: true,
    });
    rows.value = Array.isArray(res) ? res : (res?.items || []);
  } catch {
    rows.value = [];
  }
}

async function add() {
  for (const k of Object.keys(errors)) delete errors[k];

  const skuId = Number(form.skuId);
  if (!skuId || skuId <= 0) errors.sku = '请填写有效的 SKU Id';
  if (!(Number(form.seckillPrice) > 0)) errors.price = '秒杀价必须大于 0';
  if (!(Number(form.seckillStock) > 0)) errors.stock = '秒杀库存必须大于 0';
  if (Object.keys(errors).length) {
    ElMessage.warning('请先修正标红的字段');
    return;
  }

  adding.value = true;
  try {
    await request('/gateway/marketing/seckill/sessions/Items/Add', {
      body: {
        sessionId: sessionId.value,
        skuId,
        seckillPrice: Number(form.seckillPrice),
        seckillStock: Number(form.seckillStock),
        perUserLimit: Number(form.perUserLimit) || 1,
        sortOrder: Number(form.sortOrder) || 0,
      },
    });
    ElMessage.success('已添加');
    form.skuId = '';
    await load();
  } catch {
    // request 已弹提示
  } finally {
    adding.value = false;
  }
}

async function remove(row: any) {
  try {
    await ElMessageBox.confirm(
      `确定把「${row.productName || ''}」从本场次移除？`,
      '删除场次商品',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    );
  } catch {
    return;
  }

  try {
    await request('/gateway/marketing/seckill/sessions/Items/Delete', {
      body: { itemId: row.itemId ?? row.id },
    });
    ElMessage.success('已删除');
    await load();
  } catch {
    // request 已弹提示
  }
}

function back() {
  router.push('/seckill');
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
  margin-bottom: var(--space-4);
}

.form__grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  column-gap: var(--space-5);
  row-gap: var(--space-1);
}

@media (max-width: 1100px) {
  .form__grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.table {
  width: 100%;
}

.empty {
  margin: 0;
  font-size: var(--text-foot);
  color: var(--text-3);
}
</style>
