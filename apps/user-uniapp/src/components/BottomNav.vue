<template>
  <view class="bottom-nav">
    <view
      v-for="item in items"
      :key="item.url"
      class="bottom-nav__item"
      :class="{ 'bottom-nav__item--active': active === item.key }"
      @tap="go(item)"
    >
      <image class="bottom-nav__icon" :src="`/static/icons/${item.key}.svg`" mode="aspectFit" />
      <text>{{ item.label }}</text>
    </view>
  </view>
</template>

<script setup lang="ts">
defineProps<{ active: string }>();

const items = [
  { key: 'home', label: '首页', url: '/pages/index/index' },
  { key: 'category', label: '分类', url: '/pages/category/index' },
  { key: 'mall', label: '商城', url: '/pages/mall/index' },
  { key: 'seckill', label: '秒杀', url: '/pages/seckill/index' },
  { key: 'profile', label: '我的', url: '/pages/profile/index' },
];

function go(item: { url: string }) {
  uni.reLaunch({ url: item.url });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.bottom-nav {
  position: fixed;
  right: 0;
  bottom: 0;
  left: 0;
  z-index: 20;
  display: flex;
  padding: 12rpx 12rpx calc(env(safe-area-inset-bottom) + 8rpx);
  background: rgba(255, 255, 255, 0.96);
  box-shadow: 0 -1rpx 0 $hairline;
}

.bottom-nav__item {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6rpx;
  color: $text-2;
  font-size: 21rpx;
}

.bottom-nav__item--active {
  color: $brand;
  font-weight: 600;
}

.bottom-nav__icon {
  width: 34rpx;
  height: 34rpx;
}
</style>
