<template>
  <div class="login">
    <div class="login__card">
      <div class="login__brand">
        <div class="login__mark">S</div>
        <h1 class="login__title">SimpleShop</h1>
        <p class="login__sub">管理后台</p>
      </div>

      <form class="login__form" @submit.prevent="onSubmit">
        <div class="field">
          <label class="field__label" for="username">账号</label>
          <input
            id="username"
            v-model="form.username"
            class="field__input"
            :class="{ 'field__input--error': errors.username }"
            type="text"
            autocomplete="username"
            placeholder="请输入账号"
          />
          <p v-if="errors.username" class="field__error">{{ errors.username }}</p>
        </div>

        <div class="field">
          <label class="field__label" for="password">密码</label>
          <input
            id="password"
            v-model="form.password"
            class="field__input"
            :class="{ 'field__input--error': errors.password }"
            type="password"
            autocomplete="current-password"
            placeholder="请输入密码"
          />
          <p v-if="errors.password" class="field__error">{{ errors.password }}</p>
        </div>

        <button class="login__submit" type="submit" :disabled="loading">
          {{ loading ? '登录中…' : '登录' }}
        </button>
      </form>
    </div>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import { login } from '@/api/auth';

const route = useRoute();
const router = useRouter();

const form = reactive({ username: '', password: '' });
const errors = reactive({ username: '', password: '' });
const loading = ref(false);

// 提交时全量验证（DESIGN_SPEC 5.6：**只在提交时验证，不做失焦验证**）。
// 输入过程中飘红会让用户还没打完字就被告知「格式不对」，体验很差。
function validate(): boolean {
  errors.username = form.username.trim() ? '' : '请输入账号';
  errors.password = form.password ? '' : '请输入密码';
  return !errors.username && !errors.password;
}

async function onSubmit() {
  if (!validate()) {
    // 自动聚焦第一个错误字段并滚动到可见区域（DESIGN_SPEC 5.6）
    const first = document.getElementById(errors.username ? 'username' : 'password');
    first?.focus();
    first?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return;
  }

  loading.value = true;
  try {
    await login(form.username.trim(), form.password);
    const redirect = (route.query.redirect as string) || '/dashboard';
    router.replace(redirect);
  } catch (err: any) {
    // 账号密码错误属于后端返回，用全局 tip 提示，**不飘红输入框**（DESIGN_SPEC 5.6）
    ElMessage.error(err?.message || '登录失败，请稍后重试');
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped>
.login {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  padding: var(--space-6);
  background: var(--bg-page);
}

.login__card {
  width: 400px;
  max-width: 100%;
  padding: var(--space-8);
  background: var(--bg-card);
  border-radius: var(--radius-card);
  box-shadow: var(--shadow-raised);
}

.login__brand {
  text-align: center;
  margin-bottom: var(--space-7);
}

.login__mark {
  width: 48px;
  height: 48px;
  margin: 0 auto var(--space-4);
  border-radius: var(--radius-md);
  background: var(--brand);
  color: #ffffff;
  font-size: var(--text-title-2);
  font-weight: 600;
  display: flex;
  align-items: center;
  justify-content: center;
}

.login__title {
  margin: 0;
  font-size: var(--text-title-1);
  line-height: var(--lh-title-1);
  font-weight: 600;
}

.login__sub {
  margin: var(--space-1) 0 0;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.login__form {
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}

.field__label {
  font-size: var(--text-sub);
  color: var(--text-2);
}

.field__input {
  height: 40px;
  padding: 0 var(--space-3);
  border: none;
  border-radius: var(--radius-sm);
  background: var(--bg-page);
  box-shadow: 0 0 0 1px var(--divider-soft) inset;
  font-family: var(--font-sans);
  font-size: var(--text-sub);
  color: var(--text-1);
  transition: box-shadow var(--duration-fast) var(--ease);
}

.field__input::placeholder {
  color: var(--text-3);
}

.field__input:focus {
  outline: none;
  box-shadow: 0 0 0 1px var(--brand) inset, var(--shadow-card);
}

.field__input--error {
  box-shadow: 0 0 0 1px var(--danger) inset;
}

.field__error {
  margin: 0;
  font-size: var(--text-note);
  line-height: var(--lh-note);
  color: var(--danger);
}

.login__submit {
  margin-top: var(--space-2);
  height: 44px;
  border: none;
  border-radius: var(--radius-pill);
  background: var(--brand);
  color: #ffffff;
  font-family: var(--font-sans);
  font-size: var(--text-sub);
  font-weight: 500;
  cursor: pointer;
  transition: background-color var(--duration-fast) var(--ease);
}

.login__submit:hover:not(:disabled) {
  background: var(--brand-hover);
}

.login__submit:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
