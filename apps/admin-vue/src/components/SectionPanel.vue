<template>
  <section class="section">
    <header class="section__head">
      <div class="section__lead">
        <h3 class="section__title">{{ title }}</h3>
        <span v-if="hint" class="section__hint">{{ hint }}</span>
      </div>
      <div v-if="$slots.actions" class="section__actions">
        <slot name="actions" />
      </div>
    </header>

    <div class="section__body" :class="{ 'section__body--flush': flush }">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
// 详情页的一段（金额构成 / 收货信息 / 支付记录 / 退款记录 …）。
//
// 为什么不直接写 `<section class="panel"><h3>标题</h3>…</section>`：
// 那个 h3 悬在面板里没有任何上下文，既不知道这段在讲什么，
// 也不知道右侧还能放操作。抽成组件之后三件事一次到位：
// 标题、可选的操作区、以及统一的段间距。
//
// flush：去掉正文内边距，用于内部本来就是表格的段落（表格自带间距）。
withDefaults(defineProps<{
  title: string;
  hint?: string;
  flush?: boolean;
}>(), {
  hint: '',
  flush: false,
});
</script>

<style scoped>
.section {
  background: var(--bg-card);
  border-radius: var(--radius-card);
  box-shadow: 0 0 0 0.5px var(--hairline);
}

.section__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-4) var(--space-6);
}

/* 头部与正文之间一律用发丝线分隔，视线先横扫过去再落回内容，
   不用逐行去找下一个字段从哪开始 */
.section__head {
  border-bottom: 0.5px solid var(--hairline);
}

.section__lead {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  min-width: 0;
}

.section__title {
  margin: 0;
  font-size: var(--text-sub);
  line-height: var(--lh-sub);
  font-weight: 600;
}

.section__hint {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.section__actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-shrink: 0;
}

.section__body {
  padding: var(--space-2) var(--space-6) var(--space-5);
}

.section__body--flush {
  padding: 0;
}
</style>
