<template>
  <view class="page home">
    <AppHeader title="SimpleShop" :subtitle="mallName" />
    <view v-if="loading" class="loading">正在加载…</view>
    <template v-else>
      <DesignComponent
        v-for="component in components"
        :key="component.id"
        :component="component"
        :products="products"
      />
    </template>
    <BottomNav active="home" />
  </view>
</template>

<script setup lang="ts">
import { onLoad, onPullDownRefresh } from '@dcloudio/uni-app';
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import BottomNav from '@/components/BottomNav.vue';
import DesignComponent from '@/components/DesignComponent.vue';
import { request } from '@/core/http';
import { getPlatformCode } from '@/core/session-storage';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const products = ref<any[]>([]);
const components = ref<any[]>([
  { id: 'search', type: 'searchBar', props: {} },
  { id: 'banner', type: 'banner', props: { title: '今日精选' } },
  { id: 'seckill', type: 'seckillZone', props: {} },
  { id: 'title', type: 'title', props: { title: '推荐商品' } },
  { id: 'products', type: 'productGrid', props: {} },
]);
const mallName = ref('多商户商城');

async function load() {
  loading.value = true;
  try {
    session.restore();
    const platformCode = getPlatformCode();
    const [design, list] = await Promise.all([
      request<any>('/gateway/design/PlatformStore', {
        auth: false,
        params: { platformCode },
        silent: true,
      }).catch(() => null),
      request<any>('/gateway/shop/products/List', {
        method: 'POST',
        auth: false,
        data: {
          customerId: session.profile?.customerId || '0',
          page: 1,
          pageSize: 10,
        },
        silent: true,
      }).catch(() => ({ items: [] })),
    ]);
    products.value = list?.items || [];
    if (design?.configJson) {
      const config = typeof design.configJson === 'string'
        ? JSON.parse(design.configJson)
        : design.configJson;
      mallName.value = config?.mallName || mallName.value;
      const designed = config?.pages?.index?.components;
      if (Array.isArray(designed) && designed.length) components.value = designed;
    }
  } finally {
    loading.value = false;
    uni.stopPullDownRefresh();
  }
}

onLoad(load);
onPullDownRefresh(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.home {
  padding-bottom: 180rpx;
}
</style>
