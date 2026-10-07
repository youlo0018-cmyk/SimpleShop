# REQUIREMENTS_TRACE.md — 需求追溯与漂移审计

> 用途：把用户明确提出过的需求逐条映射到「当前实现位置 + 验证证据 + 是否漂移」。
> 新需求先写进这里，再改代码；发现漂移时先修正代码，再把结论写回本文和对应规格文档。
> 本文是**防漂移清单**，不是第二份业务规格：业务口径仍以 `BUSINESS.md` / `DATA_SPEC.md` / `FRONTEND_DESIGN.md` 为准。

## 1. 本次审计发现并修正的漂移

| # | 漂移 | 用户原意 | 修正 | 验证 |
|---|---|---|---|---|
| D-1 | 后台侧边栏出现独立「支付」「库存」菜单 | 支付合并到订单列表，库存合并到商品列表 | 删除 `modules.ts` 的独立路由与 `list-configs.ts` 的死配置；支付在订单列表/详情，库存在商品列表「库存」操作；写入 `FRONTEND_DESIGN.md` 4.3 / 4.4 / 6 | `admin-deep-regression.mjs`：`merged-product-inventory`、`merged-order-simulate-payment` |
| D-2 | `merchants/Options?platformId=0` 返回空 | 0 表示全部平台，超管应看到全部启用商户 | `MerchantRepository.ListEnabledByPlatform` 改为 `platformId > 0` 才按平台过滤 | `API-MP-029`；商品列表商户筛选深测 |
| D-3 | 前端多处 `Number(雪花 Id)` | 雪花 Id 必须按字符串传输与比较 | 修正 `ConfigView` / `DesignBuilderView` / `DetailView` / `FormView` / `ProductFormView` / `SeckillItemsView` / 小程序结算页最优券 Id | `npm run build`（后台）、`npm run type-check`（小程序） |
| D-4 | 场次商品页校验了 SKU Id 却不显示错误 | 提交失败必须输入框飘红 + 下方备注原因 | `SeckillItemsView` 的 SKU Id 表单项补 `:error="errors.sku"` | 后台构建 + UI 深测 |
| D-5 | 商品列表商户筛选无选项（D-2 的表象） | 下拉必须直接显示数据名称 | 后端修 D-2；前端深测等待下拉动画后再断言 | `admin-deep-regression --only products` 全绿 |
| D-6 | 券活动列表的「启用 / 停用」复用整行编辑 `Update` | 启停只该改状态 | 新增专用 `coupon-activities/SetStatus`（只写 Status + 最后操作人）；前端两个按钮改走它；启用补确认框（与其它页面一致）。**旧写法会把定向活动的 `Targets` 用 `'[]'` 洗成全场**，且模板被删后永远启用不了 | `API-MKT-108`（停用后读回，`targetType` / `targets` 原样保留）；`merged-coupon-activity-status`（UI 停用 → 启用，全程无 400） |
| D-7 | 模板已被删除的券活动仍显示「启用」 | 点了必报错的入口不该留着 | `CouponActivityItem` 增 `TemplateExists`；前端 `showWhen` 要求模板存在 | `merged-coupon-activity-status`；`admin-deep --only coupons` 25/25 |
| D-8 | 权限树把容器 Id（虚拟根 `0` / 业务大类 / 功能模块 / 空模块「品牌」）当权限点提交并落库 | 只保存叶子权限点（BUSINESS 5.4） | 前端 `RolePermissionsView` 只提交带 `code` 的叶子、空模块复选框按 `selectable` 禁用；后端 `roles/BindPermissions` 校验，容器 Id 一律 400 | `API-ADM-083d`（0 / 2101 / 2108 全部被拒，真叶子放行） |
| D-9 | `BUSINESS.md` 5.2 权限点清单与种子 / `permission` 表对不上 | 文档要能当核对口径 | 按库中实际内容同步：删 `customer:update`，`order:receive` / `order:cancel` → `order:virtual-deliver` / `order:pickup-ready` / `order:refund`；5.4 的「78 条」改回 77，模块列表补「品牌」并把「文件与日志」并回一项 | `docker exec … psql -c "select … from permission where code <> ''"` 逐条比对 |
| D-10 | 深测把「当前页签没有该状态的数据」误报成「按钮不存在」 | 每个页面每个组件都要真的点到 | 行操作改为逐页签查找（待发货才有「发货」、待取货才有「核销」） | `admin-deep --only orders` 14/14（含「核销」） |
| D-11 | 营销报表「订单明细」下钻指向不存在的路由 `/marketing/activity-records` | 点明细要能进参与记录页 | 改成 `/coupons/activity-records`（页面注册在券模块下）；原先落到 catch-all，直接把人送回工作台 | `admin-deep page-reports`：四张表 + 四档区间 + 下钻全绿 |
| D-12 | 平台装修「存草稿」100% 失败（`缺少「profile」页面画布`）；商户装修同样只发店铺页 | 存草稿要能存进去，且不能把另一页覆盖掉 | 后端 `DesignDraftMerger`：`pages` 逐页合并、其余顶层属性显式带了才覆盖，再整体校验；草稿缺失时以已发布版本为底 | `API-DS-036`（增量保存成功且 profile / tabBar 保留；全新平台只给一页仍被拒）；`page-design-platform` 存草稿成功 |
| D-13 | 装修搭建器没有右侧属性面板（DATA_SPEC 5.29 要求），组件拖进来后改不了字和图片 | 「A3 可以改组件内的内容」「商户可以改店铺页的轮播图」 | 组件库接口下发 props schema（文本 / 多行 / 图片 / 多图），`DesignBuilderView` 补第三栏属性面板（宽度、高度、内容），`PhonePreview` 支持选中态 | `page-design-platform`：选中组件 → 改标题 → 存草稿 → 重新打开仍在 |
| D-14 | 「存草稿」成功后没有任何提示（草稿其实已存进去） | 操作要有明确反馈 | `@click="saveDraft"` 会把 MouseEvent 当第一个实参传给 `saveDraft(silent = false)`，事件对象恒为真 → 提示被当「静默保存」吞掉；改成 `@click="saveDraft()"` | `page-design-platform` 断言成功提示 |
| D-15 | 两个 UI 回归脚本的默认端口是错的（`admin-ui-regression` 默认 5174 = 小程序；`user-ui-regression` 默认 5175 = 空端口） | 回归脚本必须开箱即用 | 默认值改为 5173 / 5174，与 `scripts/start-web.ps1` 对齐 | 两个脚本不带环境变量直接跑：后台 31+27+10 全绿、小程序 22/22 |
| D-16 | 物流公司的「新建 / 编辑」按钮指向不存在的 `/orders/logistics-companies/*`（路由其实挂在 `platforms` 下），点了被 catch-all 送去工作台；表单保存后的 `listRoute` 同样写错 | 列表按钮与保存回跳都要落到真实路由 | `list-configs.createRoute/rowRoute/editRoute` + `form-configs.listRoute` 全部改为 `/platforms/logistics-companies/*`；新增静态核对 `tests/ui/check-route-links.mjs`（71 条路由 × 65 处跳转）并挂进 `run-all.ps1` | `admin-deep` 的「新建按钮进入新建页」11 条 + `create-flow-logistics`；`check-route-links` 通过 |
| D-17 | 树页的「修改 / 启停」请求发的是 `{ id }`，而后端命令收的是 `CategoryId` / `PermissionId` → 一律 400「Id 必须为正数」 | 编辑与启停必须能真正保存 | `tree-configs` 增 `idBodyField`，`TreeView` 的编辑 / 启停 / 删除统一按它取字段名（原来删除是硬编码三元表达式，只有它是对的） | `admin-deep` 的 `page-category-crud`、`page-permission-crud`（新增 → 修改 → 停用 → 启用 → 删除） |
| D-18 | 分类页没有「停用 / 启用」按钮：模板只在有 `statusEndpoint` 时渲染，而分类走的是 Update，`toggleStatus` 的 else 分支成了不可达代码 | 分类要能一键启停 | 按钮条件改为 `config.updateEndpoint` | `page-category-crud` 的停用 / 启用步骤 |
| D-19 | 券模板下拉用 `coupon-templates/List` 当数据源，而该 DTO 是 `{ templateId, templateName }`，前端只认 `id/Id` → **每个选项的 value 都是空串**，选完仍是「请选择」，新建券活动永远存不了（接口层全绿） | 下拉必须能选到真实模板 | 数据源换成 `/marketing/coupon-templates/Options`（只返回启用模板、形状统一 `{id,name}`）；新增 `utils/options.js` 统一归一 id/name 键，`FormView` 与 `ListView` 共用 | `create-flow-couponActivity`（选模板 → 保存 → 列表可见） |
| D-20 | 新建账号不选角色时请求体带 `roleIds: ""`，命令里是 `long[]` → **整个命令绑定失败**（HTTP 200 + `{command:["不能为空"]}`） | 多选下拉的「空」必须是 `[]` | `FormView.fill()` 对 `multiple` 字段初始化为 `[]`；提交时把下拉的空串收敛成 `0` / `[]`（所有下拉字段在后端都是 long / int / long[]） | `create-flow-user`（连续两轮通过） |
| D-21 | 商品 SKU 表在规格项 ≥ 2 个时**列错位**：SKU 编码那一格变成售价输入框，编码框直接消失 → 商品永远存不了（提示「每个 SKU 都必须填写 SKU 编码」） | 规格列与静态列混排时插槽不能错位 | `ProductFormView` 给每个 `el-table-column` 加稳定 `key`（`v-for` 的规格列与静态列混在同一层时，无 key 的 diff 会按位置错位一格） | `create-flow-product`（含主图上传 + 规格 + SKU 编码/售价/库存） |
| D-22 | 设计器移动组件后选中项不跟随，属性面板会**悄悄切到邻居身上** | 选中态要跟着被移动的组件走 | `DesignBuilderView.onReorder/move` 在重排后同步 `selectedIndex` | `page-design-ops`（拖入 → 上移 → 下移 → 删除 → 存草稿 → 发布） |
| D-23 | 订单「发货」弹窗没有取消按钮，只能点右上角很小的 × | 弹窗要有显式取消 | `OrderListView` 发货弹窗补「取消」；`cancelDialog` 增加「无文字按钮时退回 headerbtn」的兜底 | `admin-deep` 的 `list-/orders-action-发货` |
| D-24 | 客户列表的「停用 / 启用」没有确认框，点一下客户就登不上了（其它页面的启停都有确认框） | 危险动作要有确认框与影响面 | `list-configs` 给客户启停补 `confirm`（含客户名 / 昵称 / 手机号与影响说明） | `admin-deep` 的 `list-/customers-action-停用 / 启用` |
| D-25 | 深测 `goto` 到与当前**完全相同**的 URL 时页面不会重新挂载，上一条用例的校验错误与表单值会串到下一条（新建商品一进去就顶着「商品名至少 2 个字符」跑） | 用例之间必须互相隔离 | `goto` 命中同 URL 时改为 `page.reload()` | `create-flow-product` 等新建流程用例 |
| D-26 | `marketing-regression` 的孤儿对账用例写死 `limit = 200`，而候选查询是**按时间升序**取前 N 条；开发库攒到 213 条参与记录后，刚写进去的那条排在 200 名之外 → 用例报「找不到候选」，看起来像对账坏了 | 用例要能扛住历史数据量 | 用例改用仓储允许的上限 `limit = 1000`，并把失败时的实际订单号打出来 | `marketing-regression` 98/98 |
| D-27 | 订单详情页的「快递发货」直接调 `/orders/Ship` 只传 `orderNo`，而校验器要求「物流公司必填 + 运单号 2-64 位」→ 点下去必然 400「请选择物流公司」；同时列表页与详情页各写了一套发货弹窗 | 发货要能真的发出去，且只有一份实现 | 抽出 `components/FulfillDialog.vue`（物流公司 / 运单号 / 备注 + 三个履约动作），列表页与详情页共用；虚拟发货补「发货内容必填」；备货完成后把**取货码**弹出来（列表页原来只显示一句 message，运营拿不到要交给顾客的码） | `page-order-detail-ship`（空提交 2 条字段错误 → 发货成功 → 状态 20→30）、`page-order-detail-virtual`、`page-pickup-verify-success` |
| D-28 | 死信列表的「重放」没有确认框，点一下就是一次真实的重复投递 | 有副作用的动作要有确认与影响说明 | `list-configs` 给重放补 `confirm`（说明「会重新投递给消费方，可能重复处理一次」） | `page-dead-letter-replay`（造真死信 → 确认框 → 重放 → 已重放计数 +1） |
| D-29 | 覆盖清单里还有几处按钮从未被点过：地区的「删除城市 / 删除区县」、商品的「删除规格值」、图片上传器的「替换图片」、富文本的斜体 / 标题 / 列表 / 插入图片、场次商品的「返回场次列表」 | 「每个组件每个功能都要测到」 | 逐个补进深测（富文本与插图都断言生成的 HTML；替换图片断言「仍是一张且地址变了」） | `admin-deep` 254/254；覆盖清单里 58 个按钮标签全部有用例 |
| D-30 | 三个列表的**搜索框是装饰性的**：退款列表的命令只有 `OrderNo`、积分流水与评价管理没有 `Keyword` —— 前端统一发的 `keyword` 被静默忽略，输入什么都返回全部 | 搜索框要么能筛，要么不要放 | 三处补上 `Keyword`（退款按退款单号 / 订单号模糊、积分按业务单号、评价按商品名 / 内容）；评价列表占位符同步改成「商品名 / 评价内容」（库里没有客户昵称，搜不到的不该写在占位符里）；新增静态回归 `API-ADM-101` 逐个比对「带/不带 keyword」的 total | `API-ADM-101`（12 个可搜索列表全部生效）；`admin-deep` 251/251 |
| D-31 | 后台存在**两套退款记录**：`order_refund`（订单侧执行记录，代客退款直接落这里）与 `refund_order`（支付侧审批单，退款列表读它）。测试一度以为「代客退款会出现在退款列表里」 | 口径要写清楚，避免把设计当成缺陷 | 写进本文与 `BUSINESS.md` §10.2 的说明；深测里「拒绝 + 必填原因」改为先 `refunds/Apply` 造一条真正的申请单，再在列表里拒绝 | `page-refund-dialog-partial`（代客部分退款 + 申请单被拒 → 状态 90） |
| D-32 | 深测的「搜索框」用例只验「输入能回车」，后端忽略 `keyword` 时照样通过 —— 三处装饰性搜索框就是这么漏过去的 | 用例必须能抓住「界面在骗人」 | 搜索步骤改为：填一个绝对搜不到的关键词 → 行数必须掉到 0 → 清空后恢复；断言三个数字都打出来 | `admin-deep` 254/254（17 个可搜索列表） |
| D-33 | **编辑页整批缺陷**：① 编辑平台必然 400（`PlatformCode` 非空 + 前端把 `readonlyInEdit` 字段排除在请求体外，ASP.NET Core 隐式必填拦下）；② 编辑平台的 Logo / 公告 / 备注、编辑商户的备注**保存即被清空**（列表 DTO 没返回这几个字段，而编辑页正是拿列表当数据源）；③ 物流公司 / 券模板 / 场次的编辑页一律「未找到该记录」（前端写死按 `r.id` 找行，DTO 用的却是 `logisticsId` / `templateId` / `sessionId`）；④ 编辑营销活动必然 400（`Targets` 非空，而表单根本没有这个字段）；⑤ 编辑场次 / 活动会把「排序」重置成 0（DTO 没返回 `sortOrder`）；⑥ 新建物流公司丢「备注」（Create 命令里没这个参数） | 「改一条数据」不能变成「丢一批数据」 | ① `PlatformCode` 改可空；② 列表 DTO 补 `Logo/Notice/Remark`（平台）与 `Remark`（商户）；③ `FormView` 按 `idField` 找行 + 列表页点编辑时用 router state 带行数据 + 券模板/券活动/活动改用 `Get` 端点；④ `Targets` 改可空 + 表单 `carry: ['targetType','targets']`；⑤ 场次 DTO 补 `sortOrder`、活动 DTO 补 `sortOrder`；⑥ Create 命令补 `Remark`；归属平台在编辑页标 `readonlyInEdit`（更新接口本来就忽略它） | 新增 4 条回填用例：`page-edit-roundtrip-platform` / `-merchant` / `-marketing`、`page-edit-time-roundtrip`（平台 5 字段、商户 3 字段、物流/券模板/活动/场次全部原样保存） |
| D-34 | 券活动 / 营销活动 / 秒杀场次的**时间字段不带时区标记**（`yyyy-MM-dd HH:mm`），编辑页把它交给 `new Date()` 时被当成**本地时间**解析：界面显示的是 UTC 原值，保存又按本地转 UTC —— **每编辑一次，活动时间整体偏移一个时区（实测 8 小时）** | 时间必须按 UTC 下发、按本地显示、原样存回 | 三处 DTO 的时间改成带 `Z` 的 ISO（`2026-10-07T07:35:00Z`）；C 端券活动列表同步（只影响展示） | `page-edit-time-roundtrip`：页面显示本地 17:36、保存后库里仍是 `09:36Z`（未偏移） |
| D-35 | `src/` 下**没有任何微服务级解决方案**：只有总 `SimpleShop.slnx`，README / PLAN 却声称「每个微服务一个独立解决方案」 | 「做一个总的 slnx + 每一个微服务做一个 sln 解决方案」（Rider 里按文件夹展开，不要所有项目平铺） | 为 `src/` 下全部 18 个文件夹各生成 `<服务名>.sln`（经典 `.sln` 格式；`dotnet sln add` 默认递归带上引用的 `Collaboration` 工程）；README / PLAN / 本文档的路径表同步改为 `.sln` | `dotnet sln <每个>.sln list` 18/18 通过（标准微服务 6 工程 / Gateway 3 / Scheduled 2 / Collaboration 2）；`scripts/build.ps1` 仍走总 `SimpleShop.slnx`，0 警告 0 错误 |
| D-36 | 本文档工程基线写「`User.PasswordSalt` / `Customer.PasswordSalt` 字段」，**实际库里没有 salt 列**（用户问过「盐保存了吗、没看到盐相关字段」） | 密码必须加盐且可验证，文档要能回答「盐存在哪」 | 按实现修正表述：盐编码在 `password_hash` 串内（`pbkdf2$迭代$盐$哈希`），登录 `PasswordHasher.Verify` 从串内解析盐与迭代次数；`DATA_SPEC` 2.4 / 2.5 的 `PasswordHash` 字段说明同步补上 | `tests/Collaboration.Domain.Tests` 含「同一密码两次哈希不同」用例（392/392）；API 回归含「密码不明文且随机盐」 |
| D-37 | AgileConfig 管理台**登录凭据与初始化方式没写进任何文档**（用户问「你是怎么登录的，配置文件里没看到账号密码」） | 换机器 / 重装后要能自己登进配置中心 | README 补「AgileConfig 管理台」小节：地址 `http://localhost:5000/ui`、账号密码读 `deploy/.env`（不入库，明文不进被跟踪文件——既有安全约定）、首次初始化走 `POST /admin/InitPassword`、忘记密码时清空 `agc_user` 重启重新初始化 | 实测 `POST /admin/jwt/login`（admin + `deploy/.env` 中的密码）返回 token；`/admin/InitPassword` 路由存在（GET 405） |
| D-38 | 虚拟发货「**发货内容必填**」只存在于前端弹窗（FulfillDialog 注释写着「会展示给客户」），`BUSINESS.md` 只写了「不填任何物流信息」，没有这一条 | 完整可用版：虚拟商品交付必须有内容物 | `BUSINESS.md` 6.1 / 7.1 / 7.2 三处补「虚拟发货必填发货内容（卡号 / 激活码 / 网盘链接，展示给客户）」 | 文档同步；行为验证见 D-40 |
| D-39 | 核销口径两份文档不一致：`AI_HANDOFF.md` 4.3 写「手输 + 扫码两种都支持」，`BUSINESS.md` 6.3 写「344 字符无法手输，支持粘贴 / 扫码枪」 | 用户答「q9 确认都要」（手输与扫码都要） | 统一为可实现口径：核销页提供**输入框 + 扫码枪**（可键盘粘贴，344 字符密文不现实逐字手输）；后台订单列表 / 详情、独立核销页三条入口都可用 | `PickupVerifyView` 输入框；`admin-deep` 核销用例；`API-ORD-082`（乱码码被拒且不泄露订单是否存在） |
| D-40 | **虚拟发货的发货内容被后端静默丢弃**：弹窗要求必填「卡号 / 激活码（会展示给客户）」并提交 `remark`，但 `DeliverVirtualHandler` 只用 `TryTransitStatusAsync` 改状态，`remark` 既不落库也不返回——顾客永远看不到卡号；快递发货备注同样被丢弃；`DeliverVirtualCommand` 的 XML 注释还停留在「发货即完成 20 → 50」（实现早已是 20 → 30） | 「完整可用版」：虚拟商品交付物必须真正到达客户 | 订单表新增 `ship_remark`（SQL 幂等补列）；`Order.ShipRemark` 实体字段；`TryShipAsync` 加备注参数 + 新增 `TryDeliverAsync`（状态与内容同一条 UPDATE，杜绝「已发货但内容为空」）；三个发货 Handler 全部写入；后端补 `DeliverVirtualValidator`（发货内容必填，挡绕过前端的调用）；`OrderDetailDto` 返回 `shipRemark`，后台详情与小程序虚拟订单详情展示；`DeliverVirtualCommand` 注释修正为 20 → 30 | `API-ORD-090`（发货内容回读 = 卡号 ABCD-1234）、新增 `API-ORD-090b`（空 remark 400 + errors.Remark 提示 + 状态仍 20）；`order-regression` 89/89、`payment-regression` 28/28、单元测试 392/392 |
| D-41 | **营销活动列表的「优惠」列永远空白**：列配置绑 `discountValue`，而列表 DTO 只有 `discountAmount` / `discountRate` / `giftQuantity`，后端根本没有这个字段——满折、满赠活动那一列也永远显示不出东西 | 「界面不出现空白骗人列」；优惠要按活动类型可读展示 | 后端 `PromotionActivityDto` 新增 `DiscountValue`（`PromotionNames.DiscountValue`：满减 = 金额两位小数、满折 = 「N 折」、满赠 = 「赠 N 张」）；前端列去掉 `format: 'amount'`（现在是文本） | API 实测：满减 `10.00`、满折 `8.5 折` 正确下发；`admin-deep --only 营销活动` 11/11 |
| D-42 | **营销活动「停用 / 启用」没有确认框**：`danger: true` 但没配 `confirm`，点一下立即生效（与券活动、客户启停等全站口径不一致） | 危险动作要有确认框与影响面 | `list-configs` 给两个动作补 `confirm`（含活动名与影响说明） | `admin-deep list-/promotions-action-停用 / 启用`（确认框打开并取消） |
| D-43 | **UI 回归的券活动「编辑」页 404**：`non-menu-pages.mjs` 的 `idOf` 把 `templateId` 排在 `activityId` 前面，券活动行 `{ activityId, templateId }` 被取成模板 Id 拼进 `/coupons/activities/edit/{id}` | 非菜单页巡检要真的打开对应记录 | `idOf` 把 `activityId` 提前（券模板行没有 activityId，不受影响） | `admin-ui-regression` 非菜单页 27/27（原先 26/27） |
| D-44 | **深测 create-flow 的三条「新建后列表可见」用例失败**，原因有四层：① 列表搜索框只在**回车 / 清空**时触发，脚本只 `fill` 不发请求；② 列表按 `sortOrder asc, id desc` 排序，而脚本自己把「排序」填成 5 / 6，新行不在第一页；③ 营销活动 verify 按 `a.activityId` 找行，而该 DTO 的 Id 字段叫 `id`；④ 颜色面板选择器只认旧版 `.el-color-dropdown`，Element Plus 新版是 `.el-color-picker__panel` | 「每个页面每个组件都要测到」，测试失败要指向真因 | ① `fill` 后补 `press('Enter')`；③ verify 改 `a.id ?? a.activityId` 并保留失败诊断输出；④ `fillColor` 兼容新旧两个面板类名 | `admin-deep --only 新建` 50/50（platform / couponTemplate / couponActivity / promotion 全过）；全量深测 **255/255** |
| D-45 | 工程基线「启动脚本用 PowerShell」只覆盖 Windows；目标设备换成 **Linux Mint（16G / 实际可用 13.5G）**后没有可用启动入口，且默认中间件参数（ES 1g 堆实测 1.9G、Kibana 自动堆 675M、16 个 .NET 服务 Server GC 各 ~140M）在低内存设备上启动压力大 | 换设备后要**起得来**，且尽量低内存 | 新增 `deploy/linux/*.sh`（bash，Linux 原生零依赖：common / start-infra / **init-db** / build / start-services / start-web / start-all / stop-all / status）+ `deploy/docker-compose.linux.yml` 覆盖层（ES 512m 堆、Kibana 384m 堆、MQ 内存水位 0.25、Redis 384M+noeviction、逐容器 mem_limit）；.NET 走工作站 GC（`DOTNET_gcServer=0`）+ 串行启动；三档 minimal(2.4G)/standard(2.6G)/full(4.2G)；首次初始化支持「运行时数据压缩包」（`scripts/pack-runtime-data.ps1` 打包数据卷/凭据/私钥/上传件 + 根目录 `RESTORE.md`，新机解压即用）与「`init-db.sh` + 3 个 pwsh 种子脚本（权限/角色/配置中心，一次性）」两条路径；**Windows 仍走 `scripts/*.ps1` 不变** | `bash -n` 9/9 通过（含 init-db）；`load_services` 的 node 主路径与 awk 兜底（含 CRLF 兼容）容器内实测输出 17 个服务；`docker compose config` 合并正确（ES_JAVA_OPTS 512m、NODE_OPTIONS 384m、mem_limit 全部生效、redis command 完整）；`up --dry-run` 通过；压缩包实测 96.4MB / 6923 条目 / RESTORE.md 在根 / PG 数据 + 私钥 + 证书齐全 |

## 2. 工程基线

| 需求 | 实现位置 | 状态 |
|---|---|---|
| `.NET 10` | 全部 `*.csproj` 的 `TargetFramework` | 已验证 |
| 启动脚本用 PowerShell（Windows）/ bash（Linux） | `scripts/*.ps1` + `deploy/linux/*.sh`（Linux 原生零依赖，见 D-45） | 已验证 |
| 建表用 SQL 初始化脚本 | `deploy/sql/**` + `scripts/init-tables.ps1` | 已验证 |
| 每个接口 / 方法 / 字段 100% XML 注释 | `WarningsAsErrors=CS1591` + 构建 0 警告 | 已验证 |
| 单元测试要 | `tests/Collaboration.Domain.Tests`，384/384 | 已验证 |
| 先部署 AgileConfig，再读数据库 / Redis 配置 | `ServiceBootstrap.LoadConfigurationAsync` + 各 `Program.cs` | 已验证 |
| 后台 / 小程序密码加盐 | `Collaboration.PasswordHasher`：PBKDF2-SHA256（21 万次迭代）+ 每用户 16 字节随机盐；**盐编码在 `password_hash` 串内**（`pbkdf2$迭代$盐Base64$哈希Base64`），无独立 salt 列；校验时从串内解析盐 | 已验证（表述已修正，见 D-36） |
| FreeSql 插入默认雪花 Id + 通用查询条件 | `CrudRepository` + `FilterRegistrar` + `SnowflakeId` | 已验证 |
| 每个微服务独立文件夹 + 总 `.slnx` + 每服务独立 `.sln` | `src/<Service>/...` + `SimpleShop.slnx` + `src/<Service>/<Service>.sln`（16 个微服务 + Gateway + Collaboration，共 18 个；含递归引用的 Collaboration 工程） | 已修正（D-35） |

## 3. 后台导航与交互

| 需求 | 实现位置 | 状态 |
|---|---|---|
| 侧边栏只放业务，不放「新建 X」入口 | `AdminLayout` + `modules.ts` 的 `hiddenInMenu` | 已验证 |
| 新建 / 编辑点开**新页**，不用弹窗凑合 | `FormView` / `ProductFormView` 路由 | 已验证（流程测试） |
| 列表每行有显式操作按钮，不能只靠点整行 | `ListView` 操作列 + `OrderListView` 行操作 | 已验证（深测逐行动作） |
| 支付 / 库存不单独占菜单 | 订单列表 / 详情 + 商品列表「库存」 | 已修正（D-1） |
| 危险动作确认框显示具体单号 / 影响面 | `ListView` confirm / `OrderListView` `ElMessageBox` | 已验证 |
| 拒绝 / 驳回 / 隐藏 / 退款拒绝必须填原因 | 各 confirm 配置 + 后端 Validator | 已验证 |
| 后台苹果风、简洁高级 | `styles.css` token + `element-theme.css` | 已验证（UI 截图） |
| 字体不能依赖本机，所有电脑一致 | `styles.css` 自托管 `SimpleShop Sans` | 已验证 |
| 界面不出现裸 Id / 枚举数字 / JSON 原文 / null | `format.js` + UI-RAW 检查 | 已验证 |

## 4. 后台业务与页面

| 需求 | 实现位置 | 状态 |
|---|---|---|
| 平台由超级管理员添加，平台可编辑自己信息 | `platform:create` 超管守卫 + 平台列表 / 表单 | 已验证 |
| 添加平台 / 商户的字段完整 | `platform` / `merchant` 表单配置 + 后端 Validator | 已验证 |
| 角色权限树中文显示、增删改、全部权限按钮 | `RolePermissionsView` / `TreeView` + 后端权限点 CRUD | 已验证 |
| 权限树只落库叶子权限点，容器节点不可勾 | `RolePermissionsView`（`selectable` 禁用 + 只提交叶子）+ `BindRolePermissionsHandler` 校验 | 已修正（D-8） |
| 券活动启停只改状态、不动适用范围 | `coupon-activities/SetStatus` + 列表「启用 / 停用」 | 已修正（D-6） |
| 禁止编辑内置管理员角色 | 后端 `IsBuiltin` 守卫 + 前端按钮禁用 | 已验证 |
| 商户审核通过才展示给小程序 | `Merchant` 公开可见性 + `Shop` 接口 | 已验证 |
| 商品列表含库存调整入口 | 商品列表「库存」→ SKU 调整页 | 已验证（D-1） |
| 订单列表含模拟支付成功 / 失败 | `OrderListView` 行按钮 + `/admin/orders/SimulatePayment` | 已验证（真实待支付单 + 失败 + 取消清理） |
| 订单详情含金额构成 / 支付记录 / 退款记录 | `OrderDetailView` + `SectionPanel` + `RefundDialog` | 已验证 |
| 装修草稿 + 发布；平台 / 商户装修独立 | `DesignBuilderView` + `design-regression` | 已验证 |
| 装修搭建器可改组件内容（含商户轮播图） | 组件库 props schema + `DesignBuilderView` 右侧属性面板 | 已修正（D-13） |
| 装修存草稿按页增量提交、不覆盖另一页 | `DesignDraftMerger` + `SavePlatformDraft` / `SaveMerchantDraft` | 已修正（D-12） |
| 商户装修只能调顺序 / 内容，不能改配色 | 后端剔除颜色字段 + 前端商户组件过滤 | 已验证 |
| 报表四张 + 时间区间最高一年 + 营销下钻 | `ReportView` + `report-regression`；下钻进 `/coupons/activity-records` | 已修正（D-11） |

## 5. 小程序

| 需求 | 实现位置 | 状态 |
|---|---|---|
| 固定 `platformCode`，应用内不切换 | `session-storage.ts` + 平台装修读取 | 已验证 |
| 游客只能浏览，不能加购 / 下单 / 收藏 / 评价 / 领券 / 进积分中心 | 小程序各页 `requireLogin` + 后端鉴权 | 已验证 |
| 结算页默认最优券（并列临期）、只能一张、可改选 | `checkout/index.vue` + 营销结算接口 | 已验证 |
| 地址簿 / 收藏 / 发表评价 / 追评 / 退款 | `pages/address` / `favorite` / `evaluate` / `refund` | 已验证（小程序 UI 22/22） |
| 秒杀频道可抢购 | `pages/seckill/index.vue` → `/marketing/seckill/grab` | 已验证（UI + 后端 grab 用例） |
| 金额只显示数字两位小数，不加货币符号 / 千分位 | `core/format.ts` + 页面统一走 `amount()` | 已验证 |
| 雪花 Id 禁止 `Number()` | 共享校验 / 结算页最优券 Id 修正 | 已修正（D-3） |

## 6. 业务规则

| 需求 | 实现位置 | 状态 |
|---|---|---|
| 金额全部两位小数、四舍五入 | 后端 `Math.Round(..., 2, AwayFromZero)` + 前端 `formatAmount` | 已验证 |
| 积分抵扣上限 100%，包含每日签到，365 天过期 | PointService + `point-regression` | 已验证 |
| 评价 SPU 级、标记本单全部 SKU、带图、追评、商户回复、每日更新均分 | EvaluateService + `evaluate-regression` | 已验证 |
| 虚拟 / 快递 / 自提；虚拟手动发货；虚拟签收后不可退 | OrderService + `order-regression` / `payment-regression` | 已验证 |
| 取货码 = 服务端 RSA(订单号)，公私钥都在服务器 | OrderService PickupCode | 已验证 |
| 活动新增秒杀；划出常规库存；手动结束回补；预留多场次 | MarketingService + `marketing-regression` | 已验证 |
| ES 使用 IK 分词 | ProductService + ES 自建镜像 + `API-SRC-*` | 已验证 |
| 前端提交时验证，失焦不验证；失败飘红 + 下方原因 | `FormView` / 小程序表单 + Validator | 已验证 |
| 后端错误统一 tip，不飘红输入框 | `request.js` / `core/http.ts` | 已验证 |

## 7. 明确暂缓 / 不做的项

| 项 | 依据 | 说明 |
|---|---|---|
| 秒杀场次 PV | `BUSINESS.md` §17 | 需要前端埋点 + LogService 聚合；当前报表不返回假数字 |
| 真实支付通道 | `BUSINESS.md` §22 | 只做模拟支付 |
| 券活动的删除接口 | `DATA_SPEC.md` 5.13 | **设计如此**：领过的券要能查到来源，所以活动只停用不删除。副作用是回归脚本建的活动会在开发库里累积（列表有分页，不影响使用）；e2e 收尾统一用 `SetStatus` 停用，不要再写不存在的 `Delete` |
| 运费模板 / 部分发货 / 多场次秒杀 / 评价即时重算 / 补签 / 退款退券 / 积分兑换 | `BUSINESS.md` §22 | 明确非目标 |

## 8. 防漂移规则

1. 新需求先写进本文「待确认 / 待实现」，再改 `BUSINESS.md` / `DATA_SPEC.md` / `FRONTEND_DESIGN.md`，最后写代码。
2. 改导航 / 页面入口时，必须同步检查本文第 3 节和 `FRONTEND_DESIGN.md` 第 4 节。
3. 新增 `Options` 接口时，必须明确 `platformId = 0` 的语义，并补一条 e2e。
4. 前端出现任何 `Number(xxxId)` 都视为缺陷；雪花 Id 只允许字符串比较与字符串传输。
5. 每次 UI 回归直接跑 `pwsh tests/ui/run-all.ps1`（依次跑静态核对 → 后台页面巡检 → 后台深测 → 小程序巡检）；
   也可以单独跑：
   - `node tests/ui/admin-ui-regression.mjs`
   - `node tests/ui/admin-deep-regression.mjs`
   - `node tests/ui/user-ui-regression.mjs`
6. 改任何列表 / 表单配置里的**跳转路径**（`createRoute` / `rowRoute` / `editRoute` / `listRoute` / `route` / `linkTo`）后，必须跑
   `node tests/ui/check-route-links.mjs`：它把配置里的路径与 `modules.ts` 注册的路由逐条比对。
   写错的链接不会报错，只会被 catch-all 送去工作台（物流公司就栽过）。
7. 加页面 / 加控件后，先跑 `node tests/ui/admin-coverage-audit.mjs` 生成控件清单
   （`tests/ui/admin-coverage.json`），再照清单补 `admin-deep-regression.mjs` 的用例 ——
   不要凭记忆判断「哪些控件测过了」。
8. 深测用例必须**能重复跑**：自己造的数据自己收尾（账号只能停用就停用、券活动只能停用就停用），
   手机号 / 编码 / 名称一律带随机后缀；写死唯一字段（如手机号）会让第二轮开始必然失败。
