<template>
  <view class="page store-page">
    <AppHeader :title="shopName" subtitle="店铺主页" back />
    <view v-if="loading" class="loading">正在加载店铺…</view>
    <template v-else>
      <DesignComponent
        v-for="component in components"
        :key="component.id"
        :component="component"
        :products="products"
      />
    </template>
  </view>
</template>

<script setup lang="ts">
import { onLoad } from '@dcloudio/uni-app';
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import DesignComponent from '@/components/DesignComponent.vue';
import { request } from '@/core/http';

const loading = ref(true);
const shopName = ref('店铺');
const components = ref<any[]>([
  { id: 'header', type: 'shopHeader', props: { shopName: '店铺', description: '欢迎光临' } },
  { id: 'products', type: 'productGrid', props: {} },
]);
const products = ref<any[]>([]);
const merchantId = ref('');

async function load() {
  loading.value = true;
  try {
    const design = await request<any>('/gateway/design/Store', {
      auth: false,
      params: { merchantId: merchantId.value },
      silent: true,
    });
    if (design?.configJson) {
      const config = typeof design.configJson === 'string'
        ? JSON.parse(design.configJson)
        : design.configJson;
      shopName.value = config?.shopName || design.shopName || shopName.value;
      const designed = config?.pages?.store?.components;
      if (Array.isArray(designed) && designed.length) components.value = designed;
    }
  } catch {
    // 未配置装修或商户尚未营业时展示默认店铺骨架。
  } finally {
    loading.value = false;
  }
}

onLoad((options) => {
  merchantId.value = String(options?.merchantId || '');
  load();
});
</script>
