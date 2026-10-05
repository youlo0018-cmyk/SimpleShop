<template>
  <view class="login-page">
    <view class="login-card">
      <view class="login-card__mark">S</view>
      <text class="login-card__title">登录 SimpleShop</text>
      <text class="login-card__desc">登录后可加购、下单、领券、签到与评价</text>

      <view class="field">
        <text class="field__label">账号</text>
        <input v-model="form.customerName" class="field__input" placeholder="请输入登录名" />
        <text v-if="errors.customerName" class="field__error">{{ errors.customerName }}</text>
      </view>

      <view class="field">
        <text class="field__label">密码</text>
        <input v-model="form.password" class="field__input" password placeholder="请输入密码" />
        <text v-if="errors.password" class="field__error">{{ errors.password }}</text>
      </view>

      <button class="button-primary login-card__submit" :loading="loading" @tap="submit">
        登录
      </button>
      <text class="login-card__guest" @tap="register">注册新账号</text>
      <text class="login-card__guest" @tap="guest">先随便逛逛</text>
    </view>
  </view>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const loading = ref(false);
const form = reactive({ customerName: '', password: '' });
const errors = reactive({ customerName: '', password: '' });

function validate() {
  errors.customerName = form.customerName.trim() ? '' : '请输入登录名';
  errors.password = form.password ? '' : '请输入密码';
  return !errors.customerName && !errors.password;
}

async function submit() {
  if (!validate()) return;
  loading.value = true;
  try {
    await session.login(form.customerName.trim(), form.password);
    uni.showToast({ title: '登录成功', icon: 'success' });
    setTimeout(() => {
      const pages = getCurrentPages();
      if (pages.length > 1) uni.navigateBack();
      else uni.reLaunch({ url: '/pages/profile/index' });
    }, 300);
  } catch {
    // request 已提示
  } finally {
    loading.value = false;
  }
}

function guest() {
  uni.reLaunch({ url: '/pages/index/index' });
}

function register() {
  uni.navigateTo({ url: '/pages/register/index' });
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.login-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  padding: $space-5;
  background: radial-gradient(circle at top, #eaf4ff 0, $bg-page 42%);
}

.login-card {
  width: 100%;
  padding: $space-6 $space-5;
  border-radius: 40rpx;
  background: #fff;
  box-shadow: 0 20rpx 60rpx rgba(0, 0, 0, 0.08);
  text-align: center;
}

.login-card__mark {
  width: 104rpx;
  height: 104rpx;
  margin: 0 auto $space-3;
  border-radius: 32rpx;
  background: $brand;
  color: #fff;
  font-size: 52rpx;
  font-weight: 600;
  line-height: 104rpx;
}

.login-card__title {
  display: block;
  font-size: 46rpx;
  font-weight: 600;
}

.login-card__desc {
  display: block;
  margin: $space-1 0 $space-5;
  color: $text-2;
  font-size: $font-sub;
}

.field {
  margin-top: $space-3;
  text-align: left;
}

.field__label {
  display: block;
  margin-bottom: $space-1;
  color: $text-2;
  font-size: $font-sub;
}

.field__input {
  height: 88rpx;
  padding: 0 $space-3;
  border-radius: $radius-sm;
  background: $bg-page;
  font-size: $font-body;
}

.field__error {
  display: block;
  margin-top: 8rpx;
  color: $danger;
  font-size: $font-note;
}

.login-card__submit {
  margin-top: $space-5;
}

.login-card__guest {
  display: block;
  margin-top: $space-4;
  color: $text-2;
  font-size: $font-sub;
}
</style>
