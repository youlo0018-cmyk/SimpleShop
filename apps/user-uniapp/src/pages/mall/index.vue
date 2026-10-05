<template>
  <view class="page mall-page">
    <AppHeader title="商城" subtitle="秒杀、推荐与活动" />
    <view class="tabs">
      <view
        v-for="tab in tabs"
        :key="tab.value"
        class="tabs__item"
        :class="{ 'tabs__item--active': active === tab.value }"
        @tap="active = tab.value"
      >
        {{ tab.label }}
      </view>
    </view>
    <view v-if="loading" class="loading">正在加载…</view>
    <view v-else-if="active === 'recommend'" class="product-grid">
      <ProductCard v-for="item in products" :key="item.productId" :product="item" />
    </view>
    <view v-else-if="active === 'activity'" class="activity-list">
      <view v-for="item in activities" :key="item.activityId" class="activity-card">
        <text class="activity-card__name">{{ item.activityName }}</text>
        <text class="activity-card__desc">{{ item.templateName }} · 限时优惠</text>
        <text class="activity-card__time">{{ dateTime(item.claimStartTime) }} - {{ dateTime(item.claimEndTime) }}</text>
      </view>
      <view v-if="!activities.length" class="empty">暂无进行中的活动</view>
    </view>
    <view v-else class="seckill-list">
      <view v-for="item in seckill" :key="item.sessionId" class="seckill-card" @tap="toSeckill">
        <text class="seckill-card__name">{{ item.sessionName }}</text>
        <text class="seckill-card__time">{{ dateTime(item.startTime) }}</text>
        <text class="seckill-card__status">{{ item.statusName }}</text>
      </view>
      <view v-if="!seckill.length" class="empty">暂无秒杀场次</view>
    </view>
    <BottomNav active="mall" />
  </view>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import BottomNav from '@/components/BottomNav.vue';
import ProductCard from '@/components/ProductCard.vue';
import { request } from '@/core/http';
import { dateTime } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const tabs = [
  { label: '秒杀', value: 'seckill' },
  { label: '推荐', value: 'recommend' },
  { label: '活动', value: 'activity' },
] as const;
const active = ref<'seckill' | 'recommend' | 'activity'>('recommend');
const loading = ref(true);
const products = ref<any[]>([]);
const activities = ref<any[]>([]);
const seckill = ref<any[]>([]);

async function load() {
  loading.value = true;
  const customerId = session.profile?.customerId || '0';
  const [productData, activitiesData, seckillData] = await Promise.all([
    request<any>('/gateway/shop/products/List', {
      method: 'POST',
      auth: false,
      data: { customerId, page: 1, pageSize: 30 },
      silent: true,
    }).catch(() => ({ items: [] })),
    request<any[]>('/gateway/coupons/Available', {
      method: 'POST',
      auth: false,
      data: {},
      silent: true,
    }).catch(() => []),
    request<any>('/gateway/marketing/seckill/sessions/Public', {
      method: 'POST',
      auth: false,
      data: { page: 1, pageSize: 20 },
      silent: true,
    }).catch(() => ({ items: [] })),
  ]);
  products.value = productData?.items || [];
  activities.value = Array.isArray(activitiesData) ? activitiesData : [];
  seckill.value = seckillData?.items || seckillData || [];
  loading.value = false;
}

function toSeckill() {
  uni.navigateTo({ url: '/pages/seckill/index' });
}

watch(active, (value) => {
  if (value === 'seckill') toSeckill();
});

onLoad(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.mall-page {
  padding-bottom: 180rpx;
}

.tabs {
  display: flex;
  gap: $space-2;
  margin-bottom: $space-3;
}

.tabs__item {
  flex: 1;
  padding: 18rpx 0;
  border-radius: 36rpx;
  background: #fff;
  color: $text-2;
  font-size: $font-sub;
  text-align: center;
}

.tabs__item--active {
  background: $brand;
  color: #fff;
  font-weight: 600;
}

.product-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: $space-2;
}

.activity-list,
.seckill-list {
  display: flex;
  flex-direction: column;
  gap: $space-2;
}

.activity-card,
.seckill-card {
  padding: $space-4;
  border-radius: $radius-md;
  background: #fff;
}

.activity-card__name,
.seckill-card__name {
  display: block;
  font-size: 34rpx;
  font-weight: 600;
}

.activity-card__desc,
.activity-card__time,
.seckill-card__time,
.seckill-card__status {
  display: block;
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-note;
}
</style>
