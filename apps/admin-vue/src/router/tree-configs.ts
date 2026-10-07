// 树形页面配置（分类管理 / 权限点管理）。
// 两个页面结构同构：一个 TreeView + 各自的字段映射。

export const TREES = {
  categories: {
    title: '分类管理',
    desc: '三级分类树，新增商品时按它选类目',
    endpoint: '/gateway/categories/Tree',
    method: 'GET',
    includeDisabled: true,
    createEndpoint: '/gateway/categories/Create',
    updateEndpoint: '/gateway/categories/Update',
    deleteEndpoint: '/gateway/categories/Delete',
    createPermission: 'category:create',
    updatePermission: 'category:update',
    deletePermission: 'category:delete',
    maxLevel: 3,
    idField: 'id',
    // 请求体里的 Id 字段名（后端命令是 `UpdateCategoryCommand(long CategoryId, ...)`）。
    // 它与节点上的 `idField` 不是一回事：节点里叫 `id`，请求体里叫 `categoryId`。
    // 之前两处共用 `idField`，于是编辑 / 停用分类发的是 `{ id }`，
    // 后端收不到 CategoryId（默认 0）直接 400「分类 Id 必须为正数」——
    // 界面表现是「改了名点保存，弹出这句莫名其妙的校验错误」。
    idBodyField: 'categoryId',
    parentField: 'parentId',
    nameField: 'categoryName',
    codeField: 'categoryCode',
    sortField: 'sortOrder',
    statusField: 'status',
    statusDict: 'userStatus',
    fields: [
      { name: 'categoryName', label: '分类名称', required: true, maxLength: 64 },
      { name: 'categoryCode', label: '分类编码', maxLength: 64, placeholder: '可留空' },
      { name: 'icon', label: '分类图标 URL', maxLength: 512 },
      { name: 'image', label: '分类大图 URL', maxLength: 512 },
      { name: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0 },
      { name: 'status', label: '状态', type: 'select', default: 1, options: [{ value: 1, label: '启用' }, { value: 2, label: '停用' }] },
    ],
    labelField: 'categoryName',
    search: true,
    searchPlaceholder: '分类名 / 编码',
  },

  permissions: {
    title: '权限点管理',
    desc: '权限点是运行时可维护的实体，绑定接口路径后网关立即生效',
    endpoint: '/gateway/permissions/Tree',
    method: 'GET',
    includeDisabled: true,
    createEndpoint: '/gateway/permissions/Create',
    updateEndpoint: '/gateway/permissions/Update',
    deleteEndpoint: '/gateway/permissions/Delete',
    statusEndpoint: '/gateway/permissions/Status',
    createPermission: 'permission:create',
    updatePermission: 'permission:update',
    deletePermission: 'permission:delete',
    maxLevel: 3,
    idField: 'id',
    // 同上：后端是 `UpdatePermissionCommand(long PermissionId, ...)` / `ChangePermissionStatusCommand`
    idBodyField: 'permissionId',
    parentField: 'parentId',
    nameField: 'name',
    builtinField: 'isBuiltin',
    fields: [
      { name: 'name', label: '中文名称', required: true, maxLength: 32 },
      { name: 'code', label: '权限编码', maxLength: 64, placeholder: '如 user:read，仅权限点需要' },
      { name: 'apiPath', label: '接口路径', maxLength: 512, placeholder: '多个路径用英文逗号分隔，仅权限点需要' },
      { name: 'sortOrder', label: '排序', type: 'number', min: 0, default: 0 },
      { name: 'description', label: '说明', type: 'textarea', maxLength: 200 },
    ],
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
