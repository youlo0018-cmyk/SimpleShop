<template>
  <view class="page coupon-mine">
    <AppHeader title="我的券包" subtitle="可用、已用与已过期" back />
    <view class="tabs">
      <view
        v-for="tab in tabs"
        :key="tab.value"
        class="tabs__item"
        :class="{ 'tabs__item--active': status === tab.value }"
        @tap="switchTab(tab.value)"
      >
        {{ tab.label }}
      </view>
    </view>
    <view v-if="loading" class="loading">正在加载…</view>
    <view v-else-if="items.length" class="coupon-list">
      <view v-for="item in items" :key="item.couponId" class="coupon-card" :class="{ 'coupon-card--disabled': item.status !== 1 }">
        <view class="coupon-card__value">
          <text>{{ item.couponTypeName }}</text>
          <text class="coupon-card__amount">
            {{ item.couponType === 2 ? `${item.discountRate} 折` : `¥${amount(item.discountAmount)}` }}
          </text>
        </view>
        <view class="coupon-card__info">
          <text class="coupon-card__name">{{ item.templateName }}</text>
          <text>{{ Number(item.thresholdAmount) > 0 ? `满 ${amount(item.thresholdAmount)} 可用` : '无门槛' }}</text>
          <text>有效期至 {{ item.expireAt }}</text>
          <text v-if="item.orderNo">订单 {{ item.orderNo }}</text>
        </view>
        <text class="coupon-card__status">{{ item.statusName }}</text>
      </view>
    </view>
    <view v-else class="empty">这里还没有券</view>
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
  { label: '可用', value: 1 },
  { label: '已用', value: 3 },
  { label: '已过期', value: 4 },
];
const status = ref(1);
const loading = ref(true);
const items = ref<any[]>([]);

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  loading.value = true;
  try {
    const page = await request<any>('/gateway/coupons/My', {
      method: 'POST',
      data: {
        customerId: session.profile.customerId,
        status: status.value,
        page: 1,
        pageSize: 50,
      },
      silent: true,
    });
    items.value = page?.items || [];
  } finally {
    loading.value = false;
  }
}

function switchTab(value: number) {
  status.value = value;
  load();
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.tabs {
  display: flex;
  gap: $space-2;
  margin-bottom: $space-3;
}

.tabs__item {
  flex: 1;
  padding: 16rpx 0;
  border-radius: 36rpx;
  background: #fff;
  color: $text-2;
  font-size: $font-sub;
  text-align: center;
}

.tabs__item--active {
  background: $brand;
  color: #fff;
}

.coupon-list {
  display: flex;
  flex-direction: column;
  gap: $space-2;
}

.coupon-card {
  display: flex;
  align-items: center;
  gap: $space-3;
  padding: $space-4;
  border-radius: $radius-lg;
  background: #fff;
}

.coupon-card--disabled {
  filter: grayscale(0.75);
  opacity: 0.72;
}

.coupon-card__value {
  display: flex;
  flex-direction: column;
  gap: 6rpx;
  width: 150rpx;
  color: #b35a00;
  font-size: $font-note;
}

.coupon-card__amount {
  color: $danger;
  font-size: 40rpx;
  font-weight: 700;
}

.coupon-card__info {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.coupon-card__name {
  color: $text-1;
  font-size: $font-sub;
  font-weight: 600;
}

.coupon-card__status {
  color: $brand;
  font-size: $font-note;
}
</style>
