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
              clearable
            >
              <el-option
                v-for="o in optionsOf(f)"
                :key="o.value"
                :label="o.label"
                :value="o.value"
              />
            </el-select>

            <el-color-picker v-else-if="f.type === 'color'" v-model="model[f.field]" />

            <el-input
              v-else-if="f.type === 'textarea'"
              v-model="model[f.field]"
              type="textarea"
              :rows="f.rows || 4"
              class="form__control"
              :placeholder="f.placeholder"
            />

            <el-input
              v-else-if="f.type === 'number'"
              v-model="model[f.field]"
              type="number"
              class="form__control"
              :placeholder="f.placeholder"
            />

            <el-date-picker
              v-else-if="f.type === 'datetime'"
              v-model="model[f.field]"
              type="datetime"
              class="form__control"
              placeholder="选择日期时间"
              value-format="YYYY-MM-DDTHH:mm:ss"
            />

            <el-input
              v-else
              v-model="model[f.field]"
              class="form__control"
              :type="f.secret ? 'password' : 'text'"
              :show-password="!!f.secret"
              :placeholder="f.placeholder"
            />

            <p v-if="f.help" class="form__help">{{ f.help }}</p>
          </el-form-item>
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
import { computed, onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { optionSources } from '@/router/form-configs';

const props = defineProps<{ config: any; id?: string }>();
const route = useRoute();
const router = useRouter();

const config = computed(() => props.config);
const entityId = computed(() => props.id || route.params.id || 0);
const isEdit = computed(() => Number(entityId.value) > 0);

const loading = ref(false);
const saving = ref(false);
const model = reactive<Record<string, any>>({});
const errors = reactive<Record<string, string>>({});
// 加载到的**完整**记录（含表单上没显示的字段），提交时用于补齐后端要求的必填项。
const loaded = ref<Record<string, any>>({});

// 下拉数据源。平台 / 商户 / 角色 / 券模板都要从后端取，
// 且必须显示 name 而不是 id（DATA_SPEC 4.1）。
const optionCache = reactive<Record<string, any[]>>({});

function optionsOf(f: any) {
  if (f.static) return f.static;
  return optionCache[f.options] || [];
}

const visibleFields = computed(() => config.value.fields || []);

function fill(values: Record<string, any>) {
  for (const f of visibleFields.value) {
    const v = values?.[f.field];
    model[f.field] = f.format ? f.format(v) : (v ?? f.default ?? '');
  }
}

async function loadOptions() {
  const sources = new Set<string>();
  for (const f of visibleFields.value) if (f.options) sources.add(f.options);

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
      optionCache[key] = list.map((r: any) => ({
        // 各后端 DTO 的字段名大小写不一致（RoleListItem 是 Id / RoleName，
        // 平台与商户是 id / platformName）。这里统一兜住，
        // 否则某一个下拉会静默变成一排「undefined」而页面不报错。
        value: Number(r.id ?? r.Id ?? r.value ?? r.Value ?? 0),
        label: String(
          r.name ?? r.Name ?? r.platformName ?? r.merchantName ??
          r.templateName ?? r.roleName ?? r.code ?? r.Code ?? '',
        ),
      }));
    } catch {
      optionCache[key] = [];
    }
  }
}

async function load() {
  loading.value = true;
  try {
    await loadOptions();
    if (!isEdit.value) {
      fill({});
      return;
    }

    // 编辑时怎么取原值：优先用详情端点；没有就用列表端点捞一行。
    // 后者不是偷懒 —— users / platforms / merchants 只有 List 没有 Get，
    // 而为了编辑页单独加三个 Get 端点不值当（那三张表本来就在列表页全量展示）。
    if (config.value.detailEndpoint) {
      const detail = await request(config.value.detailEndpoint, {
        method: config.value.detailMethod || 'POST',
        body: { [config.value.idField || 'id']: Number(entityId.value) },
        silent: true,
      });
      loaded.value = detail || {};
      fill(detail || {});
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
      body: { page: 1, pageSize: src.pageSize ?? 50, ...(src.body || {}) },
      silent: true,
    });
    const rows = Array.isArray(res) ? res : (res?.items || []);
    const hit = rows.find((r: any) => Number(r.id) === Number(entityId.value));
    if (!hit) ElMessage.warning('未找到该记录，可能已被删除');
    loaded.value = hit || {};
    fill(hit || {});
  } catch {
    fill({});
  } finally {
    loading.value = false;
  }
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
      body[f.field] = f.toApi ? f.toApi(model[f.field]) : model[f.field];
    }
    if (isEdit.value) body[config.value.idField || 'id'] = Number(entityId.value);

    await request(isEdit.value ? config.value.updateEndpoint : config.value.createEndpoint, { body });
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

const title = computed(() =>
  isEdit.value ? `${config.value.title}编辑` : config.value.title,
);

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

.skel {
  padding: var(--space-6);
}
</style>
