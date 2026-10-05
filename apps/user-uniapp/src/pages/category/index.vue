<template>
  <view class="page category-page">
    <AppHeader title="分类" subtitle="按类目浏览商品" />
    <view class="category-layout">
      <scroll-view class="category-layout__side" scroll-y>
        <view
          v-for="item in categories"
          :key="item.id"
          class="category-tab"
          :class="{ 'category-tab--active': String(activeId) === String(item.id) }"
          @tap="selectCategory(item)"
        >
          {{ item.categoryName }}
        </view>
      </scroll-view>
      <scroll-view class="category-layout__main" scroll-y>
        <view v-if="children.length" class="category-children">
          <view
            v-for="item in children"
            :key="item.id"
            class="category-child"
            @tap="selectCategory(item)"
          >
            <text>{{ item.categoryName }}</text>
            <text class="category-child__arrow">›</text>
          </view>
        </view>
        <view class="product-grid">
          <ProductCard v-for="item in products" :key="item.productId" :product="item" />
        </view>
        <view v-if="!products.length && !loading" class="empty">该分类暂时没有商品</view>
      </scroll-view>
    </view>
    <BottomNav active="category" />
  </view>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import BottomNav from '@/components/BottomNav.vue';
import ProductCard from '@/components/ProductCard.vue';
import { request } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const categories = ref<any[]>([]);
const activeId = ref('');
const products = ref<any[]>([]);
const loading = ref(true);

const activeNode = computed(() =>
  categories.value.find((item) => String(item.id) === String(activeId.value)),
);
const children = computed(() => activeNode.value?.children || []);

async function load() {
  loading.value = true;
  try {
    categories.value = await request<any[]>('/gateway/shop/catalog/CategoryTree', {
      auth: false,
      silent: true,
    });
    if (categories.value.length) await selectCategory(categories.value[0]);
  } finally {
    loading.value = false;
  }
}

async function selectCategory(item: any) {
  activeId.value = item.id;
  const data = await request<any>('/gateway/shop/products/List', {
    method: 'POST',
    auth: false,
    data: {
      customerId: session.profile?.customerId || '0',
      categoryId: item.id,
      page: 1,
      pageSize: 30,
    },
    silent: true,
  });
  products.value = data?.items || [];
}

onLoad(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.category-page {
  display: flex;
  flex-direction: column;
  height: 100vh;
  padding-bottom: 0;
}

.category-layout {
  flex: 1;
  display: flex;
  min-height: 0;
}

.category-layout__side {
  width: 180rpx;
  margin-right: $space-2;
}

.category-layout__main {
  flex: 1;
  min-width: 0;
  padding-bottom: 160rpx;
}

.category-tab {
  padding: $space-3 $space-2;
  border-radius: $radius-sm;
  color: $text-2;
  font-size: $font-sub;
}

.category-tab--active {
  background: #fff;
  color: $brand;
  font-weight: 600;
}

.category-children {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: $space-2;
  margin-bottom: $space-3;
}

.category-child {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: $space-2;
  border-radius: $radius-sm;
  background: #fff;
  font-size: $font-note;
}

.category-child__arrow {
  color: $text-3;
}

.product-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: $space-2;
}
</style>
