<template>
  <view class="page favorite-page">
    <AppHeader title="我的收藏" :subtitle="`${total} 件`" back />

    <view v-if="loading" class="loading">正在加载…</view>
    <view v-else-if="items.length" class="favorite-list">
      <view
        v-for="item in items"
        :key="item.spuId"
        class="favorite-card"
        :class="{ 'favorite-card--unavailable': !item.available }"
        @tap="toProduct(item)"
      >
        <image class="favorite-card__image" :src="item.mainImage || placeholder" mode="aspectFill" />
        <view class="favorite-card__body">
          <text class="favorite-card__name">{{ item.spuName || '商品已下架' }}</text>
          <text v-if="!item.available" class="favorite-card__status">
            {{ item.statusName || '已下架' }}
          </text>
          <view class="favorite-card__bottom">
            <text class="favorite-card__price">{{ amount(item.price) }}</text>
            <text class="favorite-card__remove" @tap.stop="remove(item)">取消收藏</text>
          </view>
        </view>
      </view>
    </view>
    <view v-else class="empty">
      <text class="empty__title">收藏夹是空的</text>
      <text class="empty__desc">看到喜欢的商品，点一下收藏就能在这里找到</text>
    </view>
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
const items = ref<any[]>([]);
const total = ref(0);
const placeholder = 'https://cdn.example.com/main.png';

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }

  loading.value = true;
  try {
    const data = await request<any>('/gateway/customers/favorites/List', {
      method: 'POST',
      data: { customerId: session.profile.customerId, page: 1, pageSize: 50 },
      silent: true,
    });
    items.value = data?.items || [];
    total.value = Number(data?.total || 0);
  } finally {
    loading.value = false;
  }
}

function toProduct(item: any) {
  if (!item.available) return;
  uni.navigateTo({ url: `/pages/product/detail?productId=${item.spuId}` });
}

async function remove(item: any) {
  await request('/gateway/customers/favorites/Remove', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, spuId: item.spuId },
  });
  uni.showToast({ title: '已取消收藏', icon: 'success' });
  await load();
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.favorite-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
}

.favorite-card {
  display: flex;
  gap: $space-3;
  padding: $space-3;
  border-radius: $radius-lg;
  background: $bg-card;
}

.favorite-card--unavailable {
  opacity: 0.58;
}

.favorite-card__image {
  width: 180rpx;
  height: 180rpx;
  flex: none;
  border-radius: $radius-md;
  background: $bg-page;
}

.favorite-card__body {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
  justify-content: space-between;
}

.favorite-card__name {
  display: -webkit-box;
  overflow: hidden;
  font-weight: 600;
  line-height: 1.4;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.favorite-card__status {
  color: $text-3;
  font-size: $font-note;
}

.favorite-card__bottom {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.favorite-card__price {
  color: $danger;
  font-size: $font-heading;
  font-weight: 600;
}

.favorite-card__remove {
  color: $text-2;
  font-size: $font-note;
}

.empty {
  padding: $space-6 $space-4;
  text-align: center;
}

.empty__title {
  display: block;
  font-size: $font-heading;
  font-weight: 600;
}

.empty__desc {
  display: block;
  margin-top: $space-2;
  color: $text-2;
  font-size: $font-sub;
}
</style>
