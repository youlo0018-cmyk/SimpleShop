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
      <!--
        从「客户列表」点进来看某个客户的订单时，URL 带 customerId。
        这里把它做成一个**看得见、可关掉**的筛选标记：
        静默生效的话，运营会以为看到的是全部订单，得出「这个客户没下过单」的错误结论。
      -->
      <el-tag
        v-if="customerId"
        class="filters__customer"
        type="info"
        effect="light"
        closable
        @close="clearCustomer"
      >
        仅看客户 {{ customerId }}
      </el-tag>
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
              @click.stop="openRefund(row)"
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

    <!-- 发货弹窗抽到 FulfillDialog：详情页与列表页共用一份，避免两处实现漂移 -->
    <FulfillDialog
      v-model="fulfill.open"
      :order-no="fulfill.orderNo"
      :items="fulfill.items"
      @done="load"
    />

    <!-- 部分退款：规则集中在 RefundDialog，列表与详情共用一份。 -->
    <RefundDialog
      v-model="refund.open"
      :order-no="refund.orderNo"
      :detail="refund.detail"
      @done="load"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import request from '@/api/request';
import RefundDialog from '@/components/RefundDialog.vue';
import FulfillDialog from '@/components/FulfillDialog.vue';
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
  orderNo: '',
  // 明细行交给 FulfillDialog：由它按配送方式决定显示快递 / 虚拟 / 自提哪个按钮
  items: [] as any[],
});

// 退款弹窗只存「打开哪一单」与它的详情；规则与表单都在 RefundDialog 里。
const refund = reactive({
  open: false,
  orderNo: '',
  detail: {} as any,
});

const query = reactive({
  status: 0 as number,
  keyword: '',
  merchantId: '',
  customerNo: '',
  page: 1,
  pageSize: 20,
});

// 从客户列表点进来的客户筛选。URL 带 customerId 时生效，可一键关掉。
const customerId = computed(() => String(route.query.customerId || ''));

function clearCustomer() {
  // 用 replace 而不是 push：不留一条「带筛选的旧地址」在历史里，
// 否则用户按「后退」会跳回一个看起来一样、其实筛选条件不同的页面。
  router.replace({ path: '/orders', query: { ...route.query, customerId: undefined } });
}

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
    if (customerId.value) requestBody.customerId = customerId.value;
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
  // 50 已完成（客户已确认收货）**不可退款** —— 用户明确要求「订单用户确认收货后不可退款」。
  // 之前这里把 50 也算成可退，点下去后端一律回「已完成不能退」，
  // 运营只能理解为系统坏了。后端与前端必须对同一条规则给同一个答案。
  if (![20, 30, 40].includes(status)) return false;
  const virtualOnly = row.hasVirtual && !row.hasPhysical && !row.hasSelfPickup;
  return !virtualOnly;
}

async function openFulfill(row: any) {
  // 明细行交给 FulfillDialog：它按配送方式决定显示快递 / 虚拟 / 自提哪个按钮，
  // 快递发货的物流公司与运单号校验也在那里（提交时校验，失焦不校验）。
  const detail = await request('/gateway/admin/orders/Detail', {
    body: { orderId: row.orderId },
    silent: true,
  });
  fulfill.orderNo = row.orderNo;
  fulfill.items = detail.items || [];
  fulfill.open = true;
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

async function openRefund(row: any) {
  const detail = await request('/gateway/admin/orders/Detail', {
    body: { orderId: row.orderId },
    silent: true,
  });
  refund.open = true;
  refund.orderNo = row.orderNo;
  // 详情这里已经拉过一次，直接传进弹窗 —— 让弹窗再发一次请求的话，
  // 点「退款」到看到表单之间会多一次往返
  refund.detail = detail;
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

// 🔴 只能有**一个** onMounted，而且客户筛选直接读 URL（customerId 是 computed）。
// 之前这里是两个 onMounted：第一个先 load()（不带 customerId），第二个才把
// route.query.customerId 写进 query —— 筛选值在请求发出**之后**才生效，
// 而且没人再触发一次 load，所以「从客户列表点进来看订单」永远显示全部订单。
onMounted(async () => {
  merchants.value = await request('/gateway/merchants/Options', {
    method: 'GET',
    silent: true,
  }).catch(() => []);
  await load();
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

.fulfill__form {
  margin-top: var(--space-2);
}

.fulfill__control {
  width: 100%;
}

/* 客户筛选标记：与筛选框同一行，让「为什么只有这些订单」一眼可见 */
.filters__customer {
  height: 32px;
  font-size: var(--text-foot);
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
