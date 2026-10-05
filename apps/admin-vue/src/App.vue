<template>
  <router-view />
</template>

<script setup lang="ts">
// 根组件只放路由出口。布局（侧边栏 / 顶栏）放在 AdminLayout 里，
// 登录页不需要布局——把所有结构塞进根组件会让登录页也被侧边栏包住。
</script>
