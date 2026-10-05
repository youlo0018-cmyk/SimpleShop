<template>
  <view class="product-card" @tap="open">
    <image
      class="product-card__image"
      :src="image"
      mode="aspectFill"
      @error="image = fallbackImage"
    />
    <view class="product-card__body">
      <text class="product-card__name">{{ product.spuName }}</text>
      <text v-if="product.subTitle" class="product-card__sub">{{ product.subTitle }}</text>
      <view class="product-card__price">
        <text class="product-card__amount">¥{{ amount(product.finalPrice) }}</text>
        <text v-if="Number(product.originalPrice || 0) > Number(product.finalPrice || 0)" class="product-card__original">
          ¥{{ amount(product.originalPrice) }}
        </text>
      </view>
      <view class="product-card__meta">
        <text>{{ score(product.evaluationScore) }} 分</text>
        <text>已售 {{ product.sales || 0 }}</text>
      </view>
    </view>
  </view>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { amount, score } from '@/core/format';

const props = defineProps<{ product: any }>();
const fallbackImage = '/static/images/product.svg';
const image = ref(props.product.mainImage || fallbackImage);

function open() {
  uni.navigateTo({ url: `/pages/product/detail?id=${props.product.productId}` });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.product-card {
  overflow: hidden;
  border-radius: $radius-md;
  background: #fff;
  box-shadow: $shadow-card;
}

.product-card__image {
  width: 100%;
  height: 320rpx;
  background: $bg-page;
}

.product-card__body {
  padding: $space-2;
}

.product-card__name {
  display: -webkit-box;
  overflow: hidden;
  font-size: 28rpx;
  font-weight: 600;
  line-height: 1.35;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.product-card__sub {
  display: block;
  margin-top: 6rpx;
  overflow: hidden;
  color: $text-2;
  font-size: $font-note;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.product-card__price {
  display: flex;
  align-items: baseline;
  gap: $space-2;
  margin-top: $space-2;
}

.product-card__amount {
  color: $brand;
  font-size: 34rpx;
  font-weight: 600;
}

.product-card__original {
  color: $text-3;
  font-size: $font-note;
  text-decoration: line-through;
}

.product-card__meta {
  display: flex;
  justify-content: space-between;
  margin-top: $space-1;
  color: $text-3;
  font-size: 20rpx;
}
</style>
