<template>
  <div class="uploader" :class="`uploader--${size}`">
    <!--
      每个已上传的图就是一块「瓦片」，上传入口也是一块瓦片。
      之前上传区在上面、已传图在下面两块分离，运营传完图要往下看一眼才知道传没传上，
      而且看不出顺序对不对 —— 而轮播图的顺序就是展示顺序。
    -->
    <div class="uploader__grid">
      <div
        v-for="(url, index) in urls"
        :key="url + index"
        class="tile"
        :class="{ 'tile--dragging': dragIndex === index, 'tile--over': overIndex === index && dragIndex !== index }"
        :draggable="sortable && urls.length > 1"
        @dragstart="onDragStart(index)"
        @dragover.prevent="onDragOver(index)"
        @dragleave="overIndex = -1"
        @drop.prevent="onDrop(index)"
        @dragend="onDragEnd"
      >
        <img :src="url" alt="" />

        <!-- 序号：轮播图的第几张就是展示顺序，必须看得见 -->
        <span v-if="showIndex" class="tile__index">{{ index + 1 }}</span>

        <!-- 悬停才出现的操作层：平时干净，鼠标移上去才有图标 -->
        <div class="tile__overlay">
          <button
            v-if="sortable && urls.length > 1"
            type="button"
            class="tile__act tile__act--drag"
            title="按住拖动可调整顺序"
            tabindex="-1"
          >
            <el-icon><Rank /></el-icon>
          </button>
          <button
            type="button"
            class="tile__act"
            :title="urls.length === 1 ? '替换图片' : '替换这一张'"
            @click.stop="pick(index)"
          >
            <el-icon><Refresh /></el-icon>
          </button>
          <button
            type="button"
            class="tile__act tile__act--danger"
            title="移除"
            @click.stop="remove(index)"
          >
            <el-icon><Delete /></el-icon>
          </button>
        </div>
      </div>

      <!-- 上传瓦片：到上限就没了，而不是传上去才被后端拒 -->
      <button
        v-if="canAdd"
        type="button"
        class="tile tile--add"
        :disabled="uploading"
        @click="pick(urls.length)"
      >
        <el-icon><Plus /></el-icon>
        <span v-if="size === 'default'">{{ uploading ? '上传中' : '上传图片' }}</span>
      </button>
    </div>

    <!-- 隐藏的原生 file input：瓦片上的按钮只是触发它。
         不直接用 el-upload 是因为它的文件列表样式没法塞进瓦片网格里，
         而 el-upload 的上传逻辑（走 /gateway/files/Upload）这里只需要几十行。 -->
    <input
      ref="fileInput"
      class="uploader__input"
      type="file"
      accept="image/*"
      :multiple="multiple && remaining > 1"
      @change="onFiles"
    />

    <p class="uploader__hint">{{ hintText }}</p>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { Delete, Plus, Rank, Refresh } from '@element-plus/icons-vue';
import { getToken } from '@/api/request';

const props = withDefaults(defineProps<{
  modelValue?: string | string[];
  multiple?: boolean;
  /** 最多几张。单图模式恒为 1。轮播图用 8。 */
  max?: number;
  /** compact = SKU 图那种 80×80 小图；default = 主图 / 轮播图。 */
  size?: 'default' | 'compact';
  /** 是否显示序号徽标（轮播图需要知道第几张）。 */
  showIndex?: boolean;
  /** 是否允许拖动排序。 */
  sortable?: boolean;
  hint?: string;
}>(), {
  modelValue: '',
  multiple: false,
  max: 8,
  size: 'default',
  showIndex: false,
  sortable: false,
  hint: '',
});

const emit = defineEmits<{ 'update:modelValue': [value: string | string[]] }>();

const fileInput = ref<HTMLInputElement>();
const uploading = ref(false);
// 记录「替换」的目标下标：-1 表示新增，否则替换该下标那一张
const replaceIndex = ref(-1);
const dragIndex = ref(-1);
const overIndex = ref(-1);

const urls = computed<string[]>(() => {
  if (Array.isArray(props.modelValue)) return props.modelValue.filter(Boolean);
  return props.modelValue ? [props.modelValue] : [];
});

const limit = computed(() => (props.multiple ? props.max : 1));
const remaining = computed(() => Math.max(0, limit.value - urls.value.length));
const canAdd = computed(() => remaining.value > 0);

const hintText = computed(() => {
  // 紧凑模式（SKU 图）不显示提示：那一列本来就窄，
  // 一句「建议 1:1，支持 JPG / PNG / WebP」会把整行撑高一倍
  if (props.size === 'compact') return '';
  if (props.hint) return props.hint;
  if (!props.multiple) return '建议 1:1，支持 JPG / PNG / WebP';
  return `最多 ${limit.value} 张，可拖动调整顺序`;
});

function pick(index: number) {
  replaceIndex.value = index;
  const input = fileInput.value;
  if (!input) return;
  // 先把 value 清空，否则连续选**同一个文件**时 change 不会触发
  // （浏览器认为值没变），表现为「点了没反应」。
  input.value = '';
  input.click();
}

async function onFiles(event: Event) {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files || []);
  if (!files.length) return;

  uploading.value = true;
  try {
    const uploaded: string[] = [];
    for (const file of files) {
      // 逐个上传而不是并发：并发 8 个大图会让 ToolService 一次吃满内存，
      // 而且失败时说不清是哪一张。
      const url = await uploadOne(file);
      if (url) uploaded.push(url);
    }
    if (!uploaded.length) return;

    const next = [...urls.value];
    if (replaceIndex.value >= 0 && replaceIndex.value < next.length) {
      next.splice(replaceIndex.value, 1, uploaded[0]);
    } else {
      next.push(...uploaded);
    }
    // 兜底截断：用户一次选了 10 张而只剩 2 个名额，多余的丢弃而不是提交后被后端拒
    emit('update:modelValue', props.multiple ? next.slice(0, limit.value) : (next[0] || ''));
    replaceIndex.value = -1;
  } finally {
    uploading.value = false;
    input.value = '';
  }
}

async function uploadOne(file: File): Promise<string> {
  const form = new FormData();
  form.append('file', file);
  try {
    const response = await fetch('/gateway/files/Upload', {
      method: 'POST',
      headers: { Authorization: `Bearer ${getToken()}` },
      body: form,
    });
    const payload = await response.json();
    if (!response.ok || payload?.success === false) {
      ElMessage.error(payload?.message || `${file.name} 上传失败`);
      return '';
    }
    return payload?.data?.publicUrl || '';
  } catch {
    ElMessage.error(`${file.name} 上传失败`);
    return '';
  }
}

function remove(index: number) {
  const next = urls.value.filter((_, current) => current !== index);
  emit('update:modelValue', props.multiple ? next : '');
}

function onDragStart(index: number) {
  dragIndex.value = index;
}

function onDragOver(index: number) {
  if (dragIndex.value < 0) return;
  overIndex.value = index;
}

function onDrop(index: number) {
  const from = dragIndex.value;
  dragIndex.value = -1;
  overIndex.value = -1;
  if (from < 0 || from === index) return;

  const next = [...urls.value];
  const [item] = next.splice(from, 1);
  next.splice(index, 0, item);
  emit('update:modelValue', next);
}

function onDragEnd() {
  dragIndex.value = -1;
  overIndex.value = -1;
}
</script>

<style scoped>
.uploader__grid {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
}

.tile {
  position: relative;
  width: 104px;
  height: 104px;
  overflow: hidden;
  padding: 0;
  border: 0.5px solid var(--hairline);
  border-radius: var(--radius-md);
  background: var(--bg-page);
  cursor: pointer;
  transition: border-color var(--duration-fast) var(--ease),
    opacity var(--duration-fast) var(--ease);
}

.uploader--compact .tile {
  /* SKU 图固定 80×80：SKU 表格每行都放一张，再大就把价格挤到要横向滚动了 */
  width: 80px;
  height: 80px;
}

.tile img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.tile--dragging {
  opacity: 0.4;
}

.tile--over {
  border-color: var(--brand);
}

.tile__index {
  position: absolute;
  top: 4px;
  left: 4px;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: var(--radius-sm);
  background: rgba(0, 0, 0, 0.55);
  color: #fff;
  font-size: var(--text-note);
  line-height: 18px;
  text-align: center;
}

.tile__overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--space-1);
  background: rgba(0, 0, 0, 0.45);
  opacity: 0;
  transition: opacity var(--duration-fast) var(--ease);
}

/* 鼠标移上去才显示操作图标：默认状态下只看到干净的图 */
.tile:hover .tile__overlay {
  opacity: 1;
}

.tile__act {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border: none;
  border-radius: var(--radius-sm);
  background: rgba(255, 255, 255, 0.92);
  color: var(--text-1);
  font-size: 15px;
  cursor: pointer;
}

.tile__act:hover {
  background: #fff;
}

.tile__act--danger {
  color: var(--danger-fg);
}

.tile__act--drag {
  cursor: grab;
}

.tile--add {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--space-1);
  border-style: dashed;
  border-color: var(--divider-soft);
  background: var(--bg-card);
  color: var(--text-2);
  font-size: var(--text-note);
}

.tile--add:hover:not(:disabled) {
  border-color: var(--brand);
  color: var(--brand);
}

.uploader__input {
  display: none;
}

.uploader__hint {
  margin: var(--space-2) 0 0;
  color: var(--text-3);
  font-size: var(--text-note);
}

/* 没有提示文案时不留空节点，否则紧凑模式会多出一段空白边距 */
.uploader__hint:empty {
  display: none;
}
</style>
