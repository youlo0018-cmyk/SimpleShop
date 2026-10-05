import { defineConfig } from 'vite';
import uniImport from '@dcloudio/vite-plugin-uni';

// 该包当前以 CommonJS 发布；Vite 在不同 Node 版本下有时把 default 再包一层。
// 两种形态都兼容，避免构建直接卡在 `uni is not a function`。
const uni = (typeof uniImport === 'function'
  ? uniImport
  : (uniImport as unknown as { default: typeof uniImport }).default);

// H5 开发时只跟网关打交道。真机 / 小程序构建时通过 VITE_API_BASE
// 指定网关公网地址，代码里不需要再出现任何环境判断。
export default defineConfig({
  plugins: [uni()],
  server: {
    port: 5175,
    strictPort: true,
    proxy: {
      '/gateway': {
        target: 'http://127.0.0.1:5008',
        changeOrigin: true,
      },
    },
  },
});
