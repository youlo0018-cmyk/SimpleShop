<template>
  <view class="page search-page">
    <AppHeader title="搜索" :subtitle="`${total} 件商品`" back>
      <input
        v-model="keyword"
        class="search-input"
        confirm-type="search"
        placeholder="搜索商品"
        @confirm="search"
      />
    </AppHeader>
    <view class="search-button" @tap="search">搜索</view>
    <view v-if="loading" class="loading">正在搜索…</view>
    <view v-else-if="items.length" class="product-grid">
      <ProductCard v-for="item in items" :key="item.productId" :product="item" />
    </view>
    <view v-else class="empty">没有找到商品，换个关键词试试</view>
  </view>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import ProductCard from '@/components/ProductCard.vue';
import { request } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const keyword = ref('');
const loading = ref(false);
const items = ref<any[]>([]);
const total = ref(0);

async function search() {
  loading.value = true;
  try {
    const result = await request<any>('/gateway/shop/products/Search', {
      method: 'POST',
      auth: false,
      data: {
        customerId: session.profile?.customerId || '0',
        keyword: keyword.value.trim(),
        page: 1,
        pageSize: 30,
      },
    });
    items.value = result?.items || [];
    total.value = Number(result?.total || 0);
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.search-input {
  flex: 1;
  height: 68rpx;
  padding: 0 $space-3;
  border-radius: 34rpx;
  background: #fff;
  font-size: $font-sub;
}

.search-button {
  margin: $space-3 0;
  padding: 20rpx 0;
  border-radius: 40rpx;
  background: $brand;
  color: #fff;
  text-align: center;
}

.product-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: $space-2;
}
</style>
