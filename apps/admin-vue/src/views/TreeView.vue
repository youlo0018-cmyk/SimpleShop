<template>
  <div>
    <div class="page-header">
      <div>
        <h2 class="page-header__title">{{ config.title }}</h2>
        <p class="page-header__desc">{{ config.desc }}</p>
      </div>
      <div class="tools">
        <el-input
          v-if="config.search"
          v-model="keyword"
          class="head__search"
          :placeholder="config.searchPlaceholder || '搜索'"
          clearable
        />
        <el-button
          v-if="hasPermission(config.createPermission)"
          type="primary"
          :icon="Plus"
          @click="openCreate(null)"
        >
          {{ config.title === '权限点管理' ? '新增一级权限' : '新增一级分类' }}
        </el-button>
      </div>
    </div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 6" :key="i" class="skeleton-row" />
      </div>

      <el-tree
        v-else-if="filtered.length"
        :data="filtered"
        :props="{ children: 'children', label: config.labelField }"
        node-key="id"
        default-expand-all
        :expand-on-click-node="false"
      >
        <template #default="{ data }">
          <span class="node">
            <span class="node__main">
              <span class="node__label">{{ data[config.nameField || config.labelField] }}</span>
              <span v-if="config.codeField && data[config.codeField]" class="node__code mono">
                {{ data[config.codeField] }}
              </span>
              <span v-if="config.sortField" class="node__sort">排序 {{ data[config.sortField] ?? 0 }}</span>
            </span>
            <span class="node__actions">
              <span v-if="config.statusField" class="pill" :class="statusClass(data)">
                {{ statusTextOf(data) }}
              </span>
              <el-button
                v-if="canAddChild(data) && hasPermission(config.createPermission)"
                link
                type="primary"
                @click.stop="openCreate(data)"
              >
                新增下级
              </el-button>
              <el-button
                v-if="canEdit(data) && hasPermission(config.updatePermission)"
                link
                type="primary"
                @click.stop="openEdit(data)"
              >
                编辑
              </el-button>
              <el-button v-if="config.statusEndpoint && hasPermission(config.updatePermission)" link @click.stop="toggleStatus(data)">
                {{ Number(data[config.statusField]) === 1 ? '停用' : '启用' }}
              </el-button>
              <el-button
                v-if="canDelete(data) && hasPermission(config.deletePermission)"
                link
                type="danger"
                @click.stop="askDelete(data)"
              >
                删除
              </el-button>
            </span>
          </span>
        </template>
      </el-tree>

      <div v-else class="empty">
        <div class="empty__title">{{ config.emptyHint || '暂无数据' }}</div>
      </div>
    </section>

    <el-dialog
      v-model="editor.open"
      :title="editor.title"
      width="560"
      :close-on-click-modal="false"
      align-center
    >
      <el-form label-position="top">
        <el-form-item
          v-for="field in config.fields"
          :key="field.name"
          :label="field.label"
          :error="editor.errors[field.name]"
          :class="{ 'form__wide': field.type === 'textarea' }"
        >
          <el-select v-if="field.type === 'select'" v-model="editor.values[field.name]" class="control">
            <el-option v-for="o in field.options || []" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
          <el-input
            v-else
            v-model="editor.values[field.name]"
            :type="field.type || 'text'"
            :rows="field.rows || 3"
            :maxlength="field.maxLength"
            :show-word-limit="field.maxLength != null"
            :placeholder="field.placeholder"
            class="control"
          />
          <p v-if="field.help" class="form__help">{{ field.help }}</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editor.open = false">取消</el-button>
        <el-button type="primary" :loading="editor.saving" @click="submitEditor">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="confirm.open" title="删除确认" width="440" align-center>
      <p class="confirm__text">
        删除后不可恢复。
        <span class="confirm__subject">{{ confirm.subject }}</span>
      </p>
      <template #footer>
        <el-button @click="confirm.open = false">取消</el-button>
        <el-button type="danger" :loading="confirm.saving" @click="submitDelete">删除</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import { Plus } from '@element-plus/icons-vue';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import { hasPermission } from '@/utils/session';

const props = defineProps<{ config: any }>();
const config = computed(() => props.config);

const loading = ref(true);
const keyword = ref('');
const nodes = ref<any[]>([]);
const editor = reactive({
  open: false,
  saving: false,
  mode: 'create' as 'create' | 'edit',
  title: '',
  parent: null as any,
  current: null as any,
  values: {} as Record<string, any>,
  errors: {} as Record<string, string>,
});
const confirm = reactive({
  open: false,
  saving: false,
  subject: '',
  current: null as any,
});

async function load() {
  loading.value = true;
  try {
    const data = await request(config.value.endpoint, {
      method: config.value.method || 'GET',
      params: config.value.includeDisabled ? { includeDisabled: true } : undefined,
      silent: true,
    });
    nodes.value = Array.isArray(data) ? data : (data?.items || []);
  } catch {
    nodes.value = [];
  } finally {
    loading.value = false;
  }
}

function statusClass(data: any) {
  return `pill--${statusColor(config.value.statusDict, data[config.value.statusField])}`;
}

function statusTextOf(data: any) {
  const value = data[config.value.statusField];
  return data[`${config.value.statusField}Name`] || statusText(config.value.statusDict, value);
}

function filterTree(list: any[], kw: string): any[] {
  const out: any[] = [];
  for (const node of list) {
    const children = node.children ? filterTree(node.children, kw) : [];
    const self = JSON.stringify(node).toLowerCase().includes(kw);
    if (self || children.length > 0) out.push({ ...node, children });
  }
  return out;
}

const filtered = computed(() => {
  const kw = keyword.value.trim().toLowerCase();
  return kw ? filterTree(nodes.value, kw) : nodes.value;
});

function canAddChild(data: any) {
  return Number(data.level || 1) < Number(config.value.maxLevel || 3);
}

function canEdit(data: any) {
  return !config.value.builtinField || !data[config.value.builtinField];
}

function canDelete(data: any) {
  return canEdit(data);
}

function openCreate(parent: any) {
  editor.mode = 'create';
  editor.parent = parent;
  editor.current = null;
  editor.title = parent ? '新增下级' : config.value.title.includes('权限') ? '新增一级权限' : '新增一级分类';
  editor.values = {};
  for (const field of config.value.fields || []) editor.values[field.name] = field.default ?? '';
  editor.errors = {};
  editor.open = true;
}

function openEdit(data: any) {
  editor.mode = 'edit';
  editor.current = data;
  editor.parent = null;
  editor.title = '编辑';
  editor.values = {};
  for (const field of config.value.fields || []) editor.values[field.name] = data[field.name] ?? field.default ?? '';
  editor.errors = {};
  editor.open = true;
}

function validate() {
  editor.errors = {};
  for (const field of config.value.fields || []) {
    const value = editor.values[field.name];
    const text = value === null || value === undefined ? '' : String(value).trim();
    if (field.required && !text) {
      editor.errors[field.name] = `请填写${field.label}`;
      continue;
    }
    if (field.maxLength && text.length > field.maxLength) {
      editor.errors[field.name] = `${field.label}不能超过 ${field.maxLength} 个字符`;
    }
  }
  return Object.keys(editor.errors).length === 0;
}

async function submitEditor() {
  if (!validate()) {
    ElMessage.warning('请检查标红字段');
    return;
  }

  editor.saving = true;
  try {
    const body: Record<string, any> = { ...editor.values };
    if (editor.mode === 'create') {
      body[config.value.parentField] = editor.parent ? String(editor.parent.id) : '0';
      if (config.value.title === '分类管理') body.platformId = '0';
    } else {
      body[config.value.idField] = String(editor.current.id);
    }
    for (const [key, value] of Object.entries(body)) {
      if (typeof value === 'string') body[key] = value.trim();
    }
    await request(
      editor.mode === 'create' ? config.value.createEndpoint : config.value.updateEndpoint,
      { body },
    );
    ElMessage.success(editor.mode === 'create' ? '创建成功' : '已保存');
    editor.open = false;
    await load();
  } catch {
    // request 已提示
  } finally {
    editor.saving = false;
  }
}

async function toggleStatus(data: any) {
  const next = Number(data[config.value.statusField]) === 1 ? 2 : 1;
  try {
    if (config.value.statusEndpoint) {
      await request(config.value.statusEndpoint, {
        body: { permissionId: String(data.id), status: next },
      });
    } else {
      const body: Record<string, any> = {
        [config.value.idField]: String(data.id),
        status: next,
      };
      for (const field of config.value.fields || []) {
        if (field.name !== 'status') body[field.name] = data[field.name] ?? field.default ?? '';
      }
      await request(config.value.updateEndpoint, { body });
    }
    ElMessage.success(next === 1 ? '已启用' : '已停用');
    await load();
  } catch {
    // request 已提示
  }
}

function askDelete(data: any) {
  confirm.subject = data[config.value.nameField || config.value.labelField] || '';
  confirm.current = data;
  confirm.open = true;
}

async function submitDelete() {
  confirm.saving = true;
  try {
    await request(config.value.deleteEndpoint, {
      body: config.value.title === '权限点管理'
        ? { permissionId: String(confirm.current.id) }
        : { categoryId: String(confirm.current.id) },
    });
    ElMessage.success('已删除');
    confirm.open = false;
    await load();
  } catch {
    // request 已提示
  } finally {
    confirm.saving = false;
  }
}

watch(config, load);
onMounted(load);
</script>

<style scoped>
.tools {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.head__search {
  width: 280px;
}

.skel {
  display: grid;
  gap: var(--space-3);
  padding: var(--space-6);
}

.node {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  width: 100%;
  min-height: 38px;
  padding-right: var(--space-2);
}

.node__main,
.node__actions {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-width: 0;
}

.node__label {
  font-size: var(--text-sub);
}

.node__code,
.node__sort {
  color: var(--text-2);
  font-size: var(--text-foot);
}

.node__code {
  max-width: 420px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.empty {
  padding: var(--space-8) 0;
  text-align: center;
}

.empty__title {
  color: var(--text-2);
}

.control {
  width: 100%;
}

.form__wide {
  grid-column: 1 / -1;
}

.form__help {
  margin: var(--space-1) 0 0;
  color: var(--text-2);
  font-size: var(--text-foot);
}

.confirm__text {
  margin: 0;
  color: var(--text-1);
}

.confirm__subject {
  display: block;
  margin-top: var(--space-2);
  font-weight: 600;
}
</style>
