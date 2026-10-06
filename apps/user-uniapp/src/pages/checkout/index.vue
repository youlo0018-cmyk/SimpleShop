<template>
  <view class="page checkout-page">
    <AppHeader title="确认订单" subtitle="确认收货信息与优惠" back />
    <view v-if="loading" class="loading">正在准备订单…</view>
    <template v-else>
      <view class="panel receiver">
        <view class="section-title">收货信息</view>
        <view class="address" @tap="toAddresses">
          <view v-if="selectedAddress" class="address__body">
            <view class="address__line">
              <text class="address__name">{{ selectedAddress.consigneeName }}</text>
              <text class="address__phone">{{ selectedAddress.consigneePhone }}</text>
              <text v-if="selectedAddress.isDefault" class="address__default">默认</text>
            </view>
            <text class="address__detail">
              {{ selectedAddress.regionPath }} {{ selectedAddress.detailAddress }}
            </text>
          </view>
          <view v-else class="address__empty">
            <text class="address__empty-title">请选择收货地址</text>
            <text class="address__empty-desc">点这里从地址簿选择，或新增一条地址</text>
          </view>
          <text class="address__arrow">›</text>
        </view>
        <input v-model="form.remark" class="field" placeholder="订单备注（选填）" />
      </view>

      <view class="panel goods">
        <view class="section-title">商品明细</view>
        <view v-for="line in previewLines" :key="line.skuId" class="goods__line">
          <view>
            <text class="goods__name">{{ line.productName }}</text>
            <text class="goods__spec">{{ line.skuSpecText }} × {{ line.quantity }}</text>
          </view>
          <view class="goods__right">
            <text class="amount">¥{{ amount(line.payableAmount) }}</text>
            <!-- 行优惠不为 0 才显示原价，否则每行都挂一条灰字，页面噪声极大 -->
            <text v-if="line.originalAmount !== line.payableAmount" class="goods__origin">
              ¥{{ amount(line.originalAmount) }}
            </text>
          </view>
        </view>
        <view v-if="previewError" class="preview-error">{{ previewError }}</view>
      </view>

      <view class="panel coupons">
        <view class="section-title">优惠券</view>
        <view v-if="couponOptions.length" class="coupon-options">
          <view
            v-for="option in couponOptions"
            :key="option.couponId"
            class="coupon-option"
            :class="{ 'coupon-option--active': selectedCouponId === String(option.couponId) }"
            @tap="selectCoupon(option.couponId)"
          >
            <text>{{ option.couponTypeName }} · 减 ¥{{ amount(option.discountAmount) }}</text>
            <text v-if="option.isBest" class="coupon-option__best">最优惠</text>
          </view>
          <view class="coupon-option coupon-option--none" @tap="selectCoupon(0)">
            不使用优惠券
          </view>
        </view>
        <view v-else class="muted">当前订单没有可用券</view>
      </view>

      <view class="panel points">
        <view class="section-title">积分抵扣</view>
        <view class="points__row">
          <text>可用 {{ pointBalance }} 积分</text>
          <input
            v-model="form.pointsToUse"
            class="points__input"
            type="number"
            placeholder="0"
            @blur="applyPoints"
          />
        </view>
        <!-- 上限单独一行：和说明文字挤在一行时，「积分」两个字会被折到下一行，
             读起来像「……最多可抵 11350 积 / 分」 -->
        <text v-if="preview?.maxPointsToUse" class="muted points__hint">
          本单最多可抵 {{ preview.maxPointsToUse }} 积分
        </text>
        <text class="muted">积分是最后一道优惠，可与券同用</text>
      </view>

      <view class="amount-summary">
        <view class="amount-summary__row">
          <text>商品金额</text>
          <text>¥{{ amount(preview?.goodsTotal) }}</text>
        </view>
        <view v-if="preview?.activityDiscount" class="amount-summary__row">
          <text>活动优惠</text>
          <text>-¥{{ amount(preview.activityDiscount) }}</text>
        </view>
        <view v-if="preview?.couponDiscount" class="amount-summary__row">
          <text>券优惠</text>
          <text>-¥{{ amount(preview.couponDiscount) }}</text>
        </view>
        <!-- 运费为 0 时不显示这一行（BUSINESS.md 6.2） -->
        <view v-if="preview?.freight" class="amount-summary__row">
          <text>运费</text>
          <text>¥{{ amount(preview.freight) }}</text>
        </view>
        <view v-if="preview?.pointsDeduction" class="amount-summary__row">
          <text>积分抵扣</text>
          <text>-¥{{ amount(preview.pointsDeduction) }}</text>
        </view>
        <view class="amount-summary__row amount-summary__row--strong">
          <text>应付</text>
          <text class="amount">¥{{ amount(preview?.payableAmount) }}</text>
        </view>
        <text v-if="previewError" class="preview-error">{{ previewError }}</text>
      </view>

      <button
        class="button-primary checkout-submit"
        :loading="submitting"
        :disabled="submitting || !preview || !!previewError"
        @tap="submit"
      >
        提交订单
      </button>
    </template>
  </view>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { onLoad, onShow } from '@dcloudio/uni-app';
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

/**
 * 服务端算出的结算结果。
 *
 * 金额**一律**以它为准：运费、满减、券、积分全在里面。
 * 之前这个页面在前端自己算「商品金额 − 券优惠」，既不含运费也不含活动与积分，
 * 于是页面显示的「预计应付」和真实下单金额对不上，而界面上没有任何地方解释差额。
 */
const preview = ref<any>(null);
const previewError = ref('');
const previewLines = computed<any[]>(() => preview.value?.lines || []);
const selectedAddress = ref<any>(null);
const form = reactive({
  pointsToUse: '0',
  remark: '',
});

/**
 * 拉一次服务端试算。
 *
 * 切券、改积分都要重算：券与活动互斥、积分受「商品实付 100%」上限约束，
 * 前端不复算一遍就只能显示一个过期的金额。
 */
async function refreshPreview() {
  if (!lines.value.length) return;
  previewError.value = '';
  try {
    const result = await request<any>('/gateway/orders/Preview', {
      method: 'POST',
      data: {
        customerId: session.profile?.customerId || '0',
        platformId: '0',
        merchantId: '0',
        lines: lines.value.map((line: any) => ({
          spuId: line.spuId,
          skuId: line.skuId,
          quantity: line.quantity,
        })),
        couponId: selectedCouponId.value,
        pointsToUse: Number(form.pointsToUse || 0),
      },
    });
    preview.value = result;
    couponOptions.value = result?.couponOptions || [];
  } catch (error: any) {
    // 试算失败就让提交按钮禁用：宁可明确告诉用户算不出来，
    // 也不要显示一个前端算的「预计应付」然后让下单接口拒绝。
    preview.value = null;
    previewError.value = error?.message || '结算试算失败，请稍后重试';
  }
}

function selectCoupon(couponId: number) {
  selectedCouponId.value = String(couponId);
  refreshPreview();
}

/** 积分输入失焦时才试算：边打字边打接口既吵又贵。 */
function applyPoints() {
  const max = Number(preview.value?.maxPointsToUse || 0);
  const wanted = Number(form.pointsToUse || 0);
  if (wanted > max) {
    form.pointsToUse = String(max);
    uni.showToast({ title: `本单最多可抵 ${max} 积分`, icon: 'none' });
  }
  if (wanted < 0) form.pointsToUse = '0';
  refreshPreview();
}

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
      // 第一次试算不带券，拿到服务端推荐的最优券（K9：默认选最优惠），
      // 再用这张券重算一次 —— 用户看到的「应付」从一开始就是最终价。
      await refreshPreview();
      const best = Number(preview.value?.bestCouponId || 0);
      if (best > 0) {
        selectedCouponId.value = String(best);
        await refreshPreview();
      }
    }

    const balance = await request<any>('/gateway/points/Balance', {
      params: { customerId: session.profile.customerId },
      silent: true,
    }).catch(() => null);
    pointBalance.value = Number(balance?.available || 0);

    // 默认地址：地址簿里 isDefault 的那条；没有默认就选第一条。
    const addresses = await request<any>('/gateway/customers/addresses/List', {
      method: 'POST',
      data: { customerId: session.profile.customerId, page: 1, pageSize: 100 },
      silent: true,
    }).catch(() => null);
    const list = addresses?.items || [];
    selectedAddress.value = list.find((item: any) => item.isDefault) || list[0] || null;
  } finally {
    loading.value = false;
  }
}

function toAddresses() {
  uni.navigateTo({ url: '/pages/address/list?select=1' });
}

async function submit() {
  if (!selectedAddress.value) {
    uni.showToast({ title: '请选择收货地址', icon: 'none' });
    return;
  }
  if (!/^1[3-9]\d{9}$/.test(String(selectedAddress.value.consigneePhone || '').trim())) {
    uni.showToast({ title: '手机号格式不正确', icon: 'none' });
    return;
  }

  submitting.value = true;
  try {
    // 提交前再确认一次试算成功：金额算不出来就不该让人下单。
    if (!preview.value) {
      uni.showToast({ title: previewError.value || '结算试算失败，请重试', icon: 'none' });
      return;
    }
    const created = await request<any>('/gateway/orders/Create', {
      method: 'POST',
      data: {
        customerId: session.profile?.customerId,
        platformId: '0',
        merchantId: '0',
        idempotencyKey: idempotencyKey.value,
        receiverName: selectedAddress.value.consigneeName,
        receiverPhone: selectedAddress.value.consigneePhone,
        receiverAddress: `${selectedAddress.value.regionPath} ${selectedAddress.value.detailAddress}`,
        lines: lines.value,
        couponId: selectedCouponId.value,
        pointsToUse: Number(form.pointsToUse || 0),
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

onShow(() => {
  const picked = uni.getStorageSync('simpleshop_selected_address');
  if (picked) {
    selectedAddress.value = picked;
    uni.removeStorageSync('simpleshop_selected_address');
  }
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

.address {
  display: flex;
  align-items: center;
  gap: $space-3;
  min-height: 140rpx;
  padding: $space-3;
  border-radius: $radius-md;
  background: $bg-page;
}

.address__body {
  flex: 1;
  min-width: 0;
}

.address__line {
  display: flex;
  align-items: center;
  gap: $space-2;
}

.address__name {
  font-weight: 600;
}

.address__phone {
  color: $text-2;
  font-size: $font-sub;
}

.address__default {
  padding: 2rpx 12rpx;
  border-radius: $radius-sm;
  background: rgba(0, 113, 227, 0.1);
  color: $brand;
  font-size: $font-note;
}

.address__detail {
  display: block;
  margin-top: $space-1;
  color: $text-2;
  font-size: $font-sub;
  line-height: 1.5;
}

.address__empty {
  flex: 1;
}

.address__empty-title {
  display: block;
  font-weight: 600;
}

.address__empty-desc {
  display: block;
  margin-top: $space-1;
  color: $text-2;
  font-size: $font-note;
}

.address__arrow {
  color: $text-3;
  font-size: 40rpx;
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

.goods__right {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4rpx;
}

.goods__origin {
  color: $text-2;
  font-size: $font-note;
  text-decoration: line-through;
}

.preview-error {
  display: block;
  margin-top: $space-2;
  color: $danger;
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

.points__hint {
  display: block;
  margin-top: $space-2;
  color: $brand;
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
