# AI_HANDOFF.md — AI 协作与交接文档

> 用途：新会话（或另一台机器）恢复上下文。开场说「读 AI_HANDOFF.md，按里面的进度继续」。
> 状态：**需求已确认，代码未开始**。本文件同时是协作约定与进度日志。

## 目录

1. 必读文档与分工
2. 环境与启动
3. 硬性协作约定
4. 代码现状
5. 进度日志（倒序，新条目写在最上面）
6. 历史设计决策
7. 注意事项

---

## 1. 必读文档与分工

| 文档 | 内容 | 什么时候读 |
|---|---|---|
| `BUSINESS.md` | 业务规则、角色权限、数据模型、事件与锁 | 理解需求时（**先读这个**） |
| `CODING_STANDARD.md` | 四层职责、Feature 三件套、Validator 规范、**注释规范** | 写任何代码之前 |
| `DATA_SPEC.md` | 启动时序、模型基类字段、FreeSql AOP 与仓储约定、**后台表单字段清单** | 写实体 / 仓储 / 后台表单之前 |
| `DESIGN_SPEC.md` | **苹果风设计体系**（token / 组件规范）、**原始数据禁显示清单**、硬约束检查清单 | 写任何前端页面之前 |
| `TEST_CASES.md` | **测试用例**（单元 / API / UI / 视觉截图 / P0 专项 / 越权矩阵） | 实现期照着写测试、CI 配置 |
| `REVIEW.md` | 全链路执行顺序 + 风险审计 | 改交易/库存/定时/积分/秒杀链路时 |
| 本文件 | 环境、启动、协作约定、进度 | 每次开场 |

---

## 2. 环境与启动

### 2.1 基础设施

PowerShell 一键拉起全部中间件：

```powershell
./scripts/start-infra.ps1
```

包含：`postgres` / `redis` / `consul` / `rabbitmq` / `agileconfig` / `elasticsearch`（**自建 IK 镜像**）/ `kibana` / `fluentd`。

### 2.2 数据库

**不使用 FreeSql CodeFirst**。表结构由 SQL 初始化脚本建立，脚本**幂等可重跑**：

```powershell
./scripts/init-database.ps1      # 建库（14 个 simpleshop* 库）
./scripts/init-tables.ps1         # 建表 + 种子数据（幂等）
```

### 2.3 后端（.NET 10）

```powershell
./scripts/build.ps1                        # 构建解决方案
./scripts/start-services.ps1               # 后台起全部服务
./scripts/start-services.ps1 -Service Order   # 单独起某服务
./scripts/stop-services.ps1                # 停止
```

日志落在 `logs/runtime/`。单独重启某服务时按端口找**真实进程 PID** 后再结束——
`logs/runtime/{Name}.pid` 里通常是启动包装进程，杀它可能留下旧子进程占端口。

### 2.4 前端

```powershell
cd apps/admin-vue; npm run build;  npx vite preview --host 0.0.0.0 --port 5173 --outDir dist --strictPort
cd apps/user-uniapp; npm run build:h5; npx vite preview --host 0.0.0.0 --port 5174 --outDir dist/build/h5 --strictPort
```

- 后台：http://127.0.0.1:5173
- 商城 H5：http://127.0.0.1:5174
- 微信小程序：`npm run build:mp-weixin` → 导入 `dist/build/mp-weixin`

**注意**：项目路径含 `#` 时 Vite dev-server 会白屏，**必须 build + preview**。

### 2.5 测试

```powershell
dotnet test                                  # xUnit 单元测试
./tests/e2e/api-regression.ps1               # API 回归
node ./tests/e2e/ui-regression.js            # UI 回归（需 5173 + 5174 preview）
./tests/e2e/full-chain.ps1                   # 全链路
./tests/e2e/seckill-flow.ps1                 # 秒杀链路
./tests/e2e/point-evaluate-flow.ps1          # 积分与评价链路
node ./tests/e2e/visual-regression.js        # 视觉回归（对比基线，产出 diff 三联图）
./tests/e2e/visual-baseline.ps1 -Update      # 生成或更新视觉基线（需人工 review）
./tests/e2e/p0-special.ps1                   # P0 专项
```

用例清单见 `TEST_CASES.md`（约 516 条用例、约 180 张基线图）。

### 2.6 演示账号

| 角色 | 账号 | 密码 |
|---|---|---|
| 平台超管 | `codexadmin` | `Admin123456` |
| 商户管理员 | `demo-merchant` | `Demo123456` |
| 商户操作员 | `merchantop` | `Op123456` |
| 客户 | `demo_user_01` ~ `demo_user_08` | `Test123456` |

演示平台编码 **`DEMOPL`**（平台编码规则：6 位字母，创建时用户输入）。

### 2.7 种子数据

```powershell
./scripts/seed-data.ps1        # 演示平台/商户/分类/商品/活动/券/用户/订单/秒杀场次
```

**注意**：测试脚本会创建带占位图的临时商品，跑完可清理；列表按创建时间倒排会被测试数据占据。

---

## 3. 硬性协作约定

0. **先问后写（最高优先级，凌驾其他所有约定）**：收到新需求或需求变更时，**禁止直接动手改代码或改文档**。

   | 阶段 | 动作 | 说明 |
   |---|---|---|
   | ① 理解 | 复述对需求的理解，**明确列出所有歧义点、缺失项、边界情况** | 不确定的地方必须显式点出来，不允许默默假设 |
   | ② 提问 | 分轮次提问，每轮只问**关键决策点**，并**给出推荐默认值** | 用户只需回答「按推荐」或指出要改的项，尽量减少往返次数 |
   | ③ 确认 | 自评理解度，**达到 90% 以上**才进入下一步 | 未达标继续提问，不进入写作或编码 |
   | ④ 落文档 | 把**确认后的结论**写进对应文档 | 先有文档，后有实现 |
   | ⑤ 实现 | 才开始写代码 | 需求未确认清楚之前一行代码都不写 |

   - 「不要让我开始做，先把文档定下来」这类指令，意思是**先把规则谈清楚**，不是立刻产出内容。
   - 需求描述里没写到的东西（字段、默认值、异常分支、边界值）**都属于缺失项**，要主动问，不要自行填补。
   - 用户的回答可能只覆盖部分问题——没回答的项要**显式列出并复问**，不要默认按推荐处理。

1. **文档同步**：每一步修改（代码/配置/前端/脚本）完成后，**同一次会话内**更新对应文档。
   改业务 → `BUSINESS.md`；改链路/风险 → `REVIEW.md`；新增模式/陷阱 → `CODING_STANDARD.md`；
   影响交接状态 → 本文件「进度日志」。描述必须让下一个 AI **不看聊天记录**就能继续。
2. **README 同步**：功能、架构、服务清单、技术栈、启动方式或演示账号变化时，必须同步更新 `README.md`（中英双语）。
3. **不要主动 git commit / push**，除非用户明确要求。
4. **注释同步**：改业务逻辑必须同步更新类头与步骤注释。
5. **注释完备性（强制）**：**每个接口、每个方法（含私有）、每个字段/DTO 属性都必须有 XML 注释**。
   标准见 `CODING_STANDARD.md` 第 5.0 节清单。新增服务与新增文件一律执行，**缺注释视为未完成**。
6. **工具调用保持小步执行**，避免输出过大导致中断。

---

## 4. 代码现状

**尚未开始实现。** 当前仓库只有本文档集。

### 4.1 待实现的服务（17 个）

按 `BUSINESS.md` 第 3.3 节。**建议实现顺序**（先主干后外围）：

| 阶段 | 服务 | 理由 |
|---|---|---|
| 一期·地基 | Collaboration、Gateway、Auth、User、Permission、Tool | 认证、权限、上传是所有业务的前置 |
| 一期·商品 | Product（含 ES 搜索）、Cart、Inventory | 浏览与购物的前置 |
| 一期·交易 | Order、Payment、Marketing、Scheduled | 交易闭环主干 |
| 一期·租户 | Customer、MerchantPlatform | C 端账号与平台/商户/装修 |
| 二期·增值 | Point、Evaluate | 积分与评价，消费主干事件 |
| 二期·营销 | 限时抢购（Marketing 内） | 依赖 Marketing 优惠引擎 |
| 横切 | Log | 日志消费，可最后接入 |

### 4.2 已确定的关键决策

| 决策 | 结论 |
|---|---|
| 协作库命名 | `Collaboration`，**纯类库**（非微服务，无端口/数据库/Consul 注册） |
| 分层引用 | Domain→Collaboration；Infra/Application→Domain；Api→Application+Infra，**Api 不得引用 Domain** |
| 建表方式 | **SQL 初始化脚本**，不使用 CodeFirst |
| 脚本语言 | **PowerShell**，不使用 bash |
| 运行时 | **.NET 10** |
| 公共类库位置 | **`src/Collaboration/`**，与微服务同放 `src/` 下便于发现，但**不是服务**（无 .sln 服务条目、无 Program.cs、无端口） |

### 4.3 需求确认已完成的部分

以下需求**已与用户逐条确认**，实现时不要重新提问：

| 主题 | 结论 |
|---|---|
| 游客权限 | **只能浏览**，不可领券/加购/下单/收藏/评价/进积分中心；到手价只算活动价 |
| 积分抵扣上限 | **100%**，允许实付 0.00（跳过 PaymentService） |
| 积分签到 | **包含每日签到**，7 天一轮 1/2/3/5/8/10/15，断签清零，无补签 |
| 积分有效期 | 365 天，FIFO 先到期先用 |
| 评价粒度 | **SPU 级**，标记当前订单购买的所有 SKU |
| 下单锁积分 | **接受冻结模型的复杂度**（下单冻结 → 支付实扣 / 取消解冻 / 退款回收） |
| 店铺评分 | **只统计有评价商品的均分**，**每日一更新**（03:00） |
| 配送方式 | **虚拟 / 快递 / 自提** 三种 |
| 虚拟发货 | **手动点发货，不填物流信息** |
| 虚拟退款 | **确认收货后不可退款**（仅虚拟订单；实物仍可退） |
| 取货码 | **订单号派生短码 + RSA 签名**，格式 `{8位短码}{8位签名}` |
| 密钥 | **私钥公钥都放服务器**，前端只显示与接收，加解密全部服务端完成 |
| 核销方式 | **手输 + 扫码** 两种都支持 |
| 券数量 | **订单级只能一张**；结算页默认选最优券（并列取最临期），可改选 |
| 活动类型 | 满减 / 满折 / 满赠 / **新增秒杀** |
| 秒杀库存 | **划出常规库存**，场次结束/中止立即回补 |
| 秒杀场次 | 本期**单场次**，但**预留多场次接口**（按 `SessionId` 寻址） |
| 秒杀中止 | **支持**手动提前结束 + 回补库存 |
| 模拟支付 | **后台订单列表加按钮**，可选支付成功或失败 |
| ES 分词器 | **IK** |
| 装修 | **草稿 + 发布**双状态 |
| 小程序平台 | **固定 `platformCode` 锁定**，应用内不切换 |
| 单元测试 | **要**（xUnit） |
| 设计体系 | 后台与小程序**统一苹果风**，规范见 `DESIGN_SPEC.md` |
| 原始数据 | **一律不得展示原始数据**，清单见 `DESIGN_SPEC.md` 第 6 节 |
| 金额显示 | **只显示数字保留两位小数**，不加货币符号与千分位，不做万/亿缩写 |
| 权限点 | **可增删改的实体**且每个权限点有**中文名**，**只允许超级管理员操作** |
| 角色授权 | **树结构多级勾选**（4 层）+ **「全部权限」根节点按钮**；内置管理员角色**禁止编辑** |
| 装修方式 | **拖拽搭建器**（12 列栅格、单层扁平、不嵌套），装修页模拟小程序真实显示 |
| 装修分层 | **平台装修（首页 / 我的页）与商户装修（店铺页）互不可见、无继承** |
| tabBar / 主题色 | 平台装修单独配置，**不进拖拽画布**；可配置颜色**仅限三档** |
| 商城页等其余页面 | **固定模板**，不做自由搭建 |
| 金额舍入 | **全部金额字段两位小数 + 四舍五入**，应用层显式舍入，**禁止依赖数据库 cast** |
| 商品总额 | **由各行应付累加得出**，不重新计算，保证总账平 |
| 行封底 | **券/活动造成的行应付封底 0.01**；积分抵扣**允许 0.00** |
| 前端验证时机 | **只在提交时验证，不做失焦验证**；失败飘红 + 下方备注原因 + 聚焦首错字段 |
| 后端错误表现 | **统一全局 tip 提示**，不飘红输入框 |
| 校验规则来源 | **单一 JSON**（`deploy/shared/validation-rules.json`）生成两端常量；字段绑定各端手写 |
| 商户装修能力 | **完整拖拽搭建器**，可改组件内容（含店铺轮播图，最多 5 张） |
| 商户不可改 | **任何配色**（继承平台）、tabBar、首页、我的页、地区地址 |
| 商户组件库 | **独立**，共 11 个店铺类组件 |
| C 端可见性 | **C 端与游客只返回「公开可见」数据**，由 AOP 的 `IPublicVisible` 统一注入 |
| 可见性过滤范围 | 商户（审核通过+启用）、商品（审核通过+上架）、平台、活动、券活动、用户券、秒杀场次 |
| 后台不受此过滤 | 后台上下文**不注入**可见性过滤，运营要能看到待审核与下架数据 |
| 商户被拒/停用 | **自动批量下架**其全部已上架商品，**并发 `product.changed` 同步 ES 索引** |
| 装修手动选商品 | 只能选**已审核通过且已上架**的商品；**配置时校验 + 展示时再过滤**两处都要 |
| 活动/券的 Targets | **不受**「已审核已上架」限制，允许配在未上架商品上 |

---

## 5. 进度日志

> 倒序，新条目写在**最上面**。每条格式：日期（第 N 轮）：标题 + 变更点 + 验证结果 + 回归。

### 2026-10-03（实现阶段）：补齐三项欠账 + 落地全局异常中间件与 API 回归

- **① PasswordHasher 下沉到 Collaboration**
  - 从 `CustomerService.Application/Services` 移到 `Collaboration.Domain/Security`，前后台账号共用同一份实现，
    「都要加盐」成为结构上保证而非靠人记。
  - 新增 8 条单元测试（含「同一密码两次哈希不同」直接证明随机盐）：**单元测试 22/22 通过**。
- **② 凭据外移**
  - `scripts/seed-agileconfig.ps1` 不再有硬编码默认值，改为读环境变量或 `deploy/.env`（已 gitignore）。
  - 新增 `deploy/.env.example`。
  - 已确认管理密码 `Simpleshop@2026` **不再出现在任何被跟踪文件里**（`git grep` 为空）。
  - 说明：`appsettings.json` 里的 `AppSecret` 保留——它是**只读应用凭据**且是 bootstrap 机制本身，
    与能写配置中心的管理密码性质不同。
- **③ 回归用例**：`tests/e2e/api-regression.ps1`，15 条，覆盖注册、登录、参数校验、
  以及 **4 条标红的审计与安全回归**（不存在的账号必须失败、错误提示不泄露账号存在性、
  雪花 Id 与创建时间必须落库、密码不明文且随机盐）。
  **实测 15/15 通过。**
- **补上 CODING_STANDARD 3.3 一直缺失的全局异常中间件**
  - 新增 `src/Collaboration/Collaboration.Web`（普通 Sdk + FrameworkReference，保持 Domain 无框架依赖）。
  - `GlobalExceptionMiddleware`：`ValidationException` → 400 + 字段级 errors；
    其他未处理异常 → 500 + 通用消息，**详细信息只写日志不回前端**（堆栈与连接串属信息泄露）。
  - 之前校验失败会裸奔成 500，现在正确返回 400。
- **踩坑记录**：`dotnet test` **只构建测试项目及其依赖，不会重建 `CustomerService.Api`**，
  导致服务跑的是加中间件之前的旧二进制，误判「中间件没生效」。验证任何后端改动前必须先跑 `scripts/build.ps1`。
- 另有两处回归脚本自身的手机号位数错误（13 位 / 10 位），已修正为 11 位。
- **下一步**：S1 剩余的 UserService / PermissionService / AuthService / ToolService。

### 2026-10-02（实现阶段）：修复 P0 级过滤缺陷，CustomerService 全链路验证通过

- **缺陷（上一提交遗留，P0 级）**：`Aop.ParseExpression` 的 `Result` 是**替换**整个 WHERE 而非追加，
  导致业务条件被顶掉。实测症状：不存在的账号 `not_exist_user` 能登录成功，返回的是库里第一条记录。
  影响面：租户过滤、客户过滤、公开可见性过滤**全部同样失效**，等于越权。
- **同时发现的第二个缺陷**：`Aop.CurdBefore` 在本项目调用链上**不触发**（加诊断打印验证过），
  导致雪花 Id 写成 0、`created_at` 写成 `0001-01-01`。
- **修复方案（不依赖任何 AOP 钩子）**：
  - 查询过滤改用 `IFreeSql.GlobalFilter.ApplyIf(name, condition, where)`，它是 AND 进查询的。
    新增 `FilterRegistrar`（`src/Collaboration/.../Infrastructure/FilterRegistrar.cs`），
    启动时按实体程序集逐类型注册软删 / 租户 / 客户 / 公开可见性四类过滤。
  - 审计字段（雪花 Id / CreatedAt / CreatedBy / OperationBy）**显式写在 `CrudRepository`
    的 InsertAsync / UpdateAsync**，可读可测，不依赖钩子。
  - `IPublicVisible` 改为自引用泛型 `IPublicVisible<TSelf>`，返回 `Expression<Func<TSelf,bool>>`，
    避免「SQL 字符串转表达式」这种脆弱转换。
- **实测验证（四项全过）**：
  1. 雪花 Id 正确：`customerId = 194392770340357`（原为 0）
  2. `created_at` 正确：`2026-10-02 15:33:06`（原为 0001-01-01）
  3. 不存在的账号登录 → `success=False 登录名或密码不正确`（原为 success=True 并泄露第一条记录）
  4. 软删过滤生效：手动置 `is_deleted=true` 后该行仍在库（1 行、未删除 0 行）但查询查不到
- 密码加盐已验证：PBKDF2-SHA256 + 每用户 16 字节随机盐，格式 `pbkdf2$次数$盐$哈希`，
  登录时用同一个盐重算并做 `FixedTimeEquals` 定时安全比较；错误密码与不存在账号返回**同一句提示**，不泄露账号是否存在。
  下一步把 `PasswordHasher` 下沉到 Collaboration，让后台账号（UserService/AuthService）复用同一份。
- 文档同步：`DATA_SPEC` 3.2 新增「实现方式：GlobalFilter + 仓储显式审计」并记录踩坑；
  `PLAN` 2.7 落地记录已更新。
- **待补**：把「不存在的账号必须登录失败」写成回归用例，目前只有本次手工验证。

### 2026-10-02（实现阶段）：S1 — CustomerService 四层打通到 Infrastructure

- **CustomerService.Domain**：Customer / CustomerAddress / CustomerFavorite 实体 + 三个仓储接口。唯一索引不放实体属性上（`ColumnAttribute` 没有 `IsUnique`），统一由 DDL 脚本定义。
- **deploy/sql/customer/01-create-tables.sql**：3 表 9 索引。含两条**部分唯一索引**（同一客户至多一条默认地址、同一客户同一 SPU 只留一条有效收藏），都带 `is_deleted = false` 条件，软删后不占用唯一位。实测建表成功、复跑幂等。
- **CustomerService.Application**：PasswordHasher（PBKDF2-SHA256 + 随机盐，迭代次数随哈希存储）、CustomerTokenService（HS256）、Register / Login 各含 Command + Validator + Handler。Login 对「账号不存在」与「密码错误」返回同一条消息，避免账号枚举。
- **CustomerService.Infrastructure**：三个仓储实现 + `AddInfrastructure` 注册。`SetDefaultAsync` 用 `IFreeSql.Transaction(Action)` 包住两条语句。
- **scripts/init-tables.ps1**：按服务目录名映射库名批量执行 DDL，幂等。
- **验证**：build **0 error 0 warning**；dotnet test **14/14**；建表实测通过。
- **踩坑与修正（已回写文档）**：
  1. `Features.Customer` 命名空间遮蔽 `Customer` 实体（CS0118）——`CODING_STANDARD` 陷阱 1，按规定用 using 别名。
  2. **FreeSql 3.5 自带 `BaseRepository<TEntity>`**，与我们的同名冲突（CS0104）。已把自有基类改名为 **`CrudRepository<T>`**，接口 `ICrudRepository<T>`，并回写 `DATA_SPEC` 3.5 与 `PLAN` 2.2。
  3. FreeSql 3.5 的 `IInsert.ExecuteReturnSnowflakeIdAsync` 不存在——Id 已由 AOP 填好，直接读实体属性。
  4. `IFreeSql.TransactionAsync` / `IAdo.MasterConn()` 均不存在——只有同步的 `IFreeSql.Transaction(Action)`。
- **S1 剩余**：CustomerService.Api 层（控制器 + Program 启动时序 + appsettings）、地址与收藏的 Feature、User / Permission / Auth / Tool 四服务、Gateway。

### 2026-10-02（实现阶段）：S1 进行中 — 配置源抽象 + 启动时序 S0~S4 + 单元测试骨架

- **配置源抽象**（`Collaboration.Domain/Configuration/`）：`IConfigSource` + `ConfigSourceUnavailableException`、`LocalFileConfigSource`、`ConfigurationValidator`、`BootstrapOptions`、`InfrastructureOptions`。
  - 按 PLAN.md 2.6 选项 (a) 推进：S1 起用 `appsettings` 作为配置源。**它只改来源不改语义**——没有给任何配置项提供默认值，缺项照样 fail-fast。
  - AgileConfig 分支**显式抛 NotSupportedException** 而不是静默回退到本地文件，避免「以为连着配置中心其实没有」。网络可达后只需补一个 `IConfigSource` 实现。
- **启动时序 S0~S4**（`ServiceBootstrap`）：引导配置绑定 → 选配置源 → 指数退避重试拉取 → 校验必填键 → Redis INCR 分配雪花 workerId（超上限失败不回收）。
- **单元测试**：`tests/Collaboration.Domain.Tests`，14 条全绿。覆盖 `SqlLiteral`（含注入片段转义、类型白名单抛异常）、`ConfigurationValidator`（缺失/空白/重复/大小写）、`SnowflakeId` 生命周期。
- **测试暴露并修掉一个真实缺陷**：`ConfigurationValidator` 原先依赖调用方字典的 comparer，配置源若返回不同大小写的键会误判缺失。改为内部自建 OrdinalIgnoreCase 视图。
- `InternalsVisibleTo` 只开放给测试项目，`SqlLiteral` 仍不对外公开。
- **S1 剩余**：CustomerService 完整四层（基准模板）、User / Permission / Auth / Tool 四服务、Gateway、5 个服务建表 SQL。

### 2026-10-02（实现阶段）：S0 基础设施完成

- **新增 `PLAN.md`**（实现规划，11 阶段 S0~S10）。规划严格派生自本文档集，实现中发现需要新决策必须先停下来提问，不得自行发挥。
- **S0 交付物**：
  - `deploy/docker-compose.yml`：7 个中间件全部 healthy（postgres / redis / consul / rabbitmq / elasticsearch / kibana / fluentd）+ AgileConfig（config profile，默认不启）。
  - `deploy/elasticsearch/Dockerfile`、`deploy/fluentd/Dockerfile`（自建镜像）、`deploy/fluentd/fluent.conf`。
  - `deploy/sql/00-create-databases.sql`：14 个库 + 应用角色 + 授权 + 每库 UTC，**幂等可重跑**（已实测复跑通过）。
  - `deploy/shared/validation-rules.json`：9 条校验规则单一来源。
  - `scripts/`：`start-infra` / `stop-infra` / `init-database` / `build` / `generate-validation-rules`。
  - `src/Collaboration/Collaboration.Domain`：`EntityBase` / `AdminEntityBase` / `CustomerEntityBase` / `IPublicVisible` / `ApiResponse` / `ApiResults` / `BaseApiResponseCode` / `TenantContext` / `AccessContext` / `TenantContextHolder` / `SnowflakeId` / `SqlLiteral` / `FreeSqlAopRegistrar` / `ValidationPatterns`（生成物）。
  - `NuGet.config`：隔离失效的机器级私有源。`.gitignore`、`SimpleShop.slnx`。
- **验证结果**：`./scripts/build.ps1` **0 error 0 warning**（csproj 把 CS1591 缺注释设为编译错误）；14 库可连、脚本复跑幂等；7 容器 healthy。
- **三处技术落差点（已记入 PLAN.md 2.7）**：
  1. FreeSql 3.5.311 **移除**了 `Aop.DataMapping` / `Aop.DataFilter`，改用 `ParseExpression` + `CurdBefore`；因前者只有字符串通道，过滤条件必须内联，用 `SqlLiteral` 做类型白名单防注入。
  2. PostgreSQL **不允许在 DO 块里 `CREATE DATABASE`**，改用 psql 的 `\gexec`。
  3. 雪花包名是 `Yitter.IdGenerator`（不是 `Yitter.NetCore`）；`ColumnAttribute` 的长度属性是 `StringLength`（不是 `Length`）；属性在 `FreeSql.DataAnnotations` 命名空间。
- **两处外部依赖不可用（已记入 PLAN.md 2.5/2.6）**：
  - **AgileConfig**：`registry.agileconfig.com` 与 `agileconfig.com` DNS 解析失败，Docker Hub 无 `agileconfig` 命名空间。**阻塞 S1**，需要先定配置源方案。
  - **IK 分词器**：7.x/8.x 只在 `get.infini.cloud` 分发（SSL 阻断），GitHub 无 8.x 产物。已降级 ES 内置 `smartcn`，IK 做成 `INSTALL_IK` 构建参数待恢复。
- **下一步**：等用户决策 AgileConfig 方案后进入 S1（认证与租户地基）。

### 2026-10-02（需求阶段·第六轮）：新增 TEST_CASES.md 测试用例文档

- **新增 `TEST_CASES.md`**（9 章，约 516 条用例 + 约 180 张基线图）：
  - **分层原则**：能在单元测的不放 API 测，能在 API 测的不放 UI 测。
  - **编号规则**：`层-模块-序号`，层前缀 `UT` / `API` / `UI` / `VIS`，模块前缀按域划分。
  - **优先级**：P0（资损 / 超卖 / 越权 / 数据丢失，**必须自动化且 CI 阻断合并**）、P1、P2。
  - 第 1 节 单元测试 **130 条**：金额舍入 22、优惠引擎 22、订单状态机 14、库存 12、积分 19、取货码 RSA 8、秒杀 12、评价 12、券与时间窗 9。关键用例给**具体数字**与**数据库断言**。
  - 第 2 节 API 回归 **173 条**，覆盖 21 个模块：健康 / 认证 / 网关 / RBAC / 多租户 / **C 端可见性** / 平台商户 / 分类品牌 / 商品 / 购物车 / 交易 / 库存 / 营销 / 秒杀 / 积分 / 评价 / 退款 / 装修 / 上传 / 报表 / 定时任务。
  - 第 3 节 UI 功能回归 **73 条**：表单提交验证（**失焦不验证**、飘红 + 下方原因、聚焦首错）、权限树与全部权限、拖拽搭建器、**原始数据 12 条**、**设计规范 10 条**。
  - 第 4 节 视觉回归 **100 个截图点位 → 约 180 张图**：两级截图（页面级 + 组件级），后台 45 个页面点、小程序 29 个页面点、通用组件 26 个。**基线入库随代码走，PR 变更需人工 review，CI 不自动更新基线。**
  - 第 5 节 P0 专项约 28 条：补偿回滚逐点注入、事务完整性、秒杀并发超卖、积分资损、支付金额反查、金额对账、取货码与资质。
  - 第 6 节 **越权矩阵 12 条**：五种身份 × 关键接口，**严格区分 403（无权限）与 404（防探测）**。
  - 第 7 节 测试数据：`seed-test-data.ps1` 提供 14 组边界数据（零库存、9.99 临界价、10.005 尾数、未审核商户、下架商品、过期券、0 元订单等）；**测试数据带标记字段过滤，不靠跑完清理**。
  - 第 8 节 覆盖对照：模块维度快速索引。
- **视觉回归的稳定性前提**（不满足即视为测试环境错误）：固定 Noto Sans CJK SC 字体（缺失直接构建失败）、固定 `Asia/Shanghai` + `zh-CN`、全局禁用动画、禁用随机数据与相对时间、本地占位图、后台 1440×900 / 小程序 375×812 与 390×844 两档、差异阈值 0.1% 或单元素 > 50px²。
- `AI_HANDOFF` 2.5 测试命令与 `README` 文档索引已同步。
- **代码仍未开始。**

### 2026-10-02（需求阶段·第五轮）：C 端数据可见性（公开可见过滤）

- **新增 `BUSINESS.md` §1.4「C 端数据可见性」**——与租户隔离**并列的第二个过滤维度**：
  - **逐实体公开可见条件**：`Platform` 启用；**`Merchant` 审核通过 + 启用**；**`Product` 审核通过 + 上架**；活动启用且在有效期内；券活动启用且在领取窗内；用户券未使用未过期；秒杀场次进行中。
  - **由 AOP 统一注入**（`DATA_SPEC.md` 3.2.1）：新增标记接口 `IPublicVisible`，仅在 **C 端与游客上下文**注入；**后台上下文不注入**——运营必须能看到待审核商户与下架商品。
  - AOP 过滤器由 3 类扩为 **4 类**（软删 / 租户 / 客户 / 公开可见性）。
  - **C 端接口禁用可见性旁路**：`Customer` / `Anonymous` 上下文不允许用 `ClearFilter` 绕过公开可见性过滤。
- **装修组件的商品可见性**（`BUSINESS.md` 16.4、`DATA_SPEC.md` 5.29）：
  - `productGrid` 支持 `source`（自动 / 手动）；手动时 `manualProductIds` 必须全部满足「归属正确 + 审核通过 + 已上架」，**任一不满足整单保存失败**；数量 1~20。
  - **配置时校验 + 展示时再过滤两处都要有**——配置合法不代表永久合法，商品可能后续被下架。
  - 过滤后为空则**整个组件不渲染**；后台装修页对已下架商品**标灰但不自动移除**。
  - 新增下拉接口 `GET /gateway/products/Designable`（后台上下文不注入可见性过滤，需单独过滤）。
- **商户资质与商品资质联动**（`DATA_SPEC.md` 5.2 / 5.3）：
  - 商户**审核被拒**或**停用**时，**批量下架**其全部已上架商品，**并发布 `product.changed` 同步 ES 索引**。
  - 漏发事件会导致「商品页看不到但搜索搜得到」——因为**搜索走 ES 索引，不走 C 端可见性过滤**。
- **边界场景口径**：已下单商品下架后订单页仍显示（订单行有快照）；收藏页显示但标记「已下架」置灰；已评价商品下架后评价保留；商户停用后历史订单**能查也能退款**。
- **明确不适用**：活动与券的 `Targets` **不受「已审核已上架」限制**，允许配在未上架商品上（运营常先配活动再等上架生效）。
- **`REVIEW.md` 新增 P1 风险 13 / 14 / 15**（C 端可见性被绕过、商户资质变更后未同步下架、ES 索引最终一致），P2 / P3 编号顺延。
- **代码仍未开始。**

### 2026-10-02（需求阶段·第四轮）：金额舍入口径 + 前后端验证分工 + 商户装修能力修正

- **金额舍入写入全局硬性口径**（`BUSINESS.md` 新增 8.4、`DATA_SPEC.md` 2.6 后新增小节）：
  - **所有金额字段保留两位小数、四舍五入**，统一用 `Math.Round(x, 2, MidpointRounding.AwayFromZero)`，**全系统一致**。
  - **禁止依赖数据库隐式转换**。PostgreSQL 的 `numeric → numeric(18,2)` 用的是**银行家舍入**（`0.125 → 0.12`），与四舍五入不符，必须在应用层显式舍入后再入库。
  - 下单计算链逐步舍入：原行金额 → 活动优惠 → 券优惠 → 行应付 → **商品总额 = Σ 各行应付（由行累加，不重算）** → + 运费 → − 积分抵扣 → 实付。
  - **行封底与舍入的交互**：券优惠 `6.666` 舍入为 `6.67` 导致行应付 `0.00` 时，**由券或活动造成的封底 0.01**；**积分抵扣可以把订单打到 0.00**。
  - 退款：各行 Round 2 位，退款单金额 = Σ 各行，提交时校验「累计已退 + 本次 ≤ 实付」，不平直接拒绝。
  - **不适用舍入**：折扣率、数量、积分。
- **前后端验证分工**（`CODING_STANDARD.md` 新增 3.4、3.5；`DESIGN_SPEC.md` 新增 5.6）：
  - **后端对所有需要验证的字段全部验证**，前端校验只是体验优化，不可作为安全边界。
  - 前端**只在提交时全量验证，不做失焦验证**；失败则输入框飘红 + **输入框下方**备注失败原因，并自动聚焦第一个错误字段。
  - **后端返回的错误统一用全局 tip 提示**，不飘红输入框、不定位字段。tip 用 `ElMessage`（后台）/ `uni.showToast`（小程序），不自己实现。
  - **校验规则单一来源**：`deploy/shared/validation-rules.json` 定义正则与 min/max 与文案，构建时生成 C# 常量类与 JS 常量文件。**字段与规则的绑定关系仍各端手写**（避免生成代码不可读、与 100% 注释规范冲突）。
  - `DATA_SPEC.md` 新增 5.32「仅后端验证的字段清单」——唯一性、层级、引用完整性、额度类判断共 24 项，前端不做校验。
- **商户装修能力修正**（`BUSINESS.md` 16.2/16.4、`DATA_SPEC.md` 5.30）：
  - 商户**拥有完整的拖拽搭建器**，可拖拽组件、调整顺序与宽高、**可改组件内容**（含自己店铺页的轮播图图片与链接，**最多 5 张**，与平台首页轮播无关）。
  - 商户**不能改任何配色**：全局主题色与**组件内部颜色全部继承平台**，属性面板不渲染颜色选择器，后端保存时**剔除**组件 props 中的颜色字段。
  - 商户**不能碰** tabBar、首页、我的页、地区地址。
  - **商户组件库独立**，共 11 个店铺类组件；不含会员卡、权益行、服务宫格、金刚区、秒杀专区、优惠券专区等平台专有组件。
  - **代码仍未开始。**

### 2026-10-02（需求阶段·第三轮）：新增 DESIGN_SPEC.md（苹果风）+ 商户装修 + 权限树

- **新增 `DESIGN_SPEC.md`**（8 章）：设计原则、token（色彩 / 间距梯度 / 圆角 / 字号阶 / 阴影三层 / 动效 / 触控）、字体栈、Element Plus 换肤方案与 18 个必改组件、组件规范、**原始数据禁显示清单（11 类 + 1 个例外）**、格式化工具规范、12 项硬约束检查清单。
- **权限改造**（`BUSINESS.md` 新增 5.4、`DATA_SPEC.md` 5.21/5.22 重写）：
  - 权限点**从只读种子变为可增删改的实体**，新增 `Name`（中文名）/ `ApiPath` / `ParentId` / `SortOrder` / `IsBuiltin` 字段。
  - **只允许超级管理员（`PlatformId = 0`）操作**，网关与 Handler 双重校验；新增权限点 `permission:manage`。
  - 角色授权**由穿梭框改为树结构多级勾选**（4 层：全部权限根节点 → 5 个业务大类 → 23 个功能模块 → 权限点叶子，共 78 个权限点）。
  - **「全部权限」是虚拟根节点**，纯 UI 快捷方式；存储上仍存全部叶子权限点，**不引入 `*` 特殊值**；勾选后整棵树禁用只读。
  - `platform-admin` / `merchant-admin` **禁止编辑权限、禁止删除**，初始化时直接绑定全部权限点。
  - 新增 `permission:manage`、`logistics:manage`（物流公司维护）与 `design:merchant`（商户装修读写）权限点。
  - 内置权限点**不可删除只能停用**；删除非内置点需二次确认并列出受影响角色，级联删除 `role_permission`；任何增删改**立即失效网关缓存**；**不影响已登录会话**，需重新登录。
- **装修改为拖拽搭建器**（`BUSINESS.md` 16.2~16.6 重写、`DATA_SPEC.md` 5.29/5.30 重写）：
  - **平台装修**（首页 / 我的页）与**商户装修**（店铺页）**互不可见、无继承关系**，分别落 `platform_app_config` 与 `merchant_app_config`。
  - 装修页**模拟当前小程序真实显示**，支持**拖拽组件生成页面**：12 列栅格、单层扁平列表、不做嵌套容器。
  - 组件库 6 类 22 个（布局 / 内容 / 会员 / 店铺 / 我的 / 功能），按页面可用性过滤。
  - 原「轮播图排序 / 金刚区排序 / 服务宫格排序」被组件**取代**；tabBar 与三档主题色单独编辑，**不进画布**。
  - 商城页与其他页面为**固定模板**，不做自由搭建；历史装修配置全部作废，不做兼容层。
- **原始数据禁显示**：Id 显示名称、时间本地化、金额**只显示数字保留两位小数**（不加货币符号与千分位、不做万/亿缩写）、空值统一破折号、布尔转是/否、评分一位小数、JSON 字段转可视化 UI、长文本截断加气泡、进度用进度条。**唯一例外**是后台地区地址配置页的 JSON 文本域。
- **文档集现为七份**：`BUSINESS.md` / `CODING_STANDARD.md` / `DATA_SPEC.md` / `DESIGN_SPEC.md` / `REVIEW.md` / `AI_HANDOFF.md` / `README.md`。
- **代码仍未开始。**

### 2026-10-02（需求阶段·第二轮）：新增 DATA_SPEC.md + 确立「先问后写」协作流程

- **确立协作流程（第 3 节第 0 条，凌驾其他约定）**：收到新需求或需求变更时**禁止直接动手**，必须走「理解 → 分轮提问 → 确认 90% → 落文档 → 实现」五阶段。
  起因：本轮我曾未经确认就按推测铺了一整份文档，被用户叫停并删除。规则已固化，防止复发。
- **新增 `DATA_SPEC.md`**（6 章，29 个后台表单小节）：
  - 第 1 节 启动与配置加载时序：S0~S12 十二阶段、**先读 AgileConfig 再连 DB/Redis**、fail-fast、热更新边界（仅业务开关与限额）。
  - 第 2 节 模型基类：`EntityBase` / `AdminEntityBase`（**创建人固定不变 + 只记最后操作人**）/ `CustomerEntityBase`（含 CustomerId + 用户名快照）；`User` 与 `Customer` 独立定义；时间统一 UTC；默认软删；snake_case 命名。
  - 第 3 节 FreeSql AOP 与仓储：**行级多租户隔离 + AOP 自动注入软删/租户/客户过滤**（解决「某 Handler 忘了加租户条件」的越权）；**workerId 用 Redis `INCR` 原子自增**；仓储方法集；排序白名单。
  - 第 4 节 展示与下拉：`OptionDto { Id(字符串), Name }`、10 个下拉接口清单、必须冗余返回的名称字段、枚举文案由后端下发。
  - 第 5 节 后台表单字段规格：29 个表单（平台/商户/审核/分类/品牌/商品/SKU 规格/审核/上下架/库存/活动/券模板/券活动/营销配置/秒杀场次/秒杀商品/结束中止/建号/改号/重置密码/角色/发货/取货核销/退款审批/代客退款/评价管理/模拟支付/装修/地区地址）。
- 本轮确认的关键决策：**多租户走行级隔离（A）而非物理隔离**；SKU 规格在 SPU 下动态定义、**不建全局规格字典表**；**券模板改动不影响已发出的券**（发放时快照）；发货物流公司用**字典表 + 可搜索下拉**；退款金额可按行自定义；隐藏评价**必填原因**；地区地址用 JSON 文本域 + iframe postMessage 预览。
- 文档集现为六份：`BUSINESS.md` / `CODING_STANDARD.md` / `DATA_SPEC.md` / `REVIEW.md` / `AI_HANDOFF.md` / `README.md`（已在 README 与本文档的索引表中互相登记）。
- **代码仍未开始。**

### 2026-10-02（需求阶段）：文档定稿

- 完成需求澄清与文档产出，五份文档：`BUSINESS.md` / `CODING_STANDARD.md` / `REVIEW.md` / `AI_HANDOFF.md` / `README.md`。
- **需求关键新增项**（相对初版概述）：积分服务（冻结模型、签到、100% 抵扣）、评价服务（SPU 级 + SKU 标记）、限时抢购（划库存 + Redis 预扣）、三种配送方式与运费、订单级单券、商品 Elasticsearch（IK）搜索。
- 确认工程基线：.NET 10 / PowerShell / SQL 初始化脚本 / 100% XML 注释 / xUnit 单元测试。
- 澄清的初版文档空白：`Collaboration` 是类库非微服务、「团购」为占位未落地、积分与评价原本无规则定义、运费概念原本完全缺失。
- **代码尚未开始**。下一步：确定项目根目录后，按 4.1 的阶段顺序搭骨架。

---

## 6. 历史设计决策（为什么是这样）

| 决策 | 背景 |
|---|---|
| **独立 ScheduledService** | 定时任务与请求驱动服务解耦；多实例用全局扫描锁互斥 |
| **AgileConfig 每服务独立配置** | 早期共用配置导致串库（Scheduled 连到 Order 库），已各自独立 |
| **FreeSql 统一 ORM（Auth 除外）** | Auth 先行用了 EF Core + OpenIddict，保留 |
| **gRPC 内部 / REST 仅对外** | 服务间 MagicOnion（MessagePack），网关聚合 REST |
| **平台/商户两层权限** | 需求：平台管理员全权，业务员/财务按权限点隔离，A 平台看不了 B 平台数据 |
| **小程序按平台配置动态渲染** | 不同平台入口展示完全不同的商城 |
| **雪花 ID 前端保留字符串** | 超出 JS Number 安全范围曾导致商品新增失败 |
| **前后端双重验证** | 后台 UI 大批量修复期的结论：前端提示字段级错误，后端 Validator 兜底 |
| **下单冻结积分而非支付后扣** | 支付后扣会出现「实付已按抵扣算好、积分却不足」的资损。冻结模型多一层复杂度但消除资损窗口 |
| **秒杀划出常规库存** | 秒杀若共用常规库存会把常规库存秒光；划出后秒杀不超卖也不侵占常规库存 |
| **券改为订单级单券** | 结算页交互更简单明确：默认选最优券（并列取最临期），用户可改选或取消 |
| **评价均分每日重算** | 避免高频写热点商品行。代价是 24 小时延迟，属可接受的产品权衡 |
| **ES 只用于搜索** | 交易链路绝不能依赖索引一致性。下单/结算/库存一律走 PostgreSQL |
| **SQL 脚本建表而非 CodeFirst** | 需要可审阅、可回滚、可在生产环境精确执行的 DDL |

---

## 7. 注意事项

| # | 事项 |
|---|---|
| 1 | **项目路径含 `#` 时不要用 Vite dev-server**，会白屏；必须 build + preview |
| 2 | uniapp H5 的 input 无原生 placeholder（Playwright 用 `locator('input')` 定位） |
| 3 | 同源 hash 跳转后要 `reload()` 才能加载新构建 |
| 4 | uniapp tabBar 图标必须 PNG |
| 5 | **雪花 ID 前端禁止 `Number()`**；金额/数量/枚举比较必须先 `Number()` |
| 6 | 密码策略：后台账号至少 8 位含字母与数字 |
| 7 | 手机号正则 `^1[3-9]\d{9}$`，邮箱 `^[^\s@]+@[^\s@]+\.[^\s@]{2,}$` |
| 8 | 本机服务进程可能被会话回收：冒烟前先 `/health` 检查网关 5008 与 Inventory 5062 |
| 9 | 秒杀异步落单需轮询 `GrabResult`；体验问题记为 P2 风险 21，不要在实现期擅自改成同步 |
| 10 | 评价均分每日更新是**产品决策不是 bug**，被提出时对照 P1 风险 12 解释，不要「顺手修好」 |

