<template>
  <div class="ph">
    <div class="card ph__card">
      <el-icon class="ph__icon"><Tools /></el-icon>
      <h2 class="ph__title">{{ title }}</h2>
      <p class="ph__desc">该页面尚未实现。</p>
      <p v-if="perm" class="ph__perm">
        对应后端权限点：<span class="mono">{{ perm }}</span>
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { Tools } from '@element-plus/icons-vue';

// 待实现页面的统一承接页。
// 明确写「尚未实现 + 对应权限点」而不是留白：运营看到空白会以为系统坏了，
// 而写清楚它对应哪个权限点，接手的人一眼知道这块的前后端边界在哪。
const props = defineProps<{ title?: string; perm?: string }>();

const title = props.title || '页面';
const perm = props.perm || '';
</script>

<style scoped>
.ph {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 420px;
}

.ph__card {
  text-align: center;
  max-width: 420px;
}

.ph__icon {
  font-size: var(--text-display);
  color: var(--text-3);
}

.ph__title {
  margin: var(--space-4) 0 var(--space-2);
  font-size: var(--text-title-2);
  line-height: var(--lh-title-2);
  font-weight: 600;
}

.ph__desc {
  margin: 0 0 var(--space-2);
  font-size: var(--text-sub);
  color: var(--text-2);
}

.ph__perm {
  margin: 0;
  font-size: var(--text-foot);
  color: var(--text-3);
}
</style>
