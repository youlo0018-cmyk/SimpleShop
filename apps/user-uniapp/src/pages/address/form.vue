<template>
  <view class="page address-form">
    <AppHeader :title="addressId ? '编辑地址' : '新增地址'" back />

    <view class="panel form">
      <view class="field-row">
        <text class="field-row__label">收货人</text>
        <input v-model="form.consigneeName" class="field-row__input" placeholder="请输入收货人姓名" maxlength="64" />
      </view>
      <view class="field-row">
        <text class="field-row__label">手机号</text>
        <input v-model="form.consigneePhone" class="field-row__input" type="number" maxlength="11" placeholder="请输入手机号" />
      </view>
      <picker
        mode="multiSelector"
        :range="pickerRange"
        range-key="name"
        :value="pickerValue"
        @columnchange="onColumnChange"
        @change="onRegionChange"
      >
        <view class="field-row">
          <text class="field-row__label">所在地区</text>
          <text class="field-row__input" :class="{ 'field-row__input--placeholder': !form.regionPath }">
            {{ form.regionPath || '请选择省 / 市 / 区' }}
          </text>
        </view>
      </picker>
      <view class="field-row field-row--area">
        <text class="field-row__label">详细地址</text>
        <textarea v-model="form.detailAddress" class="field-row__textarea" placeholder="街道、门牌号、小区、楼栋等" maxlength="255" />
      </view>
      <view class="field-row">
        <text class="field-row__label">标签</text>
        <input v-model="form.label" class="field-row__input" placeholder="如：家 / 公司（选填）" maxlength="16" />
      </view>
      <view class="field-row field-row--switch">
        <text class="field-row__label">设为默认地址</text>
        <switch :checked="form.isDefault" color="#0071e3" @change="onDefaultChange" />
      </view>
    </view>

    <button class="button-primary address-form__submit" :loading="submitting" @tap="submit">
      保存地址
    </button>
  </view>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { onLoad } from '@dcloudio/uni-app';
import AppHeader from '@/components/AppHeader.vue';
import { request } from '@/core/http';
import { getPlatformCode } from '@/core/session-storage';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const addressId = ref('');
const submitting = ref(false);
const regionData = ref<any[]>([]);
const provinceIndex = ref(0);
const cityIndex = ref(0);
const districtIndex = ref(0);

const form = reactive({
  consigneeName: '',
  consigneePhone: '',
  provinceCode: '',
  cityCode: '',
  districtCode: '',
  regionPath: '',
  detailAddress: '',
  label: '',
  isDefault: false,
});

const provinces = computed<any[]>(() => regionData.value || []);
const cities = computed<any[]>(() => provinces.value[provinceIndex.value]?.children || []);
const districts = computed<any[]>(() => cities.value[cityIndex.value]?.children || []);
const pickerRange = computed(() => [provinces.value, cities.value, districts.value]);
const pickerValue = computed(() => [provinceIndex.value, cityIndex.value, districtIndex.value]);

/** 把地区索引编码成短代码；后端只存储，不做行政区划查询。 */
function codeOf(index: number): string {
  return String(index + 1).padStart(2, '0');
}

function syncRegionPath() {
  const province = provinces.value[provinceIndex.value];
  const city = cities.value[cityIndex.value];
  const district = districts.value[districtIndex.value];
  if (!province || !city || !district) {
    form.provinceCode = '';
    form.cityCode = '';
    form.districtCode = '';
    form.regionPath = '';
    return;
  }

  form.provinceCode = codeOf(provinceIndex.value);
  form.cityCode = form.provinceCode + codeOf(cityIndex.value);
  form.districtCode = form.cityCode + codeOf(districtIndex.value);
  form.regionPath = `${province.name}/${city.name}/${district.name}`;
}

function onColumnChange(event: any) {
  const column = Number(event.detail.column);
  const value = Number(event.detail.value);
  if (column === 0) {
    provinceIndex.value = value;
    cityIndex.value = 0;
    districtIndex.value = 0;
  } else if (column === 1) {
    cityIndex.value = value;
    districtIndex.value = 0;
  } else {
    districtIndex.value = value;
  }
}

function onRegionChange() {
  syncRegionPath();
}

function onDefaultChange(event: any) {
  form.isDefault = Boolean(event.detail.value);
}

async function loadRegions() {
  const data = await request<any>('/gateway/regions/Public', {
    auth: false,
    params: { platformCode: getPlatformCode() },
    silent: true,
  });
  const parsed = typeof data?.regionsJson === 'string' ? JSON.parse(data.regionsJson) : data?.regionsJson;
  regionData.value = Array.isArray(parsed) ? parsed : [];
}

function locateRegion() {
  const parts = String(form.regionPath || '').split('/');
  if (parts.length !== 3) return;

  const p = provinces.value.findIndex((item) => item.name === parts[0]);
  if (p < 0) return;
  provinceIndex.value = p;

  const c = cities.value.findIndex((item) => item.name === parts[1]);
  if (c < 0) return;
  cityIndex.value = c;

  const d = districts.value.findIndex((item) => item.name === parts[2]);
  if (d >= 0) districtIndex.value = d;
}

async function loadExisting() {
  if (!addressId.value) return;
  const data = await request<any>('/gateway/customers/addresses/List', {
    method: 'POST',
    data: { customerId: session.profile?.customerId, page: 1, pageSize: 100 },
    silent: true,
  });
  const hit = (data?.items || []).find((item: any) => String(item.addressId) === String(addressId.value));
  if (!hit) {
    uni.showToast({ title: '地址不存在', icon: 'none' });
    return;
  }

  form.consigneeName = hit.consigneeName || '';
  form.consigneePhone = hit.consigneePhone || '';
  form.provinceCode = hit.provinceCode || '';
  form.cityCode = hit.cityCode || '';
  form.districtCode = hit.districtCode || '';
  form.regionPath = hit.regionPath || '';
  form.detailAddress = hit.detailAddress || '';
  form.label = hit.label || '';
  form.isDefault = Boolean(hit.isDefault);
  locateRegion();
}

function validate(): boolean {
  if (!form.consigneeName.trim()) {
    uni.showToast({ title: '请填写收货人', icon: 'none' });
    return false;
  }
  if (!/^1[3-9]\d{9}$/.test(form.consigneePhone.trim())) {
    uni.showToast({ title: '手机号格式不正确', icon: 'none' });
    return false;
  }
  if (!form.regionPath) {
    uni.showToast({ title: '请选择所在地区', icon: 'none' });
    return false;
  }
  if (!form.detailAddress.trim()) {
    uni.showToast({ title: '请填写详细地址', icon: 'none' });
    return false;
  }
  return true;
}

async function submit() {
  if (!validate()) return;
  submitting.value = true;
  try {
    const body = {
      customerId: session.profile?.customerId,
      addressId: addressId.value || undefined,
      consigneeName: form.consigneeName.trim(),
      consigneePhone: form.consigneePhone.trim(),
      provinceCode: form.provinceCode,
      cityCode: form.cityCode,
      districtCode: form.districtCode,
      regionPath: form.regionPath,
      detailAddress: form.detailAddress.trim(),
      isDefault: form.isDefault,
      label: form.label.trim(),
    };

    if (addressId.value) {
      await request('/gateway/customers/addresses/Update', { method: 'POST', data: body });
    } else {
      await request('/gateway/customers/addresses/Create', { method: 'POST', data: body });
    }

    uni.showToast({ title: '已保存', icon: 'success' });
    setTimeout(() => uni.navigateBack(), 500);
  } finally {
    submitting.value = false;
  }
}

onLoad(async (options) => {
  session.restore();
  if (!session.profile?.customerId) {
    uni.navigateTo({ url: '/pages/login/index' });
    return;
  }

  addressId.value = String(options?.addressId || '');
  await loadRegions();
  await loadExisting();
  if (!form.regionPath) syncRegionPath();
});
</script>

<style scoped lang="scss">
@use '@/styles/tokens.scss' as *;

.address-form {
  padding-bottom: 180rpx;
}

.form {
  padding: 0 $space-4;
}

.field-row {
  display: flex;
  align-items: center;
  gap: $space-3;
  min-height: 104rpx;
  border-bottom: 1rpx solid $hairline;
}

.field-row:last-child {
  border-bottom: 0;
}

.field-row--area {
  align-items: flex-start;
  padding: $space-3 0;
}

.field-row__label {
  width: 160rpx;
  flex: none;
  color: $text-2;
  font-size: $font-sub;
}

.field-row__input {
  flex: 1;
  min-width: 0;
  font-size: $font-body;
}

.field-row__input--placeholder {
  color: $text-3;
}

.field-row__textarea {
  flex: 1;
  min-height: 140rpx;
  font-size: $font-body;
  line-height: 1.5;
}

.field-row--switch {
  justify-content: space-between;
}

.address-form__submit {
  position: fixed;
  right: $space-4;
  bottom: calc(env(safe-area-inset-bottom) + $space-4);
  left: $space-4;
}
</style>
