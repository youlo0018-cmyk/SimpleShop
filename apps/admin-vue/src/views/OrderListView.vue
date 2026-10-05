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

    <el-dialog v-model="fulfill.open" title="订单发货" width="520" align-center>
      <p class="fulfill__subject">{{ fulfill.orderNo }}</p>
      <el-form label-position="top" class="fulfill__form">
        <!--
          快递发货**必填物流公司与运单号**：客服接到物流异常时，
          没有单号的订单无从追责，「已发货」只是一个空口的状态。
        -->
        <template v-if="fulfill.hasPhysical">
          <el-form-item label="物流公司" :error="fulfill.errors.company">
            <el-select
              v-model="fulfill.logisticsCompanyId"
              class="fulfill__control"
              placeholder="请选择物流公司"
              filterable
              :loading="fulfill.companiesLoading"
            >
              <el-option
                v-for="c in fulfill.companies"
                :key="c.id"
                :label="c.name"
                :value="c.id"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="运单号" :error="fulfill.errors.trackingNo">
            <el-input
              v-model="fulfill.trackingNo"
              class="fulfill__control"
              placeholder="请输入快递运单号"
              maxlength="64"
            />
          </el-form-item>
        </template>
        <el-form-item label="发货备注">
          <el-input
            v-model="fulfill.remark"
            type="textarea"
            :rows="2"
            :placeholder="fulfill.hasVirtual ? '虚拟商品卡号 / 激活码（会展示给客户）' : '选填'"
          />
        </el-form-item>
      </el-form>
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

    <!--
      部分退款。早期只有「整单退」，一次就把订单打成已退款，
      于是「退了一件、另一件还想退」这种最常见的诉求完全做不了。
      这里让运营逐行勾选、填金额与件数，订单退完剩余余额才变成已退款。
    -->
    <el-dialog v-model="refund.open" title="发起退款" width="680" align-center>
      <p class="fulfill__subject">{{ refund.orderNo }}</p>

      <div class="refund__scope">
        <el-radio-group v-model="refund.mode" size="small">
          <el-radio-button value="whole">退完剩余余额</el-radio-button>
          <el-radio-button value="partial">只退指定商品</el-radio-button>
        </el-radio-group>
        <span class="refund__balance">
          订单实付 {{ formatAmount(refund.payableAmount) }}
          <template v-if="refund.refundedAmount > 0">
            · 已退 {{ formatAmount(refund.refundedAmount) }}
          </template>
          · 还能退 {{ formatAmount(refund.remaining) }}
        </span>
      </div>

      <el-table
        v-if="refund.mode === 'partial'"
        :data="refund.items"
        class="refund__table"
        height="260"
      >
        <el-table-column label="退款" width="60" align="center">
          <template #default="{ row }">
            <el-checkbox v-model="row.picked" />
          </template>
        </el-table-column>
        <el-table-column label="商品" min-width="180">
          <template #default="{ row }">
            <div>{{ row.productName }}</div>
            <div class="sub">{{ row.skuSpecText }}</div>
          </template>
        </el-table-column>
        <el-table-column label="件数" width="92" align="right">
          <template #default="{ row }">
            <el-input-number
              v-model="row.quantity"
              size="small"
              :min="1"
              :max="row.remainingQuantity"
              controls-position="right"
              class="refund__number"
            />
          </template>
        </el-table-column>
        <el-table-column label="退款金额" width="140" align="right">
          <template #default="{ row }">
            <el-input
              v-model="row.amount"
              size="small"
              type="number"
              :disabled="!row.picked"
              :placeholder="formatAmount(row.refundableAmount)"
            />
          </template>
        </el-table-column>
        <el-table-column label="本行可退" width="110" align="right">
          <template #default="{ row }">
            <span class="num sub">{{ formatAmount(row.refundableAmount) }}</span>
          </template>
        </el-table-column>
      </el-table>

      <p class="refund__total">
        本次退款 <strong>{{ formatAmount(refundTotal) }}</strong> 元
      </p>

      <el-form label-position="top" class="fulfill__form">
        <el-form-item label="退款原因" :error="refund.errors">
          <el-input
            v-model="refund.remark"
            type="textarea"
            :rows="2"
            placeholder="至少 2 个字符，会记录到审计日志"
            maxlength="512"
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="refund.open = false">取消</el-button>
        <el-button type="danger" :loading="refund.saving" @click="submitRefund">确认退款</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
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
  logisticsCompanyId: '',
  trackingNo: '',
  companies: [] as any[],
  companiesLoading: false,
  errors: {} as Record<string, string>,
  hasPhysical: false,
  hasVirtual: false,
  hasSelfPickup: false,
});

// 退款弹窗的状态。mode 决定是「退完剩余余额」还是「只退选中的行」。
const refund = reactive({
  open: false,
  saving: false,
  orderNo: '',
  mode: 'whole' as 'whole' | 'partial',
  remark: '',
  payableAmount: 0,
  refundedAmount: 0,
  remaining: 0,
  items: [] as any[],
  errors: '',
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
  const detail = await request('/gateway/admin/orders/Detail', {
    body: { orderId: row.orderId },
    silent: true,
  });
  fulfill.open = true;
  fulfill.saving = false;
  fulfill.orderNo = row.orderNo;
  fulfill.remark = '';
  fulfill.logisticsCompanyId = '';
  fulfill.trackingNo = '';
  fulfill.errors = {};
  fulfill.hasPhysical = (detail.items || []).some((item: any) => Number(item.deliveryType) === 1);
  fulfill.hasVirtual = (detail.items || []).some((item: any) => Number(item.deliveryType) === 2);
  fulfill.hasSelfPickup = (detail.items || []).some((item: any) => Number(item.deliveryType) === 3);
  if (fulfill.hasPhysical) await loadLogisticsCompanies();
}

// 物流公司字典只在第一次发货时拉一次，之后复用。
// 每开一次弹窗都请求一遍的话，点「发货」会有一次明显的白屏等待。
async function loadLogisticsCompanies() {
  if (fulfill.companies.length || fulfill.companiesLoading) return;
  fulfill.companiesLoading = true;
  try {
    const rows = await request('/gateway/logistics-companies/Options', {
      method: 'POST',
      body: {},
      silent: true,
    });
    fulfill.companies = (Array.isArray(rows) ? rows : (rows?.items || [])).map((r: any) => ({
      id: String(r.logisticsId ?? r.LogisticsId ?? r.id ?? r.Id ?? ''),
      name: String(r.companyName ?? r.CompanyName ?? r.name ?? ''),
    }));
  } catch {
    fulfill.companies = [];
  } finally {
    fulfill.companiesLoading = false;
  }
}

async function submitFulfill(action: string) {
  // ⚠️ 只在**提交时**校验（用户要求失焦不校验）。
  // 快递发货必须选物流公司 + 填运单号；虚拟发货与自提备货不需要。
  fulfill.errors = {};
  if (action === 'Ship') {
    if (!fulfill.logisticsCompanyId) fulfill.errors.company = '请选择物流公司';
    if (!fulfill.trackingNo.trim()) fulfill.errors.trackingNo = '请填写运单号';
    if (Object.keys(fulfill.errors).length) {
      ElMessage.warning('请先补全物流信息');
      return;
    }
  }

  fulfill.saving = true;
  try {
    const response = await request(`/gateway/admin/orders/${action}`, {
      body: {
        orderNo: fulfill.orderNo,
        remark: fulfill.remark.trim(),
        logisticsCompanyId: fulfill.logisticsCompanyId,
        trackingNo: fulfill.trackingNo.trim(),
      },
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

async function openRefund(row: any) {
  const detail = await request('/gateway/admin/orders/Detail', {
    body: { orderId: row.orderId },
    silent: true,
  });

  const refunded = Number(detail.refundedAmount ?? 0);
  const payable = Number(detail.payableAmount ?? 0);

  refund.open = true;
  refund.saving = false;
  refund.orderNo = row.orderNo;
  refund.mode = 'whole';
  refund.remark = '';
  refund.errors = '';
  refund.payableAmount = payable;
  refund.refundedAmount = refunded;
  refund.remaining = Math.max(0, Number((payable - refunded).toFixed(2)));
  refund.items = (detail.items || []).map((item: any) => ({
    orderItemId: String(item.orderItemId),
    productName: item.productName,
    skuSpecText: item.skuSpecText,
    remainingQuantity: Math.max(0, Number(item.quantity ?? 0) - Number(item.refundedQuantity ?? 0)),
    refundableAmount: Number(item.refundableAmount ?? 0),
    picked: false,
    quantity: Math.max(1, Number(item.quantity ?? 1) - Number(item.refundedQuantity ?? 0)),
    amount: '' as string | number,
  }));
}

// 本次退款合计。整单退显示订单剩余可退，部分退只累加勾选的行。
const refundTotal = computed(() => {
  if (refund.mode === 'whole') return refund.remaining;
  return refund.items
    .filter((a) => a.picked)
    .reduce((sum, a) => sum + (Number(a.amount) || 0), 0);
});

async function submitRefund() {
  // 提交时统一校验，不做失焦校验（用户要求）
  refund.errors = '';
  if (!refund.remark.trim() || refund.remark.trim().length < 2) {
    refund.errors = '退款原因至少 2 个字符';
  }

  if (refund.mode === 'partial') {
    // 整单退传 null 让后端按「剩余可退余额一次退完」处理；
    // 部分退才逐行组装。拆成两个分支而不是先算再判：lines 为 null 时
    // TypeScript 收窄不过去，而且判两次也更容易漏掉某一个校验。
    const lines = refund.items
      .filter((a) => a.picked)
      .map((a) => ({
        orderItemId: a.orderItemId,
        quantity: Number(a.quantity) || 0,
        amount: Number(a.amount) || 0,
      }));

    if (!lines.length) refund.errors = refund.errors || '请至少勾选一个商品行';
    // 前端也卡一道上限：后端会拒，但让它明确报错比提交后等一个 400 好排查
    for (const line of lines) {
      if (line.amount <= 0) refund.errors = '退款金额必须大于 0';
    }
    const total = lines.reduce((sum, a) => sum + a.amount, 0);
    if (total > refund.remaining + 0.01) {
      refund.errors = `本次退款 ${total.toFixed(2)} 元超过订单剩余可退 ${refund.remaining.toFixed(2)} 元`;
    }

    if (refund.errors) {
      ElMessage.warning(refund.errors);
      return;
    }

    await postRefund(lines);
    return;
  }

  if (refund.errors) {
    ElMessage.warning(refund.errors);
    return;
  }

  await postRefund(null);
}

async function postRefund(lines: any[] | null) {
  refund.saving = true;
  try {
    const response = await request('/gateway/admin/orders/Refund', {
      body: { orderNo: refund.orderNo, remark: refund.remark.trim(), lines },
      raw: true,
    });
    ElMessage.success(response?.message || '退款已处理');
    refund.open = false;
    await load();
  } catch {
    // request 已提示，保留弹窗让运营改一个数字就能重试
  } finally {
    refund.saving = false;
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

.refund__scope {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-bottom: var(--space-3);
}

/* 余额说明用等宽数字：这是要跟退款金额反复对比的数字，字形对齐才读得准 */
.refund__balance {
  font-size: var(--text-foot);
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

.refund__table {
  width: 100%;
}

.refund__number {
  width: 100%;
}

.refund__total {
  margin: var(--space-3) 0 0;
  text-align: right;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.refund__total strong {
  color: var(--danger-fg);
  font-variant-numeric: tabular-nums;
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
