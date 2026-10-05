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
    <div class="filters">
      <el-select
        v-model="query.merchantId"
        clearable
        filterable
        placeholder="全部商户"
        @change="reload"
      >
        <el-option
          v-for="item in merchants"
          :key="item.id"
          :label="item.merchantName"
          :value="item.id"
        />
      </el-select>
      <el-input
        v-model="query.customerNo"
        clearable
        placeholder="客户编码"
        @keyup.enter="reload"
        @clear="reload"
      />
      <el-date-picker
        v-model="dateRange"
        type="datetimerange"
        start-placeholder="下单开始时间"
        end-placeholder="下单结束时间"
        value-format="YYYY-MM-DDTHH:mm:ss"
        @change="reload"
      />
    </div>

    <!-- 状态用标签页而不是下拉：七个状态平铺最省一次点击，
         而且每个标签上带当前数量，运营一眼看出「待发货还有多少」。 -->
    <el-tabs v-model="query.status" class="tabs" @tab-change="reload">
      <el-tab-pane label="全部" :name="0" />
      <el-tab-pane v-for="s in STATUS_TABS" :key="s.value" :label="s.label" :name="s.value" />
    </el-tabs>

    <section class="panel">
      <el-table v-loading="loading" :data="rows" class="table">
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

        <el-table-column label="操作" width="360" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click.stop="goDetail(row)">详情</el-button>
            <el-button
              v-if="Number(row.status) === 10 && hasPermission('order:simulate')"
              link
              @click.stop="simulate(row, true)"
            >
              模拟成功
            </el-button>
            <el-button
              v-if="Number(row.status) === 10 && hasPermission('order:simulate')"
              link
              type="danger"
              @click.stop="simulate(row, false)"
            >
              模拟失败
            </el-button>
            <el-button
              v-if="Number(row.status) === 10"
              link
              type="danger"
              @click.stop="cancelOrder(row)"
            >
              取消
            </el-button>
            <el-button
              v-if="Number(row.status) === 20"
              link
              type="primary"
              @click.stop="openFulfill(row)"
            >
              发货
            </el-button>
            <el-button
              v-if="Number(row.status) === 40"
              link
              type="primary"
              @click.stop="verifyPickup(row)"
            >
              核销
            </el-button>
            <el-button
              v-if="canRefund(row)"
              link
              type="danger"
              @click.stop="refund(row)"
            >
              退款
            </el-button>
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

    <el-dialog v-model="fulfill.open" title="订单发货" width="520" align-center>
      <p class="fulfill__subject">{{ fulfill.orderNo }}</p>
      <el-input
        v-model="fulfill.remark"
        type="textarea"
        :rows="3"
        placeholder="发货备注 / 虚拟商品卡号（选填）"
      />
      <div class="fulfill__actions">
        <el-button
          v-if="fulfill.hasPhysical"
          type="primary"
          :loading="fulfill.saving"
          @click="submitFulfill('Ship')"
        >
          快递发货
        </el-button>
        <el-button
          v-if="fulfill.hasVirtual"
          type="primary"
          :loading="fulfill.saving"
          @click="submitFulfill('DeliverVirtual')"
        >
          虚拟发货
        </el-button>
        <el-button
          v-if="fulfill.hasSelfPickup"
          type="primary"
          :loading="fulfill.saving"
          @click="submitFulfill('SelfPickupReady')"
        >
          备货完成
        </el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import request from '@/api/request';
import { statusColor } from '@/utils/dict';
import { formatAmount, formatCount, formatDateTime, emptyText, maskPhone } from '@/utils/format';
import { hasPermission } from '@/utils/session';

const STATUS_TABS = [
  { label: '待支付', value: 10 },
  { label: '待发货', value: 20 },
  { label: '待收货', value: 30 },
  { label: '待取货', value: 40 },
];

const router = useRouter();
const route = useRoute();
const loading = ref(false);
const rows = ref<any[]>([]);
const total = ref(0);
const merchants = ref<any[]>([]);
const dateRange = ref<[string, string] | null>(null);
const fulfill = reactive({
  open: false,
  saving: false,
  orderNo: '',
  remark: '',
  hasPhysical: false,
  hasVirtual: false,
  hasSelfPickup: false,
});

const query = reactive({
  status: 0 as number,
  keyword: '',
  merchantId: '',
  customerId: '',
  customerNo: '',
  page: 1,
  pageSize: 20,
});

async function load() {
  loading.value = true;
  try {
    const requestBody: Record<string, any> = {
      status: query.status,
      keyword: query.keyword,
      page: query.page,
      pageSize: query.pageSize,
      from: dateRange.value?.[0] ? new Date(dateRange.value[0]).toISOString() : null,
      to: dateRange.value?.[1] ? new Date(dateRange.value[1]).toISOString() : null,
    };
    if (query.merchantId) requestBody.merchantId = query.merchantId;
    if (query.customerId) requestBody.customerId = query.customerId;
    if (query.customerNo) requestBody.customerNo = query.customerNo;
    const data = await request('/gateway/admin/orders/List', {
      body: requestBody,
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

function canRefund(row: any) {
  const status = Number(row.status);
  if (![20, 30, 40, 50].includes(status)) return false;
  const virtualOnly = row.hasVirtual && !row.hasPhysical && !row.hasSelfPickup;
  return !(status === 50 && virtualOnly);
}

async function openFulfill(row: any) {
  const detail = await request('/gateway/admin/orders/Detail', {
    body: { orderId: row.orderId },
    silent: true,
  });
  fulfill.open = true;
  fulfill.saving = false;
  fulfill.orderNo = row.orderNo;
  fulfill.remark = '';
  fulfill.hasPhysical = (detail.items || []).some((item: any) => Number(item.deliveryType) === 1);
  fulfill.hasVirtual = (detail.items || []).some((item: any) => Number(item.deliveryType) === 2);
  fulfill.hasSelfPickup = (detail.items || []).some((item: any) => Number(item.deliveryType) === 3);
}

async function submitFulfill(action: string) {
  fulfill.saving = true;
  try {
    const response = await request(`/gateway/admin/orders/${action}`, {
      body: { orderNo: fulfill.orderNo, remark: fulfill.remark.trim() },
      raw: true,
    });
    ElMessage.success(response?.message || '操作成功');
    fulfill.open = false;
    await load();
  } finally {
    fulfill.saving = false;
  }
}

async function cancelOrder(row: any) {
  try {
    await ElMessageBox.confirm(
      `取消订单 ${row.orderNo} 会释放库存、积分与券占用。`,
      '取消订单',
      { confirmButtonText: '确认取消', cancelButtonText: '返回', type: 'warning' },
    );
  } catch {
    return;
  }
  await request('/gateway/admin/orders/Cancel', {
    body: { orderNo: row.orderNo, remark: '后台代客取消' },
  });
  ElMessage.success('订单已取消');
  await load();
}

async function refund(row: any) {
  try {
    const result = await ElMessageBox.prompt('填写退款原因', `退款 ${row.orderNo}`, {
      inputType: 'textarea',
      inputValidator: (value) => (value?.trim().length >= 2 ? true : '退款原因至少 2 个字符'),
      confirmButtonText: '确认退款',
      cancelButtonText: '取消',
      type: 'warning',
    });
    const response = await request('/gateway/admin/orders/Refund', {
      body: { orderNo: row.orderNo, remark: result.value.trim() },
      raw: true,
    });
    ElMessage.success(response?.message || '退款已处理');
    await load();
  } catch {
    // 用户取消或 request 已提示
  }
}

async function verifyPickup(row: any) {
  try {
    const result = await ElMessageBox.prompt('扫描或输入顾客取货码', '核销取货码', {
      inputValidator: (value) => (value?.trim() ? true : '请输入取货码'),
      confirmButtonText: '确认核销',
      cancelButtonText: '取消',
    });
    await request('/gateway/admin/orders/VerifyPickupCode', {
      body: { pickupCode: result.value.trim() },
    });
    ElMessage.success('取货码已核销');
    await load();
  } catch {
    // 用户取消或 request 已提示
  }
}

async function simulate(row: any, succeed: boolean) {
  try {
    await ElMessageBox.confirm(
      `${succeed ? '成功' : '失败'}模拟会直接推进支付链路，订单号 ${row.orderNo}。`,
      '模拟支付',
      { confirmButtonText: '确认', cancelButtonText: '取消', type: succeed ? 'success' : 'warning' },
    );
  } catch {
    return;
  }

  try {
    const result = await request('/gateway/admin/orders/SimulatePayment', {
      body: { orderNo: row.orderNo, succeed },
    });
    ElMessage.success(result?.message || (succeed ? '已模拟支付成功' : '已模拟支付失败'));
    await load();
  } catch {
    // request 已弹提示
  }
}

onMounted(load);

onMounted(async () => {
  query.customerId = String(route.query.customerId || '');
  merchants.value = await request('/gateway/merchants/Options', {
    method: 'GET',
    silent: true,
  }).catch(() => []);
});
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

.filters {
  display: flex;
  gap: var(--space-2);
  margin-bottom: var(--space-4);
}

.filters .el-select,
.filters .el-input {
  width: 200px;
}

.filters .el-date-editor {
  width: 360px;
}

.tabs {
  margin-bottom: var(--space-4);
}

/* 整行可点（DESIGN_SPEC 2.7）：列表项不要求精确点到文字 */
.table {
  cursor: default;
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

.fulfill__subject {
  margin: 0 0 var(--space-3);
  font-family: var(--font-mono);
  font-weight: 600;
}

.fulfill__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-4);
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
