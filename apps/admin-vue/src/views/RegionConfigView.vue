<template>
  <div>
    <div class="page-header">
      <div>
        <h2 class="page-header__title">地区地址配置</h2>
        <p class="page-header__desc">按省、市、区三级维护，小程序只展示启用的地址数据</p>
      </div>
      <div class="tools">
        <el-select v-model="platformId" placeholder="选择平台" class="platform" @change="load">
          <el-option
            v-for="item in platforms"
            :key="item.id ?? item.Id"
            :label="item.name ?? item.Name"
            :value="item.id ?? item.Id"
          />
        </el-select>
        <el-button @click="addProvince">新增省份</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </div>
    </div>

    <div v-if="loading" class="panel skel">
      <div v-for="i in 6" :key="i" class="skeleton-row" />
    </div>

    <div v-else class="region-list">
      <section v-for="(province, provinceIndex) in regions" :key="provinceIndex" class="panel province">
        <div class="province__head">
          <el-input v-model="province.name" class="name-input" placeholder="省份名称" />
          <el-input v-model="province.code" class="code-input" placeholder="省份编码" />
          <el-button link @click="addCity(province)">新增城市</el-button>
          <el-button link type="danger" @click="removeAt(regions, provinceIndex)">删除省份</el-button>
        </div>
        <div class="cities">
          <div v-for="(city, cityIndex) in province.children || []" :key="cityIndex" class="city">
            <div class="city__head">
              <el-input v-model="city.name" class="name-input" placeholder="城市名称" />
              <el-input v-model="city.code" class="code-input" placeholder="城市编码" />
              <el-button link @click="addDistrict(city)">新增区县</el-button>
              <el-button link type="danger" @click="removeAt(province.children, cityIndex)">删除城市</el-button>
            </div>
            <div class="districts">
              <div v-for="(district, districtIndex) in city.children || []" :key="districtIndex" class="district">
                <el-input v-model="district.name" class="name-input" placeholder="区县名称" />
                <el-input v-model="district.code" class="code-input" placeholder="区县编码" />
                <el-button link type="danger" @click="removeAt(city.children, districtIndex)">删除</el-button>
              </div>
            </div>
          </div>
        </div>
      </section>
      <div v-if="!regions.length" class="empty">还没有地区数据，请先新增省份</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import request from '@/api/request';

const platforms = ref<any[]>([]);
const platformId = ref('');
const regions = ref<any[]>([]);
const loading = ref(true);
const saving = ref(false);

async function loadPlatforms() {
  platforms.value = await request('/gateway/platforms/Options', {
    method: 'GET',
    silent: true,
  }).catch(() => []);
  if (platforms.value.length) {
    platformId.value = String(platforms.value[0].id ?? platforms.value[0].Id);
  }
}

function normalize(items: any[]): any[] {
  return (items || []).map((item) => ({
    name: String(item.name || ''),
    code: String(item.code || ''),
    children: normalize(item.children || []),
  }));
}

async function load() {
  if (!platformId.value) {
    regions.value = [];
    loading.value = false;
    return;
  }
  loading.value = true;
  try {
    const result = await request('/gateway/regions/Get', {
      method: 'GET',
      params: { platformId: platformId.value },
      silent: true,
    });
    regions.value = normalize(JSON.parse(result?.regionsJson || '[]'));
  } catch {
    regions.value = [];
  } finally {
    loading.value = false;
  }
}

function addProvince() {
  regions.value.push({ name: '', code: '', children: [] });
}

function addCity(province: any) {
  if (!province.children) province.children = [];
  province.children.push({ name: '', code: '', children: [] });
}

function addDistrict(city: any) {
  if (!city.children) city.children = [];
  city.children.push({ name: '', code: '' });
}

function removeAt(list: any[], index: number) {
  list.splice(index, 1);
}

function validate() {
  for (const province of regions.value) {
    if (!province.name?.trim()) return '省份名称不能为空';
    for (const city of province.children || []) {
      if (!city.name?.trim()) return `省份「${province.name}」下有城市名称为空`;
      for (const district of city.children || []) {
        if (!district.name?.trim()) return `城市「${city.name}」下有区县名称为空`;
      }
    }
  }
  return '';
}

async function save() {
  const error = validate();
  if (error) {
    ElMessage.warning(error);
    return;
  }
  saving.value = true;
  try {
    await request('/gateway/regions/Save', {
      body: {
        platformId: platformId.value,
        regionsJson: JSON.stringify(regions.value),
      },
    });
    ElMessage.success('地区地址已保存');
  } finally {
    saving.value = false;
  }
}

onMounted(async () => {
  await loadPlatforms();
  await load();
});
</script>

<style scoped>
.tools {
  display: flex;
  gap: var(--space-2);
}

.platform {
  width: 220px;
}

.region-list {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}

.province {
  padding: var(--space-4);
}

.province__head,
.city__head,
.district {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.province__head {
  padding-bottom: var(--space-3);
  border-bottom: 0.5px solid var(--hairline);
}

.cities {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin-top: var(--space-3);
  padding-left: var(--space-4);
}

.city {
  padding: var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--bg-page);
}

.districts {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin-top: var(--space-2);
  padding-left: var(--space-4);
}

.name-input {
  width: 220px;
}

.code-input {
  width: 140px;
}

.skel {
  display: grid;
  gap: var(--space-3);
  padding: var(--space-6);
}

.empty {
  padding: var(--space-8);
  color: var(--text-2);
  text-align: center;
}
</style>
