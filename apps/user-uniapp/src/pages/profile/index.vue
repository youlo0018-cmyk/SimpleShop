<template>
  <view class="page profile-page">
    <view class="profile-hero">
      <view class="profile-hero__avatar">{{ initial }}</view>
      <view class="profile-hero__body">
        <text class="profile-hero__name">{{ session.displayName }}</text>
        <text class="profile-hero__desc">{{ session.loggedIn ? '欢迎回来' : '登录后管理订单与权益' }}</text>
      </view>
      <text v-if="!session.loggedIn" class="profile-hero__login" @tap="toLogin">登录</text>
    </view>

    <view class="panel service-grid">
      <view class="service-grid__item" @tap="toOrders">
        <view class="service-grid__icon">单</view>
        <text>我的订单</text>
      </view>
      <view class="service-grid__item" @tap="toCoupons">
        <view class="service-grid__icon">券</view>
        <text>我的券包</text>
      </view>
      <view class="service-grid__item" @tap="toPoints">
        <view class="service-grid__icon">积</view>
        <text>积分中心</text>
      </view>
      <view class="service-grid__item" @tap="toEvaluates">
        <view class="service-grid__icon">评</view>
        <text>我的评价</text>
      </view>
    </view>

    <view class="panel settings">
      <view class="settings__row" @tap="toCart">
        <text>购物车</text>
        <text class="settings__arrow">›</text>
      </view>
      <view class="settings__row" @tap="toCouponCenter">
        <text>领券中心</text>
        <text class="settings__arrow">›</text>
      </view>
      <view class="settings__row" @tap="toSearch">
        <text>搜索商品</text>
        <text class="settings__arrow">›</text>
      </view>
      <view v-if="session.loggedIn" class="settings__row settings__row--danger" @tap="logout">
        <text>退出登录</text>
        <text class="settings__arrow">›</text>
      </view>
    </view>

    <BottomNav active="profile" />
  </view>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import BottomNav from '@/components/BottomNav.vue';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
session.restore();

const initial = computed(() => (session.displayName || '游').slice(0, 1));

function requireLogin() {
  if (session.loggedIn) return true;
  uni.navigateTo({ url: '/pages/login/index' });
  return false;
}

function toOrders() {
  if (requireLogin()) uni.navigateTo({ url: '/pages/order/list' });
}
function toCart() {
  if (requireLogin()) uni.navigateTo({ url: '/pages/cart/index' });
}
function toCoupons() {
  if (requireLogin()) uni.navigateTo({ url: '/pages/coupon/mine' });
}
function toPoints() {
  if (requireLogin()) uni.navigateTo({ url: '/pages/points/index' });
}
function toEvaluates() {
  if (requireLogin()) uni.navigateTo({ url: '/pages/evaluate/list' });
}
function toCouponCenter() {
  uni.navigateTo({ url: '/pages/coupon/center' });
}
function toSearch() {
  uni.navigateTo({ url: '/pages/search/index' });
}
function toLogin() {
  uni.navigateTo({ url: '/pages/login/index' });
}
function logout() {
  session.logout();
  uni.showToast({ title: '已退出登录', icon: 'none' });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.profile-page {
  padding-bottom: 180rpx;
}

.profile-hero {
  display: flex;
  align-items: center;
  gap: $space-3;
  padding: calc(env(safe-area-inset-top) + 40rpx) $space-4 $space-5;
}

.profile-hero__avatar {
  width: 112rpx;
  height: 112rpx;
  border-radius: 50%;
  background: $brand;
  color: #fff;
  font-size: 48rpx;
  font-weight: 600;
  line-height: 112rpx;
  text-align: center;
}

.profile-hero__body {
  flex: 1;
}

.profile-hero__name {
  display: block;
  font-size: 40rpx;
  font-weight: 600;
}

.profile-hero__desc {
  display: block;
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-sub;
}

.profile-hero__login {
  color: $brand;
  font-size: $font-sub;
}

.service-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  padding: $space-4;
}

.service-grid__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: $space-1;
  color: $text-2;
  font-size: $font-note;
}

.service-grid__icon {
  width: 80rpx;
  height: 80rpx;
  border-radius: 24rpx;
  background: #eef4ff;
  color: $brand;
  font-size: 30rpx;
  font-weight: 600;
  line-height: 80rpx;
  text-align: center;
}

.settings {
  margin-top: $space-3;
  padding: 0 $space-4;
}

.settings__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 96rpx;
  border-bottom: 1rpx solid $hairline;
}

.settings__row:last-child {
  border-bottom: 0;
}

.settings__row--danger {
  color: $danger;
}

.settings__arrow {
  color: $text-3;
}
</style>
