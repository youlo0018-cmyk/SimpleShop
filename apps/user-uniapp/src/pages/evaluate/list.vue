<template>
  <view class="page evaluate-page">
    <AppHeader :title="productId ? '商品评价' : '我的评价'" :subtitle="`${total} 条`" back />
    <view v-if="loading" class="loading">正在加载评价…</view>
    <view v-else-if="items.length" class="evaluate-list">
      <view v-for="item in items" :key="item.evaluateId" class="evaluate-card">
        <view class="evaluate-card__head">
          <text>{{ item.customerName || '匿名用户' }}</text>
          <text class="evaluate-card__score">{{ score(item.starScore) }} 分</text>
        </view>
        <text class="evaluate-card__content">{{ item.content }}</text>
        <view v-if="item.images?.length" class="evaluate-card__images">
          <image v-for="image in item.images" :key="image" :src="image" mode="aspectFill" />
        </view>
        <view v-for="reply in item.replies || []" :key="reply.replyId" class="reply">
          <text class="reply__name">{{ reply.replyTypeName || '商家回复' }}</text>
          <text class="reply__content">{{ reply.replyContent }}</text>
        </view>
      </view>
    </view>
    <view v-else class="empty">没有评价</view>
  </view>
</template>

<script setup lang="ts">
import { onLoad } from '@dcloudio/uni-app';
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { score } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const productId = ref('');
const loading = ref(true);
const total = ref(0);
const items = ref<any[]>([]);

async function load() {
  session.restore();
  loading.value = true;
  try {
    if (productId.value) {
      const data = await request<any>('/gateway/evaluates/List', {
        method: 'POST',
        auth: false,
        data: { spuId: productId.value, skuId: '0', page: 1, pageSize: 50 },
        silent: true,
      });
      items.value = data?.items || [];
      total.value = Number(data?.total || 0);
      return;
    }

    if (!session.profile?.customerId) {
      uni.navigateTo({ url: '/pages/login/index' });
      return;
    }
    const data = await request<any>('/gateway/evaluates/My', {
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

onLoad((options) => {
  productId.value = String(options?.productId || '');
  load();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.evaluate-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
}

.evaluate-card {
  padding: $space-4;
  border-radius: $radius-lg;
  background: #fff;
}

.evaluate-card__head {
  display: flex;
  justify-content: space-between;
  font-size: $font-sub;
}

.evaluate-card__score {
  color: $warning;
}

.evaluate-card__content {
  display: block;
  margin-top: $space-3;
  line-height: 1.6;
}

.evaluate-card__images {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: $space-2;
  margin-top: $space-3;
}

.evaluate-card__images image {
  width: 100%;
  height: 200rpx;
  border-radius: $radius-sm;
}

.reply {
  margin-top: $space-3;
  padding: $space-3;
  border-radius: $radius-sm;
  background: $bg-page;
}

.reply__name {
  display: block;
  color: $brand;
  font-size: $font-note;
}

.reply__content {
  display: block;
  margin-top: 6rpx;
  font-size: $font-sub;
}
</style>
