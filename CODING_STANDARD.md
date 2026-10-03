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

