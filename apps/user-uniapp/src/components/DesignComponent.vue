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

  <view v-else-if="component.type === 'imageText'" class="image-text">
    <image v-if="firstImage" class="image-text__image" :src="firstImage" mode="aspectFill" />
    <view class="image-text__body">
      <text class="image-text__title">{{ component.props?.title || '图文广告' }}</text>
      <text class="image-text__desc">{{ component.props?.description || component.props?.subtitle || '' }}</text>
    </view>
  </view>

  <view v-else-if="component.type === 'notice'" class="notice-card">
    <text class="notice-card__label">公告</text>
    <text class="notice-card__text">{{ component.props?.text || component.props?.content || '暂无公告' }}</text>
  </view>

  <view v-else-if="component.type === 'kingKong' || component.type === 'categoryNav'" class="quick-grid">
    <view
      v-for="(item, index) in normalizedItems(component, 8)"
      :key="index"
      class="quick-grid__item"
    >
      <image v-if="item.image || item.icon" class="quick-grid__icon" :src="item.image || item.icon" mode="aspectFit" />
      <view v-else class="quick-grid__icon quick-grid__icon--text">{{ (item.name || item.title || '入口').slice(0, 1) }}</view>
      <text>{{ item.name || item.title || `入口${index + 1}` }}</text>
    </view>
  </view>

  <scroll-view v-else-if="component.type === 'productScroll'" class="product-scroll" scroll-x>
    <view class="product-scroll__inner">
      <view v-for="item in products" :key="item.productId" class="product-scroll__item">
        <ProductCard :product="item" />
      </view>
    </view>
  </scroll-view>

  <view v-else-if="component.type === 'couponZone'" class="coupon-zone">
    <view v-for="(item, index) in normalizedItems(component, 3)" :key="index" class="coupon-zone__item">
      <text class="coupon-zone__value">{{ item.value || item.discountAmount || '优惠' }}</text>
      <text class="coupon-zone__name">{{ item.name || item.templateName || '优惠券' }}</text>
    </view>
  </view>

  <view v-else-if="component.type === 'activityZone'" class="activity-zone">
    <view v-for="(item, index) in normalizedItems(component, 3)" :key="index" class="activity-zone__item">
      <text class="activity-zone__title">{{ item.title || item.name || item.activityName || `活动 ${index + 1}` }}</text>
      <text class="activity-zone__desc">{{ item.description || item.subtitle || '限时优惠' }}</text>
    </view>
  </view>

  <view v-else-if="component.type === 'shopList'" class="shop-list">
    <view v-for="(item, index) in normalizedItems(component, 4)" :key="index" class="shop-list__item">
      <view class="shop-list__logo">{{ (item.name || item.merchantName || '店').slice(0, 1) }}</view>
      <view>
        <text class="shop-list__name">{{ item.name || item.merchantName || `店铺 ${index + 1}` }}</text>
        <text class="shop-list__desc">{{ item.description || '精选店铺' }}</text>
      </view>
    </view>
  </view>

  <view v-else-if="component.type === 'benefits'" class="benefit-list">
    <view v-for="(item, index) in normalizedItems(component, 4)" :key="index" class="benefit-list__item">
      <text>{{ item.name || item.title || `权益 ${index + 1}` }}</text>
      <text class="benefit-list__value">{{ item.value || item.description || '查看' }}</text>
    </view>
  </view>

  <view v-else-if="component.type === 'shopActivity' || component.type === 'shopCategory' || component.type === 'shopEvaluate'" class="generic-shop-block">
    <text class="generic-shop-block__title">{{ component.props?.title || component.type }}</text>
    <text class="generic-shop-block__desc">已按店铺装修配置展示</text>
  </view>

  <view v-else-if="component.type === 'divider'" class="divider" />
  <view v-else-if="component.type === 'spacer'" class="spacer" />

  <view v-else-if="component.type === 'cartFloat'" class="cart-float" @tap="toCart">
    <text>购物车</text>
  </view>

  <view v-else class="generic-block">
    <text class="generic-block__title">{{ component.props?.title || component.type || '装修组件' }}</text>
    <text class="generic-block__desc">该组件已配置，等待更多内容</text>
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
function toCart() {
  uni.navigateTo({ url: '/pages/cart/index' });
}

function normalizedItems(component: any, count: number) {
  const raw = component?.props?.items || component?.props?.categories || component?.props?.banners;
  if (Array.isArray(raw) && raw.length) return raw;
  return Array.from({ length: count }, (_, index) => ({ name: `入口${index + 1}` }));
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

.image-text,
.notice-card,
.quick-grid,
.coupon-zone,
.activity-zone,
.shop-list,
.benefit-list,
.generic-shop-block,
.generic-block {
  margin: $space-3 0;
  padding: $space-3;
  border-radius: $radius-lg;
  background: #fff;
}

.image-text__image {
  width: 100%;
  height: 300rpx;
  border-radius: $radius-md;
}

.image-text__body {
  padding-top: $space-2;
}

.image-text__title,
.generic-shop-block__title,
.generic-block__title {
  display: block;
  font-size: 34rpx;
  font-weight: 600;
}

.image-text__desc,
.generic-shop-block__desc,
.generic-block__desc {
  display: block;
  margin-top: 8rpx;
  color: $text-2;
  font-size: $font-sub;
}

.notice-card {
  display: flex;
  align-items: center;
  gap: $space-2;
}

.notice-card__label {
  padding: 6rpx 12rpx;
  border-radius: 20rpx;
  background: rgba(255, 159, 10, 0.14);
  color: #8a5200;
  font-size: $font-note;
}

.notice-card__text {
  flex: 1;
  color: $text-2;
  font-size: $font-sub;
}

.quick-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: $space-3;
}

.quick-grid__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: $space-1;
  color: $text-2;
  font-size: $font-note;
}

.quick-grid__icon {
  width: 72rpx;
  height: 72rpx;
}

.quick-grid__icon--text {
  border-radius: 22rpx;
  background: #eef4ff;
  color: $brand;
  font-size: 30rpx;
  line-height: 72rpx;
  text-align: center;
}

.product-scroll {
  width: 100%;
  margin: $space-3 0;
  white-space: nowrap;
}

.product-scroll__inner {
  display: inline-flex;
  gap: $space-2;
}

.product-scroll__item {
  width: 300rpx;
}

.coupon-zone,
.activity-zone {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: $space-2;
}

.coupon-zone__item,
.activity-zone__item {
  padding: $space-2;
  border-radius: $radius-sm;
  background: $bg-page;
  text-align: center;
}

.coupon-zone__value,
.activity-zone__title {
  display: block;
  color: $danger;
  font-weight: 600;
}

.coupon-zone__name,
.activity-zone__desc {
  display: block;
  margin-top: 6rpx;
  color: $text-2;
  font-size: $font-note;
}

.shop-list,
.benefit-list {
  display: flex;
  flex-direction: column;
  gap: $space-2;
}

.shop-list__item,
.benefit-list__item {
  display: flex;
  align-items: center;
  gap: $space-2;
}

.shop-list__logo {
  width: 72rpx;
  height: 72rpx;
  border-radius: 20rpx;
  background: $brand;
  color: #fff;
  line-height: 72rpx;
  text-align: center;
}

.shop-list__name {
  display: block;
  font-weight: 600;
}

.shop-list__desc,
.benefit-list__value {
  color: $text-2;
  font-size: $font-note;
}

.benefit-list__item {
  justify-content: space-between;
  padding: $space-2 0;
  border-bottom: 1rpx solid $hairline;
}

.divider {
  height: 1rpx;
  margin: $space-4 0;
  background: $hairline;
}

.spacer {
  height: 32rpx;
}

.cart-float {
  position: fixed;
  right: $space-3;
  bottom: 180rpx;
  z-index: 15;
  padding: 18rpx 26rpx;
  border-radius: 40rpx;
  background: $brand;
  color: #fff;
  font-size: $font-sub;
  box-shadow: 0 10rpx 30rpx rgba(0, 113, 227, 0.25);
}
</style>
