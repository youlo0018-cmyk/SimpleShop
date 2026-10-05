<template>
  <view class="page coupon-page">
    <AppHeader title="领券中心" subtitle="先领券，再下单更省" back />
    <view v-if="loading" class="loading">正在加载优惠券…</view>
    <view v-else-if="activities.length" class="coupon-list">
      <view v-for="item in activities" :key="item.activityId" class="coupon-card">
        <view class="coupon-card__left">
          <text class="coupon-card__value">
            {{ item.couponTypeName?.includes('折扣') ? '折扣' : `¥${amount(item.discountAmount)}` }}
          </text>
          <text class="coupon-card__condition">
            {{ Number(item.thresholdAmount) > 0 ? `满 ${amount(item.thresholdAmount)} 可用` : '无门槛' }}
          </text>
        </view>
        <view class="coupon-card__body">
          <text class="coupon-card__name">{{ item.templateName }}</text>
          <text class="coupon-card__desc">{{ item.activityName }}</text>
          <text class="coupon-card__time">
            {{ item.claimStartTime }} 至 {{ item.claimEndTime }}
          </text>
          <text class="coupon-card__stock">
            已领 {{ item.claimedQuantity }} / {{ item.claimQuantity }} · 每人限 {{ item.perUserLimit }} 张
          </text>
        </view>
        <button class="coupon-card__button" :disabled="claiming === item.activityId" @tap="claim(item)">
          领取
        </button>
      </view>
    </view>
    <view v-else class="empty">暂时没有可领取的券</view>
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
const loading = ref(true);
const claiming = ref('');
const activities = ref<any[]>([]);

async function load() {
  loading.value = true;
  try {
    activities.value = await request<any[]>('/gateway/coupons/Available', {
      method: 'POST',
      auth: false,
      data: {},
      silent: true,
    });
  } finally {
    loading.value = false;
  }
}

async function claim(item: any) {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  claiming.value = item.activityId;
  try {
    const result = await request<any>('/gateway/coupons/Claim', {
      method: 'POST',
      data: {
        customerId: session.profile.customerId,
        activityId: item.activityId,
        quantity: 1,
      },
    });
    uni.showToast({ title: result?.message || '领取成功', icon: 'success' });
    await load();
  } finally {
    claiming.value = '';
  }
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.coupon-page {
  padding-bottom: 80rpx;
}

.coupon-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
}

.coupon-card {
  display: flex;
  align-items: stretch;
  overflow: hidden;
  border-radius: $radius-lg;
  background: #fff;
}

.coupon-card__left {
  display: flex;
  flex-direction: column;
  justify-content: center;
  width: 210rpx;
  padding: $space-3;
  background: linear-gradient(135deg, #fff0d8, #ffe0bd);
  color: #b35a00;
  text-align: center;
}

.coupon-card__value {
  font-size: 40rpx;
  font-weight: 700;
}

.coupon-card__condition {
  margin-top: 8rpx;
  font-size: $font-note;
}

.coupon-card__body {
  flex: 1;
  min-width: 0;
  padding: $space-3;
}

.coupon-card__name {
  display: block;
  overflow: hidden;
  font-size: 32rpx;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.coupon-card__desc,
.coupon-card__time,
.coupon-card__stock {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.coupon-card__button {
  align-self: center;
  height: 64rpx;
  margin-right: $space-3;
  padding: 0 $space-3;
  border-radius: 32rpx;
  background: $brand;
  color: #fff;
  font-size: $font-sub;
  line-height: 64rpx;
}
</style>
