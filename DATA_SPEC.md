# DATA_SPEC.md — 数据模型与实现细则

> 用途：本文是**写代码时对照的落地规格**——启动时序、模型基类字段、FreeSql AOP 与仓储约定、前后台数据展示约定、后台表单字段清单。
> 业务规则来源见 `BUSINESS.md`（本文只做技术落地，不重复定义业务口径）。
> 分层与注释规范见 `CODING_STANDARD.md`，链路与风险见 `REVIEW.md`，协作流程见 `AI_HANDOFF.md` 第 3 节第 0 条。
> 字段表粒度：**字段名 / 类型 / 必填 / 默认值 / 校验规则 / 说明**。新建与编辑的差异写在「说明」列。
> 界面表现（控件样式、颜色、间距、原始数据禁显示规则）见 `DESIGN_SPEC.md`。

## 目录

1. 启动与配置加载时序
2. 模型基类与通用字段规范
3. FreeSql AOP 与仓储通用约定
4. 前后台数据展示与下拉约定
5. 后台表单字段规格

---

## 1. 启动与配置加载时序

### 1.1 铁律

| # | 规则 |
|---|---|
| 1 | **先读 AgileConfig，再连数据库 / Redis**。配置是连接的前置，不是连接的产物 |
| 2 | **任一硬依赖连不上 → fail-fast 直接退出**。不做降级启动、不做本地默认值兜底。宁可起不来，也不要带着残缺配置跑（串库事故的根因） |
| 3 | **每个服务一个独立 appId，appId = 服务名**；**每个服务一个独立数据库** |
| 4 | 连接串只出现在 AgileConfig 里，不写死在代码或 appsettings |
| 5 | 启动阶段**不依赖雪花 AOP 自动生成主键**（种子代码用「当前最大 Id + 1」） |

### 1.2 启动时序

| 阶段 | 动作 | 失败处理 |
|---|---|---|
| **S0 · 引导配置** | 读 `appsettings.json` / 环境变量，只取 AgileConfig 地址、appId、secret 三项，加日志配置 | 缺失 → **退出** |
| **S1 · 连接 AgileConfig** | 按 appId 拉取本服务配置；按指数退避重试（1s / 2s / 4s / 8s，上限 5 次） | 5 次仍失败 → **退出** |
| **S2 · 订阅变更** | 建立长轮询订阅（仅业务开关与限额类，见 1.3） | 连接断开自动重连；重连失败累计超限 → **退出** |
| **S3 · 校验配置完整性** | 校验必填键是否齐全（连接串、Redis、Consul、RabbitMQ 等） | 缺键 → **退出**并打印全部缺失键名 |
| **S4 · 分配雪花 workerId** | Redis 抢占空闲租约槽位（见 3.4） | 槽位全占或 Redis 不可用 → **退出** |
| **S5 · 初始化雪花 ID** | 用上一步的 workerId 初始化 Yitter | 失败 → **退出**；启动日志打印分配到的 workerId |
| **S6 · 注册 FreeSql** | 注入连接串、实体扫描、EntityBase 雪花 Id 钩子、**AOP（软删 + 租户，见第 3 节）** | 失败 → **退出** |
| **S7 · 注册 Redis** | 注入连接串 + db 索引 | 失败 → **退出**（锁与缓存是硬依赖） |
| **S8 · 注册 Consul** | 注册服务名 + 端口 + 健康检查 `/health` | 注册失败重试，不阻塞启动 |
| **S9 · 注册 MediatR** | `AddMediatRWithHandlers` + `ValidationBehavior` 管道 | 启动即暴露 |
| **S10 · 注册业务** | `AddInfrastructure()`（仓储）→ `AddApplication()`（Services、gRPC Client、消费者） | **必须在 `Build()` 之前** |
| **S11 · 声明 MQ 拓扑** | 声明交换机、队列、**死信交换机与 DLQ** | 失败 → **退出** |
| **S12 · 启动 Kestrel** | `Build().Run()`，`/health` 就绪 | — |

**顺序不可调整**：S6/S7 依赖 S3；S4/S5 依赖 S7 的 Redis；S10 依赖 S6/S7 已注册；S11 的消费者依赖 S10 的处理器。

### 1.3 热更新边界

| 类别 | 举例 | 热更新 |
|---|---|---|
| **连接类** | 数据库连接串、Redis 地址与 db、Consul、RabbitMQ、端口 | **否**——改完必须重启 |
| **业务开关** | `Payment:SimulateEnabled`、`FileStorage:Provider`、营销平台优先级 | **是** |
| **限额类** | `FileStorage:MaxSizeBytes`、`FileStorage:AllowedExtensions` | **是** |

| 项 | 做法 |
|---|---|
| 连接类实现 | S3 一次性快照成**不可变** Options（`IOptions<T>`） |
| 热更类实现 | 用 `IOptionsMonitor<T>`；变更时写日志，并触发必要的缓存失效（如营销快照 `Invalidate(platformId)`） |

### 1.4 AgileConfig 配置键

**所有服务共有**：

| 键 | 说明 |
|---|---|
| `ConnectionStrings:Default` | PostgreSQL 连接串（**本服务自己的库**） |
| `Redis:ConnectionString` / `Redis:Database` | Redis 连接与 db 索引（各服务**独占**一个库，避免同名 key 互相覆盖） |
| `Redis:SharedDatabase` | 跨服务共享的库号，默认 0。**只有必须被多个服务读到的键**才写在这里（当前唯一用途：后台账号的会话吊销，见 5.20）。写错库号的表现是静默失效 |
| `Consul:Address` | Consul 地址 |
| `RabbitMq:{Host,Port,UserName,Password,VirtualHost}` | MQ |
| `Jwt:{Issuer,Audience,Secret,ExpireHours}` | 客户 JWT（HS256） |

**按服务独有**：

| 键 | 归属 | 说明 |
|---|---|---|
| `OpenIddict:*` | AuthService | 客户端、RS256 证书配置 |
| 本地签名证书路径 | AuthService + Gateway | **两边必须读同一份证书文件** |
| `FileStorage:{Provider}:*`、`FileStorage:AllowedExtensions`、`FileStorage:MaxSizeBytes` | ToolService | 业务服务**只调上传接口，不读此配置** |
| `Marketing:SnapshotTtlSeconds` | MarketingService | 平台快照缓存 TTL |
| `Seckill:{RedisKeyPrefix,ReserveTimeoutSeconds}` | MarketingService | 秒杀 Redis key 前缀与预扣超时 |
| `Payment:SimulateEnabled` | PaymentService | 模拟支付总开关 |

### 1.5 健康检查

`/health` 逐项返回：配置已加载 / 数据库可连 / Redis 可连 / Consul 已注册。
**任一硬依赖不通返回非 200**，网关与 Consul 不把流量打进来。

---

## 2. 模型基类与通用字段规范

### 2.1 基类继承关系

| 基类 | 用途 | 额外字段 |
|---|---|---|
| `EntityBase` | **所有实体的根** | `Id`、`CreatedAt`、`UpdatedAt`、`IsDeleted`、`DeletedAt` |
| `AdminEntityBase : EntityBase` | **后台业务实体**（商品、订单、活动、商户、分类、券、秒杀…）——需记录谁操作的、何时操作 | 创建人 + 最后操作人 + 租户字段（见 2.2） |
| `CustomerEntityBase : EntityBase` | **前台业务实体**（地址、收藏、积分账户、客户档案）——需记录用户信息 | `CustomerId`、`CustomerName`（见 2.3） |

**`AdminEntityBase` 与 `CustomerEntityBase` 不同时继承**：一张表要么是后台业务数据（按租户隔离），要么是前台客户数据（按客户隔离）。

### 2.2 `AdminEntityBase` 字段

创建人**写入后永不修改**，操作人记录**最后一次**修改。

| 字段 | 类型 | 列 | 默认 | 说明 |
|---|---|---|---|---|
| `Id` | `long` | `bigint` | 雪花 | 主键，非自增 |
| `CreatedAt` | `DateTime` | `timestamp` | 当前 UTC | 创建时间，**此后不变** |
| `CreatedById` | `long` | `bigint` | 当前操作人 Id | **创建人，此后永不修改** |
| `CreatedByName` | `string` | `varchar(64)` | 当前操作人姓名 | **创建人姓名快照**，防止改名后历史记录跟着变 |
| `UpdatedAt` | `DateTime?` | `timestamp` | 当前 UTC | 最后更新时间 |
| `OperationId` | `long` | `bigint` | 当前操作人 Id | **最后操作人**，每次更新覆盖 |
| `OperationName` | `string` | `varchar(64)` | 当前操作人姓名 | 最后操作人姓名快照 |
| `IsDeleted` | `bool` | `boolean` | `false` | 软删标记 |
| `DeletedAt` | `DateTime?` | `timestamp` | — | 软删时间，UTC |
| `PlatformId` | `long` | `bigint` | — | 所属平台 Id。**0 = 平台自身 / 全局** |
| `MerchantId` | `long` | `bigint` | — | 所属商户 Id。**0 = 平台自身数据（非商户）** |

> **为什么创建人与操作人分开**：只保留一对「操作人」字段时，别人编辑一次就看不到「这个商品最初是谁建的」。分成两对才能同时回答「谁建的」和「谁最后改的」。
> **`MerchantId = 0` 的语义**：表示数据归平台管（平台级活动、平台分类），**不是**「没有商户」。

### 2.3 `CustomerEntityBase` 字段

| 字段 | 类型 | 列 | 默认 | 说明 |
|---|---|---|---|---|
| `Id` | `long` | `bigint` | 雪花 | 主键 |
| `CustomerId` | `long` | `bigint` | 当前客户 | 归属客户 Id。**前台实体的操作人就是 `CustomerId` 本身**，不另设 `OperationId` |
| `CustomerName` | `string` | `varchar(64)` | 当前客户昵称 | **用户名快照**，防止改名后历史记录变化 |
| `CreatedAt` | `DateTime` | `timestamp` | 当前 UTC | 创建时间 |
| `UpdatedAt` | `DateTime?` | `timestamp` | 当前 UTC | 最后更新时间（评价追评、积分变动需要） |
| `IsDeleted` | `bool` | `boolean` | `false` | 软删标记 |
| `DeletedAt` | `DateTime?` | `timestamp` | — | 软删时间 |

**手机号不放前台基类**——订单表另有收货信息快照，地址簿另有专表，避免个人信息散落各处。

### 2.4 后台账号 `User`（**不继承** `AdminEntityBase`）

后台账号自己携带租户身份，是 `TenantContext` 的来源，因此独立定义：

| 字段 | 类型 | 列 | 说明 |
|---|---|---|---|
| `Id` | `long` | `bigint` | 雪花 Id |
| `UserName` | `string` | `varchar(64)` | 登录名，**全局唯一** |
| `PasswordHash` | `string` | `varchar(256)` | **只存哈希**，禁止存明文 |
| `Phone` | `string` | `varchar(20)` | 手机号，**全局唯一** |
| `Email` | `string?` | `varchar(128)` | 邮箱，可空 |
| `NickName` | `string` | `varchar(64)` | 昵称 |
| `Avatar` | `string?` | `varchar(512)` | 头像 URL |
| `TenantType` | `int` | `integer` | **1 平台 / 2 商户**。**禁止 customer** |
| `PlatformId` | `long` | `bigint` | 所属平台。**0 = 超管（平台级）** |
| `MerchantId` | `long` | `bigint` | 所属商户；平台账号为 0 |
| `Status` | `int` | `integer` | **1 启用 / 2 停用** |
| `LastLoginAt` | `DateTime?` | `timestamp` | 最后登录时间 |
| `CreatedAt` / `UpdatedAt` / `IsDeleted` / `DeletedAt` | | | 同 `EntityBase`；账号表**不需要**操作人字段 |

**真实角色不在这个表**，绑定在权限中心 `user_role`（`BUSINESS.md` 第 5 节）。

### 2.5 前台客户 `Customer`（继承 `EntityBase`，自己的 Id 就是 CustomerId）

| 字段 | 类型 | 列 | 说明 |
|---|---|---|---|
| `CustomerName` | `string` | `varchar(64)` | 登录名，唯一 |
| `PasswordHash` | `string` | `varchar(256)` | 哈希 |
| `Phone` | `string` | `varchar(20)` | 手机号，唯一 |
| `NickName` | `string` | `varchar(64)` | 昵称（C 端展示） |
| `Avatar` | `string?` | `varchar(512)` | 头像 |
| `Gender` | `int` | `integer` | 0 未知 / 1 男 / 2 女 |
| `Birthday` | `DateTime?` | `date` | 生日 |
| `LastLoginAt` | `DateTime?` | `timestamp` | |

客户账号**与后台账号完全隔离**：不同库、不同登录端点、不同令牌（`BUSINESS.md` 第 4 节）。

**C 端接口（BUSINESS.md 180「注册 / 登录、资料、地址簿、收藏」）**：

| 接口 | 表 | 规则 |
|---|---|---|
| `POST /gateway/customers/Profile` / `UpdateProfile` | `customer` | 只改昵称 / 头像 / 性别 / 生日；**登录名与手机号不在这里改**（唯一键 + 需额外验证）。手机号**打码下发**（`138****8000`） |
| `POST /gateway/customers/addresses/List|Create|Update|Delete|SetDefault` | `customer_address` | 第一条地址自动成为默认；设默认要清掉旧的；删掉默认时剩下最新的一条自动顶上。收货手机号按 `1[3-9]\d{9}` 校验 |
| `POST /gateway/customers/favorites/List|Add|Remove` | `customer_favorite` | 单客户上限 **20**（超出返回额度不足）；重复收藏与取消都幂等；只回商品 Id（商品信息在商品服务，收藏表不存快照） |
| 归属 | — | 所有带 `customerId` 的 C 端接口一律过 `CustomerScope`：客户令牌与请求体不一致 → **403**；拿别人的地址 Id（用自己的 customerId）→ **404**（等同于不存在，不泄露 Id 是否真实） |

### 2.6 通用字段命名与类型

| 语义 | 类型 | 列类型 | 命名 | 约束 |
|---|---|---|---|---|
| 金额 | `decimal` | `numeric(18,2)` | `XxxAmount` | **禁止 float/double**；两位小数；**写入前必须显式四舍五入**（见下） |
| 积分 | `int` | `integer` | `Point` / `PointAmount` | **整数**，不存小数 |
| 数量 | `int` | `integer` | `Quantity` / `Count` | `>= 0`；交易链路 1-99 |
| 状态 | `int` | `integer` | `XxxStatus` | 注释**必须列全所有状态码** |
| 类型 | `int` | `integer` | `XxxType` | 注释必须列全枚举值 |
| 名称 | `string` | `varchar(128)` | `XxxName` | — |
| 编码 | `string` | `varchar(64)` | `XxxCode` / `XxxNo` | 唯一约束按业务定 |
| 描述 | `string?` | `varchar(512)` | `Description` / `Remark` | — |
| 单图 | `string?` | `varchar(512)` | `Image` / `Logo` / `MainImage` | — |
| 多图 | `string?` | `varchar(2000)` | `Images` | **存 JSON 数组字符串**，长度按张数限制 |
| 配置 JSON | `string?` | `text` | `XxxJson` | 注释写明序列化结构与长度上限 |

#### 金额字段的舍入（强制）

**所有金额字段保留两位小数、四舍五入。** 业务口径见 `BUSINESS.md` 8.4。

| 项 | 规则 |
|---|---|
| 舍入模式 | `Math.Round(x, 2, MidpointRounding.AwayFromZero)`，**全系统一致**；禁止用默认的 `ToEven` |
| 舍入位置 | **应用层显式舍入**，在 Entity 赋值前完成 |
| **禁止依赖数据库** | PostgreSQL 的 `numeric → numeric(18,2)` 隐式转换用的是**银行家舍入**（`0.125 → 0.12`），与四舍五入不符。列类型只是最后一道存储约束，**不能替代应用层舍入** |
| 输入校验 | 前端与后端都校验金额格式为「最多两位小数的非负数」，如 `^\d+(\.\d{1,2})?$` |
| 不适用 | 折扣率（`decimal(5,2)`，不做金额舍入）、数量、积分（整数） |
| 汇总 | 订单商品总额**由各行应付累加得出**，不重新计算 |

### 2.7 表与列命名

| 项 | 规范 | 示例 |
|---|---|---|
| 表名 | **snake_case**，与实体名单数一致 | `product`、`order_item`、`point_record` |
| 列名 | **snake_case** | `created_at`、`platform_id` |
| 主键 | 统一 `id` | — |
| 外键列 | `xxx_id`，**不建物理外键** | `merchant_id` |
| 索引 | 按查询场景建：租户列、状态列、外键列、排序列 | `idx_product_platform_status` |
| 唯一索引 | 业务唯一键 | `uk_user_username`、`uk_merchant_platform_name` |

### 2.8 时间与删除策略

| 项 | 约定 |
|---|---|
| 时区 | **一律存 UTC**，展示层统一转 `Asia/Shanghai`。容器时区变化不会污染历史数据 |
| 删除 | **默认软删**（写 `IsDeleted = true` + `DeletedAt`）。物理删除只在补偿 / 清理任务里显式调用专用方法，且必须注释写明原因 |

### 2.9 建表脚本要求

| 项 | 要求 |
|---|---|
| 位置 | `deploy/sql/<service>/`，**幂等可重跑**（`IF NOT EXISTS` / `CREATE OR REPLACE`） |
| 方式 | **不使用 FreeSql CodeFirst**（`CODING_STANDARD.md` 陷阱 16） |
| 种子 | 与 DDL 分文件；主键用「当前最大 Id + 1」，**不依赖雪花 AOP** |

---

## 3. FreeSql AOP 与仓储通用约定

### 3.1 多租户隔离方式：行级隔离 + AOP 注入

**服务 ≠ 租户。** 每个服务一个库，但**那个库同时装着所有平台、所有商户的数据**：

| 服务 | 数据库 | 同一个库里并存的数据 |
|---|---|---|
| Product | `simpleshopproduct` | A 平台商户 1 的商品、B 平台商户 2 的商品、平台自营商品…… |
| Order | `simpleshoporder` | 各平台各商户的所有订单 |
| Marketing | `simpleshopmarketing` | 各平台各商户的活动与券 |

因此 `product` 等表必须有 `PlatformId` / `MerchantId` 两列，查询按当前登录身份裁剪（`BUSINESS.md` §1.3）。

**不注入过滤的后果**：商户 A 打开商品列表会看到商户 B 的商品和订单——这就是越权。历史上这类漏洞基本都出在「某个 Handler 忘了加租户条件」。

**AOP 注入的意义**：开发者写仓储时不写租户条件，由 FreeSql AOP 在 SQL 执行前自动补上，从机制上杜绝遗漏。

### 3.2 AOP 统一注入的四类条件

在 `Aop.DataFilter` 中统一处理：

| 过滤器 | 作用范围 | 注入条件 |
|---|---|---|
| **软删过滤** | 所有 `EntityBase` 派生实体 | `IsDeleted == false` |
| **租户过滤** | 所有 `AdminEntityBase` 派生实体 | 按 `TenantContext` 追加（见下表） |
| **客户过滤** | 所有 `CustomerEntityBase` 派生实体 | `CustomerId == 当前客户` |
| **公开可见性过滤** | 实现 `IPublicVisible` 的实体 | **仅 C 端与游客上下文**追加（见 3.2.1） |

#### 3.2.1 实现方式：GlobalFilter + 仓储显式审计（不是 AOP）

> **2026-10-02 实测修正**：原先用 `Aop.ParseExpression` 追加过滤条件是**错的**。
> 它的 `Result` 是**替换**整个 WHERE，不是追加，导致业务条件被顶掉。
> 实测症状：不存在的账号 `not_exist_user` 能登录成功并返回库里第一条记录。
> 若那样部署，租户隔离 / 客户过滤 / 可见性过滤全部失效，等于越权。
> 正确做法：
> - **查询过滤**用 `IFreeSql.GlobalFilter.ApplyIf(name, condition, where)`——它 AND 进查询。
>   由 `FilterRegistrar.Register(freeSql, 实体程序集...)` 启动时注册，按实体类型逐个建表达式。
> - **审计字段**（雪花 Id / CreatedAt / CreatedBy / OperationBy）**显式写在 `CrudRepository`
>   的 InsertAsync / UpdateAsync 里**，不依赖钩子。
>   原因：实测 FreeSql 3.5 的 `Aop.CurdBefore` 在本项目调用链上没有触发，
>   依赖它会导致 Id 写成 0、CreatedAt 写成 `0001-01-01`。

#### 3.2.2 公开可见性过滤

**背景**：租户过滤解决「谁能看到谁的数据」，公开可见性过滤解决「哪些数据允许对外」。两者是**并列的独立维度**，缺一不可——漏掉后者就会把未审核内容暴露给顾客。

**接口约定**：

| 项 | 说明 |
|---|---|
| 标记接口 | `IPublicVisible`，由需要公开可见性过滤的实体实现 |
| 条件构造 | `IPublicVisible.BuildCondition(DateTime now)` 返回该实体的可见条件表达式 |
| **按上下文启用** | 仅当访问上下文为 `Customer` 或 `Anonymous` 时注入；`Admin` 上下文**不注入** |

**各实体的可见条件**（业务口径见 `BUSINESS.md` 1.4）：

| 实体 | 可见条件 |
|---|---|
| `Platform` | `Status == 1` |
| `Merchant` | **`AuditStatus == 20` 且 `Status == 1`** |
| `Product` | **`AuditStatus == 20` 且 `Status == 1`** |
| `MarketingActivity` | `Status == 1` 且 `now >= StartTime` 且 `now <= EndTime` |
| `CouponActivity` | `Status == 1` 且 `now` 在领取时间窗内 |
| `UserCoupon` | 未使用 且未过期 |
| `seckill_session` | `Status == 20` |

**注意**：`UserCoupon` 的 `Status` 语义是「已使用 / 已过期 / 有效」，与后台字典的状态名不同，实现时以 `BUSINESS.md` 13 为准。

**适用的访问上下文**：

| 上下文 | 租户过滤 | 公开可见性过滤 |
|---|---|---|
| 后台（`TenantType = backend`） | 注入 | **不注入**——运营必须能看到待审核商户与下架商品 |
| C 端（`TenantType = customer`） | 注入 | **注入** |
| 游客（未登录） | 注入 | **注入** |

租户过滤的具体语义：

| 当前登录身份 | AOP 自动追加的条件 |
|---|---|
| 平台超管（`TenantType = 平台` 且 `PlatformId = 0`） | **不加**条件，看全部平台 |
| 平台账号（`PlatformId > 0`） | `PlatformId == 当前平台` |
| 商户账号（`TenantType = 商户`） | `PlatformId == 当前平台 AND MerchantId == 当前商户` |
| 商户账号 + **商户根表**（`merchant`，见下方例外） | `Id == 当前商户` |

**例外：租户根表的身份在 `Id` 上，不在租户列上**

| 表 | 标记 | 为什么 |
|---|---|---|
| `platform` | `ITenantRoot` | 它的 `platform_id` 列恒为 0（平台不隶属于另一个平台），默认条件会让平台账号连自己的资料都查不到。**整条租户条件跳过**，`Id == 自己` 由 `PlatformRepository` 显式写 |
| `merchant` | `IMerchantRoot` | 它的 `merchant_id` 列恒为 0（商户不隶属于另一个商户），但 `platform_id` 列**是有意义的**（所属平台）。所以只把商户条件改写成 `Id == 当前商户`，平台条件那一半保持原样（平台账号看本平台商户仍然正确） |

> 踩过：`merchant` 表按默认规则过滤时，商户账号查自己的记录得到
> `merchant_id(0) == 我的商户Id` → 假 → **一条都查不到**。
> 表现是商户账号点「保存店铺装修」永远回「商户不存在」，
> 也就是**商户装修对商户本人完全不可用**（只有平台 / 超管能配）。
> 之所以一直没被发现，是因为 E2E 全程用超管令牌跑（超管不加租户条件）。
> 回归：`design-regression.ps1` API-DS-102 / API-DS-103，单测 `FilterRegistrarTests`。

**旁路**：平台级统计、跨租户对账、后台全量报表需要 `ClearFilter<T>()` 绕过过滤。
**每个用到旁路的地方必须写行内注释说明为什么**，否则等于打开了越权口子。

**C 端接口禁用可见性旁路**：`Customer` 与 `Anonymous` 上下文下**不允许**用 `ClearFilter<IPublicVisible>` 绕过公开可见性过滤。需要看到未上架商品的后台场景，一律走后台接口。

### 3.3 雪花 Id

| 项 | 约定 |
|---|---|
| 主键生成 | FreeSql `Aop.DataMapping` 在 `InsertBefore` 对 `EntityBase` 注入 Yitter 雪花 Id |
| 列类型 | `bigint`，**不自增、无 default** |
| JSON 下发 | 全局 `JsonNumberHandling.WriteAsString` → Id 自动转字符串 |
| Handler 内比较 | 用 `long` 直接比，**不要 `ToString()` 后比** |
| 前端 | **禁止 `Number(id)`**；仅判断「是否为 0」可用 `Number(id) > 0` |
| 种子 / 初始化 | **不能依赖 AOP**，用「当前最大 Id + 1」 |

### 3.4 workerId 分配：Redis 租约槽位

启动阶段 S4 执行。实现见 `WorkerIdLease`。

| 项 | 规则 |
|---|---|
| Redis key | `snowflake:worker:{服务名}:{槽位}`，每服务每槽位一个独立 key |
| 取值方式 | 从槽位 0 起逐个 `SET {key} {令牌} NX EX 90`，抢到即用；全部被占则启动失败 |
| 分配时机 | **每次启动抢一个当前空闲的槽位**；槽位随进程消失自动回收 |
| 续租 | 后台每 30 秒续租一次（TTL 90 秒，容忍连续两次续租失败）。续租前比对令牌，槽位若已被他人抢走则不续 |
| 释放 | 进程正常退出时比对令牌后 `DEL`；异常退出则等 TTL 到期 |
| 上限 | 槽位数由 `Snowflake:WorkerIdUpperBound` 给出（默认 64，与 `WorkerIdBitLength=6` 对齐） |
| 启动日志 | **必须打印分配到的 workerId 与租约 key**，便于排查 Id 重复问题 |

**为什么不用 `INCR` 自增**（实现过程中发现并修正的设计缺陷）：

`INCR` 单调递增且不回绕，看上去能保证不重号，但它在**每次启动都消耗一个 workerId**。
workerId 空间只有 64 个，开发机一天重启十几次服务，跑满 64 次之后
`snowflake:worker:UserService` 就永久大于 63，该服务**从此再也启动不了**，
唯一的修复方式是手工去 Redis 删 key。生产环境滚动发布几十次之后是同样的结局。
本项目实际已触发过一次：UserService 分配到 `workerId=64` 后直接启动失败。

租约模型把「分配新值」换成「抢占空闲槽位」，同时保住两条性质：

1. **同时存活的实例一定拿到不同槽位**（`SET NX` 保证），不会撞号；
2. **重启多少次都能起来**（槽位会随进程消失释放），不存在「用尽」状态。

槽位复用之所以安全：雪花 Id 含毫秒时间戳，前一个进程退出到后一个进程复用同一槽位之间
必然已跨过若干毫秒，两者生成 Id 的时间戳区间不重叠，Id 不会重复。

### 3.5 仓储通用方法

> **实现命名：`CrudRepository<T>` / `ICrudRepository<T>`**（不是 `BaseRepository`）。FreeSql 3.5 自带 `BaseRepository<TEntity>`，同名会让每个 Infrastructure 文件都出现 CS0104 二义性错误。文件在 `src/Collaboration/Collaboration.Domain/Repository/`。

| 方法 | 说明 |
|---|---|
| `GetByIdAsync(id)` | 含 AOP 过滤；查不到返回 `null`（不抛异常） |
| `InsertAsync(entity)` | 自动雪花 Id + `CreatedAt` + 创建人字段 |
| `InsertRangeAsync(list)` | 必须传 `ToList()`，避免命中单实体重载 |
| `UpdateAsync(entity)` | **单参**版本；内部写 `UpdatedAt` 与 `OperationId` / `OperationName` |
| `UpdateColumnsAsync(id, dto)` | **必须 `SetDto`**（见 `CODING_STANDARD.md` 陷阱 10），更新后需断言数据库 |
| `DeleteAsync(id)` | **软删**：写 `IsDeleted = true` + `DeletedAt` |
| `HardDeleteAsync(id)` | 物理删除，**仅限补偿 / 清理任务**，必须注释写明原因 |
| `ExistsAsync(predicate)` | 唯一性 / 存在性校验（需要查库的那类，放 Handler） |
| `QueryPagedAsync(cond)` | 分页查询；排序走白名单 |
| `CountAsync(predicate)` | 计数 |

### 3.6 排序白名单

排序字段**可以由前端传字段名**，但**必须经后端白名单映射**：

| 项 | 规则 |
|---|---|
| 传参 | `OrderBy`（字段名）+ `SortDirection`（`asc` / `desc`） |
| 校验 | 字段名不在该接口白名单内 → **回退默认排序**并记警告日志；`SortDirection` 只接受 `asc` / `desc` |
| 稳定性 | 排序结果必须追加**第二排序键**（通常是 `Id`），否则同值行顺序仍可能变化 |
| `total` | 与取数**必须同条件**（同一份 `WhereIf` 链） |
| 返回 | `(List<T> Items, long Total)` |

### 3.7 事务

| 场景 | 方案 |
|---|---|
| 单服务内多表写入（订单 + 明细） | `UnitOfWork`（`freeSql.Transaction`），**必须** |
| 单表写入 | 无需事务 |
| 跨服务 | **不用分布式事务**，用「本地事务 + 补偿」（见 `REVIEW.md` P0 风险 1） |

**当前已知缺口**：订单+明细、商品+SKU、发货单+明细尚未包事务（`REVIEW.md` P0 风险 2），实现时需优先补上。

### 3.8 其他易错点

| # | 易错点 |
|---|---|
| 1 | `Features/{User\|Address\|Favorite\|...}` 命名空间段会遮蔽同名实体（CS0118）→ 用 `using XxxEntity = ...` 别名 |
| 2 | 批量插入传 `IReadOnlyCollection` 会命中单实体重载 → 传 `ToList()` |
| 3 | 修改 `IRequest<T>` 返回类型时 Command 与 Handler **两处同步**，否则 CS0311 |
| 4 | 新增仓储忘记注册 DI → 启动即崩，**编译期发现不了**，必须实际启动一次 |

---

## 4. 前后台数据展示与下拉约定

### 4.1 铁律：下拉框显示 name，绝不显示 id

| 规则 | 说明 |
|---|---|
| **选项结构** | 所有下拉接口统一返回 `OptionDto { Id, Name }`，`Id` 为**字符串** |
| **下拉渲染** | `el-option` 的 label 直接取 `Name`，**不做任何「名称 → Id」的二次映射** |
| **下拉回填** | `el-select` 的 value 存 `Id`（字符串），**禁止 `Number()`** |
| **名称冗余** | 列表 / 详情**必须**由后端 join 好名称字段，前端直接渲染，**不做二次查询** |

### 4.2 下拉接口清单

| 接口 | 数据来源 | 备注 |
|---|---|---|
| `GET /gateway/platforms/Options` | `platform` 表 | 启用状态平台；平台账号**不请求**（自动锁定本平台） |
| `GET /gateway/merchants/Options` | `merchant` 表 | 已审核通过 + 启用；商户账号**不请求**（无 `merchant:read` 会 403） |
| `GET /gateway/brands/Options` | `brand` 表 | 启用状态 |
| `GET /gateway/categories/Tree` | `category` 表 | **树形结构**，最多三级 |
| `GET /gateway/products/Options` | `product` 表 | 已上架商品；`DeliveryType` 决定后续发货表单 |
| `GET /gateway/products/Designable` | `product` 表 | **装修可选商品**：已审核通过 + 已上架。后台上下文不注入可见性过滤（3.2.1），故需此接口单独过滤 |
| `GET /gateway/products/{spuId}/Skus` | `sku` 表 | **随 SpuId 联动**，只返回启用 SKU |
| `GET /gateway/coupon-templates/Options` | `coupon_template` 表 | 启用状态；供券活动与满赠活动选择 |
| `GET /gateway/seckill-sessions/Options` | `seckill_session` 表 | **只返回未开始 / 进行中**的场次 |
| `GET /gateway/permissions/Options` | `permission` 表 | 按模块分组树，供穿梭框使用 |
| `GET /gateway/roles/Options` | `role` 表 | 启用状态，供建号多选 |

**通用约定**：所有 `Options` 接口按当前 `TenantContext` 自动过滤（AOP），**商家与商户账号不需要自己传 tenant 参数**。

### 4.3 必须冗余返回的展示字段

| 场景 | 必须返回 |
|---|---|
| 订单列表 / 详情 | `PlatformName`、`MerchantName`、`CustomerName`（或脱敏 `CustomerPhoneMask`）、逐行 `ProductName`、`SkuSpecText` |
| 商品列表 | `BrandName`、`CategoryName`、`MinPrice`、`MaxPrice`、`FinalPrice`（到手价）、`ActivityTagText`、`CouponTagText`、`EvaluationScore`、`EvaluationCount` |
| 退款单 | `OrderNo`、`PlatformName`、`MerchantName`、`CustomerName`、逐行 `ProductName` |
| 评价列表 | `ProductName`（SPU 名）、`SkuSpecTextList`、`ReplyContent`、`IsAnonymous` |
| 积分流水 | `OrderNo`、`BizTypeText` |
| 秒杀管理 | `SessionName`、`ProductName`、`SkuSpecText`、`SoldCount` |
| 工作台报表 | 每个指标带 `Label` 与 `Value`，**前端不写死文案** |
| 日志列表 | `OperatorName`、`ModuleText` |

### 4.4 规格文本约定

SKU 的多个规格值必须由后端拼成**可直接展示的文本**，前端不拼字符串：

| 后端字段 | 示例 | 用途 |
|---|---|---|
| `SkuSpecText` | `"红色 / M；蓝色 / L"` | 订单、购物车、秒杀、库存列表 |
| `SkuSpecTextList` | `["红色 / M", "蓝色 / L"]` | 评价页；最多列 3 个，超出显示 `等 N 个规格` |

### 4.5 枚举文案由后端下发

| 规则 | 说明 |
|---|---|
| 枚举定义 | 统一定义在 `Collaboration/Domain/Enums`，每个成员带 `[Description]` |
| 下发格式 | 列表 / 详情返回**数值 + 文案**成对字段：`Status = 1`、`StatusText = "待支付"` |
| 前端下拉 | 选项**来自接口**；**禁止在页面里硬编码枚举文案与值** |
| 前端映射表 | 仅用于**状态标签颜色**（`utils/dict.js`），不用于文案 |

### 4.6 数字与 Id 的前端处理

后端数字统一按字符串下发，前端必须区分处理：

| 类型 | 处理 |
|---|---|
| **枚举 / 金额 / 数量** | 先 `Number()` 再比较或回填，否则 `el-radio` / `el-select` 严格比较不回显 |
| **雪花 Id** | **保持字符串**，禁止 `Number()`；比较用 `===` 原样比 |
| **金额展示** | 统一走格式化工具，保留两位小数，带千分位 |

统一封装到 `apps/admin-vue/src/utils/` 与 `apps/user-uniapp/src/common/`，**禁止在页面里重复写归一化逻辑**。

### 4.7 统一响应与分页结构

| 结构 | 定义 |
|---|---|
| 响应体 | `ApiResponse { Success, Code, Message, Data, Errors }`（`Collaboration` 定义，`ApiResults.Ok/Fail` 构造） |
| 分页体 | `PagedResult<T> { Items, Total, Page, PageSize }` |
| 下拉项 | `OptionDto { Id, Name }`（`Id` 为字符串） |

**禁止** Handler 返回 `ApiResponse` 时控制器再包 `Ok()`（双层信封，`CODING_STANDARD.md` 第 2.2 节）。

### 4.8 列表页统一约定

| 项 | 约定 |
|---|---|
| 分页 | 默认 20 条/页，可选 10 / 20 / 50 / 100 |
| 操作列 | 固定右侧，按权限点控制按钮显隐（**前端隐藏 + 后端鉴权双保险**） |
| 空状态 | 空列表有插画 / 文案，不显示空白表格 |
| 加载 | 骨架屏或 loading，避免布局跳动 |
| 租户列 | 平台账号隐藏商户列与平台列；商户账号**不请求**自己无权限的下拉接口 |
| 时间列 | 后端返回 UTC 字符串，前端统一格式化为 `Asia/Shanghai` |

---

## 5. 后台表单字段规格

> 表格列含义：**字段名 / 类型 / 必填 / 默认值 / 校验规则 / 说明**。
> 「新建」与「编辑」的差异写在「说明」列。权限点列在表头或表下单独标注。
>
> **校验规则列同时约束前后端**：后端必须有对应 Validator（`CODING_STANDARD.md` 3.3），前端在**提交时**用 `validators.js` 校验并飘红提示（3.4）。
> 正则与数值范围的**单一来源**是 `deploy/shared/validation-rules.json`，构建时生成两端常量（3.5）。
> 需要查库判断的字段（唯一性、层级、引用完整性）标注「**仅后端**」，前端不做校验，提交后由后端用 tip 提示。

### 5.1 创建 / 编辑平台

**接口**：`POST /gateway/platforms/Create`、`POST /gateway/platforms/Update`
**权限点**：创建 `platform:create`，编辑 `platform:update`
**所属服务**：MerchantPlatformService → `simpleshopmerchant.platform`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| PlatformName | string(128) | ✔ | — | 2-64 字符；trim；**全局唯一** | 平台名称 |
| PlatformCode | string(16) | ✔ | — | **6 位字母** `[A-Za-z]{6}`；trim；**全局唯一** | **编辑时只读、不可修改**（小程序 `PLATFORM_CODE` 锁定它） |
| ContactName | string(64) | ✔ | — | 2-32 字符；trim | 联系人 |
| ContactPhone | string(20) | ✔ | — | `^1[3-9]\d{9}$` | 联系电话 |
| Logo | string(512) | | 空 | URL 长度 ≤ 512；走 ToolService 上传 | **选填** |
| MallName | string(128) | ✔ | — | 2-64 字符；trim | 商城名称（小程序顶部展示） |
| Notice | string(500) | | 空 | ≤ 500 字符 | 首页公告 |
| PrimaryColor | string(16) | | `#0071e3` | 十六进制颜色 `#RRGGBB` | 主题色 |
| TabColor | string(16) | | `#0071e3` | 十六进制颜色 | TabBar 选中色 |
| BackgroundColor | string(16) | | `#f5f5f7` | 十六进制颜色 | 页面背景色 |
| ShippingFee | decimal(18,2) | | `0.00` | `>= 0`；两位小数 | **运费，仅对实物快递收取** |
| FreeShippingThreshold | decimal(18,2) | | `0` | `>= 0`；`0` 表示不启用包邮 | 满额包邮门槛，按商品实付判定 |
| Status | int | ✔ | `1` | 1 启用 / 2 停用 | 停用后小程序不可见、不可交易 |
| Remark | string(512) | | 空 | ≤ 512 字符 | 备注 |

**只读展示字段**（列表 / 详情返回，不在表单内编辑）：

| 字段 | 说明 |
|---|---|
| Id | 雪花 Id，字符串下发 |
| MerchantCount | 商户数 |
| ProductCount | 商品数 |
| CreatedAt / CreatedByName | 创建时间 / 创建人 |
| UpdatedAt / OperationName | 最后更新时间 / 最后操作人 |

**操作约束**：

| 操作 | 规则 |
|---|---|
| 删除 | **有下级数据（商户 / 商品 / 订单）时禁止删除**，只能停用。仅「无商户、无商品、无订单」的平台可删除（软删） |
| 停用 | 级联影响：小程序不可见、C 端不可下单；**历史订单与报表不受影响** |
| 复制编码 | 编辑保存时若 `PlatformCode` 传入值与原值不同，**忽略传入值**（不报错，防止前端误改） |

### 5.2 创建 / 编辑商户

**接口**：`POST /gateway/merchants/Create`、`POST /gateway/merchants/Update`
**权限点**：创建 `merchant:create`，编辑 `merchant:update`
**所属服务**：MerchantPlatformService → `simpleshopmerchant.merchant`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| MerchantName | string(128) | ✔ | — | 2-64 字符；trim；**同平台内唯一** | 商户 / 店铺名称 |
| PlatformId | long | ✔ | — | 下拉 `GET /platforms/Options`；**必须是启用状态平台** | 所属平台。商户账号**自动锁定本平台**，前端隐藏该下拉 |
| ContactName | string(64) | ✔ | — | 2-32 字符；trim | 联系人 |
| ContactPhone | string(20) | ✔ | — | `^1[3-9]\d{9}$` | 联系电话 |
| Logo | string(512) | | 空 | URL ≤ 512 | **选填** |
| Description | string(1000) | | 空 | **≤ 1000 字符** | 店铺简介（C 端店铺页展示） |
| Status | int | ✔ | `2` | 1 启用 / 2 停用 | **新建默认停用**（需审核通过后才可运营） |
| Remark | string(512) | | 空 | ≤ 512 字符 | 备注 |

**系统生成字段（只读）**：

| 字段 | 生成规则 |
|---|---|
| MerchantNo | **`PlatformCode` + 雪花 Id**（如 `DEMOPL13755080881608709`），用户不填 |
| AuditStatus | 新建时固定 `10 待审核` |

**审核相关只读字段**：

| 字段 | 说明 |
|---|---|
| AuditRemark | 审核意见 / 拒绝原因 |
| AuditedAt | 审核时间 |
| AuditorName | 审核人姓名 |
| ProductCount / OrderCount | 商品数 / 订单数 |

**操作约束**：

| 操作 | 规则 |
|---|---|
| 编辑限制 | `AuditStatus = 90 已拒绝` 时**允许编辑**（改完重新提交回到待审核）；`20 已通过` 时编辑**不重置审核状态** |
| 停用 | 商户停用后**不可新增商品、不可接单**，但**历史订单与退款仍可处理**。**副作用：批量下架该商户全部已上架商品，并发布 `product.changed` 同步 ES 索引** |
| 删除 | 有商品或订单时禁止删除，只能停用 |

### 5.3 商户审核

**接口**：`POST /gateway/merchants/{id}/Audit`
**权限点**：`merchant:audit`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| MerchantId | long | ✔ | — | 行数据带出，只读 | 商户 Id |
| AuditStatus | int | ✔ | — | **只能填 20 已通过 或 30 已驳回** | 审核结论。注意与 5.3 商户审核**不同码**（商户是 20 / 90）—— 商品驳回用 30，改动前先看 `AuditStatuses` |
| AuditRemark | string(500) | **拒绝时必填** | 空 | ≤ 500 字符 | **通过时选填，拒绝时必填** |

**副作用**：

| 审核结论 | 副作用 |
|---|---|
| 20 已通过 | 写入 `AuditedAt`、`AuditorId`、`AuditorName`；商户方可登录运营本商户商品与订单 |
| 90 已拒绝 | 除写入审核信息外，**批量下架该商户全部已上架商品，并发布 `product.changed` 同步 ES 索引** |

**为什么审核被拒要下架商品**：商品资质依赖商户资质。商户没资质了，其商品就不该继续对外销售。
**必须发 `product.changed`**：搜索走 ES 索引，不走 C 端可见性过滤；不同步会出现「商品页看不到但搜索搜得到」。

### 5.4 分类管理

**接口**：`/gateway/categories/*`
**权限点**：`category:read` / `category:create` / `category:update` / `category:delete`
**所属服务**：ProductService → `simpleshopproduct.category`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| ParentId | long | ✔ | `0` | `0` = 一级；父分类存在且 `Level < 3` | 上级分类。**选三级分类时会新建第四级 → 必须拒绝** |
| CategoryName | string(64) | ✔ | — | 1-64 字符；trim；**同父级内唯一** | 分类名称 |
| CategoryCode | string(64) | | 空 | 唯一 | 分类编码 |
| Icon | string(512) | | 空 | URL ≤ 512 | 分类图标（金刚区 / 分类页用） |
| Image | string(512) | | 空 | URL ≤ 512 | 分类大图 |
| SortOrder | int | ✔ | `0` | `>= 0` | 排序，小的在前 |
| PlatformId | long | ✔ | — | `0` = **公共分类**（全平台共用）；`> 0` = **平台私有分类** | 归属平台 |
| Status | int | ✔ | `1` | 1 启用 / 2 停用 | 停用后前台不展示，**已有商品不受影响** |

**系统计算字段（只读）**：`Level`（1 / 2 / 3），由服务端根据 `ParentId` 链计算，前端不传。

**操作约束**：

| 操作 | 规则 |
|---|---|
| 删除 | **该分类下有子分类或有商品时禁止删除**，只能停用 |
| 层级 | 强制 ≤ 3 级；新增子分类时若父级已是第 3 级 → 400 |
| 排序 | 拖拽排序后批量提交 `SortOrder` |

### 5.5 品牌管理

**接口**：`/gateway/brands/*`
**权限点**：复用 `product:read` / `product:create` / `product:update` / `product:delete`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| BrandName | string(64) | ✔ | — | 1-64 字符；trim；**全局唯一** | 品牌名 |
| Logo | string(512) | | 空 | URL ≤ 512 | 品牌 Logo |
| SortOrder | int | | `0` | `>= 0` | 排序 |
| PlatformId | long | ✔ | — | `0` = 公共品牌 | 归属平台 |
| Status | int | ✔ | `1` | 1 启用 / 2 停用 | |

**删除约束**：品牌下有商品时禁止删除，只能停用。

**重要**：**商品的品牌是选填项**（见 5.6），所以品牌表可以为空，不影响商品创建。

### 5.6 创建 / 编辑商品（SPU）

**接口**：`POST /gateway/products/Create`、`POST /gateway/products/Save`
**权限点**：创建 `product:create`，编辑 `product:update`
**所属服务**：ProductService → `simpleshopproduct.product`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SpuName | string(128) | ✔ | — | 2-128 字符；trim | 商品名 |
| SubTitle | string(200) | | 空 | ≤ 200 字符 | 副标题 |
| BrandId | long | | 空 | 下拉 `GET /brands/Options`；启用状态 | **品牌选填** |
| CategoryId | long | ✔ | — | 下拉分类树；**只能选第 3 级（叶子）分类** | 商品分类 |
| **DeliveryType** | int | ✔ | — | **1 实物快递 / 2 虚拟商品 / 3 实物自提** | 决定运费、发货表单、退款窗口 |
| MainImage | string(512) | ✔ | — | URL ≤ 512；**固定 180×180 上传框** | 主图 |
| Images | string(2000) | | 空 | JSON 数组；**≤ 6 张**；每张 ≤ 255 字符 | 轮播图，同样 180×180 固定框 |
| DetailImages | string(2000) | | 空 | JSON 数组；≤ 9 张 | 详情图 |
| OriginalPrice | decimal(18,2) | | 空 | `0` 或 `>=` 所有 SKU 售价 | 划线原价 |
| Description | string(4000) | | 空 | ≤ 4000 字符 | 商品描述 |
| AuditStatus | int | — | `10` | **新建固定 10 待审核；编辑时忽略传入值** | 审核状态 |
| Status | int | ✔ | `2` | 1 上架 / 2 下架 | **新建默认下架**；必须审核通过才能上架 |
| SortOrder | int | | `0` | `>= 0` | 排序 |
| Remark | string(512) | | 空 | ≤ 512 字符 | 备注 |

**服务端计算字段（只读）**：

| 字段 | 计算规则 |
|---|---|
| Id | 雪花 Id |
| Price | **所有启用 SKU 的最低售价** |
| MaxPrice | 所有启用 SKU 的最高售价 |
| Sales | 支付成功累加 |
| EvaluationScore / EvaluationCount | 每日 03:00 聚合重算（`BUSINESS.md` 14.5） |
| BrandName / CategoryName | 冗余返回 |

**只读展示**：创建人 / 创建时间 / 最后操作人 / 最后更新时间。

**操作约束**：

| 项 | 规则 |
|---|---|
| **品牌** | 选填，可不选 |
| **审核不通过后编辑** | **允许编辑**；编辑**不重置审核状态**，需**重新提交审核**才回到 10 待审核 |
| **DeliveryType 修改** | 已存在的未完成订单仍按**下单时快照**处理，不受影响 |
| **上架前置** | `AuditStatus` 必须为 `20 已通过`，否则拒绝上架 |
| **删除** | 有未完成订单时禁止删除（软删下架即可） |
| **上下架理由** | **不需要理由**，直接切状态 |
| 上下架 / 改价 / 编辑 | 触发 `product.changed` → ES 索引同步 |

### 5.7 商品规格与 SKU（SPU 内嵌）

#### 5.7.1 规格定义（SPU 下动态定义，不建全局规格字典）

**接口**：`POST /gateway/products/Save` 的一部分

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| Specs | SpecItem[] | ✔ | — | 至少 1 个规格项；规格项名 1-32 字符；**同 SPU 内规格项名不重复** | 规格项列表，如「颜色」「尺码」 |
| Specs[].SpecName | string(32) | ✔ | — | 1-32 字符；trim | 规格项名 |
| Specs[].SpecValues | SpecValue[] | ✔ | — | 每个规格项至少 1 个值；值名 1-32 字符；**同规格项内不重复** | 规格值列表，如 红 / 蓝 |
| Specs[].SpecValues[].ValueName | string(32) | ✔ | — | 1-32 字符 | 规格值名 |

**设计说明**：不同商品的规格千差万别（手机是「颜色+容量」，食品是「规格+口味」），建全局规格字典表维护成本高、易脏。
**选择器按 SPU 联动**：前端根据该 SPU 的 `Specs` 动态渲染规格选择器。

#### 5.7.2 SKU 列表

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| Skus | SkuItem[] | ✔ | — | **至少 1 个 SKU**；数量 1-100 | SKU 列表 |
| Skus[].SkuCode | string(64) | ✔ | — | 1-64 字符；**全局唯一**；**按编码 Upsert** | SKU 编码，是 Upsert 的依据 |
| Skus[].SpecValueIds | long[] | ✔ | — | 必须**覆盖该 SPU 所有规格项**，且每项取一个值 | 该 SKU 的规格组合 |
| Skus[].Price | decimal(18,2) | ✔ | — | `> 0`；两位小数 | 售价 |
| Skus[].OriginalPrice | decimal(18,2) | | 空 | `0` 或 `>= Price` | 划线原价 |
| Skus[].Stock | int | ✔ | `0` | `>= 0` | **初始库存**，创建时初始化库存记录 |
| Skus[].Image | string(512) | | 空 | URL ≤ 512 | SKU 图 |
| Skus[].Status | int | ✔ | `1` | 1 启用 / 2 停用 | 停用后不可下单 |

**服务端生成（只读）**：

| 字段 | 生成规则 |
|---|---|
| Id | 雪花 Id |
| SkuName | **`SpuName` + 各规格值按规格项顺序拼接**，如「红色T恤 红色 / M」 |
| SkuSpecText | `"红色 / M"`，供列表直接展示（见 4.4） |

**库存调整**：**不在商品编辑页改库存**。创建时的 `Stock` 仅用于初始化；
之后的增减走**独立的库存管理页**（见 5.9），避免在编辑商品时误改实时库存。

### 5.8 商品审核

**接口**：`POST /gateway/products/{id}/Audit`
**权限点**：`product:audit`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SpuId | long | ✔ | — | 行数据带出，只读 | 商品 Id |
| AuditStatus | int | ✔ | — | **只能填 20 已通过 或 90 已拒绝** | 审核结论 |
| AuditRemark | string(500) | **拒绝时必填** | 空 | ≤ 500 字符 | 通过时选填，拒绝时必填 |

**副作用**：写 `AuditedAt`、`AuditorId`、`AuditorName`。审核通过**不自动上架**，仍需手动上架。

### 5.9 商品上下架

**接口**：`POST /gateway/products/{id}/Status`
**权限点**：`product:update`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SpuId | long | ✔ | — | 只读 | 商品 Id |
| Status | int | ✔ | — | 1 上架 / 2 下架 | 目标状态 |

**约束**：

| 项 | 规则 |
|---|---|
| 上架前置 | `AuditStatus` 必须为 `20 已通过` |
| 理由 | **不需要填理由** |
| 副作用 | 触发 `product.changed` → ES 索引同步；下架后游客搜索不到 |

### 5.10 库存管理

**接口**：`/gateway/inventory/*`
**权限点**：`inventory:read` / `inventory:update`
**所属服务**：InventoryService → `simpleshopinventory.stock`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SkuId | long | ✔ | — | 下拉（按 SPU 联动） | SKU Id |
| SkuSpecText | string | — | — | 服务端返回，只读 | 规格文本，便于人工辨认 |
| ProductName / SpuName | string | — | — | 服务端返回，只读 | 商品名 |
| AvailableAdjust | int | | `0` | `-999999 ~ 999999` | 可用库存**调整量**（正增负减），不是最终值 |
| Remark | string(512) | ✔ | — | 2-200 字符 | 调整原因（写入库存流水） |

**只读展示**：可用 / 锁定 / 已扣减三个数、库存预警状态。

**约束**：

| 项 | 规则 |
|---|---|
| 语义 | 提交的是**调整量**而非最终值，避免并发覆盖 |
| 下限 | 调整后 `Available` **不得小于 0** |
| 流水 | 每次调整写 `StockFlow`，`BizNo` 由后端生成，带操作人与时间 |
| 锁定与已扣 | **不可手工修改**，只能通过下单 / 支付 / 取消 / 退款流程变动 |
| 预警 | `Available < 阈值`（AgileConfig 可配）在列表高亮 |

### 5.11 活动（满减 / 满折 / 满赠）

**接口**：`/gateway/marketing/activities/*`
**权限点**：`marketing:read` / `marketing:create` / `marketing:update` / `marketing:delete`
**表**：`marketing_activity` + `marketing_activity_target`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| ActivityName | string(128) | 是 | — | 2-128 字符；trim | 活动名 |
| ActivityType | int | 是 | — | 1 满减 / 2 满折 / 3 满赠 | 决定下面哪些字段必填 |
| PlatformId | long | 是 | — | 下拉平台 | 归属平台 |
| MerchantId | long | 否 | 0 | 0 表示平台活动 | 商户账号自动锁定本商户 |
| ThresholdAmount | decimal(18,2) | 满减满折必填 | — | 大于 0；两位小数 | 门槛金额 |
| DiscountAmount | decimal(18,2) | 满减必填 | — | 大于 0；两位小数 | 优惠金额 |
| DiscountRate | decimal(5,2) | 满折必填 | — | 0.01 ~ 10，最多 2 位小数 | 折扣率数值：8.5 表示 85 折 |
| GiftCouponTemplateId | long | 满赠必填 | — | 下拉启用券模板 | 赠送的券模板 |
| GiftQuantity | int | 满赠 | 1 | 1 ~ 100 | 赠送张数 |
| StartTime | DateTime | 是 | — | UTC | 开始时间 |
| EndTime | DateTime | 是 | — | 必须晚于 StartTime | 结束时间 |
| TargetType | int | 是 | — | 1 全场 / 2 指定 SPU / 3 指定 SKU | 适用范围 |
| Targets | string(2000) | 2和3时必填 | — | JSON Id 数组；最多 200 个 | 目标列表 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | 停用需立即调快照失效 |
| SortOrder | int | 否 | 0 | 大于等于 0 | 影响多活动冲突时的确定性排序 |

**只读展示**：参与订单数、参与金额、折扣总额（效果报表下钻）。

**活动报表与下钻**（BUSINESS.md 17）：

| 项 | 规则 |
|---|---|
| 数据源 | 下单试算命中活动时写 `marketing_activity_record`（活动名存快照）。订单行只存折扣额、不存活动 Id，不在判定点记一笔就**没有数据源** |
| 幂等 | 唯一索引 `(订单号, 活动 Id)`：试算会被重放，不判重会把「参与订单数」刷成两倍 |
| 参与金额 | **回订单服务取**（口径同工作台 GMV：排除待支付 / 已取消 / 已退款）。营销侧自己估一遍，两张报表必然对不上 |
| 参与订单数 | **下单即计**（含未支付 / 已取消），与下钻明细逐行对得上；金额那一列才按「已支付」算。两个口径都写在字段注释里，避免运营把「参与订单 10 / 参与金额 0」当成 bug |
| 报表接口 | `POST /gateway/reports/Marketing` 的 `activities` 段（逐活动：参与订单数 / 参与金额 / 折扣总额） |
| 下钻接口 | `POST /gateway/marketing/activities/Records`，入参 `activityId` + `range`；**时间口径必须与报表一致**，否则行数与报表对不上 |
| 权限点 | 下钻绑在 `report:marketing` 上（与报表同一个权限点） |

**约束**：

| 项 | 规则 |
|---|---|
| 每单限购 / 总量 | **没有这两项**。活动粒度是「一单命中一次」（优惠引擎对整单选一个活动），任何 ≥1 的「每单限购」与 1 等价；活动也没有总量限制（限量只存在于秒杀与券）。`promotion_activity` 表里遗留的 `per_order_limit` / `total_quantity` / `used_quantity` 三列不参与任何计算，接口与表单都不再接受 |
| 全场活动排除商品 | **不支持**。要排除某些商品必须建成「指定 SPU」类型 |
| 满赠 | 折扣额记 0，仅当该行无任何折扣可用时才命中 |
| 满赠发券 | 命中时（下单试算）把「送哪个模板、送几张」写成 `gift_grant` 承诺；**支付成功才兑现**。活动事后被改 / 停用 / 过期都不影响已下单的承诺。承诺按 `(订单号, 来源类型, 来源 Id)` 唯一，重复试算不会重复承诺 |
| 优惠额超过行金额 | 该行按 0.01 计入，不产生负数 |
| 启停 | 启停或保存后**必须调快照 `Invalidate(platformId)`** |
| 快照 | **不缓存时间窗口**，使用时按 `now` 过滤 |
| 删除 | 有参与记录时禁止删除，只能停用 |
| 归属解析 | `PlatformId` / `MerchantId` 由**服务端按令牌身份解析**，不采信请求体：超管按传入值（0 = 平台无关，测试与历史数据用）；平台账号锁定到自己的平台且**只能建平台级活动**（`MerchantId` 必须为 0）；商户账号锁定到自己的平台 + 商户。跨平台请求返回 403。理由：AOP 租户过滤只管查询 / 更新 / 删除、**不管插入**，不解析就等于让调用方决定「这条活动挂在谁名下」 |
| 商户维度匹配 | 优惠引擎**带商户维度**：一条活动只作用于 `activity.MerchantId <= 0`（平台级）或 `activity.MerchantId == 行.商户`（商户级）的订单行。所以商户级的「全场」= **本店全场**，不会减到同平台其它商户的商品上。行上的商户由服务端解析，不采信客户端（下单链路取商品归属；C 端试算回商品服务查） |
| 商户活动的目标 | 商户级活动（`MerchantId > 0`）的 `Targets` 里，SPU / SKU 必须**存在且属于本商户**（400）。平台级活动不做归属校验（平台账号本来就能管全平台商品） |
| 平台级活动的目标 | **不做归属校验**：平台账号本来就能管全平台商品，而匹配是「平台 + 目标」，指到别的平台商品也不会作用于本平台订单 |

### 5.12 券模板

**接口**：`/gateway/marketing/coupon-templates/*`
**权限点**：`coupon-template:read` / `create` / `update` / `delete`
**表**：`coupon_template`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| TemplateName | string(128) | 是 | — | 2-128 字符；trim | 模板名 |
| CouponType | int | 是 | — | 1 满减 / 2 折扣 / 3 代金(0 元减) / 4 满赠 | 决定下面哪些字段必填 |
| ThresholdAmount | decimal(18,2) | 满减折扣必填 | — | 大于 0；0 表示无门槛 | 门槛，按适用行金额合计判定 |
| DiscountAmount | decimal(18,2) | 满减代金必填 | — | 大于 0；两位小数 | 优惠额 |
| DiscountRate | decimal(5,2) | 折扣必填 | — | 0.01 ~ 10，最多 2 位小数 | 折扣率 |
| GiftCouponTemplateId | long | 满赠必填 | — | 下拉启用模板 | 赠送模板 |
| ValidDays | int | 是 | — | 1 ~ 3650 | 领取后 N 天有效 |
| TotalQuantity | int | 是 | 0 | 大于等于 0；0 表示不限量 | 总发行池子 |
| PerUserLimit | int | 是 | — | 1 ~ 100 | 每人限领 |
| PerOrderLimit | int | 否 | 1 | 1 ~ 10 | 每单限用；订单级只能一张，默认 1 |
| PlatformId | long | 是 | — | 下拉平台 | 归属平台 |
| MerchantId | long | 否 | 0 | 0 表示平台模板 | 归属商户 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | 停用后不可领取，已发出的券不受影响 |

**券快照机制（重要）**：

| 项 | 规则 |
|---|---|
| 快照时机 | 券发放给用户时（`user_coupon` 创建）把模板的类型、门槛、优惠额、折扣率、有效期天数复制一份存到用户券自己的字段 |
| 模板后续修改 | **不影响已发出的券**，已发出的券永远按自己的快照计算 |
| 券活动引用 | 券活动不做实时联动模板规则，发放时再从模板取一次快照 |
| 逐行分摊 | 券有作用域（全场 / 指定 SPU / 指定 SKU），**逐行优惠额只能由营销侧算**（`CouponCalculator.AllocateToCoveredLines`，与优惠引擎的 `ApplyDiscount` 同一套：按覆盖行原价比例、余数给金额最大的那一行）。结算试算（`coupons/Settle` 带 `couponId`）与占券（`coupons/Occupy`）都把它回给订单侧，订单侧**原样使用**；按全行比例自己分会把优惠摊到作用域外的行上，而部分退款是按行应付退的 |
| 修改提示 | 模板有未领取完的库存时允许修改，但界面须提示「不影响已领取的券」 |
| 满赠券（类型 4） | 折扣额记 0、**可以正常占用与核销**（门槛照判），占用时按模板的 `GiftCouponTemplateId` 写一条 `gift_grant` 承诺，支付成功后送出承诺的那张券 |
| 满赠发出去的券 | 没有券活动，适用范围按**全场**快照；有效期从**发放时刻**（支付成功）起算模板的 `ValidDays` |
| 归属解析 | 与 5.11 / 5.13 同一套规则：`PlatformId` / `MerchantId` 由服务端按令牌身份解析（平台账号锁本平台且只能建平台级模板、商户账号锁本商户、超管按传入值）。`MerchantId` 不在编辑入参里 —— 归属创建时锁定，事后改归属会让已发出的券跟着错位 |
| 满赠模板归属 | 类型 4 的 `GiftCouponTemplateId` 必须与本人同租户（双方都非 0 时比对），否则 400：跨租户引用等于把 A 的券挂到 B 的满赠活动上 |

**只读展示**：已发放数、已领取数、已核销数、核销率。

### 5.13 券活动（领券中心）

**接口**：`/gateway/marketing/coupon-activities/*`
**权限点**：`coupon-activity:read` / `create` / `update`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| ActivityName | string(128) | 是 | — | 2-128 字符；trim | 券活动名 |
| CouponTemplateId | long | 是 | — | 下拉启用模板 | 关联模板，发放时从模板取快照 |
| PlatformId | long | 是 | — | 下拉平台 | 归属平台 |
| ClaimStartTime | DateTime | 是 | — | UTC | 领取开始时间，与券有效期是两个不同概念 |
| ClaimEndTime | DateTime | 是 | — | 必须晚于 ClaimStartTime | 领取结束时间 |
| ClaimQuantity | int | 是 | — | 大于等于 1 | 本次发放量，与模板 TotalQuantity 是两个独立池子 |
| PerUserLimit | int | 是 | — | 1 ~ 100 | 每人限领；生效值取本值与模板 PerUserLimit 的较小者 |
| TargetType | int | 是 | — | 1 全场 / 2 指定 SPU / 3 指定 SKU | 适用范围 |
| Targets | string(2000) | 2和3时必填 | — | JSON Id 数组；最多 200 个 | 目标列表 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | |

**发放逻辑约束**：

| 项 | 规则 |
|---|---|
| 发放量校验 | ClaimQuantity 不得超过模板剩余可发量（TotalQuantity 为 0 则不限） |
| 时间窗 | 领取必须在 ClaimStartTime 与 ClaimEndTime 之间，超窗不可领 |
| 券有效期 | 从**领取时刻**起算模板快照里的 ValidDays |
| 归属解析 | 与 5.11 同一套规则：`PlatformId` / `MerchantId` 由服务端按令牌身份解析（平台账号锁本平台、商户账号锁本商户、超管按传入值）。`MerchantId` 不在编辑入参里 —— 归属创建时锁定，事后改归属会让已领取的券跟着错位 |
| 模板归属一致 | 关联模板与活动的归属必须一致（双方都非 0 时比对）：跨租户引用等于把 A 的券发到 B 的活动上，返回 400 |

### 5.14 营销配置（平台优惠优先级）

**接口**：`/gateway/marketing-config/Get` / `Save`
**权限点**：`marketing-config:read` / `marketing-config:update`
**表**：`marketing_config`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| PlatformId | long | 是 | — | 每平台一条 | 归属平台 |
| Priority | int | 是 | 2 | 1 活动优先 / 2 券优先 | 平台优惠优先级，默认券优先 |

**约束**：保存后**必须调快照 `Invalidate(platformId)`**。
规则说明文案由前端固定展示（活动优先 = 先取活动，无活动才取券）。

### 5.15 创建 / 编辑限时抢购场次

**接口**：`/gateway/marketing/seckill-sessions/Create` / `Update`
**权限点**：`seckill:read` / `seckill:create` / `seckill:update`
**表**：`seckill_session`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SessionName | string(128) | 是 | — | 2-128 字符；trim | 场次名 |
| PlatformId | long | 是 | — | 下拉平台 | 归属平台 |
| StartTime | DateTime | 是 | — | UTC；**允许早于当前时间**（实现口径：创建 → 配商品 → 发布，发布后立即开抢；抢购入口同时校验「状态=进行中」与时间窗，所以创建后未发布时抢不了）。只校验 `EndTime > StartTime` | 开始时间 |
| EndTime | DateTime | 是 | — | 必须晚于 StartTime | 结束时间 |
| Status | int | 否 | 10 | 10 未开始 / 20 进行中 / 30 已结束 / 40 已取消；**由时间驱动，不接受人工设置** | 场次状态（只读） |
| Remark | string(512) | 否 | 空 | 最多 512 字符 | 备注 |

**服务端维护的只读字段**：

| 字段 | 说明 |
|---|---|
| Id / SessionId | 雪花 Id |
| StockTransferred | 是否已从常规库存划出；为 true 后不允许改该场次商品的库存数量 |
| CreatedAt / OperationName | 创建人与最后操作人 |

**约束**：

| 项 | 规则 |
|---|---|
| 审核 | **不需要审核**，创建后即可发布，由开始与结束时间驱动状态流转 |
| 多场次预留 | 本期只用一个场次，但所有接口与实体都按 SessionId 寻址，当前场次只是查询条件 |
| 编辑限制 | Status 大于等于 20 时禁止修改时间与商品 |
| 库存划转 | 场次内添加或修改商品时执行库存划转，见 5.16 |

### 5.16 场次内添加秒杀商品

**接口**：`POST /gateway/marketing/seckill-sessions/{id}/Items`
**权限点**：`seckill:create` / `seckill:update`
**表**：`seckill_item`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| SessionId | long | 是 | — | 只读（行数据带出） | 所属场次 |
| SpuId | long | 是 | — | 下拉已上架商品 | 商品 |
| SkuId | long | 是 | — | 下拉该 SPU 的启用 SKU，随 SpuId 联动；**必须指定 SKU，不能只到 SPU** | 秒杀价与库存都按 SKU 维度 |
| SeckillPrice | decimal(18,2) | 是 | — | 大于 0；两位小数；**必须小于该 SKU 的售价** | 秒杀价 |
| SeckillStock | int | 是 | — | 大于等于 1；**必须小于等于该 SKU 当前可用库存** | 保存时从常规库存划出 |
| PerUserLimit | int | 是 | 1 | 1 ~ 10 | 每人每场次限购 |
| SortOrder | int | 否 | 0 | 大于等于 0 | 列表排序 |

**服务端维护的只读字段**：

| 字段 | 说明 |
|---|---|
| Id | 雪花 Id |
| SoldCount | 已售数量，只能由抢购流程增加，后台不可手工改 |
| Status | 1 启用 / 2 停用 |
| ProductName / SkuSpecText | 冗余返回，便于辨认 |

**约束**：

| 项 | 规则 |
|---|---|
| 库存划出 | 保存成功即执行：Available 减 SeckillStock，写 StockFlow 动作 seckill_reserve，转入 seckill_item 的 SeckillStock |
| 库存不足 | SeckillStock 大于 Available 时保存失败并提示可售库存数量，**不产生部分划转** |
| 减少库存 | 已划出的库存不能通过编辑减少，只能把该商品移出场次并回补 |
| 移除商品 | 从场次移除时立即回补剩余库存（SeckillStock 减 SoldCount）到 Available |
| 与优惠关系 | 秒杀行不再叠加普通活动；可用券（订单级一张）；可用积分（最后一道，上限 100%） |

### 5.17 场次结束 / 中止

**接口**：`/gateway/marketing/seckill-sessions/{id}/End`、`/Cancel`
**权限点**：`seckill:end`

| 操作 | 字段 | 校验规则 | 副作用 |
|---|---|---|---|
| 结束 | SessionId | Status 为 10 或 20 | Status 置 30；回补剩余库存；发布 seckill.session.ended |
| 中止 | SessionId、Remark | Status 为 10 或 20；Remark 必填，2-200 字符 | Status 置 40；**立即回补剩余库存**；发布 seckill.session.ended |

**幂等**：已结束或已取消的场次重复调用返回「场次已结束」，**不重复回补库存**。

### 5.18 建号

**接口**：`POST /gateway/users/Create`
**权限点**：`user:create`
**服务**：UserService → `simpleshopuser.app_user`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| UserName | string(64) | 是 | — | 3-64 字符；trim；全局唯一 | 登录名 |
| Password | string | 是 | — | 至少 8 位且含字母与数字 | 只传哈希入库 |
| Phone | string(20) | 是 | — | 手机号正则；trim；全局唯一 | 手机号 |
| Email | string(128) | 否 | 空 | 邮箱正则 | 邮箱 |
| NickName | string(64) | 是 | — | 1-64 字符；trim | 昵称 |
| Avatar | string(512) | 否 | 空 | URL 长度校验 | 头像 |
| TenantType | int | 是 | — | 1 平台 / 2 商户；禁止选 customer | 租户类型 |
| PlatformId | long | 平台账号必填 | 0 | 下拉平台；超管填 0 | 所属平台 |
| MerchantId | long | 商户账号必填 | 0 | 下拉商户，随 PlatformId 联动 | 所属商户 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | 状态 |
| RoleIds | long[] | 是 | — | 至少选 1 个 | 角色多选，写入 user_role |

**约束**：

| 项 | 规则 |
|---|---|
| 角色必选 | 至少一个角色。不选角色则登录后无任何后台权限（fail-closed） |
| 作用域校验 | 所选角色的 AllowedScopes 必须与 TenantType 匹配（1 平台角色只给平台账号，2 商户角色只给商户账号，3 两者皆可）。校验在**权限中心**做（角色表在那里），并在**插库之前**预检一次：建号是「先插账号再绑角色」两步，绑定失败不回滚账号，只在绑定那步拦会返回「创建成功」而账号其实没角色 |
| 租户锁定 | 平台账号只能建本平台账号；商户账号只能建本商户账号；超管（`PlatformId = 0`）可建任意 |
| 租户锁定的执行位置 | **Handler**，读网关注入的租户头。网关的 RBAC 只看权限点，而内置管理员角色绑定了全部权限点，所以只靠网关拦不住「平台账号建一个 `platformId=0` 的账号再绑 platform-admin」这条提权路径 |
| 无租户身份 | 匿名 / 客户令牌 / 直连服务端口（没有 `X-Claim-*`）一律拒绝，因为此时无法回答「这个人能管哪个租户」 |

### 5.19 改号

**接口**：`POST /gateway/users/Update`
**权限点**：`user:update`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| UserId | long | 是 | — | 只读 | 账号 Id |
| UserName | string(64) | 否 | 原值 | 3-64 字符；改后仍需全局唯一 | 改登录名 |
| Phone | string(20) | 否 | 原值 | 手机号正则；改后仍需全局唯一 | 手机号 |
| Email | string(128) | 否 | 原值 | 邮箱正则 | 邮箱 |
| NickName | string(64) | 否 | 原值 | 1-64 字符 | 昵称 |
| Avatar | string(512) | 否 | 原值 | URL 长度校验 | 头像 |
| Status | int | 否 | 原值 | 1 启用 / 2 停用 | 停用只挡新登录，已签发令牌仍有效到过期 |
| RoleIds | long[] | 否 | 原绑定 | 至少 1 个 | 全量覆盖：传了就重建该账号的全部绑定 |

**注意**：改号接口不接收也不允许修改 `TenantType` / `PlatformId` / `MerchantId` / `Password`。
租户身份变更走「停用旧号 + 新建」，密码变更走 5.20。

**语义**：除 `UserId` 外全部字段都是「传了才改，不传保持原值」；`RoleIds` 传 `null` 表示不动绑定，
传数组（哪怕只 1 个）就是全量覆盖。改号同样受 5.18 的**租户锁定**约束：
目标账号不在调用方范围内时返回 **404**（不是 403），避免泄露账号是否存在（TEST_CASES 6.2）。

### 5.20 重置密码

**接口**：`POST /gateway/users/ResetPassword`
**权限点**：`user:update`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| UserId | long | 是 | — | 只读 | 账号 Id |
| Password | string | 是 | — | 至少 8 位且含字母与数字 | 新密码；不需要旧密码，后台直接设置 |

**副作用**：写 `PasswordHash`，并把该账号在 Redis 的登录会话主动踢下线。

**踢下线的实现**（原来只有这句话、没有机制，属于「文档里有、代码里没有」）：

| 项 | 规则 |
|---|---|
| 写入方 | UserService 在重置密码成功后写 `auth:revoked:{userId}` = 当前 Unix 秒，TTL 24 小时 |
| 读取方 | 网关在**每个后台令牌请求**上读该键，与令牌的 `iat` 比对 |
| 判定 | `iat <= 吊销时刻` 即拒绝（401）。用 `<=` 而不是 `<`：`iat` 只精确到秒，同秒的令牌存在歧义，安全控制取严格的一侧；代价是「重置后同一秒内重新登录」会拿到一个立刻失效的令牌，再登一次即可 |
| 存储位置 | **共享库**（`Redis:SharedDatabase`，默认 0）。各服务的 Redis 库是独占的，写错库号的表现是「接口返回成功、旧令牌照样能用」，属于静默失败 |
| 存储不可用 | 读不到吊销状态时网关回 **503**（fail-closed），不伪装成 401 |
| 生效范围 | 只影响后台令牌；客户令牌的吊销语义归 CustomerService，网关不越界 |
| 不做的事 | 停用账号（5.19 Status=2）**不**吊销存量令牌，只挡新登录（REVIEW P2 风险 18） |

### 5.21 角色编辑

**接口**：`/gateway/permissions/roles/*`
**权限点**：`permission:read` / `create` / `update` / `delete`
**表**：`role` + `role_permission`

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| RoleName | string(64) | 是 | — | 1-64 字符；trim；全局唯一 | 角色名 |
| RoleCode | string(64) | 是 | — | 全局唯一；创建后建议不改 | 角色编码 |
| AllowedScopes | int | 是 | — | 1 平台 / 2 商户 / 3 两者 | 权限层级 |
| DataScope | int | 否 | 1 | 1 本级 / 2 本级及下级 | 数据范围 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | 状态 |
| Remark | string(512) | 否 | 空 | 最多 512 字符 | 备注 |

权限点按树结构组织，层级见下方说明。

**树的层级**（4 层，业务规则见 `BUSINESS.md` 5.4）：

| 层级 | 内容 |
|---|---|
| 根节点 | 全部权限（虚拟节点） |
| 第 1 层 | 业务大类 5 个：系统管理、商品中心、交易管理、营销中心、数据报表 |
| 第 2 层 | 功能模块 23 组：账号、客户、角色权限、平台、商户、地区地址、分类、商品、库存、订单、支付、退款、营销活动、券、营销配置、限时抢购、积分、评价、装修、报表、搜索索引、文件、日志 |
| 第 3 层 | 权限点（叶子） |

保存时提交 `PermissionIds`（**叶子权限点** Id 数组）。半选的父节点不保存。

**「全部权限」按钮**：

| 项 | 规则 |
|---|---|
| 形态 | 树根节点上的按钮，勾选即全选 |
| 语义 | 纯 UI 快捷方式。存储上仍存**全部叶子权限点**，**不引入星号特殊值** |
| 勾选后 | **整棵树禁用（只读）**，取消勾选才恢复可编辑 |
| 与 AllowedScopes | 正交。它只管权限点集合，租户范围由 AllowedScopes 决定 |
| 状态判定 | 树加载时若已勾选的叶子数等于全部叶子数，根节点显示为选中 |

**多级勾选联动**：

| 项 | 规则 |
|---|---|
| 勾父节点 | 级联勾中其下全部启用状态的叶子 |
| 勾子节点 | 父节点选中状态由子节点反算：全选=实心、半选=横线、未选=空 |
| 子节点被停用 | 父节点自动变半选，标题旁标注「含 n 项不可用」 |

**内置管理员角色**：

| 角色 | 处理 |
|---|---|
| `platform-admin` / `merchant-admin` | **禁止编辑权限**。树区域只读禁用，保存时忽略传入的 PermissionIds |
| 界面提示 | 「内置管理员角色不可修改权限」 |
| 删除 | 内置角色**不可删除** |
| 权限来源 | 初始化时直接绑定全部权限点，不通过界面配置 |

**约束**：重绑权限为物理删除后重建（`role_permission` 有唯一约束，不做原地更新）。
权限在登录时解析进令牌，改完角色权限需**重新登录**才生效。

### 5.22 权限点管理

**接口**：`/gateway/permissions/*`（树形读取与增删改）
**权限点**：`permission:read` / `create` / `update` / `delete`
**表**：`permission` + `role_permission`

> **只允许超级管理员（平台之上角色，`PlatformId = 0`）操作**。网关与 Handler 双重校验，非超管一律 403。

**权限点实体字段**：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| Name | string(64) | 是 | — | 1-32 字符；trim；**同一父节点下唯一** | 中文名，如「用户列表」 |
| Code | string(64) | 是 | — | 正则 `^[a-z][a-z0-9-]*:[a-z][a-z0-9-]*$`；**全局唯一** | 权限编码，如 `user:read` |
| ApiPath | string(512) | 是 | — | 必须以 `/gateway/` 开头；支持精确路径与 `/*` 结尾的前缀路径；多个用逗号分隔 | 绑定的接口路径 |
| ParentId | long | 是 | — | 必须是存在的节点；**禁止选叶子节点作为父** | 父节点 |
| SortOrder | int | 否 | 0 | 大于等于 0 | 同级排序，支持拖拽 |
| Description | string(512) | 否 | 空 | 最多 512 字符 | 说明 |
| Status | int | 是 | 1 | 1 启用 / 2 停用 | 停用后网关不再校验该路径 |
| IsBuiltin | bool | 否 | false | **新建时固定 false**；内置标记不可改 | 是否内置权限点 |

**树节点返回结构**（供前端渲染）：

| 字段 | 说明 |
|---|---|
| Id / Name / Code / ApiPath / ParentId / SortOrder / Status / IsBuiltin / Description | 权限点自身字段 |
| HasChildren | 是否有子节点，用于是否显示展开箭头 |
| Checked | 当前角色是否已勾选 |
| Indeterminate | 半选状态 |
| Disabled | 是否不可勾选（内置管理员角色只读 / 自身停用） |

**增删改规则**：

| 操作 | 规则 |
|---|---|
| 新增 | 超管可建，IsBuiltin 固定 false；建完刷新网关 RBAC 缓存 |
| 编辑 | 改 Code 时角色绑定不变（绑定的是 Id）；改 ApiPath 立即生效 |
| 删除（内置） | **禁止删除**，只能停用。弹窗提示「内置权限点不可删除」 |
| 删除（非内置） | 需先从所有角色解绑，或改为停用。删除前**二次确认并列出受影响角色名** |
| 删除级联 | **级联删除**该权限点在 role_permission 的全部绑定 |
| 停用 | 网关立即不再校验该路径；树中该节点禁用并让父节点半选 |
| 缓存 | 任何增删改后**必须立即失效网关 RBAC 缓存**（原本 30 秒） |
| 生效时机 | 权限已解析进令牌，**不影响已登录会话**，需重新登录 |

### 5.23 发货 / 备货完成

**接口**：`POST /gateway/orders/Ship`
**权限点**：`order:ship`
**服务**：OrderService → `simpleshoporder.shipment` + `shipment_item`

按订单行的 `DeliveryType` **动态渲染**表单：

| DeliveryType | 表单字段 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| 1 实物快递 | LogisticsCompany | 是 | 字典下拉，可搜索；允许管理员新增 | 物流公司 |
| 1 实物快递 | TrackingNo | 是 | 非空；trim；最多 50 字符；**不校验格式** | 运单号 |
| 2 虚拟商品 | 无 | — | — | **前端不渲染物流字段，也不提交** |
| 3 实物自提 | 无 | — | — | 按钮文案为「备货完成」，**前端不提交物流字段** |

**只读展示**：订单号、商品名、规格文本、金额、客户昵称与脱敏手机号、收货地址。

**约束**：

| 项 | 规则 |
|---|---|
| 前置状态 | 订单状态必须为 20 待发货 |
| 部分发货 | 不支持。订单内所有行一次性发货 |
| 自提副作用 | 状态转 40 待取货，并生成取货码：`RSA(服务端私钥, 订单号)` 的 Base64（约 344 字符）。**不是另编的 16 位短码**——用户明确要求「取货码直接使用订单号生成，但要 RSA 加密，加解密全在服务端」 |
| 快递与虚拟副作用 | 状态转 30 待收货 |
| 已发货 | 重复提交返回「订单已发货」，不重复生成发货单 |

**物流公司字典**：独立小表 `logistics_company`，内置常用物流公司；后台支持新增（复用 `inventory:update` 级别的权限，建议单独给 `order:ship`）。

### 5.24 自提取货核销

**接口**：`GET /gateway/orders/Pickup/{code}`（预览）、`POST /gateway/orders/Pickup`（确认）
**权限点**：`order:pickup`

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| PickupCode | string(512) | 是 | 非空；trim；Base64；**扫码枪 / 二维码扫码为主** | 取货码。长度是 RSA 密文的 Base64（2048 位密钥下约 344 字符），手输不现实，C 端必须给二维码 + 一键复制 |

**核销流程**：

| 步骤 | 动作 |
|---|---|
| 1 | 输入或扫描取货码，调预览接口 |
| 2 | **服务端用私钥解密取货码还原出订单号**，再按订单号查单；解不开（乱码 / 别家密钥 / 被篡改）一律返回「取货码无效」，且**不泄露该订单是否存在** |
| 3 | 校验订单状态为 40 待取货；已核销返回「该订单已取货」 |
| 4 | 展示订单信息供二次确认 |
| 5 | 确认核销 → 加订单锁 → 状态转 50 已完成 → 发 `order.completed` |

**预览返回（只读）**：订单号、客户昵称与脱敏手机号、商品名、规格文本、实付金额、备货时间。

**约束**：C 端不提供核销入口；验签与查单**全部在服务端**，前端零加密运算。

### 5.25 退款审批

**接口**：`POST /gateway/refunds/{id}/Approve`、`/Reject`
**权限点**：`approve` / `reject` 为 `refund:approve` / `refund:reject`
**服务**：PaymentService → `simpleshoppayment.refund_order` + `refund_order_item`

**审批通过**：

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| RefundId | long | 是 | 只读 | 退款单 Id |

**审批拒绝**：

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| RefundId | long | 是 | 只读 | 退款单 Id |
| RejectReason | string(500) | 是 | 2-200 字符 | 拒绝原因 |

**只读展示**：退款单号、订单号、平台名、商户名、客户昵称、逐行商品名与规格、申请金额、累计已退金额、可退余额、申请原因、申请时间。

**约束**：

| 项 | 规则 |
|---|---|
| 前置状态 | 退款单状态必须为 10 待审批 |
| 金额上限 | 审批时再校验一次「累计退款 ≤ 实付（含运费）」，防止并发超额 |
| 幂等 | 重复审批返回「该退款单已处理」 |
| 通过副作用 | 退款单转已退款，订单转 60，库存回补，积分回收，发 `payment.refunded` |
| 运费 | 部分退款不退运费 |

### 5.26 客户申请退款（后台代客发起）

**接口**：`POST /gateway/payments/Refund`
**权限点**：`refund:apply`

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| OrderId | long | 是 | 只读 | 订单 Id |
| Items | RefundItem[] | 部分退款必填 | 至少 1 行；每行含 OrderItemId 与金额 | 退款的订单行与金额 |
| Items[].Amount | decimal(18,2) | 是 | 大于 0；两位小数 | 该行退款金额 |
| Reason | string(500) | 是 | 2-200 字符 | 退款原因 |

**退款窗口校验（服务端按 DeliveryType 判定）**：

| DeliveryType | 允许退款的状态 |
|---|---|
| 2 虚拟商品 | 仅 20 待发货、30 待收货；**签收后不可退款，含部分退款** |
| 1 实物快递 / 3 实物自提 | 20、30、40、50 全程可退 |

**金额约束**：各行金额合计不得超过该行可退余额；订单级累计不得超过实付（含运费）。
**可自定义金额**：允许只退部分行或某行的部分金额。

### 5.27 评价管理（隐藏 / 回复）

**接口**：`/gateway/evaluates/*`
**权限点**：`evaluate:read` / `evaluate:manage`（隐藏）/ `evaluate:reply`（回复）
**服务**：EvaluateService → `simpleshopevaluate.evaluate` + `evaluate_sku_ref`

**隐藏评价**：

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| EvaluateId | long | 是 | 只读 | 评价 Id |
| IsHidden | bool | 是 | — | 置 true 即隐藏 |
| HiddenReason | string(500) | 隐藏时必填 | 2-200 字符 | 隐藏原因，记入后台审计；C 端不可见 |

**回复评价**：

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| EvaluateId | long | 是 | 只读 | 评价 Id |
| AppendId | long | 否 | 0 表示回复首评；非 0 时**必须是该评价自己的追评** | 被回复的追评 |
| ReplyContent | string(1000) | 是 | 2-500 字符 | 回复内容 |
| ReplyType | int | 是 | 1 商户回复 / 2 平台回复；**商户账号只能传 1**（传 2 返回 403） | 回复主体。平台账号（含超管）两种都可以：平台是更高一级的管理方，代商户回复是真实运营场景，而它冒充的是下级，不构成信任提升 |

**约束**：

| 项 | 规则 |
|---|---|
| 回复次数 | 每条评价（含追评）商户可回复 1 次、平台可回复 1 次 |
| 回复不可编辑 | 只能追加，不能修改已发回复 |
| 回复主体校验 | 由**服务端按登录账号的租户类型**判定，不采信请求体：商户账号传 `replyType = 2` 会被 403 拒掉。不校验的话，商户能把自家回复伪装成平台官方回复，而用户会把「平台官方」当可信来源 |
| 追评归属校验 | `AppendId` 必须属于该评价，否则 400。不校验的话可以把回复挂到**别人评价的追评**下面 |
| 隐藏幂等 | 重复隐藏返回成功，不重复写审计 |
| 已隐藏 | 隐藏后不再展示，**不物理删除** |

**只读展示**：商品名、SPU 名、规格清单文本、星级、内容、图片、匿名标记、订单号、是否已退款标记、回复内容与回复人。

### 5.28 模拟支付

**接口**：`POST /gateway/payments/Simulate`
**权限点**：`order:simulate`

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| OrderNo | string(32) | 是 | 只读（订单行带出） | 订单号 |
| Success | bool | 是 | — | 支付成功 / 支付失败，二选一 |

**入口**：后台订单列表每行的「模拟支付」按钮，弹窗二选一。

**约束**：

| 项 | 规则 |
|---|---|
| 前置状态 | 订单状态必须为 10 待支付 |
| 成功 | 复用确认逻辑 → 置已支付 → 发 `payment.succeeded` |
| 失败 | 订单**保持 10 待支付**，不关单，可重新发起 |
| 总开关 | 受 AgileConfig `Payment:SimulateEnabled` 控制，默认 true；为 false 时按钮隐藏且接口拒绝 |

### 5.29 平台装修（拖拽搭建器）

**接口**：`GET /gateway/platform-configs/Design/{platformId}`、`/SaveDraft`、`/Publish`
**权限点**：`design:read` / `design:update`
**服务**：MerchantPlatformService → `simpleshopmerchant.platform_app_config`
**配置结构**见 `BUSINESS.md` 16.5（`theme` / `tabBar` / `pages` / `regions`）
**页面归属与组件库**见 `BUSINESS.md` 16.3 与 16.4

**通用操作**：

| 操作 | 接口 | 说明 |
|---|---|---|
| 读取 | `GET .../Design/{platformId}` | 返回草稿；若无草稿返回已发布版本 |
| 保存草稿 | `POST .../SaveDraft` | 写入草稿，不影响线上 |
| 发布 | `POST .../Publish` | 草稿转已发布版本，version 加 1；小程序只读已发布版本 |

**装修页布局**：

| 区域 | 内容 |
|---|---|
| 左侧组件库 | 按分类分组（布局 / 内容 / 会员 / 店铺 / 我的 / 功能），**按当前页面过滤可用组件** |
| 中间画布 | 模拟手机屏幕，**按小程序真实样式渲染**；组件可拖入、上下移动、调整宽度与高度 |
| 右侧属性面板 | 选中组件后编辑其私有配置 |

**可搭建页面**：**首页（index）**、**我的页（profile）**。商城页与其他页面是固定模板，不进画布。

**画布与组件的通用字段**：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| id | string | 是 | 自动生成 | 画布内唯一；32 位内 | 组件实例标识 |
| type | string | 是 | — | 必须是组件库中已注册的类型 | 组件类型 |
| span | int | 是 | 12 | **只能取 1 / 2 / 3 / 4 / 6 / 12** | 占用栅格列数（12 列栅格） |
| height | int | 否 | 按组件默认 | 大于等于 40；上限 1000 | 固定高度（px） |
| props | object | 是 | — | 由各组件的 Validator 校验 | 组件私有配置 |

**组件库清单**（`type` 取值）：

| 分类 | 组件 |
|---|---|
| 布局 | banner 轮播图、kingKong 金刚区宫格、categoryNav 分类导航、productGrid 商品双列网格、productScroll 商品横向滑动、couponZone 优惠券专区、activityZone 活动专区、seckillZone 秒杀专区、shopList 店铺列表 |
| 内容 | imageText 图文广告、notice 公告栏、title 标题栏、divider 分割线、spacer 留白 |
| 会员 | memberCard 会员问候卡、benefits 权益行 |
| 店铺 | shopHeader 店铺头、shopActivity 店铺活动、shopCategory 店铺分类、shopEvaluate 店铺评价 |
| 我的 | serviceGrid 服务宫格 |
| 功能 | searchBar 搜索框、cartFloat 购物车浮标 |

**组件按页面过滤**：首页可用全部通用组件；我的页额外可用会员与服务宫格组件；店铺页仅可用店铺类与通用组件。

**`productGrid` 商品网格的 props 字段**（最常被手动指定商品的组件）：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| source | int | 是 | 1 | **1 自动（按分类取）/ 2 手动指定** | 商品来源 |
| categoryId | long | source 为 1 时必填 | — | 下拉分类树 | 自动取数用的分类 |
| manualProductIds | long[] | source 为 2 时必填 | — | **1 ~ 20 个**；每个必须是**本平台（平台装修）或本商户（商户装修）且 `AuditStatus = 20` 且 `Status = 1`**；任一不满足**整单保存失败** | 手动指定的商品 Id 列表 |
| sortBy | int | 否 | 1 | 1 默认 / 2 销量 / 3 价格升 / 4 价格降 / 5 上新 | 排序方式 |
| columns | int | 否 | 2 | **只能取 2 或 3** | 列数 |

**店铺轮播图 `banner` 的 props 字段**：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| banners | BannerItem[] | 是 | — | **1 ~ 5 张** | 轮播图列表 |
| banners[].image | string(512) | 是 | — | URL 长度校验 | 图片 |
| banners[].title | string(64) | 否 | 空 | 最多 64 字符 | 标题 |
| banners[].linkType | string(32) | 否 | `none` | 必须是 `linkType` 枚举内的值 | 跳转类型 |
| banners[].linkParam | string(256) | 否 | 空 | 最多 256 字符 | 跳转参数 |

**商品可见性的两处校验**：

| 时机 | 行为 |
|---|---|
| 配置时（后端） | 校验 `manualProductIds` 全部满足「归属正确 + 审核通过 + 已上架」，不满足**整单保存失败**并返回不满足的商品 |
| 展示时（C 端） | 渲染前**再过滤一次**，跳过已下架、已软删、审核被撤回的商品；过滤后为空则**整个组件不渲染** |

后台装修页对已下架的手动商品**标灰并提示「已下架」**，但不自动移除。



**TabBar 配置**（不进画布，单独编辑）：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| tabBar | TabItem[] | 是 | — | 至少 2 项、最多 5 项；pagePath 不重复 | 底部导航 |
| tabBar[].pagePath | string(128) | 是 | — | 必须是已注册的页面路径 | 页面路径 |
| tabBar[].text | string(16) | 是 | — | 1-8 字符 | 文案 |
| tabBar[].iconPath | string(512) | 是 | — | 必须是 PNG | 图标（微信小程序要求 PNG） |

**主题色配置**（不进画布）：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| theme.primary | string(16) | 否 | `#0071e3` | 十六进制颜色 | 主色 |
| theme.tabColor | string(16) | 否 | `#0071e3` | 十六进制颜色 | TabBar 选中色 |
| theme.background | string(16) | 否 | `#f5f5f7` | 十六进制颜色 | 页面背景色 |

主题色写入 CSS 变量**覆盖 `DESIGN_SPEC.md` 的默认 token**。可配置的颜色**仅限这三个**，其余颜色由设计规范固定。

**上传框统一规范**：固定 120×120，悬浮玻璃层显示「更换图片」，右上角删除角标。

**手机实时预览**：

| 项 | 规则 |
|---|---|
| 实现方式 | 后台内嵌 iframe 加载商城 H5 预览页 |
| 传参 | 用 postMessage 把当前草稿配置推给预览页，预览页即时渲染 |
| 平台上下文 | 预览时忽略小程序的平台锁定，取消息里的 platformId 渲染 |

**配置 JSON 校验**：合法 JSON；非空对象；总长度不超过 100KB。

**跳转类型 linkType 统一枚举**：

金刚区、轮播图、首页模块可用取值：
`product` 商品列表、`category` 分类页、`cart` 购物车、`order` 我的订单、`address` 收货地址、`coupon-center` 领券中心、`coupons` 我的券包、`favorites` 我的收藏、`seckill` 秒杀频道、`none` 不跳转。

我的服务宫格在上述取值之外，另加：
`points` 积分中心、`evaluate` 我的评价、`profile` 刷新资料、`support` 联系客服。

### 5.30 商户装修（店铺页拖拽搭建器）

**接口**：`GET /gateway/merchant-configs/Design/{merchantId}`、`/SaveDraft`、`/Publish`
**权限点**：`design:merchant`
**服务**：MerchantPlatformService → `simpleshopmerchant.merchant_app_config`

与平台装修**互不可见、无继承关系**：

- 配置范围：**仅该商户的店铺页（store）**
- 与平台装修关系：**独立配置**。商户装修不继承平台装修，商户账号也**看不到**平台装修
- **能力**：**拥有完整的拖拽搭建器**——可拖拽组件、调整顺序与宽高、**可改组件内容**
- 可用组件：**商户自己的 11 个店铺类组件**（清单见下），与平台组件库互不干扰
- **不可改**：**任何配色**（全局主题与组件内部颜色全部继承平台）、tabBar、首页、我的页、地区地址
- 草稿与发布：与平台一致，保存草稿 → 发布时 version 递增；C 端店铺页只读已发布版本
- 装修器：**复用平台装修的同一套搭建器与组件渲染器**，只过滤可用组件并锁定页面为 store

**商户可用组件（11 个）**：

| 分类 | 组件 |
|---|---|
| 店铺专属 | `shopHeader` 店铺头、`shopActivity` 店铺活动、`shopCategory` 店铺分类、`shopEvaluate` 店铺评价、`productGrid` 商品网格 |
| 内容与通用 | `banner` 店铺轮播图、`imageText` 图文广告、`notice` 公告栏、`title` 标题栏、`divider` 分割线、`spacer` 留白 |

**不可用**：会员卡、权益行、服务宫格、金刚区、分类导航、商品横向滑动、优惠券专区、活动专区、秒杀专区、店铺列表、搜索框、购物车浮标。


**配置字段**：

| 字段 | 类型 | 必填 | 默认值 | 校验规则 | 说明 |
|---|---|---|---|---|---|
| MerchantId | long | 是 | — | 只读；**锁定本商户**，商户账号前端隐藏 | 所属商户 |
| PlatformId | long | 是 | — | 只读，随商户带出 | 所属平台 |
| version | int | 否 | 0 | 服务端维护 | 发布版本号 |
| pages.store.components | Component[] | 是 | — | 合法 JSON 数组；组件规则见 5.29 | 店铺页组件列表 |

**店铺页默认组件**（新建商户时写入草稿）：`shopHeader` 店铺头、`shopActivity` 店铺活动、`shopCategory` 店铺分类、`productGrid` 商品网格、`shopEvaluate` 店铺评价。

**约束**：

- 不可配项：tabBar、全局主题色、商城名称、首页与我的页，**商户一律不可改**
- 商品范围：`productGrid` 等取本商户商品（AOP 按 `MerchantId` 自动过滤，前端不传商户参数）；**手动指定商品时只能选本商户已审核通过且已上架的商品**，详见 5.29
- 删除商户：商户软删时其装修配置一并软删
- **颜色**：属性面板里**不渲染任何颜色选择器**。后端在保存时**剔除**组件 props 中的颜色字段，收到即丢弃并记警告日志
- **店铺轮播图**：**最多 5 张**，每张含图片、标题、跳转类型与参数，**商户可自由修改**（与平台首页轮播图无关）
- 可改内容：组件的图片、文案、跳转类型与参数、商品范围**均可改**；`span` 与 `height` 可改

### 5.31 地区地址配置

**接口**：`GET /gateway/platform-configs/Regions?platformId=`、`POST .../SaveRegions`
**权限点**：`region:read` / `region:update`

| 字段 | 类型 | 必填 | 校验规则 | 说明 |
|---|---|---|---|---|
| RegionsJson | string | 否 | 合法 JSON；非空数组；每级必须含 name 字段；不超过 2MB | 三级地区数据 |

**三个操作按钮**：

- **加载当前数据**：读取 RegionsJson；为空时展示内置默认（31 省 / 342 市 / 3056 区县）
- **保存自定义**：校验通过后写入 RegionsJson
- **恢复默认**：提交空字符串，清空 RegionsJson，回落到内置默认；需二次确认

**只读展示**：省 / 市 / 区县数量统计、状态标签（内置默认 / 平台自定义）。

**存储约定**：空表示使用内置默认，不落库 81KB 冗余数据；内置默认数据随程序发布，不入库。

### 5.32 「仅后端验证」的字段清单

下列判断**需要查库**，前端不做校验；提交后由后端返回错误，前端用**全局 tip** 提示（不飘红输入框）。

| 表单 | 字段 | 后端校验内容 |
|---|---|---|
| 5.1 平台 | PlatformName | 全局唯一 |
| 5.1 平台 | PlatformCode | 全局唯一（6 位字母的正则前后端都验） |
| 5.2 商户 | MerchantName | 同一平台内唯一 |
| 5.2 商户 | PlatformId | 必须是启用状态的平台 |
| 5.4 分类 | CategoryName | 同一父节点下唯一 |
| 5.4 分类 | ParentId | 父分类必须存在；父子层级 + 1 ≤ 3，超三级直接拒绝 |
| 5.5 品牌 | BrandName | 全局唯一 |
| 5.6 商品 | CategoryId | 必须是存在的第 3 级（叶子）分类 |
| 5.7 SKU | SkuCode | 全局唯一（Upsert 的依据） |
| 5.7 SKU | Specs 组合 | 规格组合不得重复；必须覆盖 SPU 全部规格项 |
| 5.10 库存 | AvailableAdjust | 调整后可用库存不得小于 0 |
| 5.11 活动 | Targets | **商户级活动**（`MerchantId > 0`）：目标 SPU / SKU 必须存在且属于本商户，任一不满足整单拒绝并列出原因（`POST /internal/products/check-targets`）。**平台级活动不做归属校验**（见 5.11 约束） |
| 5.12 券模板 | TemplateName | 全局唯一 |
| 5.13 券活动 | ClaimQuantity | 不得超过模板剩余可发量 |
| 5.16 秒杀商品 | SeckillStock | 必须小于等于该 SKU 当前可用库存 |
| 5.18 建号 | UserName / Phone | 全局唯一 |
| 5.18 建号 | RoleIds | 角色作用域必须与 TenantType 匹配 |
| 5.21 角色 | RoleName / RoleCode | 全局唯一 |
| 5.22 权限点 | Name / Code | 同父节点下中文名唯一；Code 全局唯一 |
| 5.26 退款申请 | Items 金额 | 累计退款 ≤ 实付（含运费）；各行金额 ≤ 该行可退余额 |
| 5.27 评价管理 | ReplyContent | 每条评价商户 1 次、平台 1 次 |
| 5.29 / 5.30 装修 | components | 每个组件的 type 必须在组件库注册表中存在 |
| 5.29 装修 | manualProductIds | 必须全部满足「归属正确（平台装修=本平台 / 商户装修=本商户）+ 审核通过 + 已上架」，**任一不满足整单保存失败**并返回不满足的商品 |

**其余字段**（长度、格式、数值范围、日期先后、必填）**前后端都验证**。

---

## 6. 一致性检查清单

新增或修改任一表单后逐项核对：

| # | 检查项 |
|---|---|
| 1 | 字段表与 BUSINESS.md 的业务规则不冲突；有冲突以 BUSINESS.md 为准并回头改本文 |
| 2 | 每个写入口都有 FluentValidation Validator，且与字段表的校验规则列逐条对齐 |
| 3 | 每个字段都在实体上有 XML 注释（`CODING_STANDARD.md` 5.0 强制） |
| 4 | 状态类字段的注释列全所有状态码 |
| 5 | 下拉字段的数据来源能在 4.2 的下拉接口清单里找到；找不到就补接口 |
| 6 | 列表与详情返回的展示字段在 4.3 清单里，且含名称冗余 |
| 7 | 权限点已在 BUSINESS.md 5.2 的清单中登记 |
| 8 | 新增表或字段已写入 deploy/sql/ 初始化脚本（幂等可重跑） |
| 9 | 影响链路的改动已同步 REVIEW.md |
| 10 | 涉及资金、库存、积分的字段，其一致性口径已写进 BUSINESS.md |
