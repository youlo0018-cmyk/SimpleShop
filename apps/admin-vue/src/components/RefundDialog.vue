<template>
  <el-dialog v-model="visible" title="发起退款" width="680" align-center>
    <p class="refund__subject mono">{{ orderNo }}</p>

    <div class="refund__scope">
      <el-radio-group v-model="mode" size="small">
        <el-radio-button value="whole">退完剩余余额</el-radio-button>
        <el-radio-button value="partial">只退指定商品</el-radio-button>
      </el-radio-group>
      <span class="refund__balance">
        订单实付 {{ formatAmount(payableAmount) }}
        <template v-if="refundedAmount > 0"> · 已退 {{ formatAmount(refundedAmount) }}</template>
        · 还能退 {{ formatAmount(remaining) }}
      </span>
    </div>

    <!--
      表格高度按行数自适应，只有超过 6 行才滚动。
      固定 260px 的话，单行订单会在弹窗里留一大片空白 ——
      而这片空白正好落在「本次退款」上面，让人以为是金额没加载出来。
    -->
    <el-table
      v-if="mode === 'partial'"
      :data="items"
      class="refund__table"
      :max-height="items.length > 6 ? 260 : undefined"
    >
      <el-table-column label="退款" width="60" align="center">
        <template #default="{ row }">
          <el-checkbox v-model="row.picked" />
        </template>
      </el-table-column>
      <el-table-column label="商品" min-width="180">
        <template #default="{ row }">
          <div>{{ row.productName }}</div>
          <div class="refund__sub">{{ row.skuSpecText }}</div>
        </template>
      </el-table-column>
      <el-table-column label="件数" width="92" align="right">
        <template #default="{ row }">
          <el-input-number
            v-model="row.quantity"
            size="small"
            :min="1"
            :max="row.remainingQuantity"
            controls-position="right"
            class="refund__number"
          />
        </template>
      </el-table-column>
      <el-table-column label="退款金额" width="140" align="right">
        <template #default="{ row }">
          <el-input
            v-model="row.amount"
            size="small"
            type="number"
            :disabled="!row.picked"
            :placeholder="formatAmount(row.refundableAmount)"
          />
        </template>
      </el-table-column>
      <el-table-column label="本行可退" width="110" align="right">
        <template #default="{ row }">
          <span class="refund__sub num">{{ formatAmount(row.refundableAmount) }}</span>
        </template>
      </el-table-column>
    </el-table>

    <p class="refund__total">
      本次退款 <strong>{{ formatAmount(total) }}</strong> 元
    </p>

    <el-form label-position="top">
      <el-form-item label="退款原因" :error="error">
        <el-input
          v-model="remark"
          type="textarea"
          :rows="2"
          placeholder="至少 2 个字符，会记录到审计日志"
          maxlength="512"
        />
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="danger" :loading="saving" @click="submit">确认退款</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
/**
 * 部分退款对话框。
 *
 * 为什么抽成组件：订单列表与订单详情都要能退款，而退款规则（行级余额、
 * 件数上限、剩余余额）是**资损边界** —— 抄两份必然漂移，改一处忘一处就是多退钱。
 */
import { computed, reactive, ref, watch } from 'vue';
import { ElMessage } from 'element-plus';
import request from '@/api/request';
import { formatAmount } from '@/utils/format';

const props = defineProps<{
  modelValue: boolean;
  orderNo: string;
  /** 订单详情，后端已带出每行的 refundedQuantity / refundableAmount。 */
  detail: any;
}>();

const emit = defineEmits<{
  'update:modelValue': [value: boolean];
  done: [];
}>();

const visible = computed({
  get: () => props.modelValue,
  set: (value) => emit('update:modelValue', value),
});

const saving = ref(false);
const mode = ref<'whole' | 'partial'>('whole');
const remark = ref('');
const error = ref('');
const payableAmount = ref(0);
const refundedAmount = ref(0);
const items = ref<any[]>([]);

const remaining = computed(() =>
  Math.max(0, Number((payableAmount.value - refundedAmount.value).toFixed(2))),
);

const total = computed(() => {
  if (mode.value === 'whole') return remaining.value;
  return items.value
    .filter((a) => a.picked)
    .reduce((sum, a) => sum + (Number(a.amount) || 0), 0);
});

// 每次打开都按当前详情重算一次余额：上一笔退款刚退完，
// 复用自己的旧余额会让运营看到一个偏大的「还能退」。
watch(
  () => props.modelValue,
  (open) => {
    if (!open) return;
    const d = props.detail || {};
    mode.value = 'whole';
    remark.value = '';
    error.value = '';
    payableAmount.value = Number(d.payableAmount ?? 0);
    refundedAmount.value = Number(d.refundedAmount ?? 0);
    items.value = (d.items || []).map((item: any) => ({
      orderItemId: String(item.orderItemId),
      productName: item.productName,
      skuSpecText: item.skuSpecText,
      remainingQuantity: Math.max(
        0,
        Number(item.quantity ?? 0) - Number(item.refundedQuantity ?? 0),
      ),
      refundableAmount: Number(item.refundableAmount ?? 0),
      picked: false,
      quantity: Math.max(
        1,
        Number(item.quantity ?? 1) - Number(item.refundedQuantity ?? 0),
      ),
      amount: '' as string | number,
    }));
  },
);

async function submit() {
  // 提交时统一校验，不做失焦校验（用户明确要求）
  error.value = '';
  if (remark.value.trim().length < 2) error.value = '退款原因至少 2 个字符';

  let lines: any[] | null = null;
  if (mode.value === 'partial') {
    lines = items.value
      .filter((a) => a.picked)
      .map((a) => ({
        orderItemId: a.orderItemId,
        quantity: Number(a.quantity) || 0,
        amount: Number(a.amount) || 0,
      }));

    if (!lines.length) error.value = error.value || '请至少勾选一个商品行';
    for (const line of lines) {
      if (line.amount <= 0) error.value = '退款金额必须大于 0';
    }
    const sum = lines.reduce((acc, a) => acc + a.amount, 0);
    if (sum > remaining.value + 0.01) {
      error.value =
        `本次退款 ${sum.toFixed(2)} 元超过订单剩余可退 ${remaining.value.toFixed(2)} 元`;
    }

    if (error.value) {
      ElMessage.warning(error.value);
      return;
    }

    await post(lines);
    return;
  }

  if (error.value) {
    ElMessage.warning(error.value);
    return;
  }
  await post(null);
}

async function post(lines: any[] | null) {
  saving.value = true;
  try {
    const response = await request('/gateway/admin/orders/Refund', {
      body: { orderNo: props.orderNo, remark: remark.value.trim(), lines },
      raw: true,
    });
    ElMessage.success(response?.message || '退款已处理');
    visible.value = false;
    emit('done');
  } catch {
    // request 已提示，保留弹窗让运营改一个数字就能重试
  } finally {
    saving.value = false;
  }
}
</script>

<style scoped>
.refund__subject {
  margin: 0 0 var(--space-3);
  font-weight: 600;
}

.refund__scope {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-bottom: var(--space-3);
}

/* 余额说明用等宽数字：这是要跟退款金额反复对比的数字，字形对齐才读得准 */
.refund__balance {
  font-size: var(--text-foot);
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

.refund__table {
  width: 100%;
}

.refund__number {
  width: 100%;
}

.refund__sub {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.refund__total {
  margin: var(--space-3) 0 0;
  text-align: right;
  font-size: var(--text-sub);
  color: var(--text-2);
}

.refund__total strong {
  color: var(--danger-fg);
  font-variant-numeric: tabular-nums;
}
</style>
