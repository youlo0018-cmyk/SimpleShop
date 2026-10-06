<template>
  <view class="page seckill-page">
    <AppHeader title="限时抢购" subtitle="库存独立划出，售完即止" back />
    <view v-if="loading" class="loading">正在加载场次…</view>
    <view v-else-if="sessions.length" class="session-list">
      <view v-for="session in sessions" :key="session.sessionId" class="session">
        <view class="session__head">
          <view>
            <text class="session__name">{{ session.sessionName }}</text>
            <text class="session__time">{{ dateTime(session.startTime) }} 开始</text>
          </view>
          <text class="session__countdown">{{ countdown(session) }}</text>
        </view>
        <view class="session__items">
          <view
            v-for="item in session.items"
            :key="item.itemId"
            class="seckill-item"
            @tap="toProduct(item.spuId)"
          >
            <image
              class="seckill-item__image"
              :src="item.image || fallbackImage"
              mode="aspectFill"
              @error="item.image = fallbackImage"
            />
            <view class="seckill-item__body">
              <text class="seckill-item__name">{{ item.productName }}</text>
              <text class="seckill-item__spec">{{ item.skuSpecText }}</text>
              <view class="seckill-item__price">
                <text class="amount">¥{{ amount(item.seckillPrice) }}</text>
                <text class="seckill-item__original">¥{{ amount(item.originalPrice) }}</text>
              </view>
              <text class="seckill-item__stock">剩余 {{ item.remaining }} 件 · 每人限 {{ item.perUserLimit }} 件</text>
              <button
                class="button-mini seckill-item__grab"
                :loading="grabbingItemId === String(item.itemId)"
                @tap.stop="grab(item)"
              >
                立即抢购
              </button>
            </view>
          </view>
        </view>
      </view>
    </view>
    <view v-else class="empty">当前没有进行中的秒杀场次</view>
  </view>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { amount, dateTime } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const sessions = ref<any[]>([]);
const grabbingItemId = ref('');
const fallbackImage = '/static/images/product.svg';

function countdown(session: any) {
  if (Number(session.secondsToStart) > 0) return `${session.secondsToStart} 秒后开始`;
  if (Number(session.secondsToEnd) > 0) return `${session.secondsToEnd} 秒后结束`;
  return '已结束';
}

function toProduct(spuId: string) {
  uni.navigateTo({ url: `/pages/product/detail?id=${spuId}` });
}

async function load() {
  loading.value = true;
  try {
    sessions.value = await request<any[]>('/gateway/marketing/seckill/sessions/Public', {
      method: 'POST',
      auth: false,
      data: {},
      silent: true,
    });
  } finally {
    loading.value = false;
  }
}

async function grab(item: any) {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }

  const addresses = await request<any>('/gateway/customers/addresses/List', {
    method: 'POST',
    data: { customerId: session.profile.customerId, page: 1, pageSize: 100 },
    silent: true,
  }).catch(() => null);
  const address = (addresses?.items || []).find((entry: any) => entry.isDefault) || (addresses?.items || [])[0];
  if (!address) {
    uni.showToast({ title: '请先添加收货地址', icon: 'none' });
    setTimeout(() => uni.navigateTo({ url: '/pages/address/list?select=1' }), 500);
    return;
  }

  grabbingItemId.value = String(item.itemId);
  try {
    const result = await request<any>('/gateway/marketing/seckill/grab', {
      method: 'POST',
      data: {
        itemId: item.itemId,
        customerId: session.profile.customerId,
        receiverName: address.consigneeName,
        receiverPhone: address.consigneePhone,
        receiverAddress: `${address.regionPath} ${address.detailAddress}`,
        couponId: 0,
        pointsToUse: 0,
      },
    });

    if (Number(result?.resultStatus) === 1 && result?.orderNo) {
      uni.showToast({ title: '抢购成功', icon: 'success' });
      setTimeout(() => {
        uni.navigateTo({ url: `/pages/order/detail?orderNo=${result.orderNo}` });
      }, 600);
      return;
    }

    uni.showToast({ title: result?.message || '抢购失败', icon: 'none' });
  } finally {
    grabbingItemId.value = '';
  }
}

onLoad(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.seckill-page {
  padding-bottom: 100rpx;
}

.session {
  margin-bottom: $space-4;
}

.session__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: $space-2;
}

.session__name {
  display: block;
  font-size: 36rpx;
  font-weight: 600;
}

.session__time {
  display: block;
  margin-top: 4rpx;
  color: $text-2;
  font-size: $font-note;
}

.session__countdown {
  color: $danger;
  font-size: $font-note;
}

.session__items {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: $space-2;
}

.seckill-item {
  overflow: hidden;
  border-radius: $radius-md;
  background: #fff;
}

.seckill-item__image {
  width: 100%;
  height: 300rpx;
}

.seckill-item__body {
  padding: $space-2;
}

.seckill-item__name {
  display: -webkit-box;
  overflow: hidden;
  font-size: 28rpx;
  font-weight: 600;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.seckill-item__spec,
.seckill-item__stock {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.seckill-item__price {
  display: flex;
  align-items: baseline;
  gap: $space-2;
  margin-top: $space-1;
}

.seckill-item__original {
  color: $text-3;
  font-size: $font-note;
  text-decoration: line-through;
}

.seckill-item__grab {
  width: 100%;
  margin-top: $space-2;
}
</style>
