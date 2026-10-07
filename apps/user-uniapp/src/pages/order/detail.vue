<template>
  <view class="page order-detail">
    <AppHeader title="订单详情" :subtitle="order.orderNo" back />
    <view v-if="loading" class="loading">正在加载…</view>
    <template v-else-if="order.orderNo">
      <view class="status-card">
        <text class="status-card__name">{{ order.statusName }}</text>
        <text class="status-card__time">{{ order.createdAt }}</text>
      </view>

      <!-- 虚拟商品的发货内容（卡号 / 激活码）是顾客拿到的唯一交付物，
           放在最前面；快递的发货备注属内部信息，不展示给顾客。 -->
      <view v-if="isVirtual && order.shipRemark" class="panel info">
        <view class="info__row info__row--strong">
          <text>发货内容</text>
          <text class="info__value">{{ order.shipRemark }}</text>
        </view>
      </view>

      <view class="panel info">
        <view class="info__row">
          <text>收货人</text>
          <text>{{ order.receiverName }}</text>
        </view>
        <view class="info__row">
          <text>联系电话</text>
          <text>{{ order.receiverPhone }}</text>
        </view>
        <view class="info__row">
          <text>收货地址</text>
          <text class="info__value">{{ order.receiverAddress }}</text>
        </view>
      </view>

      <view class="panel info">
        <view class="info__row">
          <text>商品总额</text>
          <text>¥{{ amount(order.goodsTotal) }}</text>
        </view>
        <view class="info__row">
          <text>运费</text>
          <text>¥{{ amount(order.freight) }}</text>
        </view>
        <view class="info__row">
          <text>券优惠</text>
          <text>-¥{{ amount(order.couponDiscount) }}</text>
        </view>
        <view class="info__row">
          <text>积分抵扣</text>
          <text>-¥{{ amount(order.pointsDeduction) }}（{{ order.pointsUsed }} 积分）</text>
        </view>
        <view class="info__row info__row--strong">
          <text>实付</text>
          <text class="amount">¥{{ amount(order.payableAmount) }}</text>
        </view>
      </view>

      <view class="panel info">
        <view v-for="item in order.items" :key="item.orderItemId" class="line">
          <view>
            <text class="line__name">{{ item.productName }}</text>
            <text class="line__spec">{{ item.skuSpecText }} × {{ item.quantity }}</text>
          </view>
          <text class="amount">¥{{ amount(item.payableAmount) }}</text>
        </view>
      </view>

      <view class="actions">
        <button v-if="order.canCancel" class="button-primary" @tap="cancel">取消订单</button>
        <button v-if="order.canConfirmReceipt" class="button-primary" @tap="confirmReceipt">
          确认收货
        </button>
        <button v-if="Number(order.status) === 50" class="button-primary" @tap="toEvaluate">
          评价商品
        </button>
        <button v-if="canRefund" class="button-primary" @tap="toRefund">申请退款</button>
      </view>
    </template>
    <view v-else class="empty">订单不存在</view>
  </view>
</template>

<script setup lang="ts">
import { onLoad } from '@dcloudio/uni-app';
import { computed, ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { amount } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const order = ref<any>({});
const orderNo = ref('');

/** 订单是否包含虚拟商品（决定是否展示发货内容）。 */
const isVirtual = computed(() =>
  Array.isArray(order.value.items) && order.value.items.some((i: any) => Number(i.deliveryType) === 2));

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  loading.value = true;
  try {
    order.value = await request<any>('/gateway/orders/Detail', {
      params: { orderNo: orderNo.value, customerId: session.profile.customerId },
      silent: true,
    });
  } finally {
    loading.value = false;
  }
}

async function cancel() {
  const confirmed = await new Promise<boolean>((resolve) => {
    uni.showModal({
      title: '取消订单',
      content: '取消后会释放库存、积分与券占用。',
      success: (result) => resolve(result.confirm),
      fail: () => resolve(false),
    });
  });
  if (!confirmed) return;
  await request('/gateway/orders/Cancel', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, orderNo: orderNo.value },
  });
  uni.showToast({ title: '订单已取消', icon: 'success' });
  await load();
}

async function confirmReceipt() {
  await request('/gateway/orders/ConfirmReceipt', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, orderNo: orderNo.value },
  });
  uni.showToast({ title: '已确认收货', icon: 'success' });
  await load();
}

function toEvaluate() {
  uni.navigateTo({ url: `/pages/evaluate/list?orderNo=${orderNo.value}` });
}

function toRefund() {
  uni.navigateTo({
    url: `/pages/refund/form?orderNo=${orderNo.value}&orderId=${order.value.orderId}`,
  });
}

const canRefund = computed(() => {
  const status = Number(order.value.status);
  const items = order.value.items || [];
  const hasVirtual = items.some((item: any) => Number(item.deliveryType) === 2);
  return hasVirtual ? [20, 30].includes(status) : [20, 30, 40, 50].includes(status);
});

onLoad((options) => {
  orderNo.value = String(options?.orderNo || '');
  load();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.status-card {
  padding: $space-5;
  border-radius: $radius-lg;
  background: linear-gradient(135deg, #0071e3, #55a8ff);
  color: #fff;
}

.status-card__name {
  display: block;
  font-size: 44rpx;
  font-weight: 600;
}

.status-card__time {
  display: block;
  margin-top: $space-1;
  opacity: 0.82;
  font-size: $font-note;
}

.info {
  margin-top: $space-3;
  padding: 0 $space-4;
}

.info__row,
.line {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: $space-3;
  min-height: 96rpx;
  padding: $space-3 0;
  border-bottom: 1rpx solid $hairline;
}

.info__row:last-child,
.line:last-child {
  border-bottom: 0;
}

.info__value {
  max-width: 440rpx;
  text-align: right;
}

.info__row--strong {
  font-weight: 600;
}

.line__name {
  display: block;
  font-weight: 600;
}

.line__spec {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.actions {
  margin-top: $space-4;
}
</style>
