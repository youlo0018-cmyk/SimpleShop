<template>
  <view class="page cart-page">
    <AppHeader title="购物车" :subtitle="`${items.length} 种商品`" back />
    <view v-if="loading" class="loading">正在加载购物车…</view>
    <template v-else-if="items.length">
      <view class="cart-list">
        <view v-for="item in items" :key="item.id" class="cart-item">
          <view class="cart-item__check" :class="{ 'cart-item__check--on': item.checked }" @tap="toggle(item)">
            <text v-if="item.checked">✓</text>
          </view>
          <image class="cart-item__image" :src="item.image || fallbackImage" mode="aspectFill" @error="item.image = fallbackImage" />
          <view class="cart-item__body" :class="{ 'cart-item__body--unavailable': item.isAvailable === false }">
            <text class="cart-item__name">{{ item.skuName }}</text>
            <text class="cart-item__spec">{{ item.skuSpecText }}</text>
            <text v-if="item.isAvailable === false" class="cart-item__reason">
              {{ item.unavailableReason || '已失效' }}
            </text>
            <view class="cart-item__foot">
              <text class="amount">¥{{ amount(item.price) }}</text>
              <view class="stepper">
                <text class="stepper__button" @tap="changeQuantity(item, -1)">−</text>
                <text class="stepper__value">{{ item.quantity }}</text>
                <text class="stepper__button" @tap="changeQuantity(item, 1)">＋</text>
              </view>
            </view>
          </view>
        </view>
      </view>
      <view class="cart-footer">
        <view>
          <text class="cart-footer__label">已选 {{ selectedCount }} 件</text>
          <text class="cart-footer__amount">合计 ¥{{ amount(total) }}</text>
        </view>
        <button class="cart-footer__button" :disabled="selectedCount === 0" @tap="checkout">去结算</button>
      </view>
    </template>
    <view v-else class="empty">购物车还是空的，去逛逛吧</view>
  </view>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { onShow } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { amount } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const items = ref<any[]>([]);
const fallbackImage = '/static/images/product.svg';

const selected = computed(() => items.value.filter((item) => item.checked));
const selectedCount = computed(() =>
  selected.value.reduce((sum, item) => sum + Number(item.quantity || 0), 0),
);
const total = computed(() =>
  selected.value.reduce((sum, item) => sum + Number(item.price || 0) * Number(item.quantity || 0), 0),
);

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  loading.value = true;
  try {
    items.value = await request<any[]>('/gateway/carts/List', {
      params: { customerId: session.profile.customerId },
      silent: true,
    });
  } finally {
    loading.value = false;
  }
}

async function toggle(item: any) {
  // 已下架 / 未过审 / 规格停用的行不给勾选。
  // 服务端在列表里已经强制把它置成未勾选，这里只是不让它在前端又被勾回去 ——
  // 否则用户会勾上一堆失效商品，看到一个总价，点结算却被下单接口拒。
  if (item.isAvailable === false) {
    uni.showToast({ title: item.unavailableReason || '该商品已失效', icon: 'none' });
    return;
  }
  item.checked = !item.checked;
  await request('/gateway/carts/SetChecked', {
    method: 'POST',
    data: {
      customerId: session.profile?.customerId,
      cartId: item.id,
      checked: item.checked,
    },
  });
}

async function changeQuantity(item: any, delta: number) {
  const quantity = Number(item.quantity) + delta;
  if (quantity < 0) return;
  item.quantity = quantity;
  await request('/gateway/carts/SetQuantity', {
    method: 'POST',
    data: {
      customerId: session.profile?.customerId,
      cartId: item.id,
      quantity,
    },
  });
  if (quantity === 0) await load();
}

function checkout() {
  uni.setStorageSync('simpleshop_checkout_lines', selected.value);
  uni.navigateTo({ url: '/pages/checkout/index' });
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.cart-page {
  padding-bottom: 180rpx;
}

.cart-list {
  display: flex;
  flex-direction: column;
  gap: $space-2;
}

.cart-item {
  display: flex;
  align-items: center;
  gap: $space-2;
  padding: $space-3;
  border-radius: $radius-md;
  background: #fff;
}

.cart-item__check {
  width: 40rpx;
  height: 40rpx;
  border: 2rpx solid $hairline;
  border-radius: 50%;
  color: #fff;
  font-size: 24rpx;
  line-height: 38rpx;
  text-align: center;
}

.cart-item__check--on {
  border-color: $brand;
  background: $brand;
}

.cart-item__image {
  width: 140rpx;
  height: 140rpx;
  border-radius: $radius-sm;
  background: $bg-page;
}

.cart-item__body {
  flex: 1;
  min-width: 0;
}

.cart-item__name {
  display: -webkit-box;
  overflow: hidden;
  font-size: 28rpx;
  font-weight: 600;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.cart-item__spec {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.cart-item__body--unavailable {
  opacity: 0.5;
}

.cart-item__reason {
  display: inline-block;
  margin-top: 6rpx;
  padding: 2rpx 10rpx;
  border-radius: $radius-sm;
  background: rgba(255, 59, 48, 0.1);
  color: $danger;
  font-size: $font-note;
}

.cart-item__foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: $space-2;
}

.stepper {
  display: flex;
  align-items: center;
  overflow: hidden;
  border-radius: 28rpx;
  background: $bg-page;
}

.stepper__button,
.stepper__value {
  width: 56rpx;
  height: 52rpx;
  font-size: 30rpx;
  line-height: 52rpx;
  text-align: center;
}

.cart-footer {
  position: fixed;
  right: 0;
  bottom: 0;
  left: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: $space-2 $space-3 calc(env(safe-area-inset-bottom) + 16rpx);
  background: rgba(255, 255, 255, 0.96);
  box-shadow: 0 -1rpx 0 $hairline;
}

.cart-footer__label {
  display: block;
  color: $text-2;
  font-size: $font-note;
}

.cart-footer__amount {
  display: block;
  margin-top: 4rpx;
  color: $danger;
  font-size: 34rpx;
  font-weight: 600;
}

.cart-footer__button {
  width: 220rpx;
  height: 80rpx;
  border-radius: 40rpx;
  background: $brand;
  color: #fff;
  font-size: $font-body;
  line-height: 80rpx;
}
</style>
