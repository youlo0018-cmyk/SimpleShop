# PLAN.md — 实现规划

> 本规划**严格派生自** `BUSINESS.md`（业务）、`CODING_STANDARD.md`（规范）、`DATA_SPEC.md`（数据与表单）、`DESIGN_SPEC.md`（设计）、`REVIEW.md`（链路与风险）、`TEST_CASES.md`（用例）。
> **不引入这六份文档之外的任何设计决定。** 若实现中发现某处需要新决策，**先停下来提问**，不得自行发挥。
> 协作流程见 `AI_HANDOFF.md` 第 3 节第 0 条：先问后写，需求未确认不写代码。

## 目录

0. 规划原则
1. 阶段总览与依赖
2. S0 基础设施
3. S1 认证与租户地基
4. S2 商品与库存
5. S3 营销引擎
6. S4 交易闭环
7. S5 平台商户与装修
8. S6 积分与评价
9. S7 限时抢购
10. S8 后台前端
11. S9 小程序前端
12. S10 测试与收尾
13. 全局约束
14. 进度跟踪

---

## 0. 规划原则

| # | 原则 | 落地方式 |
|---|---|---|
| 1 | **每阶段结束系统处于可运行状态** | 不允许出现「写了一半、跑不起来」的中间态交付 |
| 2 | **严格照文档实现** | 每个阶段先列「本阶段对应的文档章节」，实现时逐条对照 |
| 3 | **文档同步同批次** | 每阶段完成时更新 `BUSINESS` / `DATA_SPEC` / `REVIEW` / `AI_HANDOFF` 进度日志 |
| 4 | **100% XML 注释** | 每个接口、方法（含私有）、字段、枚举成员。缺注释视为未完成（`CODING_STANDARD` 5.0） |
| 5 | **SQL 脚本建表** | 不用 FreeSql CodeFirst。`deploy/sql/<service>/` 下幂等可重跑（`CODING_STANDARD` 陷阱 16） |
| 6 | **AOP 统一过滤** | 软删 / 租户 / 客户 / 公开可见性四类，禁止每个 Handler 手写（`DATA_SPEC` 3.2） |
| 7 | **金额两位四舍五入** | 应用层显式 `Math.Round(x, 2, AwayFromZero)`，不依赖数据库 cast（`BUSINESS` 8.4） |
| 8 | **Apple 风 + 无原始数据** | 前端照 `DESIGN_SPEC` token 与第 6 节清单（`DESIGN_SPEC` 1~8） |
| 9 | **P0 优先** | `TEST_CASES` 第 5 节的 P0 专项是验收硬门槛 |
| 10 | **不自行 commit** | 每阶段结束 commit 一次但**不 push**（用户已授权按阶段提交） |

---

## 1. 阶段总览与依赖

```text
S0 基础设施
  └─> S1 认证与租户地基（Gateway / Auth / User / Permission / Customer / Tool）
        └─> S2 商品与库存（Product / Cart / Inventory）
              └─> S3 营销引擎（Marketing）
                    └─> S4 交易闭环（Order / Payment / Scheduled）★ 首个可演示里程碑
                          ├─> S5 平台商户与装修
                          ├─> S6 积分与评价
                          └─> S7 限时抢购
                                └─> S8 后台前端
                                      └─> S9 小程序前端
                                            └─> S10 测试与收尾
```

**S4 是第一个可演示里程碑**：跑通「注册 → 浏览 → 加购 → 下单 → 支付 → 发货 → 签收 → 退款」全链路。

**S5 / S6 / S7 互不依赖**，可并行推进，但为避免同时改动 `MarketingService` 导致冲突，**按 S5 → S6 → S7 顺序做**。

| 阶段 | 交付内容 | 阶段数 | 关键验收 |
|---|---|---|---|
| S0 | 基础设施 | 14 项 | 容器全绿 + 14 库可连 + 解决方案编译通过 |
| S1 | 认证与租户地基 | 6 服务 | 后台登录 + 令牌验签 + 权限判定 + 客户注册 |
| S2 | 商品与库存 | 3 服务 | 商品上架 + ES 搜索 + 库存锁定 |
| S3 | 营销引擎 | 1 服务 | 结算试算金额与 `TEST_CASES` 1.1/1.2 一致 |
| S4 | 交易闭环 | 3 服务 | 全链路跑通 + P0-TRD 补偿回滚全绿 |
| S5 | 平台商户与装修 | 1 服务 | 装修草稿/发布 + 商户装修隔离 |
| S6 | 积分与评价 | 2 服务 | 冻结/实扣/回收 + 评价 SPU 粒度 |
| S7 | 限时抢购 | Marketing 扩展 | 并发不超卖（P0-SEC 全绿） |
| S8 | 后台前端 | 45 页面 | 苹果风 + 原始数据零出现 |
| S9 | 小程序前端 | 29 页面 | 同上 + 拖拽搭建器可用 |
| S10 | 测试与收尾 | 全层 | 516 条用例可跑，CI 通过 |

---

## 2. S0 基础设施

### 2.1 目标

把「能编译、能连库、能起服务」的地基打好，后续每个阶段都在这个地基上盖。

**对应文档**：`DATA_SPEC` 1（启动时序）、2（模型基类）、3（AOP 与仓储）；`BUSINESS` 2（工程基线）、3（服务清单）。

### 2.2 文件清单

**部署与数据**

| 路径 | 内容 |
|---|---|
| `deploy/docker-compose.yml` | 8 个中间件：postgres、redis、consul、rabbitmq、agileconfig、elasticsearch（自建）、kibana、fluentd。含健康检查与 `depends_on` |
| `deploy/elasticsearch/Dockerfile` | 基于 ES 官方镜像安装 IK 分词插件（`BUSINESS` 15.2） |
| `deploy/elasticsearch/elasticsearch.yml` | 单节点、`discovery.type=single-node`、IK 注册 |
| `deploy/agileconfig/init-service-configs.sql` | 17 个服务的初始配置（连接串、Redis、Consul、MQ、雪花 workerId） |
| `deploy/sql/00-create-databases.sql` | 建 14 个 `simpleshop*` 库（`BUSINESS` 3.3），幂等 |
| `deploy/sql/01-create-owners.sql` | 每库独立 owner 与授权 |
| `deploy/shared/validation-rules.json` | 校验规则单一来源（`CODING_STANDARD` 3.5），9 条规则 |

**解决方案与协作库**

| 路径 | 内容 |
|---|---|
| `SimpleShop.slnx` | **根解决方案**，登记全部工程。`dotnet sln add` 会按磁盘目录自动生成嵌套的解决方案文件夹 |
| `src/<服务名>/<服务名>.slnx` | **每个微服务一个独立解决方案**，只含自己的四个分层工程。日常开发打开这个即可 |
| `tests/<项目名>.slnx` | 测试项目独立方案，便于单跑测试 |
| `src/Collaboration/Collaboration.Domain/Collaboration.Domain.csproj` | net10.0 类库 |
| `.../Common/ApiResponse.cs` | `ApiResponse { Success, Code, Message, Data, Errors }` |
| `.../Common/ApiResults.cs` | `ApiResults.Ok(data)` / `ApiResults.Fail(code, msg, errors)` |
| `.../Common/BaseApiResponseCode.cs` | 公共错误码枚举 |
| `.../Entities/EntityBase.cs` | `Id` / `CreatedAt` / `UpdatedAt` / `IsDeleted` / `DeletedAt`（`DATA_SPEC` 2.2） |
| `.../Entities/AdminEntityBase.cs` | 加创建人 + 最后操作人 + `PlatformId` / `MerchantId`（`DATA_SPEC` 2.2） |
| `.../Entities/CustomerEntityBase.cs` | 加 `CustomerId` / `CustomerName`（`DATA_SPEC` 2.3） |
| `.../Entities/IPublicVisible.cs` | 公开可见性标记接口 + `BuildCondition(now)`（`DATA_SPEC` 3.2.1） |
| `.../Repository/ICrudRepository.cs` | 通用仓储接口（`DATA_SPEC` 3.5）。命名避开 FreeSql 自带的同名 `BaseRepository` |
| `.../Repository/CrudRepository.cs` | FreeSql 实现。AOP 单独在 `FreeSqlAopRegistrar` 注册 |
| `.../Infrastructure/RedisDistributedLock.cs` | 分布式锁，键名规则见 `BUSINESS` 20.2 |
| `.../Infrastructure/MessageEnvelope.cs` | MQ 消息信封 + 幂等键 |
| `.../Infrastructure/LoggingEventPublisher.cs` | pv / operation / exception 事件发布 |
| `.../Context/TenantContext.cs` | 读 `X-Claim-*`，判 `IsPlatform` / `IsMerchant` / `IsCustomer` / `IsAnonymous` |
| `.../Context/AccessContext.cs` | 访问上下文，供 AOP 判断是否注入可见性过滤 |
| `.../Enums/*.cs` | 跨服务共享枚举：订单状态、配送方式、审核状态、活动类型、券类型等 |
| `.../Validation/ValidationPatterns.cs` | 由 `validation-rules.json` 生成的常量（构建时） |

**脚本**

| 路径 | 内容 |
|---|---|
| `scripts/start-infra.ps1` | 起 8 个容器，等健康检查通过 |
| `scripts/stop-infra.ps1` | 停容器 |
| `scripts/init-database.ps1` | 执行 `deploy/sql/00`、`01` |
| `scripts/build.ps1` | `dotnet build SimpleShop.slnx` |
| `scripts/generate-validation-rules.ps1` | 由 JSON 生成两端常量 |

### 2.3 验收命令

```powershell
./scripts/start-infra.ps1          # 8 个容器全部 healthy
./scripts/init-database.ps1        # 14 个库可 psql 连上
./scripts/generate-validation-rules.ps1
./scripts/build.ps1                # 0 error 0 warning
```

### 2.4 退出标准

- 8 个容器状态 `healthy`，含自建 ES-IK 容器能 `GET /_analyze` 验证 IK 分词生效
- `SELECT count(*) FROM pg_database WHERE datname LIKE 'simpleshop%'` = 14
- `dotnet build SimpleShop.slnx` 通过，且**无警告**
- `Collaboration.Domain` 编译产出，AOP 与配置校验有单元测试骨架
- `AI_HANDOFF` 进度日志记录本阶段

### 2.5 风险

| 风险 | 应对 | 实际结果（2026-10-02） |
|---|---|---|
| ES-IK 镜像构建失败 | 降级为 `smartcn` 分词器 | **已发生**。IK 的 7.x/8.x 只在 `get.infini.cloud` 分发，该域 SSL 被阻断；GitHub（infinilabs/analysis-ik）tag 只到 v1.10.6 / v5.0.0-rc1，无 8.x 产物。已改为内置 `smartcn`，并把 IK 安装做成 `INSTALL_IK` 构建参数，网络可达时开箱即用 |
| Docker 镜像拉取慢 | 先起快的，ES 最后 | 正常。postgres/redis/consul/rabbitmq/fluentd/kibana 均拉取成功 |
| AgileConfig 镜像不可用 | 配置先用本地 `appsettings`，注册后切回（**但不得写死默认值**，`DATA_SPEC` 1.1） | **已发生**。`registry.agileconfig.com` 与 `agileconfig.com` 均 DNS 解析失败，Docker Hub 无 `agileconfig` 命名空间。已放入 `config` profile 默认不启动，**这一项需要重新决策，见下方待决** |
| NuGet 私有源证书过期 | 加项目级 `NuGet.config` 隔离 | **已发生**。机器全局源 `nuget.companycn.net` 证书 NotTimeValid、私有阿里云源 401，只有 nuget.org 可用。已加 `NuGet.config` 只保留官方源 |

### 2.6 待决（阻塞 S1）

| # | 问题 |
|---|---|
| 1 | **AgileConfig 拿不到**。文档要求它作为唯一配置源且 fail-fast（`DATA_SPEC` 1.1）。在网络可达前，S1 的服务需要一个本地配置源。选项：(a) S1 起用 `appsettings.json` 作为配置源，AgileConfig 就绪后切换；(b) 自建 AgileConfig（需 clone 源码联网构建）；(c) 等网络恢复。**这一项必须先定，否则 S1 无法开工。** |

### 2.7 落地记录

| 项 | 结论 |
|---|---|
| 依赖版本 | FreeSql **3.5.311**（三件套同版本）；Npgsql 显式钉 **5.0.18**（FreeSql 内置 5.0.11 有高危公告，5.0.18 是 5.x 线末版且已修复；升 6.x+ 需验证 provider 兼容性）；Yitter 雪花包名是 **`Yitter.IdGenerator`**（`Yitter.NetCore` 在 nuget 上不存在）；MessagePack 3.1.10 |
| FreeSql AOP API | 3.5.x **移除了** `Aop.DataMapping` / `Aop.DataFilter`，且 `Aop.CurdBefore` 实测在本项目调用链上**不触发**。**最终方案不依赖任何 AOP 钩子**：查询过滤用 `IFreeSql.GlobalFilter.ApplyIf`（AND 进查询），审计字段显式写在 `CrudRepository`。详见 DATA_SPEC 3.2.1 |
| 条件内联 | `ParseExpression` 只有字符串通道、没有参数通道，因此过滤条件必须内联为字面量。由 `SqlLiteral` 做类型白名单（只接受令牌声明、枚举常量、服务端时钟的值），杜绝注入 |
| 建库脚本 | PostgreSQL **不允许在函数/DO 块里 `CREATE DATABASE`**，改用 psql 的 `\gexec` 做幂等批量执行；脚本必须用 psql 跑 |
| fluentd | 官方镜像不含 `fluent-plugin-elasticsearch`（会报 Unknown output plugin 并退出），已自建镜像补装；镜像内无 `ps`，健康检查改用镜像自带 ruby 做 TCP 探测 |


---

## 3. S1 认证与租户地基

**对应文档**：`BUSINESS` 4（账号域分离）、5（角色权限）；`DATA_SPEC` 2（基类）、3.2（AOP）；`REVIEW` 链路 0/1/2/3/15。

| 服务 | 端口 | 要点 |
|---|---|---|
| Gateway | 5008 | Ocelot 路由 + 双令牌验签 + RBAC + 租户声明注入 |
| Auth | 5019 / 5004 | OpenIddict password flow、公开客户端 `admin-app`、RS256 |
| User | 5011 / 5003 | 后台账号，不含客户 |
| Permission | 5022 / 5023 | 角色 / 权限点 / 绑定；权限点可增删改，仅超管 |
| Customer | 5280 / 5001 | 客户注册 / 登录（HS256）、资料、地址簿、收藏 |
| Tool | 5080 / 5081 | 统一上传，多存储，扩展名→大小→魔数三段校验 |

每服务固定四层，**以 `CustomerService` 为基准模板**。

**文件清单**：每服务 4 个 `.csproj` + `Api/Controllers` + `Application/Features` + `Domain/Entities|IRepository|Enums` + `Infrastructure/Repository|Program.cs`；`deploy/sql/{Auth,User,Permission,Customer,Tool}/`。

**验收步骤**

1. `./scripts/build.ps1` 编译通过
2. 后台登录 → 返回 RS256 令牌
3. 客户注册 → 赠送 100 积分（同步 gRPC；积分服务在 S6，此处先打通契约）
4. 客户账号走后台登录 → `invalid_grant`
5. 无绑定角色登录 → 后台接口 403
6. 权限点增删改 → 非超管 403

**退出标准**：`API-AUT-*`、`API-GW-*`、`API-RBAC-001~010` 全绿；`P0-ACL-*` 矩阵中本阶段相关行通过。

---

## 4. S2 商品与库存

**对应文档**：`BUSINESS` 15（商品与搜索）、9（库存）、8.3（购物车）；`DATA_SPEC` 5.4~5.10；`REVIEW` 链路 6/6.5/8。

| 服务 | 端口 | 要点 |
|---|---|---|
| Product | 5058 | SPU/SKU、三级分类、审核上下架、ES + IK 搜索（降级 LIKE） |
| Cart | 5060 | 累加语义、上限 99、图片快照 |
| Inventory | 5062 / 5063 | 锁定/扣减/释放/回补、流水幂等、补偿表、孤儿预留对账 |

**文件清单**：三服务四层；`ProductService.Application/Services/SearchIndexer.cs`（消费 `product.changed`）；`deploy/sql/{Product,Cart,Inventory}/`。

**验收步骤**

1. 商品上架 → ES 可搜到
2. 停 ES → 搜索降级仍返回结果并标记降级
3. 并发下单不超卖
4. 重复请求流水不增加

**退出标准**：`API-PRD-*`、`API-CART-*`、`API-INV-*`、`UT-INV-*` 全绿。

---

## 5. S3 营销引擎

**对应文档**：`BUSINESS` 11（优惠引擎）、17（报表）；`DATA_SPEC` 5.11~5.14；`TEST_CASES` 1.1、1.2。

单一服务 `MarketingService`（5072 / 5073），内含：

| 组件 | 职责 |
|---|---|
| `DiscountEngine` | 逐行贪心、活动/券互斥、平台优先级、满赠兜底、0 元减、单行封底 |
| `MarketingSnapshotCache` | 平台快照缓存，写操作必须 `Invalidate(platformId)` |
| `SettleHandler` | 下单占券（`SettleAsync`） |
| `CommitHandler` | 支付后核销券 + 满赠发券 |
| `CancelHandler` | 消费 `order.cancelled` 回退券 |
| `FinalPriceHandler` | 到手价试算（游客不计券，批量 ≤ 50） |
| 报表 | 活动 / 券效果 + 下钻 |

**文件清单**：四层 + `Application/Services/DiscountEngine.cs` + `Application/Services/MarketingSnapshotCache.cs`；`deploy/sql/Marketing/`。

**验收**：**直接跑 `UT-AMT-*`（22 条）与 `UT-DSC-*`（22 条）单元测试**。这些是纯算法、不依赖数据库，是本阶段的主要验收手段。

**退出标准**：`UT-AMT-001~022`、`UT-DSC-001~022`、`UT-CPN-*`、`UT-TIM-*` 全绿。

---

## 6. S4 交易闭环

**对应文档**：`BUSINESS` 7（状态机）、8（主链路）、10（支付退款）；`DATA_SPEC` 5.23~5.28；`REVIEW` 链路 7~13；`TEST_CASES` 5.1/5.2/5.5。

| 服务 | 端口 | 要点 |
|---|---|---|
| Order | 5064 / 5002 | 下单四步补偿（占券→锁积分→锁库存→落单，逆序回滚）、状态机、发货/备货/签收/取货 |
| Payment | 5066 | 金额服务端反查、模拟支付、后台 Simulate 按钮、退款审批 |
| Scheduled | 无端口 | 关单 30s、补偿重试、孤儿对账 10min、积分过期 02:00、评价重算 03:00、秒杀结算 1min |

**文件清单**：四层（Scheduled 单项目）；`OrderService.Application/Features/CreateOrder/CreateOrderHandler.cs`（**补偿链路的样板**）；`deploy/sql/{Order,Payment}/`。

**验收步骤**

1. `dotnet test` 跑 `UT-AMT` / `UT-DSC` / `UT-ORD`
2. `./tests/e2e/p0-special.ps1` 补偿回滚 + 事务 + 支付金额 + 金额对账
3. `./tests/e2e/full-chain.ps1` 注册→下单→支付→发货→签收→退款

**退出标准**：`P0-TRD-001~006`、`P0-TXN-*`、`P0-PAY-*`、`P0-AMT-*`、`UT-ORD-*` 全绿。**这是第一个可演示里程碑。**

---

## 7. S5 平台商户与装修

**对应文档**：`BUSINESS` 1.4（可见性）、6（配送与运费）、16（装修）；`DATA_SPEC` 5.1~5.3、5.29~5.31；`REVIEW` 链路 15。

`MerchantPlatformService`（5070 / 5070）：

| 能力 | 要点 |
|---|---|
| 平台 / 商户 | 商户审核、**被拒或停用时批量下架商品并发 `product.changed`** |
| 地区地址 | `RegionsJson` 空 = 内置默认；≤2MB 三级校验 |
| 平台装修 | 首页 + 我的页拖拽搭建器、tabBar、三档主题色、草稿 + 发布 |
| 商户装修 | 店铺页搭建器、**组件库独立（11 个）**、**不可改任何配色**、与平台互不可见 |
| 装修校验 | 组件 `type` 必须在注册表；`manualProductIds` 必须已审核已上架 |

**文件清单**：四层；`Application/Services/DesignValidator.cs`（颜色剔除 + 商品可见性校验）；`Infrastructure/Services/SeoBuilders.cs`；`deploy/sql/MerchantPlatform/`。

**验收步骤**

1. 平台装修草稿不影响线上，发布后 version 递增
2. 商户装修传颜色字段 → 被剔除并记警告日志
3. 商户手动选下架商品 → 整单保存失败
4. 商户审核被拒 → 其商品全部下架且发索引事件
5. 商户访问平台装修 → 403

**退出标准**：`API-PLT-*`、`API-DSG-*`、`API-VIS-*` 全绿。

---

## 8. S6 积分与评价

**对应文档**：`BUSINESS` 13（积分）、14（评价）；`DATA_SPEC` 5.24、5.27；`TEST_CASES` 1.5、1.8、5.4。

| 服务 | 端口 | 要点 |
|---|---|---|
| Point | 5082 / 5083 | 冻结模型、签到 7 天轮、365 天 FIFO、余额上限 10 万、抵扣上限 100% |
| Evaluate | 5084 / 5084 | SPU 级 + SKU 标记、追评 3 条 30 天、匿名、双主体回复、聚合重算 |

**文件清单**：四层；`PointService.Application/Features/{Lock,Consume,Refund,Expire,SignIn}`；
`EvaluateService.Application/Features/Evaluate/{PublishEvaluateHandler,AppendEvaluateHandler,AdminHandlers}`；
`Features/Internal/RecomputeRatingsHandler`；`deploy/sql/{point,evaluate}/`。

**与规格的偏离（有意）**：规格 14.5 写「每日 03:00 全量重算」，实现改成
**每小时一次 + 首次执行延迟 30 分钟**。理由：规格只承诺「每日一更新」「最多延迟 24 小时」，
 而**定点跑的最大问题是错过那个点就整天不跑**（发布 / 重启 / 依赖抖动都可能错过）。
 重算是幂等的，一天跑 24 次与跑 1 次结果完全相同；每次只多花几毫秒。
 30 分钟延迟是为了和积分过期任务错开——两个都扫全表，同时跑会打满连接池。

**验收步骤**

1. `dotnet test` 跑全部单元用例（当前 251 条）
2. `./tests/e2e/p0-special.ps1` 中的积分资损组全绿
3. 冻结 → 实扣 → 退款后余额回到冻结前
4. 同订单同 SPU 重复评价被拒

**退出标准**：`dotnet test` 全绿 + `./tests/e2e/run-all.ps1` 全绿（当前 318 条 / 10 个脚本）。

---

## 9. S7 限时抢购

**对应文档**：`BUSINESS` 12（限时抢购）；`DATA_SPEC` 5.15~5.17；`TEST_CASES` 1.7、5.3。

**在 `MarketingService` 内扩展**，不新增服务。

| 能力 | 要点 |
|---|---|
| 场次 | `seckill_session`，**模型与接口按 `SessionId` 寻址**（本期单场次，预留多场次） |
| 秒杀商品 | `seckill_item`，**必须指定 SKU** |
| 库存划出 | 创建时从常规库存划出；结束/中止**立即回补** |
| 抢购 | Redis 原子预扣 → 限购唯一索引 → 落单记账；接口保留 requestId + 轮询形状，**同步下单**（接 MQ 时只需换实现，前端不动） |
| 限购 | 每人每场次 1 件 |

**文件清单**：`MarketingService.Application/Features/Seckill/*`（含 `GrabHandler` / `GrabResultHandler`）；
`MarketingService.Application/Services/IOrderPort.cs`；`OrderService` 侧 `SeckillOrderHandler` + `POST /internal/orders/seckill-create`；
`deploy/sql/marketing/03-create-seckill-tables.sql`。

**验收步骤**

1. 库存 10、200 并发抢 → **恰好 10 个订单** ✅ 实测：成功 10 / 抢完 190 / 异常 0
2. 同一人 200 并发 → 恰好 1 个订单 ✅ 实测：成功 1 / 超限购 199 / 异常 0 / 订单号唯一
3. 场次中止 → 库存立即回补 ✅（`API-SKL-014`）
4. 重复结束 → 不重复回补 ✅（`API-SKL-015`）

1、2 由 **`tests/e2e/seckill-concurrency.ps1`** 单独跑（默认 200 并发，约 8 秒）。
日常回归 `marketing-regression.ps1` 只跑 30 / 10 并发（`API-SKL-030` / `API-SKL-031`），
避免 200 个 `Start-ThreadJob` 拖慢每次回归。

**退出标准**：`UT-SEC-*`、`P0-SEC-001~004` 全绿。





---

## 10. S8 后台前端

**对应文档**：`DESIGN_SPEC` 全文；`DATA_SPEC` 第 4 节（下拉与展示）、第 5 节（全部后台表单）；`TEST_CASES` 3.2~3.4、4.2。

`apps/admin-vue`（Vue 3 + Element Plus + Vite + Pinia + Tailwind）。

| 子项 | 内容 |
|---|---|
| 设计体系 | `src/styles.css` 集中 token（`DESIGN_SPEC` 2）；**18 个 Element Plus 组件换肤**（4.3） |
| 格式化 | `src/utils/format.js` + `src/utils/dict.js`（`DESIGN_SPEC` 7.2） |
| 校验 | `src/utils/validators.js` 引用 `validation-rules.json` 生成物；**提交时全量验证 + 失焦不验证**（`CODING_STANDARD` 3.4） |
| 页面 | **45 个页面**，逐个对照 `DATA_SPEC` 5.1~5.28 |
| 拖拽搭建器 | 组件库面板 + 12 列栅格画布 + 属性面板 + iframe 手机预览 + postMessage |
| 通用组件 | 20 个，全部按 `VIS-CMP` 出状态基线 |

**文件清单**：`package.json`、`vite.config.ts`、`src/main.ts`、`src/router`、`src/styles.css`、`src/utils/*`、`src/api/*`、`src/components/*`、`src/views/*`。

**验收步骤**

1. `./scripts/build.ps1` 与前端 `npm run build` 通过
2. `./tests/e2e/ui-regression.js` 功能回归 73 条中后台部分全绿
3. `node ./tests/e2e/visual-regression.js` 后台 45 个截图点全绿
4. `UI-RAW-001~008` 断言全站无原始数据
5. `UI-TOKEN-001~004` 断言无硬编码颜色/间距/圆角/字号

**退出标准**：45 个页面 + 20 个组件基线图入库并通过对比。

---

## 11. S9 小程序前端

**对应文档**：`DESIGN_SPEC` 5.5、5.6、6；`BUSINESS` 16.4；`DATA_SPEC` 5.29/5.30；`TEST_CASES` 3.3、4.3。

`apps/user-uniapp`（UniApp，H5 + 微信小程序）。

| 子项 | 内容 |
|---|---|
| 设计体系 | `src/App.vue` 全局 token（`DESIGN_SPEC` 4.1） |
| 页面 | **29 个页面**（首页、商城三 Tab、分类、店铺、详情、购物车、结算、订单、券、积分、收藏、评价、我的、秒杀频道等） |
| 装修渲染 | 平台装修（首页/我的）与商户装修（店铺页）共用一套组件渲染器 |
| 平台锁定 | 固定 `platformCode`，应用内不切换 |
| 游客边界 | **只能浏览**：加购/领券/收藏/评价/积分中心均不可用（`BUSINESS` 1.2） |

**验收步骤**

1. `npm run build:h5` 与 `npm run build:mp-weixin` 均通过
2. `node ./tests/e2e/ui-regression.js` 小程序部分全绿
3. `node ./tests/e2e/visual-regression.js` 小程序 29 个页面点 × 2 视口全绿
4. 游客访问受限页 → 跳登录

**退出标准**：29 个页面 × 2 视口基线图入库并通过对比。

---

## 12. S10 测试与收尾

**对应文档**：`TEST_CASES` 全文；`REVIEW` 第二部分风险逐条验证。

| 子项 | 内容 |
|---|---|
| 单元测试 | 130 条（`UT-*`），`dotnet test` |
| API 回归 | 173 条（`API-*`），`api-regression.ps1` |
| UI 回归 | 73 条（`UI-*`），`ui-regression.js` |
| 视觉基线 | 100 个截图点位 / 约 180 张图，**入库随代码走** |
| P0 专项 | 约 28 条，`p0-special.ps1`，**CI 阻断合并** |
| 越权矩阵 | 12 条，严格区分 403 / 404 |
| 测试数据 | `scripts/seed-test-data.ps1` 提供 14 组边界数据 |
| CI | GitHub Actions：构建 → 单测 → API 回归 → 视觉对比 → P0 阻断 |

**退出标准**

1. `REVIEW.md` 的 5 条 P0 风险逐条标注「已验证」
2. 516 条用例全部可执行且通过
3. `AI_HANDOFF` 进度日志记录完成

---

## 13. 全局约束（每个阶段都要守）

| # | 约束 | 来源 |
|---|---|---|
| 1 | 分层引用：Domain→Collaboration；Infra/Application→Domain；Api→Application+Infra，**Api 不得引用 Domain** | `BUSINESS` 3.2 |
| 2 | 四类 AOP 统一注入，**禁止 Handler 手写软删/租户/客户/可见性条件** | `DATA_SPEC` 3.2 |
| 3 | 后台上下文**不注入**可见性过滤 | `DATA_SPEC` 3.2.1 |
| 4 | 金额应用层显式 `Math.Round(x, 2, AwayFromZero)`，**不依赖数据库 cast** | `BUSINESS` 8.4 |
| 5 | **商品总额 = Σ 行应付**，不反向计算 | `BUSINESS` 8.4 |
| 6 | 券/活动造成的行应付封底 0.01；**积分可打到 0.00** | `BUSINESS` 8.4 |
| 7 | 跨服务不用分布式事务，用**本地事务 + 逆序补偿** | `DATA_SPEC` 3.7 |
| 8 | 每个写入口与列表查询都要有 Validator | `CODING_STANDARD` 3.3 |
| 9 | 字段级更新**必须 `SetDto`**，禁用 `UpdateColumns(a => obj)` | `CODING_STANDARD` 陷阱 10 |
| 10 | 上传统一走 ToolService，**业务服务禁止自建上传接口** | `CODING_STANDARD` 陷阱 13 |
| 11 | 权限**只认显式绑定**，无绑定即无权限（fail-closed） | `BUSINESS` 5.3 |
| 12 | 前端**禁止 `Number(id)`** 转换雪花 Id | `DESIGN_SPEC` 4.6 |
| 13 | 前端**失焦不验证，提交时验证**；后端错误用全局 tip | `CODING_STANDARD` 3.4 |
| 14 | 界面**不得出现原始数据**（Id/枚举/JSON/时间戳/null） | `DESIGN_SPEC` 6 |
| 15 | 颜色/间距/圆角/字号**只能引用 token**，无硬编码 | `DESIGN_SPEC` 2 |
| 16 | 每阶段结束 commit 一次，**不 push** | 用户授权 |

---

## 14. 进度跟踪

| 阶段 | 状态 | 开始 | 完成 | 备注 |
|---|---|---|---|---|
| S0 基础设施 | 已完成 | 2026-10-02 | 2026-10-02 | PostgreSQL / Redis / RabbitMQ / Consul / MinIO / **AgileConfig 自部署**（localhost:5000）。ES 分词器降级 smartcn，见 2.7 |
| S1 认证与租户地基 | 已完成 | 2026-10-02 | 2026-10-03 | Collaboration 类库、Gateway、Auth、User、Permission、Tool、Customer 全部落地 |
| S2 商品与库存 | 已完成 | 2026-10-03 | 2026-10-03 | Product（含前台只读 + 到手价）、Inventory、Point、Marketing（券 + 活动引擎）、Cart。缺 ES 搜索 |
| S3 交易闭环 | 进行中 | 2026-10-03 | | **OrderService + ScheduledService 已完成**（下单补偿链路 / 状态机 / 模拟支付 / 自提取货码 / 退款 / 完成发积分 / 支付超时关单）。缺 PaymentService |
| S4 平台商户与装修 | 进行中 | 2026-10-04 | | **平台 / 商户 / 审核 / 地区地址已完成**（MerchantPlatformService 5070，30 条回归）。审核拒绝会连带下架该商户商品并同步 ES 索引。**装修未做**；内置默认地区库只有省级，市 / 区县需运营导入 |
| S5 积分与评价 | 进行中 | 2026-10-04 | | **Point 已完成**（含过期扣减）；**Evaluate 已完成**（SPU 级 + SKU 标记 / 图片 / 追评 / 商户与平台回复 / 后台隐藏 / 每日重算均分）。评价分回写商品表并对外下发 |
| S6 限时抢购 | 进行中 | 2026-10-04 | | **场次 + 库存划出/回补（S-1）+ 抢购链路已落地**（三层防超卖：Redis 原子预扣 / 限购唯一索引 / 条件更新记账）。待接 RabbitMQ 把同步下单换成异步 |
| S7 后台前端 | 未开始 | | | 一行 UI 都没有 |
| S8 小程序前端 | 未开始 | | | 一行 UI 都没有 |
| S9 测试与收尾 | 进行中 | 2026-10-04 | | 单元 278 / 端到端 348 全绿（11 个脚本），`run-all.ps1` 汇总。缺 UI 与视觉回归 |

**更新规则**：每阶段结束时把该行改为「已完成」并填完成日期，同时在 `AI_HANDOFF.md` 进度日志追加条目（`AI_HANDOFF` 第 3 节第 1 条）。

