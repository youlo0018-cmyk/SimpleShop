<template>
  <view class="page points-page">
    <AppHeader title="积分中心" subtitle="签到赚积分，下单可抵扣" back />
    <view class="balance-card">
      <text class="balance-card__label">可用积分</text>
      <text class="balance-card__value">{{ balance.available || 0 }}</text>
      <text class="balance-card__expiring">
        {{ balance.expiringSoon || 0 }} 积分即将过期
      </text>
      <button class="balance-card__sign" :disabled="signed || signing" @tap="signIn">
        {{ signed ? '今日已签到' : '每日签到' }}
      </button>
    </view>

    <view class="panel rule-note">
      <text>积分抵扣是最后一道优惠：商品原价 → 活动 / 券 → 积分。</text>
      <text>抵扣上限为应付商品金额的 100%。</text>
    </view>

    <view class="section-title">积分流水</view>
    <view class="panel record-list">
      <view v-for="item in records" :key="item.id" class="record">
        <view>
          <text class="record__title">{{ item.actionName || item.action }}</text>
          <text class="record__time">{{ item.createdAt }}</text>
        </view>
        <text class="record__value" :class="{ 'record__value--plus': Number(item.quantity) > 0 }">
          {{ Number(item.quantity) > 0 ? '+' : '' }}{{ item.quantity }}
        </text>
      </view>
      <view v-if="!records.length" class="empty">还没有积分记录</view>
    </view>
  </view>
</template>

<script setup lang="ts">
import { onShow } from '@dcloudio/uni-app';
import { reactive, ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const records = ref<any[]>([]);
const signing = ref(false);
const signed = ref(false);
const balance = reactive({
  available: 0,
  expiringSoon: 0,
});

async function load() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  const customerId = session.profile.customerId;
  const [balanceData, recordData] = await Promise.all([
    request<any>('/gateway/points/Balance', {
      params: { customerId },
      auth: true,
      silent: true,
    }).catch(() => null),
    request<any[]>('/gateway/points/Records', {
      params: { customerId, page: 1, pageSize: 20 },
      auth: true,
      silent: true,
    }).catch(() => []),
  ]);
  Object.assign(balance, balanceData || {});
  records.value = recordData || [];
}

async function signIn() {
  if (!session.profile?.customerId || signing.value) return;
  signing.value = true;
  try {
    const result = await request<any>(
      `/gateway/points/SignIn?customerId=${session.profile.customerId}`,
      {
        method: 'POST',
        data: {},
      },
    );
    signed.value = true;
    uni.showToast({ title: result?.message || '签到成功', icon: 'success' });
    await load();
  } finally {
    signing.value = false;
  }
}

onShow(load);
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.points-page {
  padding-bottom: 80rpx;
}

.balance-card {
  position: relative;
  overflow: hidden;
  padding: $space-5;
  border-radius: 36rpx;
  background: linear-gradient(135deg, #0071e3, #55a8ff);
  color: #fff;
}

.balance-card__label,
.balance-card__value,
.balance-card__expiring {
  display: block;
}

.balance-card__label {
  opacity: 0.85;
  font-size: $font-sub;
}

.balance-card__value {
  margin-top: $space-1;
  font-size: 76rpx;
  font-weight: 600;
}

.balance-card__expiring {
  margin-top: $space-1;
  opacity: 0.82;
  font-size: $font-note;
}

.balance-card__sign {
  position: absolute;
  right: $space-4;
  bottom: $space-4;
  height: 68rpx;
  padding: 0 $space-4;
  border-radius: 34rpx;
  background: #fff;
  color: $brand;
  font-size: $font-sub;
  line-height: 68rpx;
}

.rule-note {
  display: flex;
  flex-direction: column;
  gap: $space-1;
  margin-top: $space-3;
  padding: $space-3;
  color: $text-2;
  font-size: $font-note;
}

.record-list {
  padding: 0 $space-4;
}

.record {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 104rpx;
  border-bottom: 1rpx solid $hairline;
}

.record:last-child {
  border-bottom: 0;
}

.record__title {
  display: block;
  font-size: $font-sub;
}

.record__time {
  display: block;
  margin-top: 4rpx;
  color: $text-3;
  font-size: $font-note;
}

.record__value {
  color: $danger;
  font-weight: 600;
}

.record__value--plus {
  color: $success;
}
</style>
