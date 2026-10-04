# SimpleShop 后端编码规范（DDD 分层 + 注释规范）

> 适用所有 `src/` 微服务与 `Collaboration` 协作库。
> 配套文档：`BUSINESS.md`（业务规则）、`REVIEW.md`（请求链路与风险）、`AI_HANDOFF.md`（环境与协作）。
> 工程基线见 `BUSINESS.md` 第 2 节：**.NET 10 / FreeSql + PostgreSQL / PowerShell 脚本 / SQL 初始化脚本建表**。

## 目录

1. 四层职责
2. 控制器（Api 层）
3. Feature 三件套（Application 层）
4. 仓储模式（Infrastructure 层）
5. 注释规范（强制）
6. 已知陷阱
7. 分层落地检查表

---

## 1. 四层职责

每个微服务固定四个项目：`Xxx.Api` / `Xxx.Application` / `Xxx.Domain` / `Xxx.Infrastructure`。

| 层 | 项目 | 职责 | 禁止 |
|---|---|---|---|
| Api | `*.Api` | 协议转换：HTTP 绑定 → `mediator.Send()` → 返回；文件流（`IFormFile`）等协议职责 | 业务逻辑、字段验证、`IFreeSql` 查询、事件发布 |
| Application | `*.Application` | `Features/{实体}/{动作}/` 三件套；编排仓储与外部服务；gRPC 服务实现 | 直接依赖 `IFreeSql` |
| Domain | `*.Domain` | 实体（继承 `BaseEntity`）、仓储接口、枚举、领域规则 | 框架业务依赖 |
| Infrastructure | `*.Infrastructure` | 仓储实现（`BaseRepository<T>` + `IFreeSql`）、DI 注册 | 业务规则 |

### 1.1 引用方向（强制）

| 层 | 允许引用 |
|---|---|
| Domain | `Collaboration` |
| Infrastructure | `Domain` |
| Application | `Domain` |
| Api | `Application`、`Infrastructure` |

**除上表之外不得有其他引用。** 特别注意 **Api 层不得直接引用 Domain**——DTO 定义在 Application 层，实体不出 Domain。

### 1.2 基准模板

新建服务时**照抄 `CustomerService`** 的项目结构、命名空间、`Program.cs` 注册顺序与 DI 装配方式。

---

## 2. 控制器（Api 层）

### 2.1 标准写法：一行转发

控制器**无验证、无查询、无业务分支**，只做协议转换：

```csharp
/// <summary>
/// 创建后台账号。
/// </summary>
/// <param name="command">建号命令，含用户名、密码、手机号、租户信息。</param>
/// <param name="ct">取消令牌。</param>
/// <returns>统一响应体；成功含用户信息，失败含错误码与消息。</returns>
[HttpPost]
public Task<ApiResponse> Create([FromBody] CreateUserCommand command, CancellationToken ct)
    => mediator.Send(command, ct);
```

### 2.2 两种允许的例外

**例外 1 · 绑定网关注入的租户上下文**（协议适配，不算业务）：

```csharp
// 强制回填 CustomerId，避免客户伪造请求体取消他人订单。
command = command with { CustomerId = tenant.UserId, OverrideOwnerCheck = !tenant.IsCustomer };
```

**例外 2 · Handler 是旧式 `IRequest<object>` 时**：

```csharp
return Ok(await mediator.Send(query, ct));
```

**禁止**：Handler 返回类型已是 `ApiResponse` 时再包 `Ok()`。
这会产生双层信封 `{data:{code,message,data}}`，前端 `request.js` 解包后读不到 `success` / `code`（历史缺陷：`/orders/Cancel`）。

### 2.3 其他禁止项

- 私有 helper（校验函数、查询方法）**不允许**留在控制器。
- 合理的 Api 层例外：文件上传（`IFormFile` 流处理）、AuthService（EF Core + OpenIddict 另一套模式）、纯查询转发接口。

---

## 3. Feature 三件套（Application 层）

每个动作一个文件夹：`Features/{实体}/{动作}/`，内含 `Command` + `Handler` + `Validator`。

### 3.1 Command

新代码**统一** `IRequest<ApiResponse>`（Handler 自构造完整响应，含失败语义）。

```csharp
/// <summary>
/// 创建后台账号命令。
/// </summary>
/// <param name="UserName">登录用户名，3-64 字符，全局唯一。</param>
/// <param name="Password">登录密码，至少 8 位且含字母与数字。</param>
/// <param name="Phone">手机号，格式 ^1[3-9]\d{9}$。</param>
/// <param name="PlatformId">所属平台 Id，雪花 Id；0 表示平台级（仅超管）。</param>
public record CreateUserCommand(string UserName, string Password, string Phone, long PlatformId = 0)
    : IRequest<ApiResponse>;
```

存量 `IRequest<object>` 允许存在，但**新动作不要再用**。
修改返回类型时 **Command 与 Handler 两处必须同步**，否则编译报 CS0311。

### 3.2 Handler

```csharp
/// <summary>
/// 创建后台账号处理器。
/// </summary>
/// <remarks>
/// 链路位置：后台「用户管理 → 建号」。
/// 失败语义：用户名/手机号已存在返回 400；缺少租户信息返回 400。
/// 幂等性：非幂等，重复调用会因唯一性校验失败。
/// </remarks>
public class CreateUserCommandHandler(IUserRepository repository, PermissionCenterClient permissionCenter)
    : IRequestHandler<CreateUserCommand, ApiResponse>
{
    /// <summary>
    /// 执行建号。
    /// </summary>
    /// <param name="request">建号命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回用户信息；校验失败返回错误码与消息。</returns>
    public async Task<ApiResponse> Handle(CreateUserCommand request, CancellationToken ct)
    {
        // 唯一性校验必须查库，放在 Handler 而非 Validator。
        if (await repository.ExistsAsync(request.UserName, request.Phone))
            return ApiResults.Fail(BaseApiResponseCode.BadRequest, "用户名或手机号已存在");

        return ApiResults.Ok(UserShaper.Shape(user));
    }
}
```

要点：

| 项 | 规则 |
|---|---|
| 成功/失败 | 统一 `ApiResults.Ok(data)` / `ApiResults.Fail(BaseApiResponseCode.X, "消息")`，保证前端收到的 `code` / `message` / `errors` 一致 |
| 租户过滤 | 注入 `TenantContext`（读网关注入的 `X-Claim-*`），Handler 内做 `IsPlatform` / `IsMerchant` / `IsCustomer` 裁剪 |
| 跨服务调用 | 封装为 `Application/Services/*Client`（如 `PermissionCenterClient`），**Handler 不直接写 gRPC 客户端代码** |
| 锁 / 幂等 / 事件 | 模式参考 `CreateOrderHandler`、`ConfirmPaymentHandler`（链路细节见 `REVIEW.md`） |
| 补偿回滚 | 跨服务多步操作必须写明**逆序回滚**，注释要说明「失败时回滚什么」 |

### 3.3 Validator

**模型验证必须写在这里**，不写在 Controller、不写在 Handler。

```csharp
/// <summary>
/// 创建后台账号校验器。
/// </summary>
/// <remarks>
/// 覆盖字段：UserName / Password / Phone。
/// 规则来源：与 User 实体的 [Column(StringLength)] 列长度对齐。
/// </remarks>
public class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    /// <summary>
    /// 构造校验规则。
    /// </summary>
    public CreateUserValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().Length(3, 64)
            .WithMessage("用户名必须为 3-64 个字符");
        RuleFor(x => x.Password).MinimumLength(8)
            .Must(p => p.Any(char.IsDigit) && p.Any(char.IsLetter))
            .WithMessage("密码至少 8 位且包含字母和数字");
    }
}
```

#### 管道注册

服务必须注册 MediatR 管道：

```csharp
builder.AddMediatRWithHandlers(typeof(Xxx.Application.*).Assembly, typeof(ValidationBehavior<,>).Assembly);
```

`ValidationBehavior` 自动执行验证；失败抛 `ValidationException`，全局中间件统一转
`400 + { code, message, errors: { 字段: [消息] } }`——前端 `request.js` 依赖该结构做字段级提示。

#### 覆盖要求

- **每个写入口**（Create / Update / Delete / 审批 / 状态变更）与**每个列表查询**都必须有对应 Validator。
- 列表分页统一 `Page >= 1`、`PageSize` 1-100。
- **Validator 与 Command 同目录、同批提交**。缺 Validator 视为未完成。

#### 校验规则口径（前后端一致）

| 项 | 规则 |
|---|---|
| 手机号 | `^1[3-9]\d{9}$` |
| 邮箱 | `^[^\s@]+@[^\s@]+\.[^\s@]{2,}$`（或 `EmailAddress()`） |
| 金额 | `> 0`，最多两位小数 |
| 数量 | `1-99`（交易链路）或 `1-10000`（库存明细） |
| 字符串长度 | 对齐实体 `[Column(StringLength = N)]` |
| 原价 | `0` 或 `>= 售价` |
| 分页 | `Page >= 1`，`PageSize` 1-100 |

#### 需查库的校验放 Handler

唯一性、存在性这类**需要查库**的校验写在 Handler（返回 `ApiResults.Fail`），
不要试图在 Validator 里注入仓储。

#### 前端同步

| 项 | 规则 |
|---|---|
| 正则集中 | `apps/admin-vue/src/utils/validators.js`、`apps/user-uniapp/src/common/validators.js`，**禁止在页面里重复写正则** |
| 提交前 | 字符串字段必须 `trim`，纯空格不得通过必填 |

#### 两种 400 形态

**形态 A · HTTP 400 + `errors`**：Validator 失败。有字段归属；前端**只用 tip 提示**（见 3.4）。

**形态 B · HTTP 200 + `body.code = 400`**：控制器内联 `Error(BaseApiResponseCode.BadRequest, ...)`。用于无字段归属的守卫，如「缺平台」「缺审核结论」。

### 3.4 前后端验证分工（强制）

**所有需要验证的字段，后端必须全部验证。** 后端验证是不可绕过的底线——前端校验只是体验优化。

| 层 | 职责 | 失败表现 |
|---|---|---|
| **后端** | **所有**写入口与列表查询都要有 Validator，覆盖必填、格式、范围、长度、业务约束 | HTTP 400 + `{ code, message, errors: { 字段: [消息] } }` |
| **前端** | 只验证**能本地判断**的字段（正则、长度、数值范围、日期先后） | **失焦不验证，提交时统一验证**；失败的输入框飘红 + 下方备注原因 |

#### 前端验证的行为约定

| 项 | 规则 |
|---|---|
| 验证时机 | **只在提交时全量验证**，不做失焦验证 |
| 错误态 | 输入框边框转危险色；**输入框下方**显示 12px 危险色文字说明失败原因 |
| 定位 | 提交失败时**自动聚焦第一个错误字段**并滚动到可见区域 |
| **后端返回的错误** | **统一用全局 tip 提示**，不飘红输入框、不定位字段；多个错误合并成一条消息 |
| tip 实现 | 后台用 Element Plus `ElMessage`；小程序用 `uni.showToast`。**不自己实现提示组件** |

#### 前后端都验证的字段

手机号、邮箱、金额格式与范围、数量、密码强度、平台编码、折扣率、颜色值、URL 长度、日期先后、整数类字段。

#### 只有后端验证的字段

需要查库的判断：用户名与手机号唯一性、分类是否超三级、平台编码是否重复、商户与商品的引用完整性、退款累计额度、库存是否足够。
这些**前端不做校验**，提交后由后端返回错误并用 tip 提示。

### 3.5 校验规则的单一来源

**校验规则只维护一份**，放在 `deploy/shared/validation-rules.json`；构建时生成 C# 常量类与 JS 常量文件。

| 项 | 规则 |
|---|---|
| **进 JSON 的内容** | 正则、min / max、消息文案 |
| **不进 JSON 的内容** | 字段与规则的绑定关系——仍由各端的 Validator / validators.js 手写 |
| 为什么不全量生成 | 全量生成会让 C# Validator 代码冗长且不可读，与「每个方法必须有注释」的强制规范冲突 |
| 一致性保障 | 生成物是唯一来源，前后端不可能漂移 |

#### JSON 结构

```jsonc
{
  "phone":     { "pattern": "^1[3-9]\\d{9}$", "message": "手机号格式不正确" },
  "email":     { "pattern": "^[^\\s@]+@[^\\s@]+\\.[^\\s@]{2,}$", "message": "邮箱格式不正确" },
  "amount":    { "pattern": "^\\d+(\\.\\d{1,2})?$", "min": "0.01", "message": "金额最多保留两位小数" },
  "quantity":  { "type": "integer", "min": 1, "max": 99, "message": "数量需为 1-99 的整数" },
  "password":  { "minLength": 8, "requireLetter": true, "requireDigit": true, "message": "密码至少 8 位且包含字母和数字" },
  "platformCode": { "pattern": "^[A-Za-z]{6}$", "message": "平台编码需为 6 位字母" },
  "discountRate": { "pattern": "^\\d+(\\.\\d{1,2})?$", "min": "0.01", "max": "10", "message": "折扣率需为 0.01-10，最多两位小数" },
  "hexColor":  { "pattern": "^#[0-9a-fA-F]{6}$", "message": "请使用 #RRGGBB 格式的颜色" },
  "url":       { "maxLength": 512, "message": "链接长度不能超过 512" }
}
```

#### 生成物

| 端 | 文件 | 内容 |
|---|---|---|
| 后端 | `Collaboration/Domain/Validation/ValidationPatterns.cs` | 正则与范围常量，供各 Validator 引用 |
| 后台 | `apps/admin-vue/src/utils/validation-rules.js` | 供 `validators.js` 引用 |
| 小程序 | `apps/user-uniapp/src/common/validation-rules.js` | 同上 |


---

## 4. 仓储模式（Infrastructure 层）

| 项 | 规则 |
|---|---|
| 接口位置 | `Domain/IRepository/` |
| 实现位置 | `Infrastructure/Repository/`，注入 `IFreeSql` |
| 简单 CRUD | 继承 `IBaseRepository<T>` / `BaseRepository<T>` |
| 分页组合查询 | 定义领域方法（如 `QueryPagedAsync`），实现用 `WhereIf` / `Count` / `Page`，**返回 `(List<T> Items, long Total)`** |
| total 一致性 | **total 与取数必须同条件** |
| DI 注册 | 仓储 → `Infrastructure.AddInfrastructure(builder)`；应用服务 → `Application.AddApplication(builder)`；两者必须在 `Program.cs` 的 `Build()` **之前**调用 |

**陷阱**：新增仓储忘记注册 DI → 服务启动即崩，**编译期发现不了**。新增仓储后必须实际启动一次验证。

---

## 5. 注释规范（强制）

注释的目标读者是**下一个改这段代码的人**：他需要知道「这段代码为什么存在、在整条链路的什么位置、有哪些不能踩的约束」。
**禁止**写「这行做了什么」的废话注释。

### 5.0 注释完备性（强制检查项，缺一即视为未完成）

**接口、方法、字段三类必须 100% 有 XML 注释。** 评审时按本表逐项核对：

| 对象 | 必须写 | 注释要点 |
|---|---|---|
| 接口 / 类 / 枚举 | `/// <summary>` | 职责 + 在链路中的位置 + 关键约束（锁/幂等/租户/事件） |
| **接口的每个方法** | `/// <summary>` + `/// <param>` + `/// <returns>` | 语义、输入约束、返回含义、**是否幂等 / 有副作用** |
| **类的每个方法（含私有）** | `/// <summary>` | 用途、输入输出、边界条件；私有方法说明「**为什么存在**」 |
| **DTO / Command / Query / 实体的每个字段与属性** | `/// <summary>`（或紧随其后的行内说明） | 业务含义、单位、取值/枚举含义、默认值、可空语义 |
| 枚举的每个成员 | `/// <summary>` | 触发条件与去向 |
| 控制器每个动作 | `/// <summary>` | 接口用途、入参、权限点、协议差异（HTTP 200+code vs 400） |
| Validator 类 | `/// <summary>` | 覆盖哪些字段、什么规则、与实体长度/范围的对齐关系 |
| 仓储接口方法 | `/// <summary>` + `/// <param>` + `/// <returns>` | 过滤条件、分页语义、返回元组含义 |
| MQ 消费者 | `/// <summary>` | 队列名、绑定事件、幂等键、失败策略 |

**判断标准**：打开任意一个文件，**不看实现**也能凭注释知道每个字段能填什么、每个方法会做什么、失败会发生什么。

`[Description]` 特性（实体列描述）可以与 XML 注释并存，但**不能替代**字段注释。

### 5.1 状态字段必须列全状态值

实体里的状态字段，注释中要**列全所有状态码与含义**，并说明每个状态的触发条件与去向。

示例（订单状态）：

```csharp
/// <summary>
/// 订单状态。
/// </summary>
/// <remarks>
/// 10 待支付：下单成功；20 待发货：支付成功；30 待收货：商户发货；
/// 40 待取货：自提备货完成；50 已完成：确认收货或取货核销；
/// 60 已退款：退款审批通过；91 已取消：主动取消或超时关单。
/// </remarks>
public int OrderStatus { get; set; }
```

### 5.2 必须写行内注释（`//`）的位置

| # | 场景 | 示例 |
|---|---|---|
| 1 | **分布式约束** | 为什么加锁、锁键含义、锁内为什么要重读（"锁内重读避免用旧状态覆盖关单结果"） |
| 2 | **幂等设计** | 幂等键放哪、重复投递如何去重（"流水按 BizNo+SKU+动作 幂等"） |
| 3 | **补偿逻辑** | 失败时回滚什么、bizNo 后缀如何防重（"`:lock-compensate` 后缀防重"） |
| 4 | **业务规则来源** | 魔法数字/限制的业务原因（"第三次循环说明会创建第四级 → 分类不能超 3 级"） |
| 5 | **安全考量** | 为什么强制覆盖某字段（"强制 UserId = tenant.UserId，避免替他人支付"） |

### 5.3 禁止

- 把需求原文、提交记录、"修复了 xxx bug" 写进注释（那属于文档 / 提交信息）。
- 注释与代码行为不一致——**改代码必须同步改注释**（见 5.4）。
- 空话注释：`// 获取用户`、`// 遍历列表`。

### 5.4 同步义务

修改业务逻辑时，**同一次改动内**必须更新受影响的类头与步骤注释；
`REVIEW.md` 记录的链路若受影响，也要同步修订对应小节。

### 5.5 注释扫描

提交前运行注释完备性扫描，确认新增代码的**缺注释条目为 0**。扫描脚本随实现一并提供。

---

## 6. 已知陷阱

以下每一条都是本项目**必须在设计阶段规避**的坑。

| # | 陷阱 | 说明 |
|---|---|---|
| 1 | **命名空间遮蔽实体** | `Features/{User\|Address\|Favorite\|Permission\|Category\|Product}` 段会遮蔽同名实体（CS0118）→ 用 `using XxxEntity = ...` 别名 |
| 2 | **FreeSql 分页参数** | `.Page(pageNumber, pageSize)` 第一个参数是**页码**，不是偏移量。传错会导致第二页无数据 |
| 3 | **雪花 ID 比较** | 雪花 Id 全局 JSON 配置自动转字符串；Handler 内用 `long` 比较，**不要 `ToString()` 后比** |
| 4 | **泛型不一致** | `IRequest<T>` 与 Handler 泛型必须一致（改返回类型时两处同步，否则 CS0311） |
| 5 | **仓储重载** | `IBaseRepository.UpdateAsync(entity)` 是单参；带 `CancellationToken` 的重载只有部分服务自定义接口才有 |
| 6 | **后端数字按字符串下发** | `JsonNumberHandling.WriteAsString`：前端比较/回填枚举、金额、数量必须先 `Number()`，否则 `el-radio`/`el-select` 严格比较不回显、`===` 判断失效；但 **ID 禁止 `Number()`**（雪花 Id 超出 JS 安全整数会丢精度），仅"是否为 0"可用 `Number(id) > 0` 判断 |
| 7 | **种子代码不依赖雪花 AOP** | 服务启动阶段 Yitter 尚未初始化会 NRE；新增固定主键时用「当前最大 Id + 1」 |
| 8 | **列表必须显式 ORDER BY** | PostgreSQL 无排序时按堆物理顺序返回（更新/清理后变化），表现为"随机排序"。营销引擎的活动遍历也按 `CreatedAt, Id` 固定，保证同优惠力度/满赠兜底时结果确定 |
| 9 | **购物车加购是累加语义** | `/carts/Add` 调用方传**本次增量**（加购传购买数量、购物车加减传 ±1），**不要传"目标数量"**，否则出现倍数增长；累计上限 99 由服务端与前端双重校验 |
| 10 | **字段级更新必须用 SetDto** | `UpdateColumns(a => obj)` 对捕获的匿名对象解析不出列，会**生成空 SET 静默不更新**（历史缺陷：活动启停/软删全部失效但接口仍返回成功）。正确写法 `freeSql.Update<T>().Where(...).SetDto(obj)`，**且更新后必须断言数据库** |
| 11 | **营销读路径走快照缓存** | 配置/启用状态/范围按平台缓存；写操作（保存活动、启停、保存配置）**必须调用 `Invalidate(platformId)`**。快照**不缓存时间窗口**，使用时按 `now` 过滤，避免活动提前/延迟生效 |
| 12 | **账号域禁止混用** | 客户账号只能进 CustomerService（`/customers/*`，客户 JWT），后台账号只能进 UserService + AuthService（`/users/*` 管理、`/auth/Token` 登录，OpenIddict 令牌）；后台建号禁止 `customer` 角色，客户账号从后台登录必须拒绝 |
| 13 | **统一上传入口** | 所有文件上传（商品图/装修图/头像/评价图/后续附件）一律走 ToolService `/gateway/files/Upload`，业务服务**禁止自建上传接口**；格式与分类大小限制在 AgileConfig `FileStorage:*`；校验顺序为扩展名白名单 → 大小 → 文件头魔数 |
| 14 | **权限只认显式绑定** | 禁止用账号字段（如旧 `User.Role`）做权限兜底；`user_role` 无绑定即无后台权限（**fail-closed**），全局超管通过 `platform-admin` + `PlatformId=0` 的显式绑定表达。后台登录与客户登录严格分离，历史 issuer/兜底分支一律删除 |
| 15 | **OpenIddict 陷阱** | ① 不要定义与 OpenIddict 自带 `ClientType`（public/confidential）同名的属性——会遮蔽它导致公开客户端仍被要求 client_secret（本项目的业务分类列已改名 `AppCategory`）；② 自定义声明必须 `RegisterClaims(...)` 且 `identity.SetDestinations(AccessToken)` 才会写入令牌；③ `sub` 声明是强制的；④ 令牌端点需 `DisableTransportSecurityRequirement()`（开发经 HTTP 网关）与 `AcceptAnonymousClients()`（SPA 公开客户端）；⑤ 签名必须**非对称（RS256）**，证书用 `LocalSigningCertificate` 共享路径生成，网关读取同一文件验签 |
| 16 | **建表用 SQL 脚本** | 本项目**不使用 FreeSql CodeFirst**。表结构以 `deploy/sql/` 下的初始化脚本为准；新增表/字段必须同步该脚本，且**幂等可重跑** |
| 17 | **本地多表写入无事务** | 订单+明细、商品+SKU、发货单+明细目前是分次写入，中途失败产生半截数据。涉及资金/库存的写入必须显式评估补偿路径，或引入工作单元 |
| 18 | **反向补偿不可省** | 下单链路「占券 → 锁积分 → 锁库存 → 落单」任一步失败**必须逆序回滚**。新增跨服务步骤时同步补上回滚分支与注释 |
| 19 | **金额口径含运费** | 运费不参与优惠计算，但在订单实付里**单列加总**。新增金额字段时明确它属于「商品维度」还是「含运费总额」，退款上限校验用含运费口径 |
| 20 | **实体属性必须显式列映射** | 每个持久化属性都要写 `[Column(Name = "...")]`。漏写时 FreeSql 直接拿 C# 属性名当列名（`PerOrderLimit` → `perorderlimit`），PostgreSQL 报 `column a.PerOrderLimit does not exist`，整条查询 500。写 SQL 建表脚本时按实体逐个属性核对列名 |
| 21 | **Validator 必须显式注册** | 本项目的校验器写成 `private` 嵌套类，`AddValidatorsFromAssembly` **扫不到**，只写不注册 = 完全没有校验。活动能带着「满减没填金额」「类型填 9」「结束时间早于开始时间」建出来，用户看到的是「命中活动却没便宜」。新增 Feature 必须在 `AddXxxValidators` 里逐条 `AddScoped`，不要只写 `AddValidatorsFromAssembly` |
| 22 | **RuleFor 必须是具体属性表达式** | 写 `RuleFor(x => x.Amount)`；写成方法调用 `RuleFor(x => amount(x))` 时 FluentValidation 提不出属性名，错误键变成**空字符串**，响应形如 `{"errors":{"":["满减活动必须填写优惠金额"]}}`。前端拿空键没法把提示挂到对应输入框下，等于「校验失败但用户不知道哪一栏错了」。抽共用校验辅助方法时要特别小心这一点 |
| 23 | **优惠额必须分摊到行** | 「满 100 减 20」遇到三行各 50 元，正确是各减 6.67；写成「每行各减 20」优惠额变成 60 元，直接减穿商家。券优惠与活动优惠都是**整单一个金额**，按行金额比例分摊、余数给金额最大的那行，且必须断言「各行之和 == 整单优惠」 |
| 24 | **时间一律存 UTC** | 带偏移量的时间串（`2026-10-03T19:00:00+08:00`）反序列化后 Kind=Local 且时钟值已是本地时间，直接存进 UTC 列会差一个时区。症状是「刚建的活动立刻提示不在活动时间」——代码看起来完全没问题。API 入口统一 `ToUtc`；改库调时间写 `now() AT TIME ZONE 'UTC'` 而不是 `now()` |
| 25 | **展示价必须按单个 SKU 独立定价** | 门槛按「适用行金额合计」判，所以**只要把多行放进同一次试算，优惠就会被摊到它们之间**。商品列表 / 详情要把所有 SKU 拍平成一次请求时就会出错：「满 100 减 20」遇到 200 元与 100 元两个 SKU，按合计 300 判过、20 元摊开 → 卡片显示 186.67 / 93.33，而用户真下单时只买一件、实付 180 / 80。**商品卡上的价比实付价低 = 标价不符投诉**。做法：用 `/marketing/activities/FinalPriceBatch`（分组批量，**每组独立算**），分组单位是**单个 SKU**——按商品分组也不行，同一商品的不同规格之间同样会被摊，顾客买的是一件不是全部规格 |
| 26 | **路由属性不能被吞进 XML 注释** | `/// </remarks>    [HttpPost("X")]` 挤在一行时，属性会变成**注释的一部分**。这能**正常编译、零警告**，但那个接口在运行时直接 404——编译期完全看不出来，只有调用它的人才发现。加接口后如果发现「明明有这个方法却 404」，先查属性是不是跟注释粘在一行 |
| 27 | **调外部 JSON API 时用 Dictionary，别用匿名对象** | 匿名对象 + `JsonSerializerOptions` 会被**命名策略改写**属性名：C# 里写 `@bool` 规避关键字，序列化出来是 `Bool`；`settings` 变成 `Settings`。ES 8 严格区分大小写，于是报 `unknown key [...] for create index`——**报错完全指不到真正原因**。原始字符串插值更糟：`$$"""` 的 `{{ }}` 定界符会和 JSON 的 `}}` 撞车（CS9007）。做法：用 `Dictionary<string, object?>` 构造请求体，键由序列化器原样写出；或直接写裸 JSON 字符串 |
| 28 | **搜索索引只召回 Id，权威数据回库取** | 商品价格与上下架会是变的，索引副本必然有滞后窗口。直接读副本就会出现「搜索结果显示有货、点进去已下架」「列表 99、结算 129」。索引 mapping 里**刻意不存价格**，搜到 Id 后一律回 PostgreSQL 取，并把「审核通过 + 已上架」**再过滤一次**（索引可能滞后于库） |
| 29 | **索引写失败要留兜底** | 「索引写失败只记日志、不阻塞业务保存」是对的（商品保存是主链路，ES 只是加速），代价是索引会慢慢漂移。所以必须有**对账任务**兜底：库里有索引没有 → 补写，索引有库里没有 → 清掉。做**差集对账**而不是「删索引重建」——重建期间索引是空的，那段时间用户搜索会得到零结果 |
| 30 | **URL 里的变量用 `$($x)` 显式插值** | `".../_doc/$id?refresh=true"` 里的 `?` 紧跟变量名，解析结果不符合预期：请求打到别的路径、静默无效，而现象是「计数就是没变」，排查方向会被带偏到数据上。凡是变量后面还要跟非标识符字符，一律写 `$($id)` |
| 31 | **ES 写入用 refresh=false 时，统计前必须 refresh** | 批量写不逐条 refresh（否则大批量补写慢到不可接受），但这意味着写完立刻 `_count` 拿到的是**陈旧值**。refresh 必须在写操作**之后**：放在之前会看到「补完了但计数没涨」，像是对账没生效 |
| 32 | **唯一约束冲突要沿异常链找，不能按类型 catch** | FreeSql 的 `AdoProvider` 在部分路径上把 `Npgsql.PostgresException` **包进普通 `Exception`** 再抛，最外层类型是 `System.Exception`。写 `catch (PostgresException ex) when (ex.SqlState == "23505")` 会**静默漏掉**这个分支——编译正常、类型也对，唯独运行时不进 catch。真实踩过：秒杀限购的唯一索引冲突没被识别，重复抢购返回 HTTP 500 而不是「超出限购」，而限购恰恰是防超卖的最后一道防线。做法：一律用 `PostgresErrors.IsUniqueViolation(ex)` / `IsUniqueViolationOn(ex, "索引名")`，它沿 `InnerException` 链逐层找（带 10 层上限防异常环死循环） |
| 33 | **实体基类的列，建表脚本必须逐个核对** | FreeSql 的实体映射来自 C# 类，**根本不看建表脚本**，所以「实体继承 `AdminEntityBase` 但建表漏了 `created_by_id`」能一路通过编译、启动、健康检查，直到第一次读写那张表才 `42703`（HTTP 500）。真实踩过：`seckill_grab` 漏 4 个审计列，秒杀抢购整条链路 500。新增实体后跑 `./scripts/check-table-columns.ps1`，它解析源码拿到「表名 → 基类」再核对实际列。注意基类不同要求也不同（`Order : EntityBase` 有 `platform_id` 但**不需要**操作人列），别按列名硬猜 |
| 34 | **Redis `INCR` 分配 workerId 必然用尽** | `INCR snowflake:worker:{服务}` 单调递增且不回绕，而 workerId 空间只有 64 个。每次启动消耗一个 → 开发机跑满 64 次重启后该服务**永久无法启动**，唯一修复是手工去 Redis 删 key；生产滚动发布几十次后同样结局。真实踩过：UserService 拿到 `workerId=64` 直接启动失败。做法：改用 `WorkerIdLease` 的**租约槽位**——`SET {key} {令牌} NX EX 90` 抢占空闲槽位 + 每 30 秒比对令牌续租。槽位随进程消失自动回收，于是「同时存活不撞号」与「重启多少次都能起来」同时成立。释放时**必须比对令牌**再 `DEL`，否则会把别人的租约删掉；续租同理，否则会把别人的槽位续长 |
| 35 | **虚拟商品不能校验收货信息** | 收货人 / 电话 / 地址只对**实物**（快递、自提）必填。虚拟商品没有物流也没有收货人，若无条件校验，虚拟订单与虚拟秒杀会 100% 下单失败。秒杀更严重：抢购是一键动作，用户在小程序上根本没有填地址的机会。做法：FluentValidation 里用 `When(x => x.DeliveryType != DeliveryTypes.Virtual, () => { ... })` 包起来 |
| 36 | **`-notin` / `-in` 不认通配符** | `$x -notin @('1|*', '4|*')` 做的是**字面量**比较，`1|202610...` 不等于 `1|*`，于是**每一条都落进「其它」**，计数全错。断言「没有意外结果」时要用减法（`总数 - 已分类数`）而不是 `-notin`。同理 `-eq '4'` 匹配不到 `"4\|"` 这种带分隔符的拼接返回值——写断言前先 `Group-Object` 打印一次真实分布，别凭想象写 |
| 37 | **客户实体的 `CustomerId` 不能被上下文无条件覆盖** | `CrudRepository.ApplyCreator` 与同类写法会执行 `customer.CustomerId = ctx.UserId`。而本项目接口约定是**客户端在请求体里传 `CustomerId`**（见 `OrderController` / `CartController`），服务端被直接调用（不经网关、没有租户上下文）时 `ctx.UserId = 0`——数据被记到「客户 0」名下，**所有接口都返回成功**，但「我的评价」查不到、追评被归属校验拒掉。真实踩过：评价服务上线后 5 条用例同时挂，查库才发现 `customer_id` 全是 0。做法：Handler 里**显式赋 `CustomerId = request.CustomerId`**；仓储只在 `CustomerId <= 0` 时才从上下文兜底补，且必须写注释说明为什么不能覆盖 |
| 38 | **端到端测试要按真实状态机路径推进订单** | 实物快递的路径是 **支付 → 发货 → 确认收货 → 已完成**，少发货那一步，`ConfirmReceipt` 被状态机挡下、订单停在「待发货」，后面所有「完成才能评价 / 完成才发积分 / 完成才可退款」的断言都**失去意义却仍显示通过或失败**，排查方向会被完全带偏。同类还有：`ShopSeckillController` 之类的只读接口里**混进写动作**、自提取货必须先「备货完成」再核销。写这类跨服务用例前，先读一遍 `OrderStatusMachine` |
| 39 | **参与 JSON 契约的实体必须显式标 `[JsonPropertyName]`** | `JsonSerializer.Deserialize<T>(json)` 用的是默认选项，`PropertyNameCaseInsensitive` 默认为 **false**。契约里键是小写 `name`、C# 属性是 `Name` 时**绑不上**，反序列化出来全是默认值。真实踩过：地区数据的校验报「每一级地区都必须填写名称」，而用户每一级都填了名称——症状与病因隔了三层，几乎不可能往回找。凡是参与 JSON 契约的实体（小程序上传、运营导入、跨服务传输），键名一律显式标注，**不要依赖序列化器的默认行为**。注意 Web 项目全局把 `PropertyNameCaseInsensitive` 设成了 true，所以问题**只在显式 `Deserialize<T>(json)` 时暴露**，单元测试能复现、跑在服务里却不容易发现 |
| 40 | **乐观条件要带「期望值」，不是「读到的当前值」** | 写「条件更新时把当前状态当条件」看着能防并发覆盖，实际等于「用当前状态去覆盖」，拦不住任何东西——因为读到的就是当前的。商户审核踩过：把读到的 `AuditStatus` 当条件传下去，于是**已拒绝的商户可以直接被改成已通过**，绕过「拒绝 → 重新提交 → 再审核」，而且拒绝时下架过的商品再也不会恢复。正确做法：条件里带**业务上唯一合法的源状态**（审核只能从「待审核」出发），并额外加一道显式前置判断，让失败原因可读 |
| 41 | **「通用 + 各自专属」的分层要显式建一层** | 装修组件库按页面分三层：**通用**（三页都能用）+ 首页专属 + 我的页专属 + 店铺页专属。一开始只按「首页能用的」去注册，结果**我的页与店铺页少了轮播图 / 标题栏 / 分割线**这些基础组件，搭建器画布上根本拖不出来——而首页完全正常，冒烟测试看不出任何问题。凡是「某集合 ⊇ 另一集合 + 另一集合特有」的规则，把交集**显式命名**出来（如 `AllPages`），不要让每个组件各写各的页面数组 |
| 42 | **C# 方法名不能用 🔴 这类符号** | `public void 🔴 某某用例()` 直接 CS1056 意外字符。`🔴` 只能出现在**字符串字面量**里（PowerShell 的用例显示名、文档），不能进标识符。单测用例的重点靠**类名 + 方法名 + 注释**表达，不是往前缀里塞符号 |
| 43 | **额度占用要把「审批中」算进去** | 算可退余额时只统计**已生效**的单，于是同一订单能连开 N 张退款申请，每张申请时都校验通过，最后只有一张批得过，其余永远卡在待审批——用户填完原因提交才发现余额不够。申请阶段要把「待审批」也算作已占用（当场拒），而**审批阶段必须只算已退款**（否则本单自己算进去，就变成「这笔永远超过它自己」而永远批不过）。一个地方 `includePending=true`、另一个 `false`，两处都要写注释说明为什么相反 |
| 44 | **断言「因某规则被拒」要看 errors，不是 message** | 校验失败时 `message` 统一是「请求参数校验失败」，真实原因在 `errors` 字典里（DATA_SPEC 3.5）。断言写成 `$r.message -match '退款原因需为 2~200'` 就永远匹配不上，看起来像「校验没生效」。写这类断言前先打一次真实响应体，别凭想象写 |
| 45 | **用例要验证目标规则，而不是「被什么东西挡住了」** | 传 `orderItemId = 0` 会被参数校验先拦下，用例看到「返回 400、没有退款单」就判绿了——但真正要验的「退款窗口拦住部分退款」**从没被执行**。凡是「预期失败」的用例，要先确认失败原因确实是目标规则（断言错误信息里含该规则的特征词），而不是别的更早的校验 |
| 46 | **「表和仓储方法都齐了」不等于「有人调用」** | 补偿表 `pending_stock_release` 与 `AddPendingReleaseAsync` / `GetDuePendingReleasesAsync` 早就建好，但**没有任何 Handler 调它们**——释放失败时库存直接丢失，且没有任何痕迹。判断一条补偿链路是否真的存在，要查的是**谁在写那张表**，不是表在不在。写完补偿表要立刻接上「写它的那一行代码」和「重试它的那一个任务」，两头缺一不可 |
| 47 | **UTC 列被写进本地时间，症状是「任务跑了但什么都没发生」** | `next_retry_at` 这类 UTC 列一旦混进本地时间(+08)，`NextRetryAt <= UtcNow` 会判定「还没到重试时间」，补偿任务直接跳过——**日志里看不出任何异常**，因为它只是「没查到数据」。造测试数据要用**明确的过去时间**（如 `'2000-01-01'`），不要用 `now()`：数据库的 `now()` 返回带时区的时间，转进 `timestamp without time zone` 列后与应用写入的 UTC 值不同源 |
| 48 | **ES 写入：没给 Id 就必须用 POST，不是 PUT** | `PUT /{index}/_doc` 是「按指定 Id 覆盖写」，不给 Id 会返回 **405**（`allowed: [POST]`）；想让 ES 自己生成文档 Id 只能用 `POST /{index}/_doc`。日志这种不需要业务 Id 的写入要用 POST，**并且失败必须抛异常**——静默吞掉等于 ack 掉这条消息，日志永久丢失且没人知道。真实踩过：整条 pv/operation 链路 100% 落进死信队列 |
| 49 | **ES 8 不能按 `_id` 排序** | ES 8 默认关闭 `_id` 的 fielddata，`sort: [{_id: ...}]` 直接 400（`illegal_argument_exception`）。症状极具误导性：**索引写得好好的、`_count` 也有数、一搜就报错**，极易被误判成「数据没写进去」，排查方向从第一步就错了。想要稳定翻页用 `search_after` + PIT，**不要**为 `_id` 打开 fielddata（会让整个索引内存占用暴涨） |
| 50 | **要 `term` 精确过滤的字段必须显式映射成 keyword** | 不写 mapping 时 ES 动态映射成 `text` + `.keyword` 子字段，而 `{"term":{"service":"X"}}` 打在 `text` 字段上**永远匹配不到**，返回空列表。症状是「日志明明写进去了，按服务名一条都查不出来」，同样会被误判成没写入。凡是会被 `term` / 排序 / 聚合用到的字段（id、编码、状态、类型）一律显式 `keyword`；只做全文模糊搜的（message、stackTrace）才留 `text` |
| 51 | **禁止让 DI 直接注入 `HttpClient`** | 直接注入拿到的是一个**什么都没配**的 HttpClient：没有 `BaseAddress`、没有超时。症状是运行到某个相对地址请求时才抛 `URI must be an absolute URI or BaseAddress must be set`，而 **DI 校验发现不了**（类型确实能解析）。做法：注入 `IHttpClientFactory` 再 `CreateClient("名字")`。注意 `AddHttpClient("名字", ...)` 的具名注册就是为复用同一个连接池，别每个服务各注册一个匿名客户端 |
| 52 | **`Configure<T>(section)` 注册的是 `IOptions<T>`，不是 `T`** | 直接把 Options 类注入 Handler，启动时会被 DI 校验拦下（Development 环境默认 `ValidateOnBuild=true`）。这个拦截是好事——总比等到用户点那个接口才 500 强。统一注入 `IOptions<T>` 再取 `.Value` |
| 53 | **MQ 扫描队列时，`requeue: true` 是放回队头** | 想从死信队列里「捞出指定 EventId 的那一条」，对不上时若用 `BasicNack(requeue: true)`，消息回到**队头**，下一个 `BasicGet` 拿到的又是同一条，循环原地打转、后面全扫不到。真实踩过：死信队列里只要有一条不匹配的消息，重放就**永远失败**。正确做法是「重发到死信交换机 → ack 原消息」让队头腾出来；先 publish 再 ack，中途崩溃最多重复一次、不会丢 |
| 54 | **发布端与消费端的序列化口径必须共用同一份定义** | 两端各写一个 `JsonSerializerOptions` 时，改了一处忘了另一处 →「消息发得出去、但消费端一条都解析不出来」，全部落进死信，而日志里只有一条毫无线索的 `FormatException`。真实踩过：发布端从 MessagePack+base64 换成 JSON 后，消费端还在 `Convert.FromBase64String`。做法：口径收进 `EventJson` 这一个类，并补一条**往返单测**（发布端写出来的信封，消费端能原样读回）把这类漂移钉死 |
| 55 | **覆盖写文档时，人工维护的计数不能被重置** | 死信文档按 `EventId` 覆盖写，而「重放后又失败」会再次覆盖——如果新记录把 `ReplayCount` 重置成 0，计数永远停在 0，**重放上限形同虚设**，一条永远修不好的消息可以被无限重放。症状很隐蔽：单看一次重放是成功的。做法：覆盖前先读旧记录，**沿用**人工维护过的字段（重放次数、上次重放时间），只覆盖系统字段 |
| 56 | **测试断言必须能读到 400 的响应体** | `Invoke-RestMethod` 遇 400 会抛异常，测试里只看到「400 Bad Request」。于是「校验按预期拦住了」和「服务整个挂了」在报告里长得一模一样——**前者是预期、后者是故障**，混淆它们等于把回归测试变成摆设。PowerShell 7 里要从 `$_.ErrorDetails.Message` 取响应体（`Exception.Response.GetResponseStream()` 此时已被读空并释放）。配套：断言要落到 `errors` 里的**具体字段名**，不能只看 `success=false` |
| 57 | **「写了 ES」不等于「立刻查得到」** | ES 默认 `refresh_interval` 1 秒，写完立刻 `_search` / `_count` 拿到的是陈旧值。查一次就断言会把「还没刷新」误判成「没写进去」，写成失败的测试逼着人去改本来正确的代码。做法：断言一律**轮询等待**（带超时），而不是查一次就下结论。同理，**mapping 只在建索引时生效**——改了 mapping 必须先删索引重建 |

---

## 7. 分层落地检查表

新建服务或新增功能时逐项自检：

| # | 检查项 |
|---|---|
| 1 | 四个项目已创建且引用方向符合 1.1（**Api 不得引用 Domain**） |
| 2 | `Program.cs` 在 `Build()` 前调用了 `AddInfrastructure` 与 `AddApplication` |
| 3 | MediatR 管道（`ValidationBehavior`）已注册 |
| 4 | 每个动作目录含 Command + Handler + Validator |
| 5 | Validator 与 Command 同批提交，覆盖所有写入口与列表查询 |
| 6 | Handler 注释写明链路位置、失败语义、幂等性、锁与补偿 |
| 7 | 接口 / 方法 / 字段 / 枚举成员 XML 注释 100% 覆盖 |
| 8 | 跨服务调用封装为 `Application/Services/*Client`，Handler 内不写 gRPC 客户端代码 |
| 9 | 新增表/字段已写入 `deploy/sql/` 初始化脚本（幂等可重跑） |
| 10 | 新增接口已在网关路由表与权限点种子中登记 |
| 11 | 新增 MQ 消费方已配置 **DLQ** 与幂等键 |
| 12 | 服务实际启动过一次（验证 DI 注册，不是只看编译通过） |
| 13 | 单元测试覆盖核心算法分支（优惠引擎、库存、状态机、积分、秒杀库存） |
| 14 | 相关文档已同步（`BUSINESS.md` / `REVIEW.md` / 本文件 / `AI_HANDOFF.md`） |

