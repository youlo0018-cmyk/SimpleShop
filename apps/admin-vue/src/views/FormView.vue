<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
    </div>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 4" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <el-form v-else ref="formRef" class="form" label-position="top" @submit.prevent>
        <div class="form__grid">
          <el-form-item
            v-for="f in visibleFields"
            :key="f.field"
            :label="f.label"
            :class="['form__item', { 'form__item--wide': f.type === 'textarea' }]"
            :error="errors[f.field]"
          >
            <el-select
              v-if="f.type === 'select'"
              v-model="model[f.field]"
              class="form__control"
              :placeholder="f.placeholder"
              :multiple="f.multiple"
              :filterable="f.filterable"
              :disabled="isLocked(f)"
              clearable
            >
              <el-option
                v-for="o in optionsOf(f)"
                :key="o.value"
                :label="o.label"
                :value="o.value"
              />
            </el-select>

            <el-color-picker
              v-else-if="f.type === 'color'"
              v-model="model[f.field]"
              :disabled="isLocked(f)"
            />

            <el-input
              v-else-if="f.type === 'textarea'"
              v-model="model[f.field]"
              type="textarea"
              :rows="f.rows || 4"
              class="form__control"
              :placeholder="f.placeholder"
              :disabled="isLocked(f)"
            />

            <el-input
              v-else-if="f.type === 'number'"
              v-model="model[f.field]"
              type="number"
              class="form__control"
              :placeholder="f.placeholder"
              :disabled="isLocked(f)"
            />

            <el-date-picker
              v-else-if="f.type === 'datetime'"
              v-model="model[f.field]"
              type="datetime"
              class="form__control"
              placeholder="选择日期时间"
              value-format="YYYY-MM-DDTHH:mm:ss"
              :disabled="isLocked(f)"
            />

            <el-input
              v-else
              v-model="model[f.field]"
              class="form__control"
              :type="f.secret ? 'password' : 'text'"
              :show-password="!!f.secret"
              :placeholder="f.placeholder"
              :disabled="isLocked(f)"
            />

            <p v-if="f.help" class="form__help">{{ f.help }}</p>
          </el-form-item>
        </div>

        <div v-if="config.permissionTree" class="permission-block">
          <div class="permission-block__head">
            <div>
              <h3>绑定权限点</h3>
              <p>勾选这个角色可以执行的模块与动作</p>
            </div>
            <div class="permission-block__tools">
              <el-button link type="primary" @click="selectAllPermissions">全部权限</el-button>
              <el-button link @click="clearPermissions">清空</el-button>
            </div>
          </div>
          <el-tree
            ref="permissionTreeRef"
            :data="permissionTree"
            node-key="id"
            show-checkbox
            default-expand-all
            :props="{ label: 'name', children: 'children' }"
          >
            <template #default="{ data }">
              <span class="permission-node">
                <span>{{ data.name }}</span>
                <span class="permission-node__code mono">{{ data.code || '仅菜单权限' }}</span>
              </span>
            </template>
          </el-tree>
        </div>

        <div class="form__actions">
          <el-button @click="cancel">取消</el-button>
          <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
        </div>
      </el-form>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { optionSources } from '@/router/form-configs';
import { toOptions } from '@/utils/options';

const props = defineProps<{ config: any; id?: string }>();
const route = useRoute();
const router = useRouter();

const config = computed(() => props.config);
// 雪花 Id 按字符串保存；Number() 会丢末位精度。
const entityId = computed(() => String(props.id || route.params.id || ''));
const isEdit = computed(() => entityId.value !== '' && entityId.value !== '0');

/**
 * 这个字段当前是否不可编辑。
 *
 * `readonlyInEdit` 只在编辑态锁定（编码这类「创建后不可改」的字段）；
 * `disabled` 则新建与编辑都锁定 —— 用于**根本没有第二个合法取值**的字段，
 * 比如券模板的「每单限用」按规格恒为 1。
 *
 * 之前只有前者，于是那种字段在新建时仍然能填：填了保存，
 * 后端也收，但实际不生效 —— 一个配了却不生效的旋钮比没有旋钮更糟。
 */
function isLocked(f: any): boolean {
  return Boolean(f.disabled) || (isEdit.value && Boolean(f.readonlyInEdit));
}

const loading = ref(false);
const saving = ref(false);
const model = reactive<Record<string, any>>({});
const errors = reactive<Record<string, string>>({});
// 加载到的**完整**记录（含表单上没显示的字段），提交时用于补齐后端要求的必填项。
const loaded = ref<Record<string, any>>({});

// 下拉数据源。平台 / 商户 / 角色 / 券模板都要从后端取，
// 且必须显示 name 而不是 id（DATA_SPEC 4.1）。
const optionCache = reactive<Record<string, any[]>>({});
const permissionTreeRef = ref<any>();
const permissionTree = ref<any[]>([]);

function optionsOf(f: any) {
  if (f.static) return f.static;
  if (f.options === 'platforms' && isEdit.value && String(loaded.value.platformId || '') === '0') {
    return [
      { value: '0', label: '平台超管（不归属具体平台）' },
      ...(optionCache[f.options] || []),
    ];
  }
  return optionCache[f.options] || [];
}

// 券模板这类表单要“按类型动态显示字段”：满减显示门槛与优惠额，
// 折扣显示折扣率，满赠显示赠品模板。配置声明 showWhen，组件只负责求值；
// 组件里不出现任何 couponType 的业务分支。
const visibleFields = computed(() =>
  (config.value.fields || []).filter((field: any) => !field.showWhen || field.showWhen(model)),
);

function fill(values: Record<string, any>) {
  for (const f of config.value.fields || []) {
    const v = values?.[f.field];
    if (f.format) {
      model[f.field] = f.format(v);
    } else if (v !== undefined && v !== null) {
      model[f.field] = v;
    } else if (f.default !== undefined) {
      model[f.field] = f.default;
    } else {
      // 多选下拉的「空」是 `[]` 而不是 `''`：
      // el-select 多选绑到字符串上，提交时就是 `"roleIds": ""`，
      // 而命令里是 long[] —— **整个命令**绑定失败，400 的 message 是「不能为空」，
      // 完全指不到是哪个字段（新建账号不选角色就是这么挂的）。
      model[f.field] = f.multiple ? [] : '';
    }
  }
}

async function loadOptions() {
  // 🔴 遍历 **config.fields** 而不是 visibleFields。
  // 「新建账号」默认 tenantType=1，只有「所属平台」可见，「所属商户」被 showWhen 藏着；
  // 只加载可见字段的话，商户下拉永远是空的 —— 用户切到「商户账号」才发现选不了商户，
  // 而控制台一条错都没有。数据源与字段可见性是两件事，不该绑在一起。
  const sources = new Set<string>();
  for (const f of config.value.fields || []) if (f.options) sources.add(f.options);

  for (const key of sources) {
    if (optionCache[key]) continue;
    const src = optionSources[key];
    if (!src) continue;
    try {
      const rows = await request(src.url, {
        method: src.method || 'GET',
        params: src.method && src.method !== 'GET' ? undefined : (src.params || src.body),
        body: src.method && src.method !== 'GET' ? (src.body || src.params) : undefined,
        silent: true,
      });
      const list = Array.isArray(rows) ? rows : (rows?.items || []);
      // 字段名归一集中到 utils/options.js：这里以前只认 id/Id/value，
      // 券模板的 { templateId, templateName } 就整排变成了空值。
      optionCache[key] = toOptions(list);
    } catch {
      optionCache[key] = [];
    }
  }
}

async function load() {
  loading.value = true;
  try {
    await loadOptions();
    if (config.value.permissionTree) {
      permissionTree.value = await request('/gateway/permissions/Tree', {
        method: 'GET',
        params: { includeDisabled: true },
        silent: true,
      });
    }
    if (!isEdit.value) {
      fill({});
      return;
    }

    // 从列表页点「编辑」进来时，ListView 会把那一行放进 router state。
    // 优先用它：列表兜底只取第一页，券模板这种跨好几页的列表，
    // 从第 2 页点编辑会捞不到那一行（提示「未找到该记录」+ 表单全空）。
    // 直接用路由里带过来的行，既省一次请求，也不受分页影响。
    const seeded = (history.state as any)?.row;
    if (seeded && String(seeded[config.value.idField || 'id'] ?? seeded.id ?? '') === String(entityId.value)) {
      loaded.value = seeded;
      fill(seeded);
      if (config.value.permissionTree && seeded.permissionIds) {
        await nextTick();
        permissionTreeRef.value?.setCheckedKeys(seeded.permissionIds.map(String), false);
      }
      return;
    }

    // 编辑时怎么取原值：优先用详情端点；没有就用列表端点捞一行。
    // 后者不是偷懒 —— users / platforms / merchants 只有 List 没有 Get，
    // 而为了编辑页单独加三个 Get 端点不值当（那三张表本来就在列表页全量展示）。
    if (config.value.detailEndpoint) {
      const detailMethod = config.value.detailMethod || 'POST';
      const detail = await request(config.value.detailEndpoint, {
        method: detailMethod,
        params: detailMethod === 'GET'
          ? { [config.value.idField || 'id']: String(entityId.value) }
          : undefined,
        body: detailMethod === 'GET'
          ? undefined
          : { [config.value.idField || 'id']: String(entityId.value) },
        silent: true,
      });
      loaded.value = detail || {};
      fill(detail || {});
      if (config.value.permissionTree && detail?.permissionIds) {
        await nextTick();
        permissionTreeRef.value?.setCheckedKeys(detail.permissionIds.map(String), false);
      }
      return;
    }

    const src = config.value.listSource;
    if (!src) {
      fill({});
      return;
    }
    // pageSize 默认 50：各端点的上限并不统一（评价 50 / 平台 100 / 客户与日志 200），
    // 取**最小**那个才不会被任何一个端点拒掉。
    // 曾用 200，结果编辑平台 / 编辑商户 / 编辑场次三个页面一律 400。
    const res = await request(src.url, {
      method: src.method || 'POST',
      params: (src.method || 'POST') === 'GET'
        ? { page: 1, pageSize: src.pageSize ?? 50, ...(src.body || {}) }
        : undefined,
      body: (src.method || 'POST') === 'GET'
        ? undefined
        : { page: 1, pageSize: src.pageSize ?? 50, ...(src.body || {}) },
      silent: true,
    });
    const rows = Array.isArray(res) ? res : (res?.items || []);
    // 按配置里的 idField 找，而不是写死 `id`：
    // 物流公司是 logisticsId、券模板是 templateId、场次是 sessionId ——
    // 写死 `id` 时这三类编辑页一律「未找到该记录」，表单全空，
    // 保存又把空值写回去（备注 / Logo / 排序直接被清掉）。
    const idField = config.value.idField || 'id';
    const hit = rows.find((r: any) =>
      String(r[idField] ?? r.id ?? r.Id ?? '') === String(entityId.value));
    if (!hit) ElMessage.warning('未找到该记录，可能已被删除');
    loaded.value = hit || {};
    fill(hit || {});
    if (config.value.permissionTree && config.value.permissionDetailEndpoint) {
      const detail = await request(config.value.permissionDetailEndpoint, {
        method: 'GET',
        params: { [config.value.idField || 'id']: String(entityId.value) },
        silent: true,
      }).catch(() => null);
      await nextTick();
      permissionTreeRef.value?.setCheckedKeys((detail?.permissionIds || []).map(String), false);
    }
  } catch {
    fill({});
  } finally {
    loading.value = false;
  }
}

function flattenPermissionIds(nodes: any[]): string[] {
  return nodes.flatMap((node) => [String(node.id), ...flattenPermissionIds(node.children || [])]);
}

function selectedPermissionIds(): string[] {
  const tree = permissionTreeRef.value;
  if (!tree) return [];
  return [
    ...tree.getCheckedKeys(false).map(String),
    ...tree.getHalfCheckedKeys().map(String),
  ];
}

function selectAllPermissions() {
  permissionTreeRef.value?.setCheckedKeys(flattenPermissionIds(permissionTree.value), false);
}

function clearPermissions() {
  permissionTreeRef.value?.setCheckedKeys([], false);
}

// ⚠️ 只在**提交时**校验，失焦不校验（用户明确要求）。
// 边打字边飘红很烦：手机号填到第 7 位时它已经红了，
// 而这时用户并没有填错，只是还没填完。
function validate() {
  for (const k of Object.keys(errors)) delete errors[k];

  for (const f of visibleFields.value) {
    const raw = model[f.field];
    const value = Array.isArray(raw) ? raw.join(',') : raw;
    const empty = value === '' || value === null || value === undefined;

    if (f.required && empty) {
      errors[f.field] = `${f.label}不能为空`;
      continue;
    }
    if (empty) continue;

    if (f.pattern && !new RegExp(f.pattern).test(String(value))) {
      errors[f.field] = f.patternMessage || `${f.label}格式不正确`;
      continue;
    }
    if (f.min != null && Number(value) < f.min) {
      errors[f.field] = `${f.label}不能小于 ${f.min}`;
      continue;
    }
    if (f.max != null && Number(value) > f.max) {
      errors[f.field] = `${f.label}不能大于 ${f.max}`;
    }
  }

  return Object.keys(errors).length === 0;
}

async function submit() {
  if (!validate()) {
    ElMessage.warning('请先修正标红的字段');
    // 表单很长时错误可能在视口外，滚过去用户才知道哪里错了
    const first = document.querySelector('.el-form-item.is-error');
    first?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    return;
  }

  saving.value = true;
  try {
    const body: Record<string, any> = {};
    // carry：把「加载时读到、但表单上没显示」的字段一起提交。
    // 小程序配置就是这种情况：它只显示商城名/公告/主题色，
    // 但 platforms/Update 要求 platformName / contactPhone 等字段非空，
    // 只提交可见字段会被后端 400 挡下。
    for (const key of config.value.carry || []) {
      if (loaded.value[key] !== undefined) body[key] = loaded.value[key];
    }
    for (const f of visibleFields.value) {
      if (f.readonlyInEdit && isEdit.value) continue;
      if (f.toApi) {
        body[f.field] = f.toApi(model[f.field]);
        continue;
      }
      let v = model[f.field];
      // 下拉是 clearable 的：清空后模型是 `''`，但所有下拉字段在后端都是
      // long / int / long[]（本文件里没有任何字符串取值的下拉）。
      // 把 `''` 原样发出去同样是整单绑定失败，所以空值统一收敛成 0 / []。
      if (f.type === 'select' && v === '') v = f.multiple ? [] : 0;
      body[f.field] = v;
    }
    if (isEdit.value) body[config.value.idField || 'id'] = String(entityId.value);

    const result = await request<any>(
      isEdit.value ? config.value.updateEndpoint : config.value.createEndpoint,
      { body },
    );
    if (config.value.permissionTree) {
      const roleId = isEdit.value ? String(entityId.value) : String(result);
      await request(config.value.permissionBindEndpoint, {
        method: 'POST',
        body: { roleId, permissionIds: selectedPermissionIds() },
      });
    }
    ElMessage.success(isEdit.value ? '已保存' : '创建成功');
    router.push(config.value.listRoute);
  } catch {
    // request 已弹提示，保留表单让用户改
  } finally {
    saving.value = false;
  }
}

function cancel() {
  router.push(config.value.listRoute);
}

const title = computed(() => {
  if (!isEdit.value) return config.value.title;
  const base = config.value.title.startsWith('新建')
    ? config.value.title.slice(2)
    : config.value.title;
  return `编辑${base}`;
});

onMounted(load);
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

.form {
  padding: var(--space-6);
}

/* 两列。窄屏自动折成一列 —— 后台也有人在笔记本上用。 */
.form__grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  column-gap: var(--space-5);
}

@media (max-width: 1100px) {
  .form__grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.form__item {
  min-width: 0;
}

.form__item--wide {
  grid-column: 1 / -1;
}

.form__control {
  width: 100%;
}

.form__help {
  margin: var(--space-1) 0 0;
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-2);
}

.form__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-2);
  padding-top: var(--space-4);
  border-top: 1px solid var(--hairline);
}

.permission-block {
  margin-top: var(--space-5);
  padding-top: var(--space-5);
  border-top: 0.5px solid var(--hairline);
}

.permission-block__head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  margin-bottom: var(--space-3);
}

.permission-block__head h3 {
  margin: 0;
  font-size: var(--text-title-3);
}

.permission-block__head p {
  margin: var(--space-1) 0 0;
  color: var(--text-2);
  font-size: var(--text-foot);
}

.permission-block__tools {
  display: flex;
  gap: var(--space-2);
}

.permission-node {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.permission-node__code {
  color: var(--text-3);
}

.skel {
  padding: var(--space-6);
}
</style>
