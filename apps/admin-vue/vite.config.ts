import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath, URL } from 'node:url';

// 后台开发配置。
// 说明两点：
//   1. 代理指向本机网关 5008 —— 前端**只**跟网关打交道，
//      直连各业务服务端口会让 CORS 与鉴权两件事在前端各写一遍。
//   2. 仓库路径含中文，Vite 的 fs.allow 必须放开，否则热更新会报 403。
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    fs: {
      // 允许仓库根目录（中文路径），保证热更新能读到 src 外的共享资源
      allow: [fileURLToPath(new URL('../../', import.meta.url))],
    },
    proxy: {
      '/gateway': {
        target: 'http://127.0.0.1:5008',
        changeOrigin: true,
      },
    },
  },
  preview: {
    port: 5174,
    strictPort: true,
    // ⚠️ preview **不继承** server.proxy，必须单独再写一遍。
    // 漏掉的表现是：页面能打开、样式也正常，但一登录就 404 /gateway/auth/token，
    // 很容易被误判成「后端没起」或「令牌端点配错了」。
    proxy: {
      '/gateway': {
        target: 'http://127.0.0.1:5008',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    // 源码里有中文字段名（XxxText），不做压缩关键字改名
    chunkSizeWarningLimit: 1200,
  },
});
