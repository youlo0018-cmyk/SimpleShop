<template>
  <div>
    <div class="page-header">
      <div>
        <h2 class="page-header__title">分配权限</h2>
        <p class="page-header__desc">
          {{ role.roleName ? `${role.roleName}（${role.code}）` : '正在读取角色…' }}
        </p>
      </div>
      <div class="tools">
        <el-button :disabled="loading || role.isBuiltin" @click="selectAll">全部权限</el-button>
        <el-button :disabled="loading || role.isBuiltin" @click="clearAll">清空</el-button>
        <el-button type="primary" :loading="saving" :disabled="loading || role.isBuiltin" @click="save">
          保存
        </el-button>
      </div>
    </div>

    <div v-if="role.isBuiltin" class="notice">内置管理员角色的权限锁定，不能在界面重绑。</div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 8" :key="i" class="skeleton-row" />
      </div>
      <el-tree
        v-else
        ref="treeRef"
        :data="permissions"
        node-key="id"
        show-checkbox
        default-expand-all
        :props="{ label: 'name', children: 'children', disabled: (data: any) => data.selectable === false }"
      >
        <template #default="{ data }">
          <span class="perm">
            <span>{{ data.name }}</span>
            <span class="perm__code mono">{{ data.code || '—' }}</span>
            <span class="perm__path mono">{{ data.apiPath || '仅菜单权限' }}</span>
            <span v-if="Number(data.status) === 2" class="pill pill--neutral">停用</span>
          </span>
        </template>
      </el-tree>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, type ElTree } from 'element-plus';
import request from '@/api/request';

const route = useRoute();
const router = useRouter();
const treeRef = ref<InstanceType<typeof ElTree>>();
const loading = ref(true);
const saving = ref(false);
const permissions = ref<any[]>([]);
const role = reactive({
  roleName: '',
  code: '',
  isBuiltin: false,
});

// 只收**叶子**（有 code 的节点）。业务大类 / 功能模块 / 虚拟根「全部权限」都是容器，
// 它们只是勾选快捷方式，不是权限点本身 —— BUSINESS.md 5.4「半选节点不保存，只保存叶子」。
// 之前把容器 Id（含虚拟根 0 和没有权限点的空模块）一并提交，等于往 role_permission
// 里写进一批查不到 code 的绑定：权限不会多出来，但角色详情回显与「已绑定 N 项」计数都会失真。
function leafIds(nodes: any[]): string[] {
  return nodes.flatMap((node) => [
    ...(node.code ? [String(node.id)] : []),
    ...leafIds(node.children || []),
  ]);
}

function checkedIds(): string[] {
  const tree = treeRef.value;
  if (!tree) return [];
  const leaves = new Set(leafIds(permissions.value));
  return tree
    .getCheckedKeys(false)
    .map(String)
    .filter((id) => leaves.has(id));
}

async function load() {
  loading.value = true;
  try {
    const [detail, tree] = await Promise.all([
      request('/gateway/roles/Detail', {
        method: 'GET',
        params: { roleId: route.params.id },
      }),
      request('/gateway/permissions/Tree', { method: 'GET' }),
    ]);
    Object.assign(role, detail || {});
    permissions.value = Array.isArray(tree) ? tree : [];
    const ids = (detail?.permissionIds || []).map(String);
    await Promise.resolve();
    treeRef.value?.setCheckedKeys(ids, false);
  } catch {
    permissions.value = [];
  } finally {
    loading.value = false;
  }
}

function selectAll() {
  treeRef.value?.setCheckedKeys(leafIds(permissions.value), false);
}

function clearAll() {
  treeRef.value?.setCheckedKeys([], false);
}

async function save() {
  saving.value = true;
  try {
    await request('/gateway/roles/BindPermissions', {
      body: {
        roleId: route.params.id,
        permissionIds: checkedIds(),
      },
    });
    ElMessage.success('权限已保存');
    router.push('/roles');
  } catch {
    // 统一错误提示由 request 处理
  } finally {
    saving.value = false;
  }
}

onMounted(load);
</script>

<style scoped>
.tools {
  display: flex;
  gap: var(--space-2);
}

.notice {
  margin-bottom: var(--space-4);
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  background: var(--warning-bg);
  color: var(--warning-fg);
  font-size: var(--text-foot);
}

.skel {
  display: grid;
  gap: var(--space-3);
  padding: var(--space-6);
}

.perm {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-width: 0;
}

.perm__code {
  color: var(--text-2);
}

.perm__path {
  color: var(--text-3);
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
