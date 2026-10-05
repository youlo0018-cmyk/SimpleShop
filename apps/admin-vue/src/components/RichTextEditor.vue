<template>
  <div class="rich-editor">
    <div class="rich-editor__toolbar">
      <button type="button" title="加粗" @click="command('bold')"><b>B</b></button>
      <button type="button" title="斜体" @click="command('italic')"><i>I</i></button>
      <button type="button" title="标题" @click="command('formatBlock', 'h3')">H</button>
      <button type="button" title="列表" @click="command('insertUnorderedList')">•</button>
      <button type="button" title="插入图片" @click="pickImage">图</button>
    </div>
    <div
      ref="editorRef"
      class="rich-editor__body"
      contenteditable="true"
      :data-placeholder="placeholder"
      @input="onInput"
    />
    <input ref="fileRef" type="file" accept="image/*" hidden @change="uploadImage" />
  </div>
</template>

<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import { getToken } from '@/api/request';

const props = withDefaults(defineProps<{
  modelValue?: string;
  placeholder?: string;
}>(), {
  modelValue: '',
  placeholder: '请输入内容',
});
const emit = defineEmits<{ 'update:modelValue': [value: string] }>();
const editorRef = ref<HTMLDivElement>();
const fileRef = ref<HTMLInputElement>();

function sync(value: string) {
  if (editorRef.value && editorRef.value.innerHTML !== value) {
    editorRef.value.innerHTML = value || '';
  }
}

function onInput() {
  emit('update:modelValue', editorRef.value?.innerHTML || '');
}

function command(name: string, value?: string) {
  editorRef.value?.focus();
  document.execCommand(name, false, value);
  onInput();
}

function pickImage() {
  fileRef.value?.click();
}

async function uploadImage() {
  const file = fileRef.value?.files?.[0];
  if (!file) return;
  const form = new FormData();
  form.append('file', file);
  const response = await fetch('/gateway/files/Upload', {
    method: 'POST',
    headers: { Authorization: `Bearer ${getToken()}` },
    body: form,
  });
  const payload = await response.json();
  fileRef.value!.value = '';
  if (!response.ok || payload?.success === false) {
    ElMessage.error(payload?.message || '图片上传失败');
    return;
  }
  const url = payload?.data?.publicUrl || '';
  editorRef.value?.focus();
  document.execCommand('insertImage', false, url);
  onInput();
}

watch(() => props.modelValue, (value) => sync(value || ''));
onMounted(() => nextTick(() => sync(props.modelValue || '')));
</script>

<style scoped>
.rich-editor {
  overflow: hidden;
  border: 1px solid var(--divider-soft);
  border-radius: var(--radius-sm);
  background: var(--bg-card);
}

.rich-editor__toolbar {
  display: flex;
  gap: var(--space-1);
  padding: var(--space-2);
  border-bottom: 1px solid var(--hairline);
  background: var(--bg-page);
}

.rich-editor__toolbar button {
  min-width: 32px;
  height: 32px;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--text-1);
  cursor: pointer;
}

.rich-editor__toolbar button:hover {
  background: var(--bg-card);
}

.rich-editor__body {
  min-height: 180px;
  padding: var(--space-4);
  outline: none;
  line-height: 1.7;
}

.rich-editor__body:empty::before {
  color: var(--text-3);
  content: attr(data-placeholder);
}
</style>
