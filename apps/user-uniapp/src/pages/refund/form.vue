<template>
  <view class="page refund-form">
    <AppHeader title="申请退款" :subtitle="orderNo" back />

    <view class="panel summary">
      <text class="summary__label">退款方式</text>
      <text class="summary__value">整单退款（含运费）</text>
      <text class="summary__hint">提交后等待平台或商户审批；审批通过才真正退回。</text>
    </view>

    <view class="panel reason">
      <text class="reason__label">退款原因</text>
      <textarea
        v-model="reason"
        class="reason__input"
        maxlength="200"
        placeholder="请填写退款原因（2-200 个字符）"
      />
      <text class="reason__count">{{ reason.length }} / 200</text>
    </view>

    <button class="button-primary refund-form__submit" :loading="submitting" @tap="submit">
      提交退款申请
    </button>
  </view>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const orderNo = ref('');
const orderId = ref('');
const reason = ref('');
const submitting = ref(false);

async function submit() {
  const text = reason.value.trim();
  if (text.length < 2 || text.length > 200) {
    uni.showToast({ title: '退款原因需为 2-200 个字符', icon: 'none' });
    return;
  }

  submitting.value = true;
  try {
    await request('/gateway/refunds/Apply', {
      method: 'POST',
      data: {
        orderId: orderId.value,
        orderNo: orderNo.value,
        items: [],
        reason: text,
      },
    });
    uni.showToast({ title: '退款申请已提交', icon: 'success' });
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

  orderNo.value = String(options?.orderNo || '');
  orderId.value = String(options?.orderId || '');
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.refund-form {
  padding-bottom: 180rpx;
}

.panel {
  margin-bottom: $space-3;
  padding: $space-4;
}

.summary__label,
.reason__label {
  display: block;
  color: $text-2;
  font-size: $font-sub;
}

.summary__value {
  display: block;
  margin-top: $space-2;
  font-size: $font-heading;
  font-weight: 600;
}

.summary__hint {
  display: block;
  margin-top: $space-2;
  color: $text-2;
  font-size: $font-note;
  line-height: 1.5;
}

.reason__input {
  width: 100%;
  min-height: 220rpx;
  margin-top: $space-2;
  font-size: $font-body;
  line-height: 1.6;
}

.reason__count {
  display: block;
  color: $text-3;
  font-size: $font-note;
  text-align: right;
}

.refund-form__submit {
  position: fixed;
  right: $space-4;
  bottom: calc(env(safe-area-inset-bottom) + $space-4);
  left: $space-4;
}
</style>
