<template>
  <view class="register-page">
    <view class="register-card">
      <AppHeader title="注册账号" subtitle="注册后即可加购、下单与领券" back />
      <view class="field">
        <text class="field__label">登录名</text>
        <input v-model="form.customerName" class="field__input" placeholder="3-64 位字母、数字或下划线" />
        <text v-if="errors.customerName" class="field__error">{{ errors.customerName }}</text>
      </view>
      <view class="field">
        <text class="field__label">手机号</text>
        <input v-model="form.phone" class="field__input" type="number" maxlength="11" placeholder="11 位手机号" />
        <text v-if="errors.phone" class="field__error">{{ errors.phone }}</text>
      </view>
      <view class="field">
        <text class="field__label">昵称</text>
        <input v-model="form.nickName" class="field__input" placeholder="选填" />
      </view>
      <view class="field">
        <text class="field__label">密码</text>
        <input v-model="form.password" class="field__input" password placeholder="至少 8 位，包含字母和数字" />
        <text v-if="errors.password" class="field__error">{{ errors.password }}</text>
      </view>
      <button class="button-primary register-card__submit" :loading="loading" @tap="submit">注册并登录</button>
    </view>
  </view>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue';
import AppHeader from '@/components/AppHeader.vue';
import { useSessionStore } from '@/stores/session';
import { isCustomerName, isPassword, isPhone } from '@/common/validators';

const session = useSessionStore();
const loading = ref(false);
const form = reactive({
  customerName: '',
  phone: '',
  nickName: '',
  password: '',
});
const errors = reactive({
  customerName: '',
  phone: '',
  password: '',
});

function validate() {
  errors.customerName = isCustomerName(form.customerName.trim())
    ? ''
    : '登录名需为 3-64 位字母、数字或下划线';
  errors.phone = isPhone(form.phone.trim()) ? '' : '手机号格式不正确';
  errors.password = isPassword(form.password)
    ? ''
    : '密码至少 8 位且同时包含字母和数字';
  return !errors.customerName && !errors.phone && !errors.password;
}

async function submit() {
  if (!validate()) return;
  loading.value = true;
  try {
    await session.register(
      form.customerName.trim(),
      form.password,
      form.phone.trim(),
      form.nickName.trim() || form.customerName.trim(),
    );
    uni.showToast({ title: '注册成功', icon: 'success' });
    setTimeout(() => uni.reLaunch({ url: '/pages/profile/index' }), 400);
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.register-page {
  min-height: 100vh;
  padding: $space-5;
  background: $bg-page;
}

.register-card {
  padding: $space-4;
  border-radius: 40rpx;
  background: #fff;
  box-shadow: 0 20rpx 60rpx rgba(0, 0, 0, 0.08);
}

.field {
  margin-top: $space-3;
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

.register-card__submit {
  margin-top: $space-5;
}
</style>
