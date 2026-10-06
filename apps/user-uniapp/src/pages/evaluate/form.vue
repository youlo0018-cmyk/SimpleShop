<template>
  <view class="page evaluate-form">
    <AppHeader :title="evaluateId ? '追加评价' : '发表评价'" back />

    <view class="panel product">
      <text class="product__name">{{ spuName || '商品' }}</text>
      <text class="product__hint">{{ evaluateId ? '追评不会改变商品均分' : '评价发布后均分每日更新' }}</text>
    </view>

    <view class="panel rating">
      <text class="section-title">评分</text>
      <view class="stars">
        <text
          v-for="star in 5"
          :key="star"
          class="stars__item"
          :class="{ 'stars__item--active': star <= starScore }"
          @tap="starScore = star"
        >
          ★
        </text>
      </view>
      <text class="rating__hint">{{ starScore > 0 ? `${starScore} 分` : '不打分（选填）' }}</text>
    </view>

    <view class="panel content">
      <text class="section-title">评价内容</text>
      <textarea
        v-model="content"
        class="content__input"
        maxlength="1000"
        placeholder="说说你的使用感受（选填，配图也可以）"
      />
      <view class="images">
        <view v-for="(image, index) in images" :key="image" class="images__item">
          <image :src="image" mode="aspectFill" />
          <text class="images__remove" @tap="removeImage(index)">×</text>
        </view>
        <view v-if="images.length < 9" class="images__add" @tap="chooseImages">＋</view>
      </view>
      <text class="content__count">{{ content.length }} / 1000</text>
    </view>

    <view v-if="!evaluateId" class="panel anonymous">
      <text>匿名评价</text>
      <switch :checked="isAnonymous" color="#0071e3" @change="onAnonymousChange" />
    </view>

    <button class="button-primary evaluate-form__submit" :loading="submitting" @tap="submit">
      {{ evaluateId ? '提交追评' : '发布评价' }}
    </button>
  </view>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request, uploadImage } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const evaluateId = ref('');
const orderNo = ref('');
const spuId = ref('');
const spuName = ref('');
const starScore = ref(5);
const content = ref('');
const images = ref<string[]>([]);
const isAnonymous = ref(false);
const submitting = ref(false);

function onAnonymousChange(event: any) {
  isAnonymous.value = Boolean(event.detail.value);
}

function removeImage(index: number) {
  images.value.splice(index, 1);
}

function chooseImages() {
  uni.chooseImage({
    count: 9 - images.value.length,
    sizeType: ['compressed'],
    sourceType: ['album', 'camera'],
    success: async (result) => {
      const paths = result.tempFilePaths as string[];
      uni.showLoading({ title: '上传中…' });
      try {
        for (const path of paths) {
          const url = await uploadImage(path);
          images.value.push(url);
        }
      } catch (error: any) {
        uni.showToast({ title: error?.message || '图片上传失败', icon: 'none' });
      } finally {
        uni.hideLoading();
      }
    },
  });
}

function validate(): boolean {
  if (!evaluateId.value && (starScore.value < 1 || starScore.value > 5)) {
    uni.showToast({ title: '请选择 1 到 5 星', icon: 'none' });
    return false;
  }
  if (!content.value.trim() && images.value.length === 0) {
    uni.showToast({ title: '请填写评价内容或上传图片', icon: 'none' });
    return false;
  }
  return true;
}

async function submit() {
  if (!validate()) return;
  submitting.value = true;
  try {
    if (evaluateId.value) {
      await request('/gateway/evaluates/Append', {
        method: 'POST',
        data: {
          customerId: session.profile?.customerId,
          evaluateId: evaluateId.value,
          starScore: starScore.value,
          content: content.value.trim(),
          images: images.value,
        },
      });
    } else {
      await request('/gateway/evaluates/Publish', {
        method: 'POST',
        data: {
          customerId: session.profile?.customerId,
          orderNo: orderNo.value,
          spuId: spuId.value,
          starScore: starScore.value,
          content: content.value.trim(),
          images: images.value,
          isAnonymous: isAnonymous.value,
        },
      });
    }

    uni.showToast({ title: evaluateId.value ? '追评成功' : '评价成功', icon: 'success' });
    setTimeout(() => uni.navigateBack(), 600);
  } finally {
    submitting.value = false;
  }
}

onLoad((options) => {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }

  evaluateId.value = String(options?.evaluateId || '');
  if (evaluateId.value) starScore.value = 0;
  orderNo.value = String(options?.orderNo || '');
  spuId.value = String(options?.spuId || '');
  spuName.value = decodeURIComponent(String(options?.spuName || ''));
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.evaluate-form {
  padding-bottom: 180rpx;
}

.panel {
  margin-bottom: $space-3;
  padding: $space-4;
}

.product__name {
  display: block;
  font-weight: 600;
}

.product__hint {
  display: block;
  margin-top: $space-1;
  color: $text-2;
  font-size: $font-note;
}

.section-title {
  display: block;
  margin-bottom: $space-3;
  color: $text-2;
  font-size: $font-sub;
}

.stars {
  display: flex;
  gap: $space-2;
}

.stars__item {
  color: $text-3;
  font-size: 64rpx;
  line-height: 1;
}

.stars__item--active {
  color: $warning;
}

.rating__hint {
  display: block;
  margin-top: $space-2;
  color: $text-2;
  font-size: $font-note;
}

.content__input {
  width: 100%;
  min-height: 220rpx;
  font-size: $font-body;
  line-height: 1.6;
}

.content__count {
  display: block;
  margin-top: $space-2;
  color: $text-3;
  font-size: $font-note;
  text-align: right;
}

.images {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: $space-2;
  margin-top: $space-3;
}

.images__item {
  position: relative;
  aspect-ratio: 1;
}

.images__item image {
  width: 100%;
  height: 100%;
  border-radius: $radius-sm;
}

.images__remove {
  position: absolute;
  top: -12rpx;
  right: -12rpx;
  width: 36rpx;
  height: 36rpx;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  font-size: 28rpx;
  line-height: 34rpx;
  text-align: center;
}

.images__add {
  display: flex;
  align-items: center;
  justify-content: center;
  aspect-ratio: 1;
  border: 1rpx dashed $hairline;
  border-radius: $radius-sm;
  color: $text-3;
  font-size: 52rpx;
}

.anonymous {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.evaluate-form__submit {
  position: fixed;
  right: $space-4;
  bottom: calc(env(safe-area-inset-bottom) + $space-4);
  left: $space-4;
}
</style>
