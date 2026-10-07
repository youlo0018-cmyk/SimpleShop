<!--
  订单发货弹窗（快递 / 虚拟 / 自提三种履约方式共用）。

  为什么抽成组件：列表页与详情页都要发货，两处各写一份的结果是
  **详情页那份漏掉了物流公司与运单号** —— 它直接调 `/orders/Ship` 只传 orderNo，
  而校验器要求「物流公司必填 + 运单号 2-64 位」，点下去必然 400「请选择物流公司」。
  接口层面的 e2e 一直是拿完整参数测的，所以这条只在界面上暴露。
-->
<template>
  <el-dialog
    :model-value="modelValue"
    title="订单发货"
    width="520"
    align-center
    @update:model-value="emit('update:modelValue', $event)"
  >
    <p class="fulfill__subject">{{ orderNo }}</p>
    <el-form label-position="top" class="fulfill__form">
      <template v-if="hasPhysical">
        <el-form-item label="物流公司" :error="errors.company">
          <el-select
            v-model="logisticsCompanyId"
            class="fulfill__control"
            placeholder="请选择物流公司"
            filterable
            :loading="companiesLoading"
          >
            <el-option v-for="c in companies" :key="c.id" :label="c.name" :value="c.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="运单号" :error="errors.trackingNo">
          <el-input
            v-model="trackingNo"
            class="fulfill__control"
            placeholder="请输入快递运单号"
            maxlength="64"
          />
        </el-form-item>
      </template>
      <el-form-item :label="hasVirtual ? '卡号 / 激活码 / 发货备注' : '发货备注'" :error="errors.remark">
        <el-input
          v-model="remark"
          type="textarea"
          :rows="2"
          :placeholder="hasVirtual ? '虚拟商品卡号 / 激活码（会展示给客户）' : '选填'"
        />
      </el-form-item>
    </el-form>
    <div class="fulfill__actions">
      <el-button v-if="hasPhysical" type="primary" :loading="saving" @click="submit('Ship')">
        快递发货
      </el-button>
      <el-button v-if="hasVirtual" type="primary" :loading="saving" @click="submit('DeliverVirtual')">
        虚拟发货
      </el-button>
      <el-button v-if="hasSelfPickup" type="primary" :loading="saving" @click="submit('SelfPickupReady')">
        备货完成
      </el-button>
      <!-- 显式取消：只靠右上角那个很小的 × 与后台其它弹窗不一致 -->
      <el-button @click="emit('update:modelValue', false)">取消</el-button>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import request from '@/api/request';

const props = withDefaults(defineProps<{
  modelValue: boolean;
  orderNo: string;
  /** 订单明细行，用来判断这单是快递 / 虚拟 / 自提（决定显示哪几个按钮）。 */
  items?: any[];
}>(), { items: () => [] });

const emit = defineEmits<{
  'update:modelValue': [value: boolean];
  done: [];
}>();

const remark = ref('');
const logisticsCompanyId = ref('');
const trackingNo = ref('');
const saving = ref(false);
const companiesLoading = ref(false);
const companies = ref<any[]>([]);
const errors = reactive<Record<string, string>>({});

const hasPhysical = computed(() => props.items.some((i) => Number(i.deliveryType) === 1));
const hasVirtual = computed(() => props.items.some((i) => Number(i.deliveryType) === 2));
const hasSelfPickup = computed(() => props.items.some((i) => Number(i.deliveryType) === 3));

watch(
  () => props.modelValue,
  async (open) => {
    if (!open) return;
    remark.value = '';
    logisticsCompanyId.value = '';
    trackingNo.value = '';
    for (const k of Object.keys(errors)) delete errors[k];
    if (hasPhysical.value) await loadCompanies();
  },
);

// 物流公司字典只拉一次，之后复用：每开一次弹窗都请求会有一眼可见的白屏等待
async function loadCompanies() {
  if (companies.value.length || companiesLoading.value) return;
  companiesLoading.value = true;
  try {
    const rows = await request('/gateway/logistics-companies/Options', {
      method: 'POST',
      body: {},
      silent: true,
    });
    companies.value = (Array.isArray(rows) ? rows : (rows?.items || [])).map((r: any) => ({
      id: String(r.logisticsId ?? r.LogisticsId ?? r.id ?? r.Id ?? ''),
      name: String(r.companyName ?? r.CompanyName ?? r.name ?? ''),
    }));
  } catch {
    companies.value = [];
  } finally {
    companiesLoading.value = false;
  }
}

async function submit(action: string) {
  // 只在**提交时**校验（用户要求失焦不校验）
  for (const k of Object.keys(errors)) delete errors[k];
  if (action === 'Ship') {
    if (!logisticsCompanyId.value) errors.company = '请选择物流公司';
    if (!trackingNo.value.trim()) errors.trackingNo = '请填写运单号';
    if (Object.keys(errors).length) {
      ElMessage.warning('请先补全物流信息');
      return;
    }
  }
  // 虚拟发货必须写清楚发的是什么（卡号 / 激活码 / 网盘链接）：
  // 内容会原样展示给客户，空着发货等于顾客付了钱什么都拿不到。
  if (action === 'DeliverVirtual' && !remark.value.trim()) {
    errors.remark = '请填写发货内容（卡号 / 激活码）';
    ElMessage.warning('请先填写发货内容');
    return;
  }

  saving.value = true;
  try {
    const response = await request(`/gateway/admin/orders/${action}`, {
      body: {
        orderNo: props.orderNo,
        remark: remark.value.trim(),
        logisticsCompanyId: logisticsCompanyId.value,
        trackingNo: trackingNo.value.trim(),
      },
      raw: true,
    });
    ElMessage.success(response?.message || '操作成功');
    emit('update:modelValue', false);
    emit('done');

    // 备货完成的**取货码只在 data 里**，后端那句 message 只有「请把取货码交给顾客」。
    // 不单独弹出来的话，运营拿不到要交给顾客的那串码 ——
    // 详情页原来是靠一个 alert 显示的，抽成组件时不能把这一步弄丢。
    if (action === 'SelfPickupReady' && response?.data?.pickupCode) {
      await ElMessageBox.alert(
        response.data.pickupCode,
        '备货完成，请把取货码交给顾客',
        { confirmButtonText: '知道了' },
      ).catch(() => {});
    }
  } catch {
    // request 已弹提示
  } finally {
    saving.value = false;
  }
}
</script>

<style scoped>
.fulfill__subject {
  margin: 0 0 var(--space-3);
  font-family: var(--font-mono);
  font-size: var(--text-sub);
  color: var(--text-2);
}

.fulfill__control {
  width: 100%;
}

.fulfill__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
}
</style>
