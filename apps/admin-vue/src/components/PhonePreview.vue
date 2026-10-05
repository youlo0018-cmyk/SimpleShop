<template>
  <div class="stage">
    <!--
      手机外框。参考 SimpleShop/apps/admin-vue 的 AppDesign.vue：
      运营改的是「用户在手机上看到的东西」，预览必须长得像手机才判断得准。
      之前是一个白底方框 + 一行组件类型文字，改完完全看不出成品长什么样。
    -->
    <div class="phone">
      <div class="phone__notch" />
      <div class="phone__screen" :class="{ 'phone__screen--over': dropActive }" @dragover.prevent @dragleave="dropActive = false">
        <div class="status-bar">
          <span>9:41</span>
          <span class="status-bar__icons">●●● ▮</span>
        </div>

        <div class="nav-bar">{{ pageTitle }}</div>

        <div class="screen-scroll">
          <template v-if="!components.length">
            <div class="empty">
              <div class="empty__title">页面还是空的</div>
              <div class="empty__desc">从左侧把组件拖进来</div>
            </div>
          </template>

          <div
            v-for="(c, i) in components"
            :key="c.id || i"
            class="slot"
            :class="{ 'slot--dragging': dragIndex === i, 'slot--over': dropIndex === i && dragIndex !== i }"
            :draggable="true"
            @dragstart.stop="dragIndex = i"
            @dragover.prevent.stop="dropIndex = i"
            @dragleave="dropIndex = -1"
            @drop.prevent.stop="emit('reorder', i)"
            @dragend="dragIndex = -1; dropIndex = -1"
          >
            <div class="slot__chrome">
              <span class="slot__name">{{ nameOf(c.type) }}</span>
              <span class="slot__ops" @mousedown.stop @dragstart.stop>
                <button
                  type="button"
                  class="slot__op"
                  :disabled="i === 0"
                  title="上移"
                  @click.stop="emit('move', i, -1)"
                >
                  ↑
                </button>
                <button
                  type="button"
                  class="slot__op"
                  :disabled="i === components.length - 1"
                  title="下移"
                  @click.stop="emit('move', i, 1)"
                >
                  ↓
                </button>
                <button
                  type="button"
                  class="slot__op slot__op--danger"
                  title="删除"
                  @click.stop="emit('remove', i)"
                >
                  删除
                </button>
              </span>
            </div>

            <!-- 组件本体：与小程序 DesignComponent.vue 一一对应，
                 保证「预览里长这样」=「用户看到长这样」。 -->
            <div class="mock">
              <template v-if="c.type === 'banner'">
                <div class="m-banner">
                  <img v-if="firstImage(c)" :src="firstImage(c)" alt="" />
                  <div v-else class="m-banner__ph">轮播图</div>
                  <span v-if="c.props?.title" class="m-banner__cap">{{ c.props.title }}</span>
                </div>
              </template>

              <template v-else-if="c.type === 'searchBar'">
                <div class="m-search">搜索商品、品牌、分类</div>
              </template>

              <template v-else-if="c.type === 'title'">
                <div class="m-title">
                  <b>{{ c.props?.title || '精选推荐' }}</b>
                  <span v-if="c.props?.subtitle">{{ c.props.subtitle }}</span>
                </div>
              </template>

              <template v-else-if="c.type === 'notice'">
                <div class="m-notice">
                  <em>公告</em>
                  <span>{{ c.props?.text || c.props?.content || '暂无公告' }}</span>
                </div>
              </template>

              <template v-else-if="c.type === 'seckillZone'">
                <div class="m-seckill">
                  <div>
                    <b>限时抢购</b>
                    <span>场次库存单独划出，售完即止</span>
                  </div>
                  <i>去看看 ›</i>
                </div>
              </template>

              <template v-else-if="c.type === 'memberCard'">
                <div class="m-member">
                  <b>会员积分</b>
                  <span>签到赚积分，下单可抵扣</span>
                </div>
              </template>

              <template v-else-if="c.type === 'serviceGrid'">
                <div class="m-quad">
                  <div v-for="(item, index) in itemsOf(c, 4)" :key="index" class="m-quad__item">
                    <span class="m-quad__icon">{{ (item.name || item.title || '入口').slice(0, 1) }}</span>
                    <i>{{ item.name || item.title || `入口${index + 1}` }}</i>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'kingKong' || c.type === 'categoryNav'">
                <div class="m-card">
                  <div class="m-card__title">{{ c.props?.title || '分类导航' }}</div>
                  <div class="m-quad">
                    <div v-for="(item, index) in itemsOf(c, 8)" :key="index" class="m-quad__item">
                      <span class="m-quad__icon">{{ (item.name || item.title || '入口').slice(0, 1) }}</span>
                      <i>{{ item.name || item.title || `入口${index + 1}` }}</i>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'productGrid' || c.type === 'productScroll'">
                <div class="m-card">
                  <div class="m-card__title">{{ c.props?.title || '为你推荐' }}</div>
                  <div class="m-products">
                    <div v-for="(p, index) in mockProducts" :key="index" class="m-product">
                      <img :src="p.image" alt="" />
                      <b>{{ p.name }}</b>
                      <em>¥{{ p.price }}</em>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'couponZone'">
                <div class="m-card">
                  <div class="m-card__title">优惠券</div>
                  <div class="m-three">
                    <div v-for="(item, index) in itemsOf(c, 3)" :key="index" class="m-three__item">
                      <b>{{ item.value || item.discountAmount || '优惠' }}</b>
                      <span>{{ item.name || item.templateName || '优惠券' }}</span>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'activityZone'">
                <div class="m-card">
                  <div class="m-card__title">活动</div>
                  <div class="m-three">
                    <div v-for="(item, index) in itemsOf(c, 3)" :key="index" class="m-three__item">
                      <b>{{ item.title || item.activityName || `活动${index + 1}` }}</b>
                      <span>{{ item.description || '限时优惠' }}</span>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'shopHeader'">
                <div class="m-card m-shop">
                  <span class="m-shop__logo">{{ (c.props?.shopName || '店').slice(0, 1) }}</span>
                  <div>
                    <b>{{ c.props?.shopName || '店铺' }}</b>
                    <span>{{ c.props?.description || '欢迎光临' }}</span>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'shopList'">
                <div class="m-card">
                  <div class="m-shoplist">
                    <div v-for="(item, index) in itemsOf(c, 3)" :key="index" class="m-shoplist__row">
                      <span class="m-shoplist__logo">{{ (item.name || item.merchantName || '店').slice(0, 1) }}</span>
                      <div>
                        <b>{{ item.name || item.merchantName || `店铺 ${index + 1}` }}</b>
                        <span>{{ item.description || '精选店铺' }}</span>
                      </div>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'benefits'">
                <div class="m-card">
                  <div class="m-benefit" v-for="(item, index) in itemsOf(c, 4)" :key="index">
                    <span>{{ item.name || item.title || `权益 ${index + 1}` }}</span>
                    <i>{{ item.value || item.description || '查看' }}</i>
                  </div>
                </div>
              </template>

              <template v-else-if="c.type === 'imageText'">
                <div class="m-card">
                  <img v-if="firstImage(c)" class="m-imagetext__img" :src="firstImage(c)" alt="" />
                  <b>{{ c.props?.title || '图文广告' }}</b>
                  <span>{{ c.props?.description || c.props?.subtitle || '' }}</span>
                </div>
              </template>

              <template v-else-if="c.type === 'divider'">
                <div class="m-divider" />
              </template>

              <template v-else-if="c.type === 'spacer'">
                <div class="m-spacer" />
              </template>

              <template v-else-if="c.type === 'cartFloat'">
                <div class="m-cart">购物车</div>
              </template>

              <!-- 店铺活动 / 店铺分类 / 店铺评价：小程序上是一句话占位，
                   预览保持同样的说法，不要画成别的样子，
                   否则运营会以为这里能配更复杂的内容。 -->
              <template v-else>
                <div class="m-card m-generic">
                  <b>{{ c.props?.title || nameOf(c.type) }}</b>
                  <span>已按店铺装修配置展示</span>
                </div>
              </template>
            </div>
          </div>
        </div>

        <div class="tabbar">
          <span v-for="tab in tabs" :key="tab.key" :class="{ 'tabbar__on': tab.active }">
            <i>{{ tab.icon }}</i>{{ tab.label }}
          </span>
        </div>
      </div>
    </div>

    <p class="stage__tip">预览为小程序实际渲染示意；拖动组件可排序</p>
  </div>
</template>

<script setup lang="ts">
/**
 * 装修画布的手机预览。
 *
 * 组件渲染与小程序端 apps/user-uniapp/src/components/DesignComponent.vue **一一对应**。
 * 两边不一致时会出现最难查的一类问题：后台预览里好好的，发布到小程序是乱的，
 * 而配置 JSON 完全相同 —— 排查的人根本想不到要去比对两个组件文件。
 */
import { computed, ref } from 'vue';

const props = defineProps<{
  page: string;
  components: any[];
  library?: any[];
}>();

const emit = defineEmits<{
  reorder: [index: number];
  move: [index: number, delta: number];
  remove: [index: number];
}>();

const dragIndex = ref(-1);
const dropIndex = ref(-1);
const dropActive = ref(false);

const pageTitle = computed(() =>
  props.page === 'profile' ? '我的' : props.page === 'store' ? '店铺' : '首页',
);

const tabs = computed(() => {
  if (props.page === 'store') return [{ key: 'store', label: '店铺', icon: '店', active: true }];
  return [
    { key: 'index', label: '首页', icon: '首', active: props.page === 'index' },
    { key: 'profile', label: '我的', icon: '我', active: props.page === 'profile' },
  ];
});

// 商品占位图：用内联 SVG 而不是外链，外链挂了预览就出现裂图，
// 而裂图会被误当成「运营没上传商品图」。
const placeholder = (label: string) =>
  'data:image/svg+xml;utf8,' +
  encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120">
       <rect width="120" height="120" fill="#f0f2f5"/>
       <text x="60" y="68" font-size="16" fill="#a1a1a6" text-anchor="middle">${label}</text>
     </svg>`,
  );

const mockProducts = [
  { name: '示例商品 A', price: '99.00', image: placeholder('商品') },
  { name: '示例商品 B', price: '128.00', image: placeholder('商品') },
  { name: '示例商品 C', price: '59.00', image: placeholder('商品') },
  { name: '示例商品 D', price: '199.00', image: placeholder('商品') },
];

function nameOf(type: string) {
  return props.library?.find((c: any) => c.type === type)?.name || type;
}

function firstImage(c: any): string {
  return c?.props?.images?.[0] || c?.props?.image || '';
}

// 没配内容时给占位项：空网格看起来像坏了，占位项才看得出「这里有 8 个入口」。
function itemsOf(c: any, count: number) {
  const raw = c?.props?.items || c?.props?.categories || c?.props?.banners;
  if (Array.isArray(raw) && raw.length) return raw;
  return Array.from({ length: count }, (_, index) => ({ name: `入口${index + 1}` }));
}

defineExpose({ dropActive });
</script>

<style scoped>
.stage {
  display: grid;
  justify-items: center;
  gap: var(--space-3);
}

/* 手机外框：390×780 是真实小程序的量级，
   让人对成品高度有直觉。之前是一个 320px 的白框，看着像网页而不是手机。 */
.phone {
  position: relative;
  width: 320px;
  padding: 10px;
  border-radius: 34px;
  background: #111;
  box-shadow: 0 24px 60px rgba(0, 0, 0, 0.22);
}

.phone__notch {
  position: absolute;
  top: 11px;
  left: 50%;
  z-index: 3;
  width: 100px;
  height: 18px;
  transform: translateX(-50%);
  border-radius: 0 0 12px 12px;
  background: #111;
}

.phone__screen {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 620px;
  overflow: hidden;
  position: relative;
  border-radius: 26px;
  background: var(--bg-page);
}

.phone__screen--over {
  outline: 2px dashed var(--brand);
  outline-offset: -4px;
}

.status-bar {
  display: flex;
  justify-content: space-between;
  padding: 8px 20px 4px;
  color: #1d1d1f;
  font-size: 11px;
  font-weight: 600;
}

.nav-bar {
  padding: 6px 0 8px;
  color: #1d1d1f;
  font-size: 15px;
  font-weight: 600;
  text-align: center;
}

.screen-scroll {
  flex: 1;
  overflow-y: auto;
  padding-bottom: var(--space-3);
}

.tabbar {
  display: flex;
  justify-content: space-around;
  padding: 8px 0 10px;
  border-top: 0.5px solid var(--hairline);
  background: #fff;
}

.tabbar span {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  color: #8e8e93;
  font-size: 10px;
}

.tabbar span i {
  font-style: normal;
  font-size: 14px;
}

.tabbar__on {
  color: var(--brand) !important;
}

.stage__tip {
  margin: 0;
  color: var(--text-3);
  font-size: var(--text-note);
}

.empty {
  padding: var(--space-8) 0;
  text-align: center;
}

.empty__title {
  font-size: var(--text-foot);
  color: var(--text-2);
}

.empty__desc {
  margin-top: var(--space-1);
  font-size: var(--text-note);
  color: var(--text-3);
}

/* ---------- 组件槽位：编辑态的边框与工具条 ---------- */
.slot {
  position: relative;
  margin: var(--space-1) 10px;
  border: 1px solid transparent;
  border-radius: var(--radius-md);
}

.slot:hover {
  border-color: var(--brand);
}

.slot--dragging {
  opacity: 0.4;
}

.slot--over {
  border-color: var(--brand);
  border-style: dashed;
}

.slot__chrome {
  display: none;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-1);
  padding: 2px 4px;
  border-radius: var(--radius-sm) var(--radius-sm) 0 0;
  background: var(--brand);
  color: #fff;
  font-size: 10px;
}

.slot:hover .slot__chrome {
  display: flex;
}

.slot__ops {
  display: flex;
  gap: 2px;
}

.slot__op {
  padding: 1px 5px;
  border: none;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.22);
  color: #fff;
  font-size: 10px;
  cursor: pointer;
}

.slot__op:disabled {
  opacity: 0.4;
}

.slot__op--danger {
  background: rgba(255, 59, 48, 0.85);
}

/* ---------- 组件本体：小程序端的真实观感 ---------- */
.mock {
  font-size: 12px;
}

.m-banner {
  position: relative;
  overflow: hidden;
  height: 132px;
  margin-top: var(--space-1);
  border-radius: var(--radius-md);
  background: linear-gradient(135deg, #e8eef7, #f5f7fa);
}

.m-banner img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.m-banner__ph {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  color: var(--text-3);
  font-size: var(--text-note);
}

.m-banner__cap {
  position: absolute;
  bottom: 8px;
  left: 12px;
  color: #111;
  font-size: 15px;
  font-weight: 600;
}

.m-search {
  height: 32px;
  margin: var(--space-2) 10px 0;
  padding: 0 var(--space-3);
  border-radius: 16px;
  background: #fff;
  color: var(--text-3);
  line-height: 32px;
}

.m-title {
  margin: var(--space-3) 12px var(--space-1);
}

.m-title b {
  display: block;
  font-size: 15px;
  font-weight: 600;
}

.m-title span {
  display: block;
  margin-top: 2px;
  color: var(--text-2);
  font-size: 11px;
}

.m-notice {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: var(--space-2) 10px 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  background: #fff;
}

.m-notice em {
  padding: 1px 6px;
  border-radius: 10px;
  background: rgba(255, 159, 10, 0.14);
  color: #8a5200;
  font-size: 10px;
  font-style: normal;
}

.m-notice span {
  overflow: hidden;
  color: var(--text-2);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.m-seckill {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: var(--space-2) 10px 0;
  padding: var(--space-3);
  border-radius: var(--radius-md);
  background: linear-gradient(120deg, #ffe7d9, #fff2e7);
}

.m-seckill b {
  display: block;
  font-size: 14px;
  font-weight: 600;
}

.m-seckill span {
  display: block;
  margin-top: 2px;
  color: #a05a2c;
  font-size: 10px;
}

.m-seckill i {
  color: #a05a2c;
  font-size: 11px;
  font-style: normal;
}

.m-member {
  margin: var(--space-3) 10px 0;
  padding: var(--space-4);
  border-radius: var(--radius-md);
  background: linear-gradient(135deg, #0071e3, #49a7ff);
  color: #fff;
}

.m-member b {
  display: block;
  font-size: 16px;
  font-weight: 600;
}

.m-member span {
  display: block;
  margin-top: var(--space-1);
  opacity: 0.9;
  font-size: 11px;
}

.m-card {
  margin: var(--space-2) 10px 0;
  padding: var(--space-3);
  border-radius: var(--radius-md);
  background: #fff;
}

.m-card__title {
  margin-bottom: var(--space-2);
  font-size: 13px;
  font-weight: 600;
}

.m-quad {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-2);
}

.m-quad__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  color: var(--text-2);
  font-size: 10px;
}

.m-quad__icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  border-radius: 10px;
  background: #eef4ff;
  color: var(--brand);
  font-size: 13px;
  font-weight: 600;
}

.m-quad__item i {
  font-style: normal;
}

.m-products {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-2);
}

.m-product img {
  width: 100%;
  height: 72px;
  border-radius: var(--radius-sm);
  object-fit: cover;
}

.m-product b {
  display: block;
  margin-top: 3px;
  overflow: hidden;
  font-size: 11px;
  font-weight: 400;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.m-product em {
  display: block;
  color: var(--danger);
  font-size: 12px;
  font-style: normal;
  font-weight: 600;
}

.m-three {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--space-2);
}

.m-three__item {
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  background: var(--bg-page);
  text-align: center;
}

.m-three__item b {
  display: block;
  color: var(--danger);
  font-size: 11px;
}

.m-three__item span {
  display: block;
  margin-top: 2px;
  color: var(--text-2);
  font-size: 10px;
}

.m-shop {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.m-shop__logo {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 46px;
  height: 46px;
  border-radius: 12px;
  background: var(--brand);
  color: #fff;
  font-size: 18px;
}

.m-shop b {
  display: block;
  font-size: 14px;
  font-weight: 600;
}

.m-shop span {
  display: block;
  margin-top: 3px;
  color: var(--text-2);
  font-size: 11px;
}

.m-shoplist {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.m-shoplist__row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.m-shoplist__logo {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 8px;
  background: var(--brand);
  color: #fff;
  font-size: 12px;
}

.m-shoplist__row b {
  display: block;
  font-size: 12px;
  font-weight: 600;
}

.m-shoplist__row span {
  display: block;
  color: var(--text-2);
  font-size: 10px;
}

.m-benefit {
  display: flex;
  justify-content: space-between;
  padding: var(--space-2) 0;
  border-bottom: 0.5px solid var(--hairline);
}

.m-benefit:last-child {
  border-bottom: none;
}

.m-benefit i {
  color: var(--text-2);
  font-size: 11px;
  font-style: normal;
}

.m-imagetext__img {
  width: 100%;
  height: 120px;
  margin-bottom: var(--space-2);
  border-radius: var(--radius-sm);
  object-fit: cover;
}

.m-card > b {
  display: block;
  font-size: 13px;
  font-weight: 600;
}

.m-card > span {
  display: block;
  margin-top: 3px;
  color: var(--text-2);
  font-size: 11px;
}

.m-generic {
  text-align: center;
}

.m-divider {
  height: 1px;
  margin: var(--space-3) 12px;
  background: var(--hairline);
}

.m-spacer {
  height: 14px;
}

.m-cart {
  position: absolute;
  right: var(--space-4);
  bottom: var(--space-6);
  padding: 6px 14px;
  border-radius: 20px;
  background: var(--brand);
  color: #fff;
  font-size: 11px;
  box-shadow: 0 10px 30px rgba(0, 113, 227, 0.25);
}
</style>
