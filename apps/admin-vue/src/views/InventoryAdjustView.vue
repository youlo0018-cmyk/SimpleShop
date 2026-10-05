<template>
  <div>
    <div class="page-header">
      <div>
        <h2 class="page-header__title">调整库存</h2>
        <p class="page-header__desc">{{ product.spuName || '商品库存按 SKU 维护' }}</p>
      </div>
      <el-button @click="router.push('/products')">返回商品列表</el-button>
    </div>

    <div v-if="loading" class="panel skel">
      <div v-for="i in 4" :key="i" class="skeleton-row" />
    </div>

    <div v-else class="panel table-panel">
      <el-table :data="rows">
        <el-table-column label="规格" min-width="180">
          <template #default="{ row }">{{ row.skuSpecText || row.skuName || '默认规格' }}</template>
        </el-table-column>
        <el-table-column label="当前可用" width="110" align="right">
          <template #default="{ row }">{{ row.available }}</template>
        </el-table-column>
        <el-table-column label="调整量" width="150">
          <template #default="{ row }">
            <el-input v-model.number="row.availableAdjust" type="number" placeholder="可正可负" />
          </template>
        </el-table-column>
        <el-table-column label="调整原因" min-width="220">
          <template #default="{ row }">
            <el-input v-model="row.remark" placeholder="必填，至少 2 个字符" />
          </template>
        </el-table-column>
        <el-table-column label="调整后" width="110" align="right">
          <template #default="{ row }">
            {{ Number(row.available) + Number(row.availableAdjust || 0) }}
          </template>
        </el-table-column>
        <el-table-column label="操作" width="100" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="adjust(row)">提交</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';

const route = useRoute();
const router = useRouter();
const loading = ref(true);
const product = reactive<any>({});
const rows = ref<any[]>([]);

async function load() {
  loading.value = true;
  try {
    const detail = await request(`/gateway/products/Detail?productId=${route.params.id}`, {
      method: 'GET',
      silent: true,
    });
    Object.assign(product, detail || {});
    const inventory = await request('/gateway/inventory/List', {
      method: 'GET',
      params: { keyword: detail?.spuName || '', page: 1, pageSize: 200 },
      silent: true,
    });
    const stocks = Array.isArray(inventory) ? inventory : (inventory?.items || []);
    rows.value = (detail?.skus || []).map((sku: any) => {
      const stock = stocks.find((item: any) => String(item.skuId) === String(sku.id));
      return {
        skuId: String(sku.id),
        skuName: sku.skuName,
        skuSpecText: sku.skuSpecText,
        available: Number(stock?.available || 0),
        warnThreshold: Number(stock?.warnThreshold || 0),
        availableAdjust: 0,
        remark: '',
      };
    });
  } finally {
    loading.value = false;
  }
}

async function adjust(row: any) {
  if (!row.availableAdjust) {
    ElMessage.warning('请填写调整量');
    return;
  }
  if (!row.remark || row.remark.trim().length < 2) {
    ElMessage.warning('请填写调整原因（至少 2 个字符）');
    return;
  }
  const response = await request('/gateway/inventory/Adjust', {
    body: {
      skuId: row.skuId,
      availableAdjust: Number(row.availableAdjust),
      remark: row.remark.trim(),
      warnThreshold: row.warnThreshold,
    },
    raw: true,
  });
  ElMessage.success(response?.message || '库存已调整');
  row.available = Number(row.available) + Number(row.availableAdjust);
  row.availableAdjust = 0;
  row.remark = '';
}

onMounted(load);
</script>

<style scoped>
.table-panel {
  padding: var(--space-4);
}

.skel {
  display: grid;
  gap: var(--space-3);
  padding: var(--space-6);
}
</style>
