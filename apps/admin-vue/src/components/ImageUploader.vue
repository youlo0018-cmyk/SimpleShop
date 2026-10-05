<template>
  <div class="image-uploader">
    <el-upload
      :file-list="fileList"
      :http-request="upload"
      :show-file-list="false"
      :multiple="multiple"
      accept="image/*"
      list-type="picture-card"
    >
      <div class="image-uploader__trigger">
        <el-icon><Plus /></el-icon>
        <span>上传图片</span>
      </div>
    </el-upload>
    <div v-if="urls.length" class="image-uploader__list">
      <div v-for="(url, index) in urls" :key="url + index" class="image-uploader__item">
        <img :src="url" alt="已上传图片" />
        <button type="button" class="image-uploader__remove" @click="remove(index)">×</button>
      </div>
    </div>
    <p class="image-uploader__hint">{{ hint || (multiple ? '可上传多张图片' : '支持图片文件') }}</p>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { ElMessage, type UploadRequestOptions } from 'element-plus';
import { Plus } from '@element-plus/icons-vue';
import { getToken } from '@/api/request';

const props = withDefaults(defineProps<{
  modelValue?: string | string[];
  multiple?: boolean;
  hint?: string;
}>(), {
  modelValue: '',
  multiple: false,
  hint: '',
});

const emit = defineEmits<{ 'update:modelValue': [value: string | string[]] }>();

const urls = computed<string[]>(() => {
  if (Array.isArray(props.modelValue)) return props.modelValue.filter(Boolean);
  return props.modelValue ? [props.modelValue] : [];
});

const fileList = computed(() =>
  urls.value.map((url, index) => ({ name: `图片 ${index + 1}`, url })),
);

async function upload(options: UploadRequestOptions) {
  const form = new FormData();
  form.append('file', options.file);
  const response = await fetch('/gateway/files/Upload', {
    method: 'POST',
    headers: { Authorization: `Bearer ${getToken()}` },
    body: form,
  });
  const payload = await response.json();
  if (!response.ok || payload?.success === false) {
    const message = payload?.message || '图片上传失败';
    ElMessage.error(message);
    options.onError({ name: 'UploadError', message, status: response.status, method: 'POST', url: '/gateway/files/Upload' } as any);
    return;
  }
  const url = payload?.data?.publicUrl || '';
  const next = props.multiple ? [...urls.value, url] : [url];
  emit('update:modelValue', props.multiple ? next : url);
  options.onSuccess(payload);
}

function remove(index: number) {
  const next = urls.value.filter((_, current) => current !== index);
  emit('update:modelValue', props.multiple ? next : '');
}
</script>

<style scoped>
.image-uploader__trigger {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-1);
  color: var(--text-2);
  font-size: var(--text-foot);
}

.image-uploader__list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-2);
}

.image-uploader__item {
  position: relative;
  width: 96px;
  height: 96px;
  overflow: hidden;
  border-radius: var(--radius-sm);
  background: var(--bg-page);
}

.image-uploader__item img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.image-uploader__remove {
  position: absolute;
  top: 4px;
  right: 4px;
  width: 22px;
  height: 22px;
  border: none;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.55);
  color: #fff;
  cursor: pointer;
}

.image-uploader__hint {
  margin: var(--space-1) 0 0;
  color: var(--text-3);
  font-size: var(--text-note);
}
</style>
