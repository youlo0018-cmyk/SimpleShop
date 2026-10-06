<template>
  <view class="page address-page">
    <AppHeader title="收货地址" :subtitle="`${items.length} 条`" back>
      <text class="header-action" @tap="toCreate">新增</text>
    </AppHeader>

    <view v-if="loading" class="loading">正在加载…</view>
    <view v-else-if="items.length" class="address-list">
      <view
        v-for="item in items"
        :key="item.addressId"
        class="address-card"
        @tap="selectOrEdit(item)"
      >
        <view class="address-card__top">
          <text class="address-card__name">{{ item.consigneeName }}</text>
          <text class="address-card__phone">{{ item.consigneePhone }}</text>
          <text v-if="item.isDefault" class="address-card__default">默认</text>
          <text v-if="item.label" class="address-card__label">{{ item.label }}</text>
        </view>
        <text class="address-card__region">{{ item.regionPath }} {{ item.detailAddress }}</text>
        <view class="address-card__actions">
          <text v-if="!item.isDefault" class="address-card__action" @tap.stop="setDefault(item)">
            设为默认
          </text>
          <text class="address-card__action" @tap.stop="toEdit(item)">编辑</text>
          <text class="address-card__action address-card__action--danger" @tap.stop="remove(item)">
            删除
          </text>
        </view>
      </view>
    </view>
    <view v-else class="empty">
      <text class="empty__title">还没有收货地址</text>
      <text class="empty__desc">新增一条地址，下单时就能直接选择</text>
    </view>

    <button class="button-primary address-page__add" @tap="toCreate">新增收货地址</button>
  </view>
</template>

<script setup lang="ts">
import { onLoad, onShow } from '@dcloudio/uni-app';
import { ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const items = ref<any[]>([]);
const selecting = ref(false);

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }

  loading.value = true;
  try {
    const data = await request<any>('/gateway/customers/addresses/List', {
      method: 'POST',
      data: { customerId: session.profile.customerId, page: 1, pageSize: 100 },
      silent: true,
    });
    items.value = data?.items || [];
  } finally {
    loading.value = false;
  }
}

function toCreate() {
  uni.navigateTo({ url: '/pages/address/form' });
}

function toEdit(item: any) {
  uni.navigateTo({ url: `/pages/address/form?addressId=${item.addressId}` });
}

function selectOrEdit(item: any) {
  if (!selecting.value) {
    toEdit(item);
    return;
  }

  uni.setStorageSync('simpleshop_selected_address', item);
  uni.navigateBack();
}

async function setDefault(item: any) {
  await request('/gateway/customers/addresses/SetDefault', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, addressId: item.addressId },
  });
  uni.showToast({ title: '已设为默认', icon: 'success' });
  await load();
}

async function remove(item: any) {
  const confirmed = await new Promise<boolean>((resolve) => {
    uni.showModal({
      title: '删除地址',
      content: `确定删除「${item.consigneeName} ${item.regionPath}」这条地址吗？`,
      success: (result) => resolve(result.confirm),
      fail: () => resolve(false),
    });
  });
  if (!confirmed) return;

  await request('/gateway/customers/addresses/Delete', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, addressId: item.addressId },
  });
  uni.showToast({ title: '已删除', icon: 'success' });
  await load();
}

onLoad((options) => {
  selecting.value = String(options?.select || '') === '1';
});

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.address-page {
  padding-bottom: 180rpx;
}

.header-action {
  color: $brand;
  font-size: $font-sub;
}

.address-list {
  display: flex;
  flex-direction: column;
  gap: $space-3;
}

.address-card {
  padding: $space-4;
  border-radius: $radius-lg;
  background: $bg-card;
}

.address-card__top {
  display: flex;
  align-items: center;
  gap: $space-2;
}

.address-card__name {
  font-weight: 600;
}

.address-card__phone {
  color: $text-2;
  font-size: $font-sub;
}

.address-card__default {
  padding: 2rpx 12rpx;
  border-radius: $radius-sm;
  background: rgba(0, 113, 227, 0.1);
  color: $brand;
  font-size: $font-note;
}

.address-card__label {
  padding: 2rpx 12rpx;
  border-radius: $radius-sm;
  background: $bg-page;
  color: $text-2;
  font-size: $font-note;
}

.address-card__region {
  display: block;
  margin-top: $space-2;
  color: $text-2;
  font-size: $font-sub;
  line-height: 1.5;
}

.address-card__actions {
  display: flex;
  justify-content: flex-end;
  gap: $space-4;
  margin-top: $space-3;
  padding-top: $space-3;
  border-top: 1rpx solid $hairline;
}

.address-card__action {
  color: $text-2;
  font-size: $font-note;
}

.address-card__action--danger {
  color: $danger;
}

.empty {
  padding: $space-6 $space-4;
  text-align: center;
}

.empty__title {
  display: block;
  font-size: $font-heading;
  font-weight: 600;
}

.empty__desc {
  display: block;
  margin-top: $space-2;
  color: $text-2;
  font-size: $font-sub;
}

.address-page__add {
  position: fixed;
  right: $space-4;
  bottom: calc(env(safe-area-inset-bottom) + $space-4);
  left: $space-4;
}
</style>
