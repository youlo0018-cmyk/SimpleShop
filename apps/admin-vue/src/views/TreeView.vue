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

      <div v-else-if="filtered.length" class="tree-layout">
        <div class="tree-layout__tree">
          <el-tree
            :data="filtered"
            :props="{ children: 'children', label: config.labelField }"
            node-key="id"
            :default-expand-all="false"
            :expand-on-click-node="false"
            @node-click="selectNode"
          >
            <template #default="{ data }">
              <span class="node">
                <span class="node__main">
                  <span class="node__label">{{ data[config.nameField || config.labelField] }}</span>
                  <span v-if="config.codeField && data[config.codeField]" class="node__code mono">
                    {{ data[config.codeField] }}
                  </span>
                </span>
                <span v-if="config.statusField" class="pill" :class="statusClass(data)">
                  {{ statusTextOf(data) }}
                </span>
              </span>
            </template>
          </el-tree>
        </div>

        <aside class="tree-layout__detail">
          <template v-if="selected">
            <div class="detail-title">
              <h3>{{ selected[config.nameField || config.labelField] }}</h3>
              <span v-if="config.statusField" class="pill" :class="statusClass(selected)">
                {{ statusTextOf(selected) }}
              </span>
            </div>
            <div class="detail-grid">
              <div v-for="field in config.fields || []" :key="field.name" class="detail-row">
                <span>{{ field.label }}</span>
                <strong>{{ selected[field.name] || '—' }}</strong>
              </div>
            </div>
            <div class="detail-actions">
              <el-button
                v-if="canAddChild(selected) && hasPermission(config.createPermission)"
                @click="openCreate(selected)"
              >
                新增下级
              </el-button>
              <el-button
                v-if="canEdit(selected) && hasPermission(config.updatePermission)"
                type="primary"
                @click="openEdit(selected)"
              >
                修改
              </el-button>
              <!--
                有 statusEndpoint 的（权限点）走专用启停接口；没有的（分类）走 Update
                整行提交。以前这里只认 statusEndpoint，于是分类页永远不显示这个按钮，
                `toggleStatus` 里那段 else 分支成了**不可达代码** ——
                分类想停用只能进「修改」弹窗改状态，看起来像是漏做了这个按钮。
              -->
              <el-button
                v-if="config.updateEndpoint && hasPermission(config.updatePermission)"
                @click="toggleStatus(selected)"
              >
                {{ Number(selected[config.statusField]) === 1 ? '停用' : '启用' }}
              </el-button>
              <el-button
                v-if="canDelete(selected) && hasPermission(config.deletePermission)"
                type="danger"
                @click="askDelete(selected)"
              >
                删除
              </el-button>
            </div>
          </template>
          <div v-else class="detail-empty">选择左侧节点查看详情</div>
        </aside>
      </div>

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
const selected = ref<any>(null);
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
  return Boolean(data);
}

function canDelete(data: any) {
  return !config.value.builtinField || !data[config.value.builtinField];
}

function selectNode(data: any) {
  selected.value = data;
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
      // 用 idBodyField（请求体字段名），不是 idField（节点字段名）。
      // 两者混用会让编辑请求发成 `{ id }`，后端命令收不到 CategoryId / PermissionId，
      // 校验器直接判「Id 必须为正数」—— 这条路径此前从未被点通过。
      body[config.value.idBodyField || config.value.idField] = String(editor.current.id);
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
        body: { [config.value.idBodyField || config.value.idField]: String(data.id), status: next },
      });
    } else {
      const body: Record<string, any> = {
        [config.value.idBodyField || config.value.idField]: String(data.id),
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
      // 删除也走同一个字段名。以前这里硬编码了一个三元表达式（按标题判断），
      // 正是「同一个 Id 在四个地方各写一遍」的根源 —— 改对了删除、漏了编辑。
      body: { [config.value.idBodyField || config.value.idField]: String(confirm.current.id) },
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

.tree-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 360px;
  min-height: 560px;
}

.tree-layout__tree {
  min-width: 0;
  padding: var(--space-4);
}

.tree-layout__detail {
  border-left: 0.5px solid var(--hairline);
  padding: var(--space-5);
}

.detail-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  margin-bottom: var(--space-4);
}

.detail-title h3 {
  margin: 0;
  font-size: var(--text-title-3);
}

.detail-grid {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.detail-row {
  display: flex;
  justify-content: space-between;
  gap: var(--space-3);
  padding-bottom: var(--space-2);
  border-bottom: 0.5px solid var(--hairline);
  color: var(--text-2);
  font-size: var(--text-foot);
}

.detail-row strong {
  max-width: 210px;
  overflow: hidden;
  color: var(--text-1);
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-5);
}

.detail-empty {
  padding-top: 160px;
  color: var(--text-3);
  text-align: center;
}

@media (max-width: 1100px) {
  .tree-layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .tree-layout__detail {
    border-top: 0.5px solid var(--hairline);
    border-left: none;
  }
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
