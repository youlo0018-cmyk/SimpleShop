<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ config.title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
      <el-select
        v-if="config.platformScoped"
        v-model="platformId"
        class="head__platform"
        placeholder="选择平台"
        filterable
        :loading="platformsLoading"
        @change="reload"
      >
        <el-option v-for="p in platforms" :key="p.id" :label="p.name" :value="p.id" />
      </el-select>
    </div>

    <p v-if="config.hint" class="hint">{{ config.hint }}</p>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 3" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <el-form v-else class="form" label-position="top">
        <el-form-item
          v-for="f in config.fields"
          :key="f.field"
          :label="f.label"
          :class="{ 'form__item--wide': f.type === 'textarea' }"
        >
          <el-select
            v-if="f.type === 'select'"
            v-model="model[f.field]"
            class="form__control"
            :placeholder="f.placeholder"
          >
            <el-option
              v-for="o in f.options || []"
              :key="o.value"
              :label="o.label"
              :value="o.value"
            />
          </el-select>
          <el-input
            v-else-if="f.type === 'textarea'"
            v-model="model[f.field]"
            type="textarea"
            :rows="f.rows || 16"
            class="form__control form__control--code"
            :placeholder="f.placeholder"
            spellcheck="false"
          />
          <el-input
            v-else-if="f.type === 'number'"
            v-model="model[f.field]"
            type="number"
            class="form__control"
            :placeholder="f.placeholder"
          />
          <el-input v-else v-model="model[f.field]" class="form__control" :placeholder="f.placeholder" />
          <p v-if="f.help" class="form__help">{{ f.help }}</p>
          <p v-if="f.showBytes && byteSize != null" class="form__bytes">
            {{ formatBytes(byteSize) }} / 2 MB
          </p>
        </el-form-item>

        <div class="form__actions">
          <el-button :disabled="saving" @click="reset">恢复默认值</el-button>
          <el-button type="primary" :loading="saving" @click="save">保存</el-button>
        </div>
      </el-form>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { formatBytes } from '@/utils/format';

const props = defineProps<{ config: any }>();
const config = computed(() => props.config);

const loading = ref(true);
const saving = ref(false);
const platformsLoading = ref(false);
const platforms = ref<any[]>([]);
// 雪花 Id 必须按字符串保存：Number() 会丢末位精度，保存到错误平台。
const platformId = ref<string>('');

const model = reactive<Record<string, any>>({});
// 地区数据的字节数由后端算好（regionsResult.byteSize），跟着响应走而不是配置里写死。
const byteSize = ref<number | null>(null);

// 「恢复默认值」不是清空，而是回到后端给的内置值。
// 所以要保留一份首次加载时的快照 —— 清空会让运营误以为要删数据。
const pristine = ref<Record<string, any>>({});

function fill(values: Record<string, any>) {
  byteSize.value = values?.byteSize ?? null;
  for (const f of config.value.fields) {
    const raw = values?.[f.field];
    // format：接口值 → 表单值，与 parse 互逆。
    model[f.field] = f.format ? f.format(raw) : (raw ?? '');
  }
}

async function loadPlatforms() {
  if (!config.value.platformScoped || platforms.value.length) return;
  platformsLoading.value = true;
  try {
    // 必须是 GET：后端这个端点写的是 [HttpGet("Options")]，
    // 不带 method 会走默认的 POST，于是 405。
    // 症状是「配置页加载失败、控制台一条 405」，而页面上看不出是哪个接口错了。
    const rows = await request('/gateway/platforms/Options', { method: 'GET', silent: true });
    platforms.value = Array.isArray(rows) ? rows : [];
    // 第一个平台：地区地址 / 优惠优先级都必须先有平台才能改。
    // 不预选的话页面会停在一个空表单上，用户会以为坏了。
    if (platforms.value.length && !platformId.value) {
      platformId.value = String(platforms.value[0].id ?? '');
    }
  } catch {
    platforms.value = [];
  } finally {
    platformsLoading.value = false;
  }
}

async function reload() {
  // 平台维度的配置必须有平台才能查。用 0 去查的话后端要么 400、
  // 要么静默返回别的平台的数据 —— 两者都会让运营在错误的平台上改配置。
  if (config.value.platformScoped && !platformId.value) {
    loading.value = false;
    fill({});
    return;
  }

  loading.value = true;
  try {
    const url = config.value.getEndpoint;
    const body = await request(url, {
      method: config.value.getMethod || 'GET',
      params: config.value.getMethod === 'GET' || !config.value.getMethod
        ? { platformId: platformId.value }
        : undefined,
      body: config.value.getMethod === 'POST'
        ? { platformId: platformId.value }
        : undefined,
      silent: true,
    });

    fill(body || {});
    pristine.value = { ...model };
  } catch {
    fill({});
  } finally {
    loading.value = false;
  }
}

async function save() {
  saving.value = true;
  try {
    const body: Record<string, any> = {};
    for (const f of config.value.fields) {
      // parse：表单值 → 接口值。签到奖励在表单上是「1,2,3,5」文本，
      // 接口要的是整数数组。在配置里声明怎么转，而不是在组件里写 if ——
      // 组件不该知道「签到奖励」这种业务细节。
      body[f.field] = f.parse ? f.parse(model[f.field]) : model[f.field];
    }
    if (config.value.platformScoped) body.platformId = platformId.value;

    await request(config.value.saveEndpoint, { body });
    ElMessage.success('已保存');
    pristine.value = { ...model };
  } catch {
    // request 已弹提示
  } finally {
    saving.value = false;
  }
}

function reset() {
  fill({ ...pristine.value });
  ElMessage.info('已恢复到当前生效值。如需保存请点击「保存」。');
}

watch([platformId, config], () => reload());

onMounted(async () => {
  await loadPlatforms();
  await reload();
});
</script>

<style scoped>
.head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
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

.head__platform {
  width: 260px;
}

.hint {
  margin: 0 0 var(--space-4);
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  background: var(--bg-page);
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-2);
}

.form {
  padding: var(--space-6);
}

.form__item--wide {
  width: 100%;
}

.form__control {
  width: 100%;
}

/* 等宽 + 关闭拼写检查：地区数据是 JSON 数组，
   用正文字体渲染会难以分辨逗号和句号、括号的大小差异。 */
.form__control--code :deep(textarea) {
  font-family: var(--font-mono);
  font-size: var(--text-foot);
  line-height: 1.6;
}

.form__help {
  margin: var(--space-1) 0 0;
  font-size: var(--text-foot);
  line-height: var(--lh-foot);
  color: var(--text-2);
}

.form__bytes {
  margin: var(--space-1) 0 0;
  font-size: var(--text-note);
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}

.form__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  padding-top: var(--space-4);
  border-top: 1px solid var(--hairline);
}

.skel {
  padding: var(--space-6);
}
</style>
