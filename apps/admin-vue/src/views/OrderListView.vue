<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">订单</h2>
        <p class="head__desc">共 {{ total }} 单</p>
      </div>
      <el-input
        v-model="query.keyword"
        class="head__search"
        placeholder="订单号 / 收货人 / 手机号"
        clearable
        @keyup.enter="reload"
        @clear="reload"
      />
    </div>

    <!-- 状态用标签页而不是下拉：七个状态平铺最省一次点击，
         而且每个标签上带当前数量，运营一眼看出「待发货还有多少」。 -->
    <el-tabs v-model="query.status" class="tabs" @tab-change="reload">
      <el-tab-pane label="全部" :name="0" />
      <el-tab-pane v-for="s in STATUS_TABS" :key="s.value" :label="s.label" :name="s.value" />
    </el-tabs>

    <section class="panel">
      <el-table v-loading="loading" :data="rows" class="table" @row-click="goDetail">
        <el-table-column label="订单号" min-width="200">
          <template #default="{ row }">
            <span class="mono">{{ row.orderNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="商品" min-width="90">
          <template #default="{ row }">
            <span class="num">{{ formatCount(row.itemQuantity) }} 件</span>
          </template>
        </el-table-column>

        <el-table-column label="实付金额" min-width="120" align="right">
          <template #default="{ row }">
            <span class="num strong">{{ formatAmount(row.payableAmount) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="收货人" min-width="130">
          <template #default="{ row }">
            <div>{{ emptyText(row.receiverName) }}</div>
            <div class="sub">{{ maskPhone(row.receiverPhone) }}</div>
          </template>
        </el-table-column>

        <el-table-column label="状态" width="110" align="center">
          <template #default="{ row }">
            <!-- 文案用后端下发的 statusName，字典只负责颜色（DESIGN_SPEC 7.3） -->
            <span class="pill" :class="'pill--' + statusColor('order', row.status)">
              {{ row.statusName }}
            </span>
          </template>
        </el-table-column>

        <el-table-column label="下单时间" width="150">
          <template #default="{ row }">
            <span class="sub">{{ formatDateTime(row.createdAt) }}</span>
          </template>
        </el-table-column>

        <template #empty>
          <div class="empty">
            <div class="empty__title">没有符合条件的订单</div>
            <div class="empty__desc">换个状态或关键词试试</div>
          </div>
        </template>
      </el-table>

      <div v-if="total > 0" class="pager">
        <el-pagination
          v-model:current-page="query.page"
          v-model:page-size="query.pageSize"
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
import { onMounted, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
import request from '@/api/request';
import { statusColor } from '@/utils/dict';
import { formatAmount, formatCount, formatDateTime, emptyText, maskPhone } from '@/utils/format';

const STATUS_TABS = [
  { label: '待支付', value: 10 },
  { label: '待发货', value: 20 },
  { label: '待收货', value: 30 },
  { label: '待取货', value: 40 },
];

const router = useRouter();
const loading = ref(false);
const rows = ref<any[]>([]);
const total = ref(0);

const query = reactive({
  status: 0 as number,
  keyword: '',
  page: 1,
  pageSize: 20,
});

async function load() {
  loading.value = true;
  try {
    const data = await request('/gateway/admin/orders/List', {
      body: { ...query },
      silent: true,
    });
    rows.value = data?.items || [];
    total.value = Number(data?.total || 0);
  } catch {
    rows.value = [];
    total.value = 0;
  } finally {
    loading.value = false;
  }
}

function reload() {
  query.page = 1;
  load();
}

function goDetail(row: any) {
  router.push('/orders/detail/' + row.orderId);
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

.head__search {
  width: 280px;
}

.tabs {
  margin-bottom: var(--space-4);
}

/* 整行可点（DESIGN_SPEC 2.7）：列表项不要求精确点到文字 */
.table {
  cursor: pointer;
}

.strong {
  font-weight: 600;
}

.sub {
  font-size: var(--text-foot);
  color: var(--text-2);
  line-height: var(--lh-foot);
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
</style>
