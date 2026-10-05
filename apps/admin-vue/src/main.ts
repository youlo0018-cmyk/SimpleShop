import { createApp } from 'vue';
import { createPinia } from 'pinia';
import ElementPlus from 'element-plus';
import * as ElIcons from '@element-plus/icons-vue';
import zhCn from 'element-plus/es/locale/lang/zh-cn';
import 'element-plus/dist/index.css';

// 顺序不能换：token 必须先加载，否则 Element Plus 组件拿到的是无 token 的 CSS 变量，
// 首个页面会闪一下没有苹果风的样子。
import './styles.css';
import './element-theme.css';

import App from './App.vue';
import router from './router';

const app = createApp(App);

// 侧边栏图标是按**字符串名**动态渲染的（<component :is="meta.icon" />），
// 而动态组件只能解析全局注册过的组件。
// 少注册这一步的症状是：菜单文字都在，图标位置一片空白——
// 页面不报错、构建也通过，只有肉眼看截图才发现。
Object.entries(ElIcons).forEach(([name, component]) => {
  app.component(name, component);
});

app.use(createPinia());
app.use(router);
app.use(ElementPlus, { locale: zhCn });

app.mount('#app');
