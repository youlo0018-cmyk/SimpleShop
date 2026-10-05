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
        <section class="panel">
          <h3 class="card__title">金额构成</h3>
          <div class="row">
            <span class="row__label">商品总额</span>
            <span class="row__value">{{ formatAmount(order.goodsTotal) }}</span>
          </div>
          <div class="row">
            <span class="row__label">运费</span>
            <span class="row__value">{{ formatAmount(order.freight) }}</span>
          </div>
          <div class="row">
            <span class="row__label">积分抵扣</span>
            <span class="row__value">{{ formatAmount(order.pointsDeduction) }}</span>
          </div>
          <div class="row">
            <span class="row__label">实付金额</span>
            <span class="row__value strong">{{ formatAmount(order.payableAmount) }}</span>
          </div>
        </section>

        <section class="panel">
          <h3 class="card__title">收货信息</h3>
          <div class="row">
            <span class="row__label">收货人</span>
            <span>{{ emptyText(order.receiverName) }}</span>
          </div>
          <div class="row">
            <span class="row__label">联系电话</span>
            <span class="mono">{{ maskPhone(order.receiverPhone) }}</span>
          </div>
          <div class="row">
            <span class="row__label">收货地址</span>
            <span class="addr">{{ emptyText(order.receiverAddress) }}</span>
          </div>
          <div class="row">
            <span class="row__label">积分使用</span>
            <span class="num">{{ formatCount(order.pointsUsed) }}</span>
          </div>
          <div class="row">
            <span class="row__label">订单备注</span>
            <span>{{ emptyText(order.remark) }}</span>
          </div>
        </section>
      </div>

      <section class="panel">
        <h3 class="card__title">商品明细</h3>
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
      </section>
    </template>

    <el-empty v-else-if="loaded" description="订单不存在或已被删除" />
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { computed } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import { formatAmount, formatCount, emptyText, maskPhone } from '@/utils/format';

const route = useRoute();
const router = useRouter();

const loading = ref(true);
// loaded 与 loading 分开：404 之后 loading 会变 false，
// 只看 loading 的话 loading=false 会被误当成「有数据」，于是满屏 undefined
const loaded = ref(false);

const order = reactive<any>({});

const hasDelivery = (type: number) =>
  (order.items || []).some((item: any) => Number(item.deliveryType) === type);

const canShip = computed(() => Number(order.status) === 20 && hasDelivery(1));
const canVirtualDeliver = computed(() => Number(order.status) === 20 && hasDelivery(2));
const canPickupReady = computed(() => Number(order.status) === 20 && hasDelivery(3));
const isVirtualOnly = computed(() =>
  (order.items || []).length > 0 &&
  (order.items || []).every((item: any) => Number(item.deliveryType) === 2),
);
const canRefund = computed(() =>
  !isVirtualOnly.value && [20, 30, 40, 50].includes(Number(order.status)),
);

async function load() {
  loading.value = true;
  try {
    const data = await request('/gateway/admin/orders/Detail', {
      body: { orderId: route.params.id },
      silent: true,
    });
    Object.assign(order, data || {});
  } catch {
    Object.keys(order).forEach((k) => delete order[k]);
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

async function refund() {
  try {
    const result = await ElMessageBox.prompt('填写退款原因', '代客发起退款', {
      inputType: 'textarea',
      inputPlaceholder: '至少 2 个字符，会记录到审计日志',
      inputValidator: (value) => (value?.trim().length >= 2 ? true : '退款原因至少 2 个字符'),
      confirmButtonText: '确认退款',
      cancelButtonText: '取消',
    });
    await act('Refund', { orderNo: order.orderNo, remark: result.value.trim() }, '退款已处理');
  } catch {
    // 用户取消不提示
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
