<template>
  <view class="page detail-page">
    <view v-if="loading" class="loading">正在加载商品…</view>
    <template v-else-if="product">
      <swiper class="gallery" :indicator-dots="true" circular>
        <swiper-item v-for="image in gallery" :key="image">
          <image class="gallery__image" :src="image" mode="aspectFill" />
        </swiper-item>
      </swiper>
      <view class="panel detail">
        <text class="detail__name">{{ product.spuName }}</text>
        <text v-if="product.subTitle" class="detail__sub">{{ product.subTitle }}</text>
        <view class="detail__price">
          <text class="amount detail__amount">¥{{ amount(selectedSku?.finalPrice || minPrice) }}</text>
          <text
            v-if="Number(selectedSku?.originalPrice || 0) > Number(selectedSku?.finalPrice || 0)"
            class="detail__original"
          >
            ¥{{ amount(selectedSku.originalPrice) }}
          </text>
          <text class="detail__score">{{ score(product.evaluationScore) }} 分 · {{ product.evaluationCount || 0 }} 条评价</text>
        </view>
        <view class="detail__delivery">{{ deliveryName }}</view>
      </view>

      <view class="panel sku-picker">
        <view v-for="spec in product.specs" :key="spec.specId" class="sku-row">
          <text class="sku-row__label">{{ spec.specName }}</text>
          <view class="sku-row__values">
            <view
              v-for="value in spec.values"
              :key="value.specValueId"
              class="sku-value"
              :class="{ 'sku-value--active': selectedValueIds.includes(String(value.specValueId)) }"
              @tap="selectValue(spec.specId, String(value.specValueId), spec.values)"
            >
              {{ value.valueName }}
            </view>
          </view>
        </view>
        <view class="sku-stock">库存以结算页为准，当前规格：{{ selectedSku?.skuSpecText || '请选择' }}</view>
      </view>

      <view class="panel detail-images">
        <view class="section-title">商品详情</view>
        <image
          v-for="image in detailImages"
          :key="image"
          class="detail-images__image"
          :src="image"
          mode="widthFix"
        />
      </view>

      <view class="panel evaluation">
        <view class="section-title">
          <text>商品评价</text>
          <text class="evaluation__more" @tap="toEvaluates">查看全部 ›</text>
        </view>
        <view v-if="evaluations.length">
          <view v-for="item in evaluations" :key="item.evaluateId" class="eval-row">
            <text class="eval-row__name">{{ item.customerName || '匿名用户' }}</text>
            <text class="eval-row__score">{{ score(item.starScore) }} 分</text>
            <text class="eval-row__content">{{ item.content }}</text>
          </view>
        </view>
        <view v-else class="empty">还没有评价</view>
      </view>

      <view class="detail-actions">
        <button class="detail-actions__cart" @tap="addCart">加入购物车</button>
        <button class="detail-actions__buy" @tap="buyNow">立即购买</button>
      </view>
    </template>
    <view v-else class="empty">商品不存在或已下架</view>
  </view>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import { request } from '@/core/http';
import { amount, imageList, score } from '@/core/format';
import { DELIVERY_NAMES, displayName } from '@/core/dict';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(true);
const product = ref<any>(null);
const selectedValueIds = ref<string[]>([]);
const evaluations = ref<any[]>([]);
const productId = ref('');

const gallery = computed(() => {
  const images = imageList(product.value?.images || '');
  return images.length ? images : [product.value?.mainImage || ''];
});
const detailImages = computed(() => imageList(product.value?.detailImages || ''));
const minPrice = computed(() =>
  Math.min(...(product.value?.skus || []).map((sku: any) => Number(sku.finalPrice || 0))),
);
const selectedSku = computed(() => {
  if (!product.value?.skus?.length) return null;
  if (!selectedValueIds.value.length) return product.value.skus[0];
  return product.value.skus.find((sku: any) =>
    selectedValueIds.value.every((id) => (sku.specValueIds || []).map(String).includes(id)),
  ) || product.value.skus[0];
});
const deliveryName = computed(() => displayName(product.value?.deliveryType, DELIVERY_NAMES));

function selectValue(specId: number, valueId: string, values: any[]) {
  const sameSpec = new Set(values.map((value) => String(value.specValueId)));
  selectedValueIds.value = [
    ...selectedValueIds.value.filter((id) => !sameSpec.has(id)),
    valueId,
  ];
}

function requireLogin() {
  if (session.loggedIn) return true;
  uni.navigateTo({ url: '/pages/login/index' });
  return false;
}

function addCart() {
  if (!requireLogin() || !selectedSku.value) return;
  request('/gateway/carts/Add', {
    method: 'POST',
    data: { skuId: selectedSku.value.skuId, quantity: 1 },
  }).then(() => uni.showToast({ title: '已加入购物车', icon: 'success' }));
}

function buyNow() {
  if (!requireLogin() || !selectedSku.value) return;
  uni.setStorageSync('simpleshop_buy_now', {
    spuId: product.value.productId,
    skuId: selectedSku.value.skuId,
    quantity: 1,
  });
  uni.showToast({ title: '请在结算页确认订单', icon: 'none' });
}

function toEvaluates() {
  uni.navigateTo({ url: `/pages/evaluate/list?productId=${product.value.productId}` });
}

async function load() {
  loading.value = true;
  try {
    product.value = await request<any>('/gateway/shop/products/Detail', {
      method: 'POST',
      auth: false,
      data: {
        customerId: session.profile?.customerId || '0',
        productId: productId.value,
      },
      silent: true,
    });
    evaluations.value = await request<any>('/gateway/evaluates/List', {
      method: 'POST',
      auth: false,
      data: { spuId: productId.value, page: 1, pageSize: 3 },
      silent: true,
    }).then((result: any) => result?.items || []).catch(() => []);
  } catch {
    product.value = null;
  } finally {
    loading.value = false;
  }
}

onLoad((options) => {
  productId.value = String(options?.id || '');
  load();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.detail-page {
  padding-bottom: 180rpx;
}

.gallery,
.gallery__image {
  width: 100%;
  height: 750rpx;
  border-radius: $radius-lg;
}

.detail {
  margin-top: $space-3;
  padding: $space-4;
}

.detail__name {
  display: block;
  font-size: 42rpx;
  font-weight: 600;
}

.detail__sub,
.detail__score,
.detail__delivery {
  display: block;
  margin-top: $space-1;
  color: $text-2;
  font-size: $font-sub;
}

.detail__price {
  display: flex;
  align-items: baseline;
  gap: $space-2;
  flex-wrap: wrap;
  margin-top: $space-3;
}

.detail__amount {
  font-size: 52rpx;
}

.detail__original {
  color: $text-3;
  text-decoration: line-through;
}

.sku-picker,
.detail-images,
.evaluation {
  margin-top: $space-3;
  padding: $space-4;
}

.sku-row {
  margin-bottom: $space-3;
}

.sku-row__label {
  display: block;
  margin-bottom: $space-2;
  font-weight: 600;
}

.sku-row__values {
  display: flex;
  gap: $space-2;
  flex-wrap: wrap;
}

.sku-value {
  padding: 14rpx 28rpx;
  border: 1rpx solid $hairline;
  border-radius: 36rpx;
  background: $bg-page;
  font-size: $font-sub;
}

.sku-value--active {
  border-color: $brand;
  background: #eef4ff;
  color: $brand;
}

.sku-stock {
  color: $text-2;
  font-size: $font-note;
}

.detail-images__image {
  width: 100%;
  margin-bottom: $space-2;
  border-radius: $radius-sm;
}

.evaluation__more {
  color: $brand;
  font-size: $font-note;
  font-weight: 400;
}

.eval-row {
  padding: $space-3 0;
  border-bottom: 1rpx solid $hairline;
}

.eval-row__name,
.eval-row__score,
.eval-row__content {
  display: block;
}

.eval-row__score,
.eval-row__content {
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-sub;
}

.detail-actions {
  position: fixed;
  right: 0;
  bottom: 0;
  left: 0;
  z-index: 20;
  display: flex;
  gap: $space-2;
  padding: $space-2 $space-3 calc(env(safe-area-inset-bottom) + 16rpx);
  background: rgba(255, 255, 255, 0.96);
}

.detail-actions__cart,
.detail-actions__buy {
  flex: 1;
  height: 84rpx;
  border-radius: 42rpx;
  font-size: $font-body;
  line-height: 84rpx;
}

.detail-actions__cart {
  background: #eef4ff;
  color: $brand;
}

.detail-actions__buy {
  background: $brand;
  color: #fff;
}
</style>
