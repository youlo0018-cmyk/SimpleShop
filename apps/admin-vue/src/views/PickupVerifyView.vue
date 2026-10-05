<template>
  <div>
    <div class="head">
      <h2 class="head__title">取货码核销</h2>
      <p class="head__desc">扫客户出示的取货码完成自提取货</p>
    </div>

    <section class="panel">
      <el-form label-position="top" class="form" @submit.prevent>
        <el-form-item label="取货码">
          <el-input
            v-model="code"
            class="form__control"
            placeholder="请输入或扫描取货码"
            clearable
            @keyup.enter="verify"
          />
        </el-form-item>

        <div class="form__actions">
          <el-button type="primary" :loading="loading" @click="verify">核销</el-button>
          <el-button :disabled="!result" @click="clear">清空</el-button>
        </div>
      </el-form>

      <div v-if="result" class="result">
        <div class="result__row">
          <span class="result__label">订单号</span>
          <span class="result__value result__value--mono">{{ emptyText(result.orderNo) }}</span>
        </div>
        <div class="result__row">
          <span class="result__label">订单状态</span>
          <span class="pill" :class="'pill--' + statusColor('order', result.status)">
            {{ result.statusName || statusText('order', result.status) }}
          </span>
        </div>
        <p class="result__msg" :class="result.verified ? 'result__msg--ok' : 'result__msg--warn'">
          {{ result.verified ? '核销成功，订单已完成' : '未发生核销，请核对取货码是否正确' }}
        </p>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import { emptyText } from '@/utils/format';

const code = ref('');
const loading = ref(false);
const result = ref<any>(null);

async function verify() {
  const value = code.value.trim();
  if (!value) {
    ElMessage.warning('请先输入取货码');
    return;
  }

  loading.value = true;
  try {
    const res = await request('/gateway/admin/orders/VerifyPickupCode', {
      body: { pickupCode: value },
    });
    result.value = res;
    if (res?.verified) ElMessage.success('核销成功');
  } catch {
    // request 已弹提示；清掉上一次的结果，避免界面上留着一条不属于本次输入的订单
    result.value = null;
  } finally {
    loading.value = false;
  }
}

function clear() {
  code.value = '';
  result.value = null;
}
</script>

<style scoped>
.head {
  margin-bottom: var(--space-5);
}

.head__title {
  margin: 0;
  font-size: var(--text-display);
  line-height: var(--lh-display);
  font-weight: 600;
  letter-spacing: -0.5px;
}

.head__desc {
  margin: var(--space-1) 0 0;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.panel {
  padding: var(--space-6);
}

.form {
  max-width: 420px;
}

.form__control {
  width: 100%;
}

.form__actions {
  display: flex;
  gap: var(--space-2);
}

.result {
  margin-top: var(--space-6);
  padding-top: var(--space-4);
  border-top: 1px solid var(--hairline);
  max-width: 420px;
}

.result__row {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-2) 0;
}

.result__label {
  width: 72px;
  font-size: var(--text-foot);
  color: var(--text-2);
}

.result__value {
  font-size: var(--text-sub);
}

.result__value--mono {
  font-family: var(--font-mono);
}

.result__msg {
  margin: var(--space-3) 0 0;
  font-size: var(--text-sub);
  font-weight: 500;
}

.result__msg--ok {
  color: var(--success-fg);
}

.result__msg--warn {
  color: var(--warning-fg);
}
</style>
