# SimpleShop

> 多平台（多租户）微服务电商系统
> Multi-tenant microservices e-commerce platform

---

# 中文

## 项目简介

SimpleShop 是一个**多平台（多租户）电商系统**，目标是跑通完整交易闭环：

> 注册 → 浏览 → 加购 → 下单 → 支付 → 发货/备货 → 签收/取货 → 评价 → 退款

系统以 **平台（Platform）/ 商户（Merchant）/ 客户（Customer）** 三层角色组织数据，
每个平台拥有独立商城（独立首页装修、主题色、商城名称、入口），
同一套小程序按平台配置动态渲染出完全不同的商城界面。

## 核心能力

| 模块 | 能力 |
|---|---|
| **交易闭环** | 幂等下单、库存锁定/扣减/释放/回补、模拟支付、支付超时关单、发货/备货/签收/取货核销/取消、退款申请与审批（累计限额） |
| **营销中心** | 平台/商户活动（满减/满折/满赠/**限时抢购**）、券模板、券活动、领券中心与券包；逐商品贪心 + 券/活动互斥 + 平台优先级配置；订单优惠快照与效果报表 |
| **积分** | 注册赠送、订单签收、发表评价、**每日签到**；下单冻结抵扣（上限 100%，允许 0 元订单）、365 天有效期、FIFO 先到期先用 |
| **评价** | **SPU 级评价**并标记该订单购买的所有 SKU；1–5 星、最多 9 张图、匿名、追评、商户/平台回复；商品与店铺均分每日重算 |
| **配送与运费** | **实物快递 / 虚拟商品 / 实物自提** 三种方式；平台级固定运费 + 满额包邮；自提取货码采用 **RSA-2048 签名**，支持手输与扫码核销 |
| **多平台小程序** | 铺满头图 + 定位/评分/悬浮搜索、四宫格金刚区、Hi 会员问候卡、优惠专区、商城页（**秒杀/推荐/活动**）、店铺页、商品详情页（红色到手价）、我的页；后台**可视化拖拽装修 + 手机实时预览**，草稿 + 发布双状态 |
| **权限中心** | 角色/权限点/用户绑定，平台与商户两层权限；网关 RBAC（权限点 ↔ 接口路径），权限只认显式绑定（fail-closed） |
| **商品与库存** | SPU/SKU、三级分类、审核上下架、图片上传（魔数校验）、库存流水幂等与补偿、**Elasticsearch + IK 中文分词搜索**（不可用时自动降级） |
| **报表与日志** | 工作台经营报表、营销/券效果报表、秒杀效果报表、积分报表；PV/操作/异常日志经 RabbitMQ 入 Elasticsearch |

## 技术栈

| 类别 | 选型 |
|---|---|
| 运行时 | **.NET 10** |
| 架构 | DDD 四层（Api / Application / Domain / Infrastructure）+ `Collaboration` 公共类库 |
| 网关 | **Ocelot**（双令牌验签 + RBAC + 租户声明注入） |
| 服务通信 | **gRPC（MagicOnion / MessagePack）** + **Consul** 服务发现 |
| 事件驱动 | **RabbitMQ** |
| 配置中心 | **AgileConfig**（每服务独立配置） |
| ORM / 数据库 | **FreeSql + PostgreSQL**（AuthService 用 EF Core + OpenIddict） |
| 令牌 | **OpenIddict**（后台 RS256）/ 客户 JWT（HS256） |
| 验证 / 命令 | **FluentValidation** / **MediatR** |
| 缓存与锁 | **Redis** |
| ID | **Yitter** 雪花 Id |
| 搜索 | **Elasticsearch + IK 分词器** |
| 日志 | **EFK**（Elasticsearch + Fluentd + Kibana） |
| 文件存储 | Local / 阿里云 OSS / 腾讯云 COS / 微软云 Azure（AgileConfig 切换） |
| 管理后台 | **Vue 3 + Element Plus** |
| 商城端 | **UniApp**（H5 + 微信小程序） |
| 测试 | **xUnit** 单元测试 + PowerShell API 回归 + Playwright UI 回归 |

## 服务清单

共 **17 个后端服务**：

| 服务 | HTTP | gRPC | 数据库 | 职责 |
|---|---|---|---|---|
| Gateway | 5008 | - | - | 路由 + 双令牌验签 + RBAC + 租户声明 |
| Auth | 5019 | 5004 | simpleshopauth | 后台令牌（OpenIddict，RS256） |
| User | 5011 | 5003 | simpleshopuser | 后台账号域 |
| Customer | 5280 | 5001 | simpleshopcustomer | 前台客户域 |
| Tool | 5080 | 5081 | simpleshoptool | 工具服务（统一文件上传） |
| Permission | 5022 | 5023 | simpleshoppermission | 权限中心 |
| Product | 5058 | 5058 | simpleshopproduct | 商品 + Elasticsearch 搜索 |
| Cart | 5060 | 5060 | simpleshopcart | 购物车 |
| Inventory | 5062 | 5063 | simpleshopinventory | 库存 |
| Order | 5064 | 5002 | simpleshoporder | 订单 |
| Payment | 5066 | 5066 | simpleshoppayment | 支付与退款 |
| Marketing | 5072 | 5073 | simpleshopmarketing | 活动 / 券 / 限时抢购 / 优惠计算 |
| MerchantPlatform | 5070 | 5070 | simpleshopmerchant | 平台 / 商户 / 装修 |
| Point | 5082 | 5083 | simpleshoppoint | 积分 |
| Evaluate | 5084 | 5084 | simpleshopevaluate | 评价 |
| Scheduled | - | - | simpleshopscheduled | 定时任务 |
| Log | 5088 | - | Elasticsearch | 日志消费 |

## 快速开始

Windows 用 **PowerShell**（`scripts/*.ps1`）；Linux（Mint / Ubuntu）用 **bash**（`deploy/linux/*.sh`，含低内存档）。

| 步骤 | 命令 | 说明 |
|---|---|---|
| 1 | `./scripts/start-infra.ps1` | 拉起 PostgreSQL / Redis / Consul / RabbitMQ / AgileConfig / Elasticsearch(IK) / Kibana / Fluentd |
| 2 | `./scripts/init-database.ps1` | 建 14 个 `simpleshop*` 库 |
| 3 | `./scripts/init-tables.ps1` | 建表 + 种子数据（**SQL 脚本，幂等可重跑**） |
| 4 | `./scripts/build.ps1` | 构建 .NET 10 解决方案 |
| 5 | `./scripts/start-services.ps1` | 后台启动全部服务 |
| 6 | `./scripts/seed-data.ps1` | 生成演示数据（平台/商户/商品/活动/券/用户/订单/秒杀场次） |
| 7 | 后台 `apps/admin-vue` → build + preview `:5173` | 管理后台 |
| 8 | 商城 `apps/user-uniapp` → build + preview `:5174` | 商城 H5（微信小程序 `build:mp-weixin`） |

**Linux Mint / Ubuntu（16G 低内存档）**

```bash
cd deploy/linux
chmod +x ./*.sh && ./build.sh     # 首次：构建后端 + 前端
./start-infra.sh minimal && ./init-db.sh   # 全新设备首次：建库建表（数据卷迁移可跳过）
./start-all.sh                    # minimal 档全套（~2.4G）；需要搜索/日志面板时用 ./start-all.sh full（~4.2G）
./status.sh                       # 状态与内存占用；./stop-all.sh 全停
```

分档与内存预算、首次准备（docker 组 / `vm.max_map_count` / swap）、排障见 [`deploy/linux/README.md`](deploy/linux/README.md)。

**AgileConfig 管理台**

| 项 | 值 |
|---|---|
| 地址 | `http://localhost:5000/ui` |
| 账号 | `deploy/.env` 的 `AGILECONFIG_ADMIN_USER`（默认 `admin`） |
| 密码 | `deploy/.env` 的 `AGILECONFIG_ADMIN_PASSWORD`（该文件已 gitignore，**不入库**） |
| 首次初始化 | 部署脚本调用 `POST /admin/InitPassword` 设置初始密码；忘记密码时清空 `simpleshop_configcenter` 的 `agc_user` 后重启容器可重新初始化 |

> 管理密码只存在于 `deploy/.env`，被跟踪的文档与脚本里都不出现明文（安全约定，见 `AI_HANDOFF.md`）。
> 各微服务读取配置用的是只读应用凭据（`AGILECONFIG_APP_SECRET`），与能写配置的管理密码是两回事。

**演示账号**

| 角色 | 账号 | 密码 |
|---|---|---|
| 平台超管 | `codexadmin` | `Admin123456` |
| 商户管理员 | `demo-merchant` | `Demo123456` |
| 商户操作员 | `merchantop` | `Op123456` |
| 客户 | `demo_user_01` ~ `demo_user_08` | `Test123456` |

演示平台编码：`DEMOPL`。

## 测试

| 命令 | 范围 |
|---|---|
| `dotnet test` | xUnit 单元测试（优惠引擎、库存、状态机、积分、秒杀库存） |
| `./tests/e2e/api-regression.ps1` | API 回归（认证、RBAC、多租户、交易、营销、积分、评价、秒杀） |
| `./tests/e2e/full-chain.ps1` | 全链路（注册→下单→支付→发货→签收→评价→积分→退款） |
| `./tests/e2e/seckill-flow.ps1` | 秒杀链路 |
| `./tests/e2e/point-evaluate-flow.ps1` | 积分与评价链路 |
| `node ./tests/e2e/ui-regression.js` | UI 回归（Playwright，需 5173 + 5174 preview） |

## 项目结构

| 路径 | 内容 |
|---|---|
| `src/` | 后端微服务（每服务四层） |
| `src/Collaboration/` | 公共类库（通用仓储、模型基类、gRPC 契约、注册扩展、公共枚举与 DTO） |
| `SimpleShop.slnx` | **根解决方案**：登记全部工程，按磁盘目录自动嵌套 |
| `src/<服务名>/<服务名>.sln` | **每个微服务一个独立解决方案**（经典 `.sln` 格式）：含自己的四个分层工程 + 引用的 `Collaboration` 工程；根目录 `SimpleShop.slnx` 仍是全量入口。Rider / VS 里按文件夹打开单个服务时用这个 |
| `Gateway/` | Ocelot 网关 |
| `apps/admin-vue/` | 管理后台（Vue3 + Element Plus） |
| `apps/user-uniapp/` | 商城端（UniApp：H5 + 微信小程序） |
| `tests/e2e/` | 端到端脚本（`api-regression.ps1` / `ui-regression.js` / `visual-regression.js` / `p0-special.ps1`） |
| `tests/visual/` | 视觉回归产物：`baseline` 基线 / `current` 本次 / `diff` 差异三联图 |
| `scripts/` | PowerShell 启动与初始化脚本 |
| `deploy/` | docker-compose（AgileConfig / EFK / 自建 ES-IK 镜像） |
| `deploy/linux/` | **Linux 低内存启动脚本**（bash：start-all / start-services / start-web / stop-all / status；三档 minimal/standard/full） |
| `deploy/docker-compose.linux.yml` | Linux 低内存覆盖层（ES 512m 堆 / Kibana 384m 堆 / 容器 mem_limit 等，与基础 compose 叠加使用） |
| `deploy/sql/<service>/` | **各服务建表 DDL 与种子数据**（幂等可重跑，不用 CodeFirst） |
| `deploy/elasticsearch/` | 自建 ES 镜像 Dockerfile（装 IK 分词插件） |
| `deploy/shared/` | **校验规则单一来源**（`validation-rules.json`），构建时生成 C# 与 JS 常量 |
| `logs/runtime/` | 服务运行日志 |

测试用例文档见根目录 [`TEST_CASES.md`](TEST_CASES.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [`BUSINESS.md`](BUSINESS.md) | 业务域、角色权限、数据模型、事件与锁、**全部业务规则** |
| [`CODING_STANDARD.md`](CODING_STANDARD.md) | 四层职责、Validator 规范、**注释规范**、已知陷阱 |
| [`DATA_SPEC.md`](DATA_SPEC.md) | 启动时序、模型基类字段、FreeSql AOP 与仓储约定、**后台表单字段清单** |
| [`DESIGN_SPEC.md`](DESIGN_SPEC.md) | **苹果风设计体系**、**原始数据禁显示清单**、硬约束检查清单 |
| [`TEST_CASES.md`](TEST_CASES.md) | 测试用例：单元 / API / UI / 视觉截图 / P0 专项 / 越权矩阵 |
| [`REVIEW.md`](REVIEW.md) | 全链路执行顺序、风险审计 |
| [`AI_HANDOFF.md`](AI_HANDOFF.md) | 环境启动、协作约定、进度日志 |

---

# English

## Overview

SimpleShop is a **multi-tenant (multi-platform) e-commerce system** built to run the full transaction loop end to end:

> Register → Browse → Add to Cart → Place Order → Pay → Ship / Prepare → Confirm Receipt or Pickup → Review → Refund

The domain is organized around three roles: **Platform**, **Merchant**, and **Customer**.
Each platform owns an independent storefront (its own home-page decoration, theme color, mall name, and entry points),
and the same mini program renders a completely different mall per platform based on its published configuration.

## Features

| Module | Capabilities |
|---|---|
| **Transaction loop** | Idempotent order placement, stock lock / deduct / release / restore, simulated payment, payment-timeout auto-cancel, shipping / preparation / receipt confirmation / pickup verification / cancel, refund requests and approval (cumulative cap) |
| **Marketing** | Platform and merchant activities (spend-threshold off, percentage off, gift, **flash sale**), coupon templates, coupon campaigns, coupon center and wallet; per-item greedy matching with coupon/activity mutual exclusion and per-platform priority; order discount snapshots and effectiveness reports |
| **Points** | Registration bonus, order completion, product review, and **daily sign-in**; frozen balance applied at checkout (up to 100%, allowing 0.00 orders), 365-day expiry, FIFO consumption by earliest expiry |
| **Reviews** | **SPU-level reviews** tagged with every SKU purchased in that order; 1–5 stars, up to 9 images, anonymous, follow-ups, merchant/platform replies; product and merchant scores recomputed daily |
| **Delivery & shipping fee** | **Express / virtual goods / self-pickup**; platform-level flat shipping fee with free-shipping threshold; self-pickup codes signed with **RSA-2048**, verifiable by manual entry or QR scan |
| **Multi-platform mini program** | Full-bleed hero with location, rating and floating search, four-cell shortcut grid, member greeting card, coupon zone, mall page (**flash sale / recommended / activities**), store page, product page with red final-price, profile page; backend **visual drag-and-drop decoration with live phone preview**, draft + publish workflow |
| **Permissions** | Roles, permission points, and user bindings across platform and merchant scopes; gateway RBAC mapping permission points to API paths, fail-closed on missing binding |
| **Products & inventory** | SPU/SKU, three-level categories, audit-based listing, image upload with magic-number validation, idempotent inventory ledger with compensation, **Elasticsearch search with IK Chinese analyzer** (automatic degradation when unavailable) |
| **Reports & logging** | Workbench business reports, marketing/coupon effectiveness, flash sale effectiveness, points reports; PV/operation/exception logs streamed through RabbitMQ into Elasticsearch |

## Tech Stack

| Category | Choice |
|---|---|
| Runtime | **.NET 10** |
| Architecture | DDD with four layers (Api / Application / Domain / Infrastructure) plus a shared `Collaboration` library |
| Gateway | **Ocelot** (dual-token validation, RBAC, tenant claim injection) |
| Service communication | **gRPC (MagicOnion / MessagePack)** + **Consul** service discovery |
| Event driven | **RabbitMQ** |
| Configuration | **AgileConfig** (per-service isolated configuration) |
| ORM / Database | **FreeSql + PostgreSQL** (AuthService uses EF Core + OpenIddict) |
| Tokens | **OpenIddict** (backend, RS256) / customer JWT (HS256) |
| Validation / Dispatch | **FluentValidation** / **MediatR** |
| Cache & locks | **Redis** |
| IDs | **Yitter** snowflake IDs |
| Search | **Elasticsearch + IK analyzer** |
| Logging | **EFK** (Elasticsearch + Fluentd + Kibana) |
| Object storage | Local / Aliyun OSS / Tencent COS / Azure Blob (switched via AgileConfig) |
| Admin console | **Vue 3 + Element Plus** |
| Storefront | **UniApp** (H5 + WeChat Mini Program) |
| Testing | **xUnit** unit tests + PowerShell API regression + Playwright UI regression |

## Services

**17 backend services** in total:

| Service | HTTP | gRPC | Database | Responsibility |
|---|---|---|---|---|
| Gateway | 5008 | - | - | Routing, dual-token validation, RBAC, tenant claims |
| Auth | 5019 | 5004 | simpleshopauth | Backend tokens (OpenIddict, RS256) |
| User | 5011 | 5003 | simpleshopuser | Backend accounts |
| Customer | 5280 | 5001 | simpleshopcustomer | Customer accounts |
| Tool | 5080 | 5081 | simpleshoptool | Tool service (unified file upload) |
| Permission | 5022 | 5023 | simpleshoppermission | Permission center |
| Product | 5058 | 5058 | simpleshopproduct | Products + Elasticsearch search |
| Cart | 5060 | 5060 | simpleshopcart | Shopping cart |
| Inventory | 5062 | 5063 | simpleshopinventory | Stock |
| Order | 5064 | 5002 | simpleshoporder | Orders |
| Payment | 5066 | 5066 | simpleshoppayment | Payments and refunds |
| Marketing | 5072 | 5073 | simpleshopmarketing | Activities, coupons, flash sale, discount engine |
| MerchantPlatform | 5070 | 5070 | simpleshopmerchant | Platforms, merchants, decoration |
| Point | 5082 | 5083 | simpleshoppoint | Points |
| Evaluate | 5084 | 5084 | simpleshopevaluate | Reviews |
| Scheduled | - | - | simpleshopscheduled | Scheduled jobs |
| Log | 5088 | - | Elasticsearch | Log ingestion |

## Quick Start

All scripts are **PowerShell**.

| Step | Command | Description |
|---|---|---|
| 1 | `./scripts/start-infra.ps1` | Start PostgreSQL, Redis, Consul, RabbitMQ, AgileConfig, Elasticsearch (IK), Kibana, Fluentd |
| 2 | `./scripts/init-database.ps1` | Create the 14 `simpleshop*` databases |
| 3 | `./scripts/init-tables.ps1` | Create tables and seed data (**SQL scripts, idempotent and re-runnable**) |
| 4 | `./scripts/build.ps1` | Build the .NET 10 solution |
| 5 | `./scripts/start-services.ps1` | Start all services in the background |
| 6 | `./scripts/seed-data.ps1` | Generate demo data (platform, merchants, products, activities, coupons, users, orders, flash sale session) |
| 7 | `apps/admin-vue` → build + preview on `:5173` | Admin console |
| 8 | `apps/user-uniapp` → build + preview on `:5174` | Storefront H5 (WeChat Mini Program via `build:mp-weixin`) |

**Demo accounts**

| Role | Username | Password |
|---|---|---|
| Platform super admin | `codexadmin` | `Admin123456` |
| Merchant admin | `demo-merchant` | `Demo123456` |
| Merchant operator | `merchantop` | `Op123456` |
| Customers | `demo_user_01` ~ `demo_user_08` | `Test123456` |

Demo platform code: `DEMOPL`.

## Testing

| Command | Scope |
|---|---|
| `dotnet test` | xUnit unit tests (discount engine, inventory, state machine, points, flash sale stock) |
| `./tests/e2e/api-regression.ps1` | API regression (auth, RBAC, multi-tenancy, transactions, marketing, points, reviews, flash sale) |
| `./tests/e2e/full-chain.ps1` | Full chain (register → order → pay → ship → receive → review → points → refund) |
| `./tests/e2e/seckill-flow.ps1` | Flash sale flow |
| `./tests/e2e/point-evaluate-flow.ps1` | Points and reviews flow |
| `node ./tests/e2e/ui-regression.js` | UI regression (Playwright, requires previews on 5173 and 5174) |

## Project Layout

| Path | Contents |
|---|---|
| `src/` | Backend microservices (four layers each) |
| `src/Collaboration/` | Shared library (generic repository, base models, gRPC contracts, registration extensions, shared enums and DTOs) |
| `Gateway/` | Ocelot gateway |
| `apps/admin-vue/` | Admin console (Vue 3 + Element Plus) |
| `apps/user-uniapp/` | Storefront (UniApp: H5 + WeChat Mini Program) |
| `tests/e2e/` | End-to-end scripts (`api-regression.ps1` / `ui-regression.js` / `visual-regression.js` / `p0-special.ps1`) |
| `tests/visual/` | Visual regression artifacts: `baseline` / `current` / `diff` triptychs |
| `scripts/` | PowerShell startup and initialization scripts |
| `deploy/` | docker-compose (AgileConfig / EFK / custom ES-IK image) |
| `deploy/sql/<service>/` | **DDL and seed data per service** (idempotent, re-runnable; no CodeFirst) |
| `deploy/elasticsearch/` | Custom ES image Dockerfile (IK analyzer plugin) |
| `deploy/shared/` | **Single source of validation rules** (`validation-rules.json`), generates C# and JS constants |
| `logs/runtime/` | Service runtime logs |

See [`TEST_CASES.md`](TEST_CASES.md) in the repository root for the test case reference.

## Documentation

| Document | Contents |
|---|---|
| [`BUSINESS.md`](BUSINESS.md) | Business domains, roles and permissions, data model, events and locks, **all business rules** |
| [`CODING_STANDARD.md`](CODING_STANDARD.md) | Layer responsibilities, Validator conventions, **comment standards**, known pitfalls |
| [`DATA_SPEC.md`](DATA_SPEC.md) | Startup sequence, base entity fields, FreeSql AOP and repository conventions, **admin form field reference** |
| [`DESIGN_SPEC.md`](DESIGN_SPEC.md) | **Apple-style design system**, **forbidden raw-data display list**, hard-constraint checklist |
| [`TEST_CASES.md`](TEST_CASES.md) | Test cases: unit / API / UI / visual regression / P0 special / permission matrix |
| [`REVIEW.md`](REVIEW.md) | End-to-end request flows and risk audit |
| [`AI_HANDOFF.md`](AI_HANDOFF.md) | Environment setup, collaboration conventions, progress log |

---

## License

见 [`LICENSE`](LICENSE)。

