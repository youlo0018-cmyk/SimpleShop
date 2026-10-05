<template>
  <view v-if="component.type === 'banner'" class="banner">
    <image class="banner__image" :src="firstImage || fallbackBanner" mode="aspectFill" />
    <view class="banner__caption">{{ component.props?.title || '好物上新' }}</view>
  </view>

  <view v-else-if="component.type === 'title'" class="title-block">
    <text class="title-block__text">{{ component.props?.title || '精选推荐' }}</text>
    <text v-if="component.props?.subtitle" class="title-block__sub">{{ component.props.subtitle }}</text>
  </view>

  <view v-else-if="component.type === 'searchBar'" class="search-entry" @tap="toSearch">
    <text>搜索商品、品牌、分类</text>
  </view>

  <view v-else-if="component.type === 'seckillZone'" class="seckill-zone" @tap="toSeckill">
    <view>
      <text class="seckill-zone__title">限时抢购</text>
      <text class="seckill-zone__desc">场次库存单独划出，售完即止</text>
    </view>
    <text class="seckill-zone__action">去看看 ›</text>
  </view>

  <view v-else-if="component.type === 'productGrid'" class="product-grid">
    <ProductCard v-for="item in products" :key="item.productId" :product="item" />
  </view>

  <view v-else-if="component.type === 'memberCard'" class="member-card" @tap="toPoints">
    <text class="member-card__title">会员积分</text>
    <text class="member-card__desc">签到赚积分，下单可抵扣</text>
  </view>

  <view v-else-if="component.type === 'serviceGrid'" class="service-grid">
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

  <view v-else-if="component.type === 'shopHeader'" class="shop-header">
    <image class="shop-header__logo" :src="component.props?.logo || fallbackBanner" mode="aspectFill" />
    <view>
      <text class="shop-header__title">{{ component.props?.shopName || '店铺' }}</text>
      <text class="shop-header__desc">{{ component.props?.description || '欢迎光临' }}</text>
    </view>
  </view>
</template>

<script setup lang="ts">
import ProductCard from './ProductCard.vue';

const props = defineProps<{
  component: any;
  products?: any[];
}>();

const fallbackBanner = '/static/images/banner.svg';
const firstImage = props.component?.props?.images?.[0] || props.component?.props?.image;

function toSearch() {
  uni.navigateTo({ url: '/pages/search/index' });
}
function toSeckill() {
  uni.navigateTo({ url: '/pages/seckill/index' });
}
function toPoints() {
  uni.navigateTo({ url: '/pages/points/index' });
}
function toOrders() {
  uni.navigateTo({ url: '/pages/order/list' });
}
function toCoupons() {
  uni.navigateTo({ url: '/pages/coupon/mine' });
}
function toEvaluates() {
  uni.navigateTo({ url: '/pages/evaluate/list' });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.banner {
  position: relative;
  overflow: hidden;
  height: 320rpx;
  margin-bottom: $space-3;
  border-radius: $radius-lg;
}

.banner__image {
  width: 100%;
  height: 100%;
}

.banner__caption {
  position: absolute;
  bottom: 24rpx;
  left: 28rpx;
  color: #111;
  font-size: 38rpx;
  font-weight: 600;
}

.title-block {
  margin: $space-4 0 $space-3;
}

.title-block__text {
  display: block;
  font-size: 40rpx;
  font-weight: 600;
}

.title-block__sub {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-sub;
}

.search-entry {
  height: 76rpx;
  margin: $space-3 0;
  padding: 0 $space-3;
  border-radius: 38rpx;
  background: #fff;
  color: $text-3;
  font-size: $font-sub;
  line-height: 76rpx;
}

.seckill-zone {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: $space-3 0;
  padding: $space-4;
  border-radius: $radius-lg;
  background: linear-gradient(120deg, #ffe7d9, #fff2e7);
}

.seckill-zone__title {
  display: block;
  font-size: 36rpx;
  font-weight: 600;
}

.seckill-zone__desc {
  display: block;
  margin-top: 6rpx;
  color: #a05a2c;
  font-size: $font-note;
}

.seckill-zone__action {
  color: #a05a2c;
  font-size: $font-sub;
}

.product-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: $space-2;
}

.member-card {
  margin: $space-4 0;
  padding: $space-5;
  border-radius: $radius-lg;
  background: linear-gradient(135deg, #0071e3, #49a7ff);
  color: #fff;
}

.member-card__title {
  display: block;
  font-size: 40rpx;
  font-weight: 600;
}

.member-card__desc {
  display: block;
  margin-top: $space-1;
  opacity: 0.9;
  font-size: $font-sub;
}

.service-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: $space-2;
  margin: $space-4 0;
}

.service-grid__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: $space-1;
  font-size: $font-note;
}

.service-grid__icon {
  width: 76rpx;
  height: 76rpx;
  border-radius: 24rpx;
  background: #eef4ff;
  color: $brand;
  font-size: 28rpx;
  font-weight: 600;
  line-height: 76rpx;
  text-align: center;
}

.shop-header {
  display: flex;
  align-items: center;
  gap: $space-3;
  padding: $space-4;
  border-radius: $radius-lg;
  background: #fff;
}

.shop-header__logo {
  width: 112rpx;
  height: 112rpx;
  border-radius: 28rpx;
}

.shop-header__title {
  display: block;
  font-size: 38rpx;
  font-weight: 600;
}

.shop-header__desc {
  display: block;
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-sub;
}
</style>
