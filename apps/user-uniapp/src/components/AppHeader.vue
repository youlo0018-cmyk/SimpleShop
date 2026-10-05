<template>
  <view class="header">
    <view v-if="back" class="header__back" @tap="goBack">‹</view>
    <view class="header__body">
      <text class="header__title">{{ title }}</text>
      <text v-if="subtitle" class="header__subtitle">{{ subtitle }}</text>
    </view>
    <view class="header__slot">
      <slot />
    </view>
  </view>
</template>

<script setup lang="ts">
defineProps<{
  title: string;
  subtitle?: string;
  back?: boolean;
}>();

function goBack() {
  const pages = getCurrentPages();
  if (pages.length > 1) uni.navigateBack();
  else uni.switchTab({ url: '/pages/index/index' });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.header {
  display: flex;
  align-items: center;
  gap: $space-2;
  min-height: 104rpx;
  padding: calc(env(safe-area-inset-top) + 16rpx) 0 12rpx;
}

.header__back {
  width: 64rpx;
  height: 64rpx;
  border-radius: 50%;
  background: #fff;
  color: $text-1;
  font-size: 48rpx;
  line-height: 58rpx;
  text-align: center;
}

.header__body {
  flex: 1;
  min-width: 0;
}

.header__title {
  display: block;
  overflow: hidden;
  font-size: 40rpx;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.header__subtitle {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}
</style>
