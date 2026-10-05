<template>
  <view class="page order-page">
    <AppHeader title="我的订单" subtitle="查看物流与售后状态" back />
    <scroll-view class="tabs" scroll-x :show-scrollbar="false">
      <view
        v-for="tab in tabs"
        :key="tab.value"
        class="tabs__item"
        :class="{ 'tabs__item--active': status === tab.value }"
        @tap="switchTab(tab.value)"
      >
        {{ tab.label }}
      </view>
    </scroll-view>
    <view v-if="loading" class="loading">正在加载订单…</view>
    <view v-else-if="orders.length" class="order-list">
      <view v-for="order in orders" :key="order.orderId" class="order-card" @tap="toDetail(order)">
        <view class="order-card__head">
          <text class="order-card__no">{{ order.orderNo }}</text>
          <text class="order-card__status">{{ order.statusName }}</text>
        </view>
        <view class="order-card__body">
          <text class="order-card__product">{{ order.firstProductName }}</text>
          <text class="order-card__meta">共 {{ order.itemCount }} 件</text>
        </view>
        <view class="order-card__foot">
          <text>{{ order.createdAt }}</text>
          <text class="amount">实付 ¥{{ amount(order.payableAmount) }}</text>
        </view>
      </view>
    </view>
    <view v-else class="empty">这里还没有订单</view>
  </view>
</template>

<script setup lang="ts">
import { onShow } from '@dcloudio/uni-app';
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { amount } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const tabs = [
  { label: '全部', value: 0 },
  { label: '待付款', value: 10 },
  { label: '待发货', value: 20 },
  { label: '待收货', value: 30 },
  { label: '待取货', value: 40 },
  { label: '已完成', value: 50 },
  { label: '退款', value: 60 },
  { label: '已取消', value: 91 },
];
const status = ref(0);
const loading = ref(true);
const orders = ref<any[]>([]);

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  loading.value = true;
  try {
    const page = await request<any>('/gateway/orders/List', {
      params: {
        customerId: session.profile.customerId,
        status: status.value,
        page: 1,
        pageSize: 50,
      },
      silent: true,
    });
    orders.value = page?.items || [];
  } finally {
    loading.value = false;
  }
}

function switchTab(value: number) {
  status.value = value;
  load();
}

function toDetail(order: any) {
  uni.navigateTo({ url: `/pages/order/detail?orderNo=${order.orderNo}` });
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.tabs {
  white-space: nowrap;
}

.tabs__item {
  display: inline-block;
  margin-right: $space-2;
  padding: 14rpx 28rpx;
  border-radius: 34rpx;
  background: #fff;
  color: $text-2;
  font-size: $font-sub;
}

.tabs__item--active {
  background: $brand;
  color: #fff;
}

.order-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
  margin-top: $space-3;
}

.order-card {
  padding: $space-4;
  border-radius: $radius-lg;
  background: #fff;
}

.order-card__head,
.order-card__foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.order-card__no {
  color: $text-2;
  font-family: monospace;
  font-size: $font-note;
}

.order-card__status {
  color: $brand;
  font-size: $font-sub;
}

.order-card__body {
  padding: $space-3 0;
}

.order-card__product {
  display: block;
  font-size: 32rpx;
  font-weight: 600;
}

.order-card__meta,
.order-card__foot {
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-note;
}
</style>
