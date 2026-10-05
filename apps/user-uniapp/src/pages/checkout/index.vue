<template>
  <view class="page checkout-page">
    <AppHeader title="确认订单" subtitle="确认收货信息与优惠" back />
    <view v-if="loading" class="loading">正在准备订单…</view>
    <template v-else>
      <view class="panel receiver">
        <view class="section-title">收货信息</view>
        <input v-model="form.receiverName" class="field" placeholder="收货人姓名" />
        <input v-model="form.receiverPhone" class="field" type="number" maxlength="11" placeholder="手机号" />
        <textarea v-model="form.receiverAddress" class="field field--area" placeholder="详细收货地址" />
        <input v-model="form.remark" class="field" placeholder="订单备注（选填）" />
      </view>

      <view class="panel goods">
        <view class="section-title">商品明细</view>
        <view v-for="line in lines" :key="line.skuId" class="goods__line">
          <view>
            <text class="goods__name">{{ line.productName }}</text>
            <text class="goods__spec">{{ line.skuSpecText }} × {{ line.quantity }}</text>
          </view>
          <text class="amount">¥{{ amount(Number(line.unitPrice) * Number(line.quantity)) }}</text>
        </view>
      </view>

      <view class="panel coupons">
        <view class="section-title">优惠券</view>
        <view v-if="couponOptions.length" class="coupon-options">
          <view
            v-for="option in couponOptions"
            :key="option.couponId"
            class="coupon-option"
            :class="{ 'coupon-option--active': selectedCouponId === String(option.couponId) }"
            @tap="selectedCouponId = String(option.couponId)"
          >
            <text>{{ option.couponTypeName }} · 减 ¥{{ amount(option.discountAmount) }}</text>
            <text v-if="option.isBest" class="coupon-option__best">最优惠</text>
          </view>
          <view class="coupon-option coupon-option--none" @tap="selectedCouponId = '0'">
            不使用优惠券
          </view>
        </view>
        <view v-else class="muted">当前订单没有可用券</view>
      </view>

      <view class="panel points">
        <view class="section-title">积分抵扣</view>
        <view class="points__row">
          <text>可用 {{ pointBalance }} 积分</text>
          <input v-model="form.pointsToUse" class="points__input" type="number" placeholder="0" />
        </view>
        <text class="muted">积分是最后一道优惠，抵扣上限为应付商品金额的 100%</text>
      </view>

      <view class="amount-summary">
        <view class="amount-summary__row">
          <text>商品金额</text>
          <text>¥{{ amount(goodsAmount) }}</text>
        </view>
        <view class="amount-summary__row">
          <text>券优惠</text>
          <text>-¥{{ amount(selectedCoupon?.discountAmount || 0) }}</text>
        </view>
        <view class="amount-summary__row amount-summary__row--strong">
          <text>预计应付</text>
          <text class="amount">¥{{ amount(estimatedPayable) }}</text>
        </view>
      </view>

      <button class="button-primary checkout-submit" :loading="submitting" @tap="submit">提交订单</button>
    </template>
  </view>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { amount } from '@/core/format';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const submitting = ref(false);
const lines = ref<any[]>([]);
const couponOptions = ref<any[]>([]);
const selectedCouponId = ref('0');
const pointBalance = ref(0);
const idempotencyKey = ref('');
const form = reactive({
  receiverName: '',
  receiverPhone: '',
  receiverAddress: '',
  pointsToUse: '0',
  remark: '',
});

const goodsAmount = computed(() =>
  lines.value.reduce((sum, line) => sum + Number(line.unitPrice) * Number(line.quantity), 0),
);
const selectedCoupon = computed(() =>
  couponOptions.value.find((item) => String(item.couponId) === selectedCouponId.value),
);
const estimatedPayable = computed(() =>
  Math.max(0, goodsAmount.value - Number(selectedCoupon.value?.discountAmount || 0)),
);

async function prepare() {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }
  loading.value = true;
  try {
    const raw = uni.getStorageSync('simpleshop_checkout_lines') || [];
    const source = Array.isArray(raw) ? raw : [];
    lines.value = await Promise.all(source.map(async (item: any) => {
      const detail = await request<any>('/gateway/shop/products/Detail', {
        method: 'POST',
        auth: false,
        data: { customerId: session.profile?.customerId || '0', productId: item.productId },
        silent: true,
      });
      const sku = detail?.skus?.find((entry: any) => String(entry.skuId) === String(item.skuId));
      return {
        spuId: String(item.productId),
        skuId: String(item.skuId),
        quantity: Number(item.quantity),
        unitPrice: Number(sku?.finalPrice ?? item.price),
        productName: sku?.skuName || item.skuName,
        skuSpecText: sku?.skuSpecText || item.skuSpecText,
        deliveryType: Number(detail?.deliveryType || 1),
        sourceType: 1,
      };
    }));

    if (lines.value.length) {
      const settleLines = lines.value.map((line) => ({
        spuId: line.spuId,
        skuId: line.skuId,
        quantity: line.quantity,
        unitPrice: line.unitPrice,
      }));
      const settle = await request<any>('/gateway/coupons/Settle', {
        method: 'POST',
        data: { customerId: session.profile.customerId, lines: settleLines },
        silent: true,
      }).catch(() => null);
      couponOptions.value = settle?.options || [];
      selectedCouponId.value = String(settle?.best?.couponId || 0);
    }

    const balance = await request<any>('/gateway/points/Balance', {
      params: { customerId: session.profile.customerId },
      silent: true,
    }).catch(() => null);
    pointBalance.value = Number(balance?.available || 0);
  } finally {
    loading.value = false;
  }
}

async function submit() {
  if (!form.receiverName.trim() || !form.receiverPhone.trim() || !form.receiverAddress.trim()) {
    uni.showToast({ title: '请完整填写收货信息', icon: 'none' });
    return;
  }
  if (!/^1[3-9]\d{9}$/.test(form.receiverPhone.trim())) {
    uni.showToast({ title: '手机号格式不正确', icon: 'none' });
    return;
  }

  submitting.value = true;
  try {
    const created = await request<any>('/gateway/orders/Create', {
      method: 'POST',
      data: {
        customerId: session.profile?.customerId,
        platformId: '0',
        merchantId: '0',
        idempotencyKey: idempotencyKey.value,
        receiverName: form.receiverName.trim(),
        receiverPhone: form.receiverPhone.trim(),
        receiverAddress: form.receiverAddress.trim(),
        lines: lines.value,
        couponId: selectedCouponId.value,
        pointsToUse: Number(form.pointsToUse || 0),
        freight: 0,
        remark: form.remark.trim(),
      },
    });
    uni.removeStorageSync('simpleshop_checkout_lines');
    uni.removeStorageSync('simpleshop_buy_now');
    uni.showToast({ title: '下单成功', icon: 'success' });
    setTimeout(() => {
      uni.redirectTo({ url: `/pages/order/detail?orderNo=${created.orderNo}` });
    }, 500);
  } finally {
    submitting.value = false;
  }
}

onLoad(() => {
  idempotencyKey.value = `ui-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
  prepare();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.checkout-page {
  padding-bottom: 180rpx;
}

.receiver,
.goods,
.coupons,
.points {
  margin-bottom: $space-3;
  padding: $space-4;
}

.field {
  width: 100%;
  min-height: 80rpx;
  margin-bottom: $space-2;
  padding: 0 $space-3;
  border-radius: $radius-sm;
  background: $bg-page;
  font-size: $font-sub;
}

.field--area {
  height: 160rpx;
  padding-top: $space-2;
}

.goods__line {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: $space-2;
  padding: $space-2 0;
  border-bottom: 1rpx solid $hairline;
}

.goods__line:last-child {
  border-bottom: 0;
}

.goods__name {
  display: block;
  font-weight: 600;
}

.goods__spec {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.coupon-options {
  display: flex;
  flex-direction: column;
  gap: $space-2;
}

.coupon-option {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: $space-3;
  border: 2rpx solid $hairline;
  border-radius: $radius-sm;
  font-size: $font-sub;
}

.coupon-option--active {
  border-color: $brand;
  background: #eef4ff;
  color: $brand;
}

.coupon-option--none {
  justify-content: center;
  color: $text-2;
}

.coupon-option__best {
  font-size: $font-note;
}

.points__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.points__input {
  width: 180rpx;
  height: 72rpx;
  padding: 0 $space-2;
  border-radius: $radius-sm;
  background: $bg-page;
  text-align: right;
}

.amount-summary {
  margin: $space-3 0;
  padding: $space-4;
  border-radius: $radius-md;
  background: #fff;
}

.amount-summary__row {
  display: flex;
  justify-content: space-between;
  padding: $space-1 0;
  color: $text-2;
}

.amount-summary__row--strong {
  color: $text-1;
  font-weight: 600;
}

.checkout-submit {
  margin-top: $space-4;
}
</style>
