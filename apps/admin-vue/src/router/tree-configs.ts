// 树形页面配置（分类管理 / 权限点管理）。
// 两个页面结构同构：一个 TreeView + 各自的字段映射。

export const TREES = {
  categories: {
    title: '分类管理',
    desc: '三级分类树，新增商品时按它选类目',
    endpoint: '/gateway/categories/Tree',
    method: 'GET',
    labelField: 'categoryName',
    codeField: 'categoryCode',
    sortField: 'sortOrder',
    statusField: 'status',
    statusDict: 'userStatus',
    search: true,
    searchPlaceholder: '分类名 / 编码',
  },

  permissions: {
    title: '权限点管理',
    desc: '权限点是运行时可维护的实体，绑定接口路径后网关立即生效',
    endpoint: '/gateway/permissions/Tree',
    method: 'GET',
    labelField: 'name',
    // 权限点没有「排序」概念，只有接口路径——那是它与分类最大的区别
    codeField: 'apiPath',
    statusField: 'status',
    statusDict: 'permissionStatus',
    search: true,
    searchPlaceholder: '权限名 / 编码 / 路径',
  },
};

export default TREES;
