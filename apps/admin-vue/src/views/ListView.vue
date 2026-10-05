<template>
  <div>
    <div class="head">
      <div>
        <h2 class="head__title">{{ config.title }}</h2>
        <p class="head__desc">{{ config.desc }}</p>
      </div>
      <el-input
        v-if="config.search"
        v-model="keyword"
        class="head__search"
        :placeholder="config.searchPlaceholder || '搜索'"
        clearable
        @keyup.enter="reload"
        @clear="reload"
      />
    </div>

    <el-tabs v-if="tabs.length" v-model="status" class="tabs" @tab-change="reload">
      <el-tab-pane v-for="t in tabs" :key="t.value" :label="t.label" :name="t.value" />
    </el-tabs>

    <section class="panel">
      <div v-if="loading" class="skel">
        <div v-for="i in 5" :key="i" class="skeleton-row" style="margin-bottom: var(--space-3)" />
      </div>

      <el-table v-else-if="rows.length" :data="rows" class="table" @row-click="onRow">
        <el-table-column
          v-for="c in config.columns"
          :key="c.field"
          :label="c.label"
          :min-width="c.width"
          :align="c.align || 'left'"
        >
          <template #default="{ row }">
            <span v-if="c.dict" class="pill" :class="'pill--' + statusColor(c.dict, row[c.field])">
              {{ row[c.field + 'Name'] || statusText(c.dict, row[c.field]) }}
            </span>
            <!-- 🔴 v-else 不能少。之前给它加 mono 选项时把 v-else 弄丢了，
                 两个 span 就都渲染了：字典列会变成「色标签 + 裸枚举值」并排，
                 比如「已通过 20」——直接违反「界面不出现枚举数字」这条硬约束。
                 普通列看不出来（两行渲染的是同一段文字），只有状态列才暴露。 -->
            <span v-else :class="c.num ? 'num' : c.mono ? 'mono' : ''">{{ render(c, row) }}</span>
          </template>
        </el-table-column>

        <!-- 🔴 操作列必须直接放在 el-table 里，**不能**用
             `<template #default>` 包起来再加 v-if。
             #default 是「整行的作用域插槽」，v-if 一旦为真，
             这个插槽就**顶替**了默认插槽的全部内容 ——
             外面那些 v-for 生成的列会被整个丢弃。
             症状是表格只剩「操作」一列、数据列全没了，
             而控制台一行错都没有，纯看代码很难发现。
             v-if 放在 el-table-column 上是没问题的（它是普通组件）。 -->
        <el-table-column
          v-if="config.actions"
          label="操作"
          :width="config.actionsWidth || 160"
          fixed="right"
          align="center"
        >
          <template #default="{ row }">
            <el-button
              v-for="a in config.actions"
              :key="a.label"
              :type="a.type || 'text'"
              :danger="a.danger"
              @click.stop="runAction(a, row)"
            >
              {{ a.label }}
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-else class="empty">
        <div class="empty__title">没有符合条件的数据</div>
        <div class="empty__desc">{{ config.emptyHint || '换个筛选条件或关键词试试' }}</div>
      </div>

      <div v-if="paged && rows.length" class="pager">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          background
          @current-change="load"
          @size-change="reload"
        />
      </div>
    </section>

    <!--
      🔴 危险动作必须走确认对话框，不能点一下就直接发请求（DESIGN_SPEC 5.4）。
      「审批通过退款」误点一次就是真的把钱退了，而它长得和旁边的「查看详情」一模一样。
      确认框里必须显示**具体对象**（订单号 / 退款单号），只写「确定要执行吗？」
      等于没确认 —— 用户根本不知道自己在确认哪一单。

      需要填理由的动作（审核不通过、拒绝退款）多一个输入框。
      这里只做长度提示、不写正则：真正的规则以服务端为准，
      前端再写一份只会和后端漂移，用户会撞上「前端过了后端没过」。
    -->
    <el-dialog
      v-model="confirm.open"
      :title="confirm.title"
      :width="460"
      :close-on-click-modal="false"
      align-center
    >
      <p class="confirm__text">
        {{ confirm.message }}
        <span v-if="confirm.subject" class="confirm__subject">{{ confirm.subject }}</span>
      </p>

      <el-form v-if="confirm.withReason" label-position="top" class="confirm__form">
        <el-form-item :label="confirm.reasonLabel">
          <el-input
            v-model="confirm.reason"
            type="textarea"
            :rows="3"
            :maxlength="200"
            show-word-limit
            :placeholder="confirm.reasonPlaceholder"
          />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="confirm.open = false">取消</el-button>
        <!-- 危险动作的主按钮用 danger 色：确认框里那个主动作必须长得像「会出事」 -->
        <el-button :type="confirm.danger ? 'danger' : 'primary'" :loading="confirm.saving" @click="submitConfirm">
          {{ confirm.okText }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { statusColor, statusText } from '@/utils/dict';
import {
  formatAmount,
  formatCount,
  formatDateTime,
  formatScore,
  emptyText,
} from '@/utils/format';

const props = defineProps<{ config: any }>();
// 🔴 computed 而非快照：同一个组件服务多个列表页，
// 快照会让切换后仍显示上一个列表的数据（见 CODING_STANDARD 68）
const config = computed(() => props.config);

type Fmt = (v: unknown) => string;

const FORMATTERS: Record<string, Fmt> = {
  amount: (v: unknown) => formatAmount(v),
  count: (v: unknown) => formatCount(v),
  time: (v: unknown) => formatDateTime(v),
  text: (v: unknown) => emptyText(v),
  // 评分最多一位小数，无数据显示 5.0（规格 6.1 第 8 类）
  score: (v: unknown) => formatScore(v),
  // 耗时带单位，毫秒。空值走「—」而不是 0ms —— 分不清「没记录」和「瞬时完成」
  ms: (v: unknown) => (v === '' || v === null || v === undefined ? '—' : `${v} ms`),
};

function render(def: any, row: any) {
  const fn = FORMATTERS[def.format];
  return fn ? fn(row?.[def.field]) : emptyText(row?.[def.field]);
}

const router = useRouter();
const loading = ref(true);
const rows = ref<any[]>([]);
const total = ref(0);
const paged = ref(false);

// 用独立 ref 而不是塞进一个 reactive 对象：模板里要直接绑定 v-model，
// reactive 的字段在模板作用域里取不到（会报 Property does not exist），
// 为此还得额外定义一堆 computed 转发，纯属绕路。
const status = ref(0);
const keyword = ref('');
const page = ref(1);
const pageSize = ref(20);

// 确认对话框的状态。放在一个 ref 里而不是多个散 ref：
// 它们总是同时被写入与读取，拆开就会出现「对话框开了但理由还是上一次的」。
const confirm = ref({
  open: false,
  saving: false,
  danger: true,
  title: '',
  message: '',
  subject: '',
  okText: '确定',
  withReason: false,
  reasonLabel: '原因',
  reasonPlaceholder: '',
  reason: '',
  action: null as any,
  row: null as any,
});

const tabs = computed(() => config.value.tabs || []);

async function load() {
  loading.value = true;
  try {
    const body: any = {};
    if (config.value.byStatus && status.value) body.status = status.value;
    if (config.value.search && keyword.value.trim()) body.keyword = keyword.value.trim();
    body.page = page.value;
    body.pageSize = pageSize.value;

    const result = await request(config.value.endpoint, {
      method: config.value.method || 'POST',
      body: config.value.method === 'GET' ? undefined : body,
      params: config.value.method === 'GET' ? body : undefined,
      silent: true,
    });

    // 后端返回结构不统一：有的接口回 { items, total }，有的（products/List、
    // inventory/List）回**裸数组**。这里两种都兼容，
    // 免得为了一个接口的形状差异就得改组件。
    if (Array.isArray(result)) {
      rows.value = result;
      total.value = result.length;
      paged.value = false;
    } else {
      rows.value = result?.items || [];
      total.value = Number(result?.total || 0);
      paged.value = true;
    }
  } catch {
    rows.value = [];
    total.value = 0;
  } finally {
    loading.value = false;
  }
}

function reload() {
  page.value = 1;
  load();
}

function onRow(row: any) {
  if (config.value.rowClick) config.value.rowClick(row, router);
}

async function runAction(action: any, row: any) {
  // 有 confirm 声明的动作走对话框，没有的直接发请求。
  // 分成两条路而不是「全都走对话框」：像「重置密码」这种一次性的轻动作，
  // 弹框只会让人多按一次。
  if (action.confirm) {
    confirm.value = {
      open: true,
      saving: false,
      danger: action.danger !== false,
      title: action.confirm.title || '请确认',
      message: action.confirm.message || '此操作不可撤销，确定继续？',
      subject: typeof action.confirm.subject === 'function'
        ? action.confirm.subject(row)
        : action.confirm.subject || '',
      okText: action.confirm.okText || action.label || '确定',
      withReason: !!action.confirm.withReason,
      reasonLabel: action.confirm.reasonLabel || '原因',
      reasonPlaceholder: action.confirm.reasonPlaceholder || '请填写原因',
      reason: '',
      action,
      row,
    };
    return;
  }

  try {
    await request(action.endpoint, { body: { ...action.build(row) } });
    ElMessage.success(action.okText || '操作成功');
    await load();
  } catch {
    // request 已经弹过提示，这里不重复弹
  }
}

async function submitConfirm() {
  const c = confirm.value;
  if (!c.action) return;

  // 理由必填时先在前端拦一道：省掉一次往返，也避免用户点了「确定」之后
  // 才看到一行红字。仅提示长度，真正规则以服务端为准。
  if (c.withReason && c.reason.trim().length < 2) {
    ElMessage.warning('请填写原因（至少 2 个字符）');
    return;
  }

  c.saving = true;
  try {
    await request(c.action.endpoint, {
      body: { ...c.action.build(c.row, c.reason.trim()) },
    });
    ElMessage.success(c.action.okText || '操作成功');
    confirm.value.open = false;
    await load();
  } catch {
    // request 已经弹过提示。保留对话框让用户能改理由重试，
    // 直接关掉的话改一个字就得重新点一次按钮。
  } finally {
    c.saving = false;
  }
}

watch([status, config], () => reload());
onMounted(load);
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

.head__search {
  width: 280px;
}

.tabs {
  margin-bottom: var(--space-4);
}

.table {
  cursor: pointer;
}

.pager {
  padding: 0 var(--space-6);
}

.empty {
  padding: var(--space-8) 0;
}

.empty__title {
  font-size: var(--text-sub);
  color: var(--text-2);
}

.empty__desc {
  margin-top: var(--space-1);
  font-size: var(--text-foot);
  color: var(--text-3);
}

.skel {
  padding: var(--space-6);
}

.confirm__text {
  margin: 0;
  font-size: var(--text-sub);
  line-height: var(--lh-body);
  color: var(--text-1);
}

/* 单号单独一行、用等宽字体：确认的是「哪一单」必须一眼看清，
   混在正文里容易被当成普通文字扫过去。 */
.confirm__subject {
  display: block;
  margin-top: var(--space-2);
  font-family: var(--font-mono);
  font-size: var(--text-body);
  color: var(--text-1);
}

.confirm__form {
  margin-top: var(--space-4);
}
</style>
