<template>
  <div class="shell">
    <aside class="shell__side" :style="{ width: collapsed ? SIDEBAR_COLLAPSED : SIDEBAR }">
      <div class="brand">
        <div class="brand__mark">S</div>
        <div v-show="!collapsed" class="brand__text">
          <div class="brand__name">SimpleShop</div>
          <div class="brand__sub">管理后台</div>
        </div>
      </div>

      <nav class="nav" @mouseleave="hovered = ''">
        <!-- 分组标题 -->
        <template v-for="sec in sections" :key="sec.title">
          <div v-show="!collapsed" class="nav__section">{{ sec.title }}</div>
          <template v-for="item in sec.items" :key="item.path">
        <div
          class="nav__item"
          @mouseenter="hovered = item.path"
        >
          <RouterLink :to="firstLeaf(item)" class="nav__link">
            <el-icon class="nav__icon">
              <component :is="item.meta?.icon" />
            </el-icon>
            <span v-show="!collapsed" class="nav__label">{{ item.meta?.title }}</span>
          </RouterLink>

          <div v-show="!collapsed && isActive(item)" class="nav__sub">
            <RouterLink
              v-for="leaf in leaves(item)"
              :key="leaf.fullPath"
              :to="leaf.fullPath"
              class="nav__sublink"
              :class="{ 'nav__sublink--active': route.path === leaf.path }"
            >
              {{ leaf.meta.title }}
            </RouterLink>
          </div>

          <div v-if="collapsed && hovered === item.path" class="nav__fly">
            <RouterLink
              v-for="leaf in leaves(item)"
              :key="leaf.fullPath"
              :to="leaf.fullPath"
              class="nav__flylink"
            >
              {{ leaf.meta.title }}
            </RouterLink>
          </div>
        </div>
          </template>
        </template>
      </nav>

      <button class="collapse" type="button" @click="collapsed = !collapsed">
        <el-icon>
          <component :is="collapsed ? 'DArrowRight' : 'DArrowLeft'" />
        </el-icon>
        <span v-show="!collapsed">收起侧栏</span>
      </button>
    </aside>

    <div class="shell__main">
      <header class="topbar">
        <h1 class="topbar__title">{{ currentTitle }}</h1>
        <div class="topbar__right">
          <el-button text @click="onLogout">退出登录</el-button>
        </div>
      </header>

      <main class="shell__body">
        <RouterView />
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter, RouterLink, RouterView } from 'vue-router';
import { adminRoutes } from '@/router/modules';
import { logout } from '@/api/auth';

// 布局常量与 styles.css 的 --sidebar-w / --sidebar-w-collapsed 保持一致。
// 这里不用 CSS 变量取值是因为绑定到 style 时需要具体字符串。
const SIDEBAR = '224px';
const SIDEBAR_COLLAPSED = '64px';

const route = useRoute();
const router = useRouter();

const collapsed = ref(false);
const hovered = ref('');

// 二级路由解析成绝对路径。菜单与 router 用同一份数据，
// 加页面时不会出现「菜单加了路由没加」这类漏配。
function leaves(group: any) {
  const base = ('/' + group.path).replace(/\/+/g, '/');
  return (group.children || [])
    // 带参数（:id）的页面**不进菜单**：它们要一个真实 Id 才能打开，
    // 而菜单里只有一个字面量 ':id'，点进去必然 404 / 查不到数据。
    // 正确的入口是从列表页点某一行进去——运营要看的也是「某一单」，
    // 不是「随便某一单」。用规则判断而不是逐个打 hidden 标记，
    // 是为了将来新增带参路由时不会漏标。
    .filter((c: any) => c.meta?.title && !String(c.path).includes(':'))
    .map((c: any) => {
      const full = (base + '/' + c.path).replace(/\/+/g, '/').replace(/\/$/, '');
      return { ...c, path: full, fullPath: full };
    });
}

// 无二级的模块（工作台）直接用自身路径
function firstLeaf(group: any) {
  const list = leaves(group);
  if (list.length > 0) return list[0].fullPath;
  return ('/' + group.path).replace(/\/$/, '');
}

function isActive(group: any) {
  return route.path.indexOf('/' + group.path) === 0;
}

// 菜单顺序：工作台在前，其余按路由声明顺序（modules.ts 里已按业务分组排好）
const menus = adminRoutes.filter((r: any) => !r.meta?.hidden);

// 侧边栏按**业务域**分组，而不是 23 项平铺一条。
// 平铺的副作用不只是长：它没有任何结构暗示，运营只能逐条扫，
// 「我要去退款」得先在 23 行里找「退款」两个字。
// 分组是**展示层**的组织方式，路由本身仍在 modules.ts 里，这里只做归类不新增条目，
// 所以不会出现「菜单里有、路由里没有」的漂移。
const SECTION_ORDER = [
  { title: '总览', paths: ['dashboard'] },
  { title: '组织与账号', paths: ['platforms', 'merchants', 'users', 'customers', 'roles'] },
  { title: '商品与库存', paths: ['categories', 'brands', 'products', 'inventory', 'search-index'] },
  { title: '交易', paths: ['orders', 'payments', 'refunds'] },
  { title: '营销与权益', paths: ['promotions', 'coupons', 'seckill', 'points', 'evaluates'] },
  { title: '内容与数据', paths: ['design', 'reports', 'logs'] },
  { title: '系统', paths: ['files'] },
];

const sections = SECTION_ORDER.map((s) => ({
  title: s.title,
  items: menus.filter((m: any) => s.paths.indexOf(m.path) >= 0),
}))
  // 丢掉空分组：把 path 改名后这里会自动少一组，不会留下一个光秃秃的标题
  .filter((s: any) => s.items.length > 0);

const currentTitle = computed(() => (route.meta?.title as string) || '工作台');

function onLogout() {
  logout();
  router.replace('/login');
}
</script>

<style scoped>
.shell {
  display: flex;
  height: 100%;
  background: var(--bg-page);
}

.shell__side {
  flex: none;
  display: flex;
  flex-direction: column;
  background: var(--bg-card);
  box-shadow: var(--shadow-card);
  transition: width var(--duration-base) var(--ease);
  overflow: hidden;
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  height: var(--header-h);
  padding: 0 var(--space-4);
  flex: none;
}

.brand__mark {
  width: 28px;
  height: 28px;
  border-radius: var(--radius-sm);
  background: var(--brand);
  color: #ffffff;
  font-size: var(--text-sub);
  font-weight: 600;
  display: flex;
  align-items: center;
  justify-content: center;
  flex: none;
}

.brand__name {
  font-size: var(--text-sub);
  font-weight: 600;
  line-height: var(--lh-foot);
}

.brand__sub {
  font-size: var(--text-note);
  color: var(--text-2);
  line-height: var(--lh-note);
}

.nav {
  flex: 1;
  overflow-y: auto;
  padding: var(--space-2) var(--space-2) var(--space-4);
}

/* 分组标题：字很小、颜色很淡，只提供「结构暗示」不抢注意力。
   用 12px + 三级文字色而不是加粗放大——加粗会让 7 个小标题比菜单项还重。 */
.nav__section {
  padding: var(--space-4) var(--space-3) var(--space-1);
  font-size: var(--text-note);
  line-height: var(--lh-note);
  color: var(--text-3);
}

.nav__item {
  position: relative;
}

.nav__link {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-height: var(--hit-min);
  padding: 0 var(--space-3);
  border-radius: var(--radius-md);
  color: var(--text-1);
  text-decoration: none;
  font-size: var(--text-sub);
  transition: background-color var(--duration-fast) var(--ease);
}

.nav__link:hover {
  background: rgba(0, 113, 227, 0.08);
}

.nav__icon {
  font-size: var(--text-body);
  color: var(--text-2);
  flex: none;
}

.nav__label {
  white-space: nowrap;
}

.nav__sub {
  padding: var(--space-1) 0 var(--space-2) 0;
}

.nav__sublink {
  display: block;
  padding: var(--space-2) var(--space-3) var(--space-2) 44px;
  border-radius: var(--radius-sm);
  font-size: var(--text-foot);
  color: var(--text-2);
  text-decoration: none;
}

.nav__sublink:hover {
  background: rgba(0, 113, 227, 0.06);
}

.nav__sublink--active {
  color: var(--brand);
  font-weight: 500;
  background: rgba(0, 113, 227, 0.1);
}

.nav__fly {
  position: absolute;
  left: 100%;
  top: 0;
  z-index: 20;
  min-width: 160px;
  padding: var(--space-1);
  background: var(--bg-card);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-raised);
}

.nav__flylink {
  display: block;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  font-size: var(--text-foot);
  color: var(--text-1);
  text-decoration: none;
  white-space: nowrap;
}

.nav__flylink:hover {
  background: rgba(0, 113, 227, 0.08);
}

.collapse {
  flex: none;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-height: var(--hit-min);
  margin: 0;
  padding: 0 var(--space-4);
  border: none;
  border-top: 1px solid var(--divider-soft);
  background: transparent;
  color: var(--text-2);
  font-family: var(--font-sans);
  font-size: var(--text-foot);
  cursor: pointer;
}

.collapse:hover {
  color: var(--brand);
}

.shell__main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.topbar {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: var(--header-h);
  padding: 0 var(--space-6);
  background: var(--bg-card);
  box-shadow: var(--shadow-card);
}

.topbar__title {
  margin: 0;
  font-size: var(--text-title-3);
  line-height: var(--lh-title-3);
  font-weight: 600;
}

.shell__body {
  flex: 1;
  overflow-y: auto;
  padding: var(--space-6);
}
</style>
