<template>
  <div>
    <div class="head">
      <div>
        <el-button text class="back" @click="$router.push('/orders')">← 返回订单列表</el-button>
        <h2 class="head__title">{{ order.orderNo || '订单详情' }}</h2>
        <p class="head__desc">
          <span v-if="order.statusName" class="pill" :class="'pill--' + statusColor('order', order.status)">
            {{ order.statusName }}
          </span>
          <span v-if="order.createdAt" class="head__time">下单于 {{ order.createdAt }}</span>
        </p>
      </div>
      <div class="head__actions">
        <!-- 动作只在状态允许时出现：按钮常驻再报错，等于让运营每次都先踩一次坑 -->
        <el-button v-if="Number(order.status) === 10" @click="simulate(true)">模拟支付成功</el-button>
        <el-button v-if="Number(order.status) === 10" @click="simulate(false)">模拟支付失败</el-button>
        <el-button v-if="Number(order.status) === 10" danger @click="cancelOrder">取消订单</el-button>
        <el-button v-if="canShip" type="primary" @click="ship">快递发货</el-button>
        <el-button v-if="canVirtualDeliver" type="primary" @click="deliverVirtual">虚拟发货</el-button>
        <el-button v-if="canPickupReady" type="primary" @click="pickupReady">备货完成</el-button>
        <el-button v-if="Number(order.status) === 40" type="primary" @click="verifyPickup">核销取货码</el-button>
        <el-button v-if="canRefund" danger @click="refund">代客退款</el-button>
      </div>
    </div>

    <div v-if="loading" class="panel skel">
      <div class="skeleton-row" style="width: 100%; height: 120px" />
    </div>

    <template v-else-if="loaded">
      <div class="grid">
        <!--
          金额段：实付金额放大做主视觉，其余拆解项退成小字行。
          之前四个数字用同样大的字号平铺，看的人要在四行里找哪个是实付；
          而「实付」恰恰是这一页唯一需要第一眼读到的数。
        -->
        <SectionPanel title="金额" :hint="refundSummary">
          <div class="amount">
            <span class="amount__label">实付金额</span>
            <span class="amount__value">{{ formatAmount(order.payableAmount) }}</span>
          </div>
          <div class="lines">
            <div class="lines__row">
              <span>商品总额</span>
              <span class="num">{{ formatAmount(order.goodsTotal) }}</span>
            </div>
            <div class="lines__row">
              <span>运费</span>
              <span class="num">{{ formatAmount(order.freight) }}</span>
            </div>
            <div class="lines__row">
              <span>积分抵扣</span>
              <span class="num">{{ formatAmount(order.pointsDeduction) }}</span>
            </div>
            <div v-if="Number(order.couponDiscount) > 0" class="lines__row">
              <span>优惠券</span>
              <span class="num">{{ formatAmount(order.couponDiscount) }}</span>
            </div>
            <div v-if="Number(order.refundedAmount) > 0" class="lines__row">
              <span>已退款</span>
              <span class="num lines__minus">{{ formatAmount(order.refundedAmount) }}</span>
            </div>
            <div class="lines__row">
              <span>使用积分</span>
              <span class="num">{{ formatCount(order.pointsUsed) }}</span>
            </div>
          </div>
        </SectionPanel>

        <SectionPanel title="收货与物流">
          <div class="lines">
            <div class="lines__row">
              <span>收货人</span>
              <span>{{ emptyText(order.receiverName) }}</span>
            </div>
            <div class="lines__row">
              <span>联系电话</span>
              <span class="mono">{{ maskPhone(order.receiverPhone) }}</span>
            </div>
            <div class="lines__row">
              <span>收货地址</span>
              <span class="addr">{{ emptyText(order.receiverAddress) }}</span>
            </div>
            <!-- 未发货时**不显示**这两行：显示「—」等于告诉运营「物流信息丢了」，
                 而它只是还没填。发货之后才出现，含义才准确。 -->
            <template v-if="order.trackingNo">
              <div class="lines__row">
                <span>物流公司</span>
                <span>{{ emptyText(order.logisticsCompanyName) }}</span>
              </div>
              <div class="lines__row">
                <span>运单号</span>
                <span class="mono">{{ order.trackingNo }}</span>
              </div>
              <div v-if="order.shippedAt" class="lines__row">
                <span>发货时间</span>
                <span class="sub">{{ formatDateTime(order.shippedAt) }}</span>
              </div>
            </template>
            <div class="lines__row">
              <span>订单备注</span>
              <span>{{ emptyText(order.remark) }}</span>
            </div>
          </div>
        </SectionPanel>
      </div>

      <!-- 支付记录用表格：单号、金额、状态、渠道、时间要横向对比，
           竖着排成一行行「标签 + 值」会让这几列没法一眼扫完。 -->
      <SectionPanel title="支付记录" flush>
        <el-table v-if="payments.length" :data="payments" class="table">
          <el-table-column label="支付单号" min-width="200">
            <template #default="{ row }">
              <span class="mono">{{ row.paymentNo }}</span>
            </template>
          </el-table-column>
          <el-table-column label="渠道" width="110">
            <template #default="{ row }">
              {{ statusText('paymentChannel', row.channel) || '—' }}
            </template>
          </el-table-column>
          <el-table-column label="金额" width="120" align="right">
            <template #default="{ row }">
              <span class="num strong">{{ formatAmount(row.amount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="状态" width="110" align="center">
            <template #default="{ row }">
              <span class="pill" :class="'pill--' + statusColor('payment', row.status)">
                {{ row.statusName }}
              </span>
            </template>
          </el-table-column>
          <el-table-column label="失败原因" min-width="140">
            <template #default="{ row }">{{ emptyText(row.failReason) }}</template>
          </el-table-column>
          <el-table-column label="支付时间" width="150">
            <template #default="{ row }">
              <span class="sub">{{ formatDateTime(row.paidAt) }}</span>
            </template>
          </el-table-column>
        </el-table>
        <p v-else class="empty">暂无支付单</p>
      </SectionPanel>

      <!--
        代客退款记录（订单侧，支持多次部分退款）。
        与下面的「退款申请」是两条链路：这一段是后台直接退的，已经生效；
        下一段是客户在小程序上申请、还在等审批的。
        混成一张表的话，运营会以为「待审批」的那笔也已经退到客户账上了。
      -->
      <SectionPanel title="代客退款" :hint="`${refunds.length} 笔`">
        <template #actions>
          <el-button v-if="canRefund" type="danger" plain size="small" @click="refundOpen = true">
            发起退款
          </el-button>
        </template>
        <el-table v-if="refunds.length" :data="refunds" class="table">
          <el-table-column label="退款单号" min-width="180">
            <template #default="{ row }">
              <span class="mono">{{ row.refundNo }}</span>
            </template>
          </el-table-column>
          <el-table-column label="类型" width="110" align="center">
            <template #default="{ row }">
              <span class="pill" :class="row.fullyRefunded ? 'pill--info' : 'pill--neutral'">
                {{ row.refundTypeName }}
              </span>
            </template>
          </el-table-column>
          <el-table-column label="金额" width="110" align="right">
            <template #default="{ row }">
              <span class="num strong">{{ formatAmount(row.amount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="商品" min-width="200">
            <template #default="{ row }">
              <div v-for="item in row.items || []" :key="item.orderItemId" class="sub">
                {{ item.productName }} × {{ item.quantity }} · {{ formatAmount(item.amount) }}
              </div>
            </template>
          </el-table-column>
          <el-table-column label="原因" min-width="160">
            <template #default="{ row }">{{ emptyText(row.reason) }}</template>
          </el-table-column>
          <el-table-column label="操作人" width="110">
            <template #default="{ row }">{{ emptyText(row.operatorName) }}</template>
          </el-table-column>
          <el-table-column label="退款时间" width="150">
            <template #default="{ row }">
              <span class="sub">{{ formatDateTime(row.createdAt) }}</span>
            </template>
          </el-table-column>
        </el-table>
        <p v-else class="empty">暂无代客退款</p>
      </SectionPanel>

      <!-- 客户在小程序发起的退款申请：先申请、再审批（规格 10.2） -->
      <SectionPanel title="退款申请" :hint="`${refundRequests.length} 笔`">
        <el-table v-if="refundRequests.length" :data="refundRequests" class="table">
          <el-table-column label="退款单号" min-width="180">
            <template #default="{ row }">
              <span class="mono">{{ row.refundNo }}</span>
            </template>
          </el-table-column>
          <el-table-column label="类型" width="110" align="center">
            <template #default="{ row }">{{ row.refundTypeName }}</template>
          </el-table-column>
          <el-table-column label="金额" width="110" align="right">
            <template #default="{ row }">
              <span class="num strong">{{ formatAmount(row.amount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="状态" width="110" align="center">
            <template #default="{ row }">
              <span class="pill" :class="'pill--' + statusColor('refund', row.status)">
                {{ row.statusName }}
              </span>
            </template>
          </el-table-column>
          <el-table-column label="原因" min-width="160">
            <template #default="{ row }">{{ emptyText(row.reason) }}</template>
          </el-table-column>
          <el-table-column label="申请时间" width="150">
            <template #default="{ row }">
              <span class="sub">{{ formatDateTime(row.createdAt) }}</span>
            </template>
          </el-table-column>
          <el-table-column v-if="hasPendingRefund" label="操作" width="140" align="center">
            <template #default="{ row }">
              <template v-if="Number(row.status) === 10">
                <el-button type="primary" link @click="approveRefund(row)">通过</el-button>
                <el-button type="danger" link @click="rejectRefund(row)">拒绝</el-button>
              </template>
            </template>
          </el-table-column>
        </el-table>
        <p v-else class="empty">暂无客户发起的退款申请</p>
      </SectionPanel>

      <SectionPanel title="商品明细" :hint="`${(order.items || []).length} 行`" flush>
        <el-table :data="order.items || []" class="table">
          <el-table-column label="商品" min-width="200">
            <template #default="{ row }">
              <div>{{ row.productName }}</div>
              <div class="sub">{{ row.skuSpecText }}</div>
            </template>
          </el-table-column>
          <el-table-column label="单价" min-width="100" align="right">
            <template #default="{ row }">
              <span class="num">{{ formatAmount(row.price) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="数量" width="90" align="right">
            <template #default="{ row }">
              <span class="num">{{ formatCount(row.quantity) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="优惠" min-width="110" align="right">
            <template #default="{ row }">
              <span class="num">{{ formatAmount(row.activityDiscount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="小计" min-width="110" align="right">
            <template #default="{ row }">
              <span class="num strong">{{ formatAmount(row.payableAmount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="配送" width="100" align="center">
            <template #default="{ row }">
              <span class="pill pill--neutral">
                {{ statusText('delivery', row.deliveryType) || '其他' }}
              </span>
            </template>
          </el-table-column>
          <el-table-column label="来源" width="100" align="center">
            <template #default="{ row }">
              <span class="pill" :class="'pill--' + statusColor('orderSource', row.sourceType)">
                {{ statusText('orderSource', row.sourceType) || '其他' }}
              </span>
            </template>
          </el-table-column>
        </el-table>
      </SectionPanel>
    </template>

    <el-empty v-else-if="loaded" description="订单不存在或已被删除" />

    <RefundDialog
      v-model="refundOpen"
      :order-no="order.orderNo"
      :detail="order"
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
import SectionPanel from '@/components/SectionPanel.vue';
import { statusColor, statusText } from '@/utils/dict';
import {
  formatAmount,
  formatCount,
  formatDateTime,
  emptyText,
  maskPhone,
} from '@/utils/format';

const route = useRoute();
const router = useRouter();

const loading = ref(true);
// loaded 与 loading 分开：404 之后 loading 会变 false，
// 只看 loading 的话 loading=false 会被误当成「有数据」，于是满屏 undefined
const loaded = ref(false);

const order = reactive<any>({});
const payments = ref<any[]>([]);
// 两条退款链路分开存：
// refunds = 后台代客退款（订单侧，已生效，支持多次部分退款）
// refundRequests = 客户在小程序发起的申请（支付服务，待审批）
// 混在一个数组里的话，运营会把「待审批」当成「已经退到客户账上」。
const refunds = ref<any[]>([]);
const refundRequests = ref<any[]>([]);
const refundOpen = ref(false);

const hasDelivery = (type: number) =>
  (order.items || []).some((item: any) => Number(item.deliveryType) === type);

const canShip = computed(() => Number(order.status) === 20 && hasDelivery(1));
const canVirtualDeliver = computed(() => Number(order.status) === 20 && hasDelivery(2));
const canPickupReady = computed(() => Number(order.status) === 20 && hasDelivery(3));
const isVirtualOnly = computed(() =>
  (order.items || []).length > 0 &&
  (order.items || []).every((item: any) => Number(item.deliveryType) === 2),
);
// 50 已完成（客户已确认收货）**不可退款** —— 用户明确要求。
// 之前把 50 算成可退，按钮显示出来后端一律回错，运营只能理解为系统坏了。
const canRefund = computed(() =>
  !isVirtualOnly.value && [20, 30, 40].includes(Number(order.status)),
);

const hasPendingRefund = computed(() =>
  refundRequests.value.some((a: any) => Number(a.status) === 10),
);

// 金额段的副标题：退过款时把「还能退多少」直接写在标题旁边，
// 运营不必先点进退款弹窗才知道这一单还剩多少余额。
const refundSummary = computed(() => {
  const refunded = Number(order.refundedAmount ?? 0);
  if (refunded <= 0) return '';
  const remaining = Math.max(0, Number(order.payableAmount ?? 0) - refunded);
  return `已退 ${formatAmount(refunded)} · 还能退 ${formatAmount(remaining)}`;
});

async function load() {
  loading.value = true;
  try {
    const data = await request('/gateway/admin/orders/Detail', {
      body: { orderId: route.params.id },
      silent: true,
    });
    Object.assign(order, data || {});

    // 三个附属列表并行拉。**各自 catch 成空数组**：
    // 「退款记录接口挂了」不该让整页变成空白 —— 金额与商品明细才是这页的主体，
    // 把它们挂在同一条失败链上会让一次可恢复的小故障变成「订单详情打不开」。
    [payments.value, refunds.value, refundRequests.value] = await Promise.all([
      request('/gateway/admin/payments/List', {
        body: { page: 1, pageSize: 20, keyword: order.orderNo },
        silent: true,
      }).then((result: any) => result?.items || []).catch(() => []),
      // 后台代客退款（订单侧，含多次部分退款）
      request('/gateway/admin/orders/Refunds', {
        body: { orderId: route.params.id },
        silent: true,
      }).catch(() => []),
      // 客户在小程序发起的退款申请（支付服务侧，待审批）
      request('/gateway/refunds/List', {
        body: { page: 1, pageSize: 20, keyword: order.orderNo },
        silent: true,
      }).then((result: any) => result?.items || []).catch(() => []),
    ]);
  } catch {
    Object.keys(order).forEach((k) => delete order[k]);
    payments.value = [];
    refunds.value = [];
    refundRequests.value = [];
  } finally {
    loading.value = false;
    loaded.value = true;
  }
}

// 发货 / 模拟支付都走「调完就重新拉详情」：
// 本地改状态字段看着省事，但页面显示的状态与服务端可能不一致
// （幂等命中、并发被别人改过），下次进来又变回去——比不做还难查。
async function act(path: string, body: any, okText: string) {
  try {
    await request(`/gateway/admin/orders/${path}`, { body });
    ElMessage.success(okText);
    await load();
  } catch {
    // request 已经弹过错误提示，这里不再重复弹
  }
}

function ship() {
  act('Ship', { orderNo: order.orderNo }, '发货成功');
}

async function cancelOrder() {
  try {
    await ElMessageBox.confirm(
      `取消订单 ${order.orderNo} 会释放库存、解冻积分并回退券占用。`,
      '取消订单',
      { confirmButtonText: '确认取消', cancelButtonText: '返回', type: 'warning' },
    );
  } catch {
    return;
  }
  await act('Cancel', { orderNo: order.orderNo, remark: '后台代客取消' }, '订单已取消');
}

function simulate(succeed: boolean) {
  act(
    'SimulatePayment',
    { orderNo: order.orderNo, succeed },
    succeed ? '已模拟支付成功' : '已模拟支付失败',
  );
}

async function deliverVirtual() {
  try {
    const result = await ElMessageBox.prompt('填写卡号 / 激活码 / 发货备注', '虚拟发货', {
      inputPlaceholder: '会展示给客户，请确认内容无误',
      inputValidator: (value) => (value?.trim() ? true : '请填写发货内容'),
      confirmButtonText: '确认发货',
      cancelButtonText: '取消',
    });
    await act('DeliverVirtual', { orderNo: order.orderNo, remark: result.value.trim() }, '虚拟商品已发货并完成');
  } catch {
    // 用户取消不提示
  }
}

async function pickupReady() {
  try {
    const result = await request('/gateway/admin/orders/SelfPickupReady', {
      body: { orderNo: order.orderNo },
    });
    await load();
    await ElMessageBox.alert(result?.pickupCode || '未返回取货码', '备货完成，请把取货码交给顾客', {
      confirmButtonText: '知道了',
    });
  } catch {
    // request 已提示
  }
}

async function verifyPickup() {
  try {
    const result = await ElMessageBox.prompt('输入或扫描顾客出示的取货码', '核销取货码', {
      inputPlaceholder: '扫码枪可直接扫描后回车',
      inputValidator: (value) => (value?.trim() ? true : '请填写取货码'),
      confirmButtonText: '确认核销',
      cancelButtonText: '取消',
    });
    await act('VerifyPickupCode', { pickupCode: result.value.trim() }, '取货码已核销');
  } catch {
    // 用户取消不提示
  }
}

// 退款交给 RefundDialog：它要处理行级余额、件数上限与「只退部分」的分支，
// 用一个只问原因的 prompt 框根本承载不了这些。
function refund() {
  refundOpen.value = true;
}

async function approveRefund(item: any) {
  try {
    await ElMessageBox.confirm(`确认通过退款单 ${item.refundNo}？`, '通过退款', {
      confirmButtonText: '确认退款',
      cancelButtonText: '取消',
      type: 'warning',
    });
    await request('/gateway/refunds/Approve', { body: { refundId: item.refundId } });
    ElMessage.success('退款已通过');
    await load();
  } catch {
    // 用户取消或 request 已提示
  }
}

async function rejectRefund(item: any) {
  try {
    const result = await ElMessageBox.prompt('填写拒绝原因', '拒绝退款', {
      inputType: 'textarea',
      inputValidator: (value) => (value?.trim().length >= 2 ? true : '拒绝原因至少 2 个字符'),
      confirmButtonText: '拒绝',
      cancelButtonText: '取消',
      type: 'warning',
    });
    await request('/gateway/refunds/Reject', {
      body: { refundId: item.refundId, rejectReason: result.value.trim() },
    });
    ElMessage.success('退款已拒绝');
    await load();
  } catch {
    // 用户取消或 request 已提示
  }
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

.back {
  padding-left: 0;
  margin-bottom: var(--space-1);
}

.head__title {
  margin: 0;
  font-size: var(--text-title-1);
  line-height: var(--lh-title-1);
  font-weight: 600;
  letter-spacing: -0.5px;
}

.head__desc {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin: var(--space-2) 0 0;
}

.head__time {
  font-size: var(--text-foot);
  color: var(--text-3);
}

.head__actions {
  display: flex;
  gap: var(--space-2);
}

.grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--space-4);
  margin-bottom: var(--space-4);
}

.grid > :deep(.section),
:deep(.section) {
  margin-bottom: var(--space-4);
}

/* 实付金额是这一页唯一需要第一眼读到的数：放大 + 等宽数字，
   其余拆解项退成小字行，避免四个数字平铺让人找不到哪个是实付。 */
.amount {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  padding: var(--space-4) 0 var(--space-5);
}

.amount__label {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.amount__value {
  font-size: var(--text-display);
  line-height: var(--lh-display);
  font-weight: 600;
  letter-spacing: -0.5px;
  font-variant-numeric: tabular-nums;
}

/* 拆解项：标签与值分居两端，中间留白。靠发丝线分隔而不是再套一层盒子。 */
.lines__row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-2) 0;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.lines__row + .lines__row {
  border-top: 0.5px solid var(--hairline);
}

.lines__minus {
  color: var(--danger-fg);
}

.empty {
  margin: 0;
  padding: var(--space-6);
  text-align: center;
  font-size: var(--text-foot);
  color: var(--text-3);
}

.table {
  width: 100%;
}

.sub {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.strong {
  font-weight: 600;
}

.sub {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.addr {
  max-width: 320px;
  text-align: right;
}

.skel {
  padding: var(--space-6);
}
</style>
