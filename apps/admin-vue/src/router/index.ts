import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router';
import { isLoggedIn } from '@/api/auth';

// 菜单结构同时驱动侧边栏与路由，单一来源（DESIGN_SPEC「不凑合」原则：
// 菜单与路由各写一份，加页面时必然漏一处）。
// 后续页面按模块拆到各自文件，由 modules.ts 汇总。
import { adminRoutes } from './modules';

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/LoginView.vue'),
    meta: { public: true, title: '登录' },
  },
  {
    path: '/',
    component: () => import('@/layouts/AdminLayout.vue'),
    redirect: '/dashboard',
    children: adminRoutes,
  },
  {
    // 兜底：未匹配到的路径回工作台，而不是白屏
    path: '/:pathMatch(.*)*',
    redirect: '/dashboard',
  },
];

const router = createRouter({
  // hash 模式：后台是纯静态托管，history 模式需要服务端把任意路径回退到 index.html。
  // Nginx 没配 rewrite 时 history 模式会直接 404。
  history: createWebHashHistory(),
  routes,
  scrollBehavior: () => ({ top: 0 }),
});

router.beforeEach((to) => {
  if (to.meta?.public) return true;
  if (isLoggedIn()) return true;
  // 未登录一律回登录页，并带上原路径，登录后跳回
  return { path: '/login', query: { redirect: to.fullPath } };
});

router.afterEach((to) => {
  const title = to.meta?.title;
  document.title = title ? `${title} · SimpleShop` : 'SimpleShop 管理后台';
});

export default router;
