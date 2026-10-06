<template>
  <view class="page evaluate-page">
    <AppHeader :title="title" :subtitle="subtitle" back>
      <text v-if="!productId && !orderNo && session.loggedIn" class="header-action" @tap="pickEvaluable">
        去评价
      </text>
    </AppHeader>

    <view v-if="loading" class="loading">正在加载…</view>

    <view v-else-if="orderNo" class="evaluable-list">
      <view v-for="item in evaluableItems" :key="item.spuId" class="evaluable-card">
        <image class="evaluable-card__image" :src="item.mainImage || placeholder" mode="aspectFill" />
        <view class="evaluable-card__body">
          <text class="evaluable-card__name">{{ item.spuName }}</text>
          <text class="evaluable-card__spec">{{ item.skuSpecs }}</text>
        </view>
        <button v-if="!item.evaluated" class="button-mini" @tap="toPublish(item)">去评价</button>
        <text v-else class="evaluable-card__done">已评价</text>
      </view>
      <view v-if="!evaluableItems.length" class="empty">这一单没有可评价的商品</view>
    </view>

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
          <text class="reply__content">{{ reply.content }}</text>
        </view>
        <view v-for="append in item.appends || []" :key="append.appendId" class="append">
          <text class="append__title">追评 {{ append.starScore ? `${append.starScore} 分` : '' }}</text>
          <text class="append__content">{{ append.content }}</text>
          <view v-if="append.images?.length" class="evaluate-card__images">
            <image v-for="image in append.images" :key="image" :src="image" mode="aspectFill" />
          </view>
        </view>
        <view v-if="!productId && !orderNo" class="evaluate-card__actions">
          <text class="evaluate-card__action" @tap="toAppend(item)">追评</text>
        </view>
      </view>
    </view>

    <view v-else class="empty">没有评价</view>
  </view>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { score } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const productId = ref('');
const orderNo = ref('');
const loading = ref(true);
const total = ref(0);
const items = ref<any[]>([]);
const evaluableItems = ref<any[]>([]);
const placeholder = 'https://cdn.example.com/main.png';

const title = computed(() => {
  if (productId.value) return '商品评价';
  if (orderNo.value) return '订单评价';
  return '我的评价';
});
const subtitle = computed(() => (orderNo.value ? `${evaluableItems.value.length} 件商品` : `${total.value} 条`));

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

    if (orderNo.value) {
      evaluableItems.value = await request<any[]>('/gateway/evaluates/Evaluable', {
        method: 'POST',
        data: { customerId: session.profile.customerId, orderNo: orderNo.value },
        silent: true,
      }) || [];
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

function toPublish(item: any) {
  uni.navigateTo({
    url: `/pages/evaluate/form?orderNo=${item.orderNo}&spuId=${item.spuId}&spuName=${encodeURIComponent(item.spuName || '')}`,
  });
}

function toAppend(item: any) {
  uni.navigateTo({
    url: `/pages/evaluate/form?evaluateId=${item.evaluateId}&spuName=${encodeURIComponent(item.spuName || '')}`,
  });
}

async function pickEvaluable() {
  const data = await request<any[]>('/gateway/evaluates/Evaluable', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, orderNo: '' },
    silent: true,
  }) || [];
  const pending = data.filter((item: any) => !item.evaluated);
  if (!pending.length) {
    uni.showToast({ title: '没有待评价的商品', icon: 'none' });
    return;
  }

  uni.showActionSheet({
    itemList: pending.slice(0, 10).map((item: any) => `${item.spuName}（${item.skuSpecs}）`),
    success: (result) => toPublish(pending[result.tapIndex]),
  });
}

onLoad((options) => {
  productId.value = String(options?.productId || '');
  orderNo.value = String(options?.orderNo || '');
  load();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.header-action {
  color: $brand;
  font-size: $font-sub;
}

.evaluate-list,
.evaluable-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
}

.evaluate-card,
.evaluable-card {
  padding: $space-4;
  border-radius: $radius-lg;
  background: $bg-card;
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

.evaluate-card__actions {
  display: flex;
  justify-content: flex-end;
  margin-top: $space-3;
  padding-top: $space-3;
  border-top: 1rpx solid $hairline;
}

.evaluate-card__action {
  color: $brand;
  font-size: $font-note;
}

.reply,
.append {
  margin-top: $space-3;
  padding: $space-3;
  border-radius: $radius-sm;
  background: $bg-page;
}

.reply__name,
.append__title {
  display: block;
  color: $brand;
  font-size: $font-note;
}

.reply__content,
.append__content {
  display: block;
  margin-top: 6rpx;
  font-size: $font-sub;
}

.evaluable-card {
  display: flex;
  align-items: center;
  gap: $space-3;
}

.evaluable-card__image {
  width: 140rpx;
  height: 140rpx;
  flex: none;
  border-radius: $radius-md;
  background: $bg-page;
}

.evaluable-card__body {
  flex: 1;
  min-width: 0;
}

.evaluable-card__name {
  display: block;
  font-weight: 600;
}

.evaluable-card__spec {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.evaluable-card__done {
  color: $text-3;
  font-size: $font-note;
}
</style>
