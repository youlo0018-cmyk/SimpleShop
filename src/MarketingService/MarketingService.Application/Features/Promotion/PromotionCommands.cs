using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.Entities;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Promotion;

/// <summary>新建营销活动。</summary>
/// <param name="ActivityName">活动名，2-128 字符。</param>
/// <param name="ActivityType">活动类型，见 <see cref="ActivityTypes"/>。</param>
/// <param name="ThresholdAmount">门槛金额，0 表示无门槛。</param>
/// <param name="DiscountAmount">优惠金额（满减用）。</param>
/// <param name="DiscountRate">折扣率（满折用），0.01 ~ 10，8.5 表示 85 折。</param>
/// <param name="GiftTemplateId">满赠赠送的券模板 Id。</param>
/// <param name="SessionId">场次 Id，普通活动传 0。</param>
/// <param name="TargetType">适用范围类型，见 <see cref="TargetTypes"/>。</param>
/// <param name="Targets">适用范围的 JSON 文本，如 <c>[1001,1002]</c>。</param>
/// <param name="StartTime">开始时间（UTC）。</param>
/// <param name="EndTime">结束时间（UTC）。</param>
/// <param name="PerOrderLimit">每单限购，0 表示不限。</param>
/// <param name="TotalQuantity">总参与人次上限，0 表示不限。</param>
/// <param name="SortOrder">排序。</param>
/// <param name="Status">状态，1 启用 / 2 停用。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id，0 表示平台自营。</param>
public record CreatePromotionActivityCommand(
    string ActivityName,
    int ActivityType,
    decimal ThresholdAmount = 0m,
    decimal DiscountAmount = 0m,
    decimal DiscountRate = 0m,
    long GiftTemplateId = 0,
    long SessionId = 0,
    int TargetType = TargetTypes.All,
    string Targets = "[]",
    DateTime StartTime = default,
    DateTime EndTime = default,
    int PerOrderLimit = 0,
    int TotalQuantity = 0,
    int SortOrder = 0,
    int Status = 1,
    long PlatformId = 0,
    long MerchantId = 0) : IRequest<ApiResponse<long>>;

/// <summary>编辑营销活动。</summary>
/// <param name="ActivityId">活动 Id。</param>
/// <param name="ActivityName">活动名。</param>
/// <param name="ActivityType">活动类型。</param>
/// <param name="ThresholdAmount">门槛金额。</param>
/// <param name="DiscountAmount">优惠金额。</param>
/// <param name="DiscountRate">折扣率。</param>
/// <param name="GiftTemplateId">满赠赠送的券模板 Id。</param>
/// <param name="TargetType">适用范围类型。</param>
/// <param name="Targets">适用范围的 JSON 文本。</param>
/// <param name="StartTime">开始时间（UTC）。</param>
/// <param name="EndTime">结束时间（UTC）。</param>
/// <param name="PerOrderLimit">每单限购。</param>
/// <param name="TotalQuantity">总参与人次上限。</param>
/// <param name="SortOrder">排序。</param>
/// <param name="Status">状态。</param>
public record UpdatePromotionActivityCommand(
    long ActivityId,
    string ActivityName,
    int ActivityType,
    decimal ThresholdAmount,
    decimal DiscountAmount,
    decimal DiscountRate,
    long GiftTemplateId,
    int TargetType,
    string Targets,
    DateTime StartTime,
    DateTime EndTime,
    int PerOrderLimit,
    int TotalQuantity,
    int SortOrder,
    int Status) : IRequest<ApiResponse>;

/// <summary>活动分页（后台）。</summary>
/// <param name="ActivityType">活动类型，0 表示全部。</param>
/// <param name="Keyword">按活动名模糊匹配。</param>
/// <param name="Status">状态，0 表示全部。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryPromotionActivitiesCommand(
    int ActivityType = 0, string Keyword = "", int Status = 0, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedPromotionResult>>;

/// <summary>启停活动。</summary>
/// <param name="ActivityId">活动 Id。</param>
/// <param name="Status">目标状态，1 启用 / 2 停用。</param>
public record SetPromotionStatusCommand(long ActivityId, int Status) : IRequest<ApiResponse>;

/// <summary>删除活动。</summary>
/// <param name="ActivityId">活动 Id。</param>
public record DeletePromotionActivityCommand(long ActivityId) : IRequest<ApiResponse>;

/// <summary>取单个活动。</summary>
/// <param name="ActivityId">活动 Id。</param>
public record GetPromotionActivityCommand(long ActivityId) : IRequest<ApiResponse<PromotionActivityDto>>;

/// <summary>到手价试算（BUSINESS.md 11.5）。</summary>
/// <param name="CustomerId">客户 Id；<b>传 0 表示游客</b>，只算活动价不计券。</param>
/// <param name="Lines">订单行，最多 50 行（列表页整页合并成一次试算）。</param>
/// <param name="SessionId">场次 Id，普通场景传 0。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record CalculateFinalPriceCommand(
    long CustomerId, IReadOnlyList<PromotionOrderLine> Lines, long SessionId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<FinalPriceDto>>;

/// <summary>分组批量试算：<b>每一组独立算</b>，组与组之间不影响。</summary>
/// <param name="CustomerId">客户 Id；0 表示游客。</param>
/// <param name="Groups">若干组订单行，每组是一次独立的试算。</param>
/// <param name="SessionId">场次 Id，普通场景传 0。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <remarks>
/// <para><b>为什么不能把整页商品塞进一次普通试算</b>：门槛是按「适用行金额合计」判的。
/// 「满 100 减 20」遇到一页里的 200 元和 100 元两个商品，普通试算会把它们当成一单，
/// 门槛按合计 300 判定通过，然后把 20 元按比例摊到两件上——200 元那件显示 186.67，
/// 100 元那件显示 93.33。用户在商品卡上看到的「到手价」于是<b>比他自己买一单真实拿到的要便宜</b>，
/// 点进去下单发现价格对不上，这是最典型的「标价与实付不符」投诉。</para>
///
/// <para>所以列表页要的是「每个商品单独算一次价」，而不是「一页商品算一次价」。
/// 分组批量就是为这个场景准备的：<b>一次 HTTP 调用</b>（列表页每屏几十个商品，
/// 逐个商品调就是几十次跨服务调用），但每组各自判定门槛、各自分摊。</para>
/// </remarks>
public record CalculateFinalPriceBatchCommand(
    long CustomerId, IReadOnlyList<IReadOnlyList<PromotionOrderLine>> Groups,
    long SessionId = 0, long PlatformId = 0)
    : IRequest<ApiResponse<IReadOnlyList<FinalPriceDto>>>;

/// <summary>参与到手价试算的一行。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">该行金额（单价 × 数量），已含数量，两位小数。</param>
public readonly record struct PromotionOrderLine(long SpuId, long SkuId, decimal Amount);

/// <summary>活动详情。</summary>
/// <param name="Id">活动 Id。</param>
/// <param name="ActivityName">活动名。</param>
/// <param name="ActivityType">活动类型。</param>
/// <param name="TypeName">活动类型中文名。</param>
/// <param name="ThresholdAmount">门槛金额。</param>
/// <param name="DiscountAmount">优惠金额。</param>
/// <param name="DiscountRate">折扣率。</param>
/// <param name="TargetType">适用范围类型。</param>
/// <param name="TargetName">适用范围中文名。</param>
/// <param name="StartTime">开始时间。</param>
/// <param name="EndTime">结束时间。</param>
/// <param name="Status">状态。</param>
/// <param name="StatusName">状态中文名。</param>
public sealed record PromotionActivityDto(
    long Id, string ActivityName, int ActivityType, string TypeName,
    decimal ThresholdAmount, decimal DiscountAmount, decimal DiscountRate,
    int TargetType, string TargetName,
    string StartTime, string EndTime, int Status, string StatusName);

/// <summary>活动分页结果。</summary>
/// <param name="Items">当页活动。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedPromotionResult(
    IReadOnlyList<PromotionActivityDto> Items, long Total, int Page, int PageSize);

/// <summary>到手价结果。</summary>
/// <param name="OriginalTotal">原价合计。</param>
/// <param name="ActivityDiscount">活动优惠合计。</param>
/// <param name="CouponDiscount">券优惠合计。</param>
/// <param name="FinalPrice">到手价合计，<b>不含运费</b>。</param>
/// <param name="CouponId">选中的券 Id，0 表示没用券。</param>
/// <param name="CouponCode">选中的券码。</param>
/// <param name="UsedActivity">是否命中了活动。</param>
/// <param name="IsVisitor">是否按游客口径计算（只算活动不计券）。</param>
/// <param name="Lines">逐行拆分。</param>
public sealed record FinalPriceDto(
    decimal OriginalTotal, decimal ActivityDiscount, decimal CouponDiscount, decimal FinalPrice,
    long CouponId, string CouponCode, bool UsedActivity, bool IsVisitor,
    IReadOnlyList<FinalPriceLineDto> Lines);

/// <summary>到手价的逐行拆分。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="OriginalAmount">原价。</param>
/// <param name="ActivityDiscount">活动优惠额。</param>
/// <param name="CouponDiscount">分摊到该行的券优惠额。</param>
/// <param name="PayableAmount">到手价，封底 0.01。</param>
/// <param name="Source">优惠来源标签。</param>
/// <param name="SourceName">来源名称（活动名或券码）。</param>
public sealed record FinalPriceLineDto(
    long SpuId, long SkuId, decimal OriginalAmount,
    decimal ActivityDiscount, decimal CouponDiscount, decimal PayableAmount,
    string Source, string SourceName);

/// <summary>营销活动的校验规则与注册入口。</summary>
/// <remarks>
/// <para><b>必须显式注册</b>，不能指望 <c>AddValidatorsFromAssembly</c>。
/// 踩过：只写校验器、不注册，活动就能带着明显非法的配置建出来
/// （满减没填金额、活动类型填 9、结束时间早于开始时间，全部建成功）。
/// 症状是「活动命中了却什么也不减」，用户看到的是已参与活动却没便宜。</para>
///
/// <para>原因是本项目的校验器都写成 <b>private 嵌套类</b>（见 <c>CouponValidators</c>），
/// 而程序集扫描依赖类型可被枚举到，私有嵌套类不在其列。
/// 券那一组之所以一直有效，正是因为它同样做了显式注册——两张校验器写法一样，
/// 差别只在这一个显式注册调用。</para>
/// </remarks>
public static class PromotionValidators
{
    /// <summary>单次试算最多多少行。列表页整页商品的合并试算上限。</summary>
    public const int MaxLinesPerRequest = 50;

    /// <summary>注册全部营销活动校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddPromotionValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreatePromotionActivityCommand>, CreatePromotionValidator>();
        services.AddScoped<IValidator<UpdatePromotionActivityCommand>, UpdatePromotionValidator>();
        services.AddScoped<IValidator<QueryPromotionActivitiesCommand>, QueryPromotionValidator>();
        services.AddScoped<IValidator<SetPromotionStatusCommand>, SetPromotionStatusValidator>();
        services.AddScoped<IValidator<DeletePromotionActivityCommand>, DeletePromotionValidator>();
        services.AddScoped<IValidator<GetPromotionActivityCommand>, GetPromotionValidator>();
        services.AddScoped<IValidator<CalculateFinalPriceCommand>, FinalPriceValidator>();
        services.AddScoped<IValidator<QuoteOrderDiscountCommand>, QuoteOrderDiscountValidator>();
        services.AddScoped<IValidator<CalculateFinalPriceBatchCommand>, FinalPriceBatchValidator>();
    }

    /// <summary>新建校验。</summary>
    private sealed class CreatePromotionValidator : AbstractValidator<CreatePromotionActivityCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreatePromotionValidator()
        {
            RuleFor(x => x.ActivityName).NotEmpty().MinimumLength(2).MaximumLength(128)
                .WithMessage("活动名 2-128 个字符");

            // 下面每条 RuleFor 都必须写成 `x => x.某个属性` 的**具体属性表达式**。
            //
            // 踩过的坑：原本抽了一个共用的泛型辅助方法，内部是
            // `RuleFor(x => amount(x))` 这种**方法调用表达式**。FluentValidation
            // 从表达式里提取属性名，方法调用提不出来，于是错误键变成空字符串，
            // 响应变成 {"errors":{"":["满减活动必须填写优惠金额，且大于 0"]}}。
            // 前端拿到空键就没法把这句提示挂到对应输入框下面——而那正是需求里
            // 「提交校验失败时输入框下方备注失败原因」要的东西。
            // 校验失败但用户不知道是哪一栏错了，比不校验还糟。
            // 括号是必需的：关系模式（>= A and <= B）与相等模式（== C）之间要用 or 连接时，
            // 必须把关系模式整体括起来，否则编译器报 CS9135「应为 int 类型的常量值」。
            RuleFor(x => x.ActivityType).Must(a =>
                    a is (>= ActivityTypes.FullReduction and <= ActivityTypes.Discount) or ActivityTypes.Gift)
                .WithMessage("活动类型不正确");

            RuleFor(x => x.ActivityType).Must(a => a != ActivityTypes.Seckill)
                .WithMessage("限时抢购请通过秒杀场次入口维护，不要用普通活动入口");

            RuleFor(x => x.ThresholdAmount).InclusiveBetween(0m, 9_999_999.99m)
                .WithMessage("门槛金额超出范围");

            // 满减必须给金额、满折必须给折扣率、满赠必须给券模板。
            // 不校验就会出现「建了个满减但没填金额」的活动：它会命中却什么也不减，
            // 用户看到「已参与活动」却没便宜——这是最容易被投诉的一种配置错误。
            When(x => x.ActivityType == ActivityTypes.FullReduction, () =>
            {
                RuleFor(x => x.DiscountAmount).InclusiveBetween(0.01m, 9_999_999.99m)
                    .WithMessage("满减活动必须填写优惠金额，且大于 0");
            });

            When(x => x.ActivityType == ActivityTypes.Discount, () =>
            {
                RuleFor(x => x.DiscountRate).InclusiveBetween(0.01m, 10m)
                    .WithMessage("满折折扣率必须在 0.01 ~ 10 之间，8.5 表示 85 折");
            });

            When(x => x.ActivityType == ActivityTypes.Gift, () =>
            {
                RuleFor(x => x.GiftTemplateId).GreaterThan(0)
                    .WithMessage("满赠活动必须选择赠送的券模板");
            });

            RuleFor(x => x.TargetType).Must(a => a is >= TargetTypes.All and <= TargetTypes.BySku)
                .WithMessage("适用范围类型不正确");

            // 指定 SPU / SKU 时 Targets 必须能解析成非空数组，否则活动对谁都生效
            RuleFor(x => x.Targets).Must((x, v) =>
            {
                if (x.TargetType == TargetTypes.All) return true;
                var ids = Domain.Services.PromotionCalculator.ParseTargets(v);
                return ids.Count > 0;
            }).WithMessage("指定商品范围时必须填写商品 Id 列表");

            RuleFor(x => x.StartTime).NotEqual(default(DateTime)).WithMessage("请填写活动开始时间");
            RuleFor(x => x.EndTime).NotEqual(default(DateTime)).WithMessage("请填写活动结束时间");
            RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("活动结束时间必须晚于开始时间");
            RuleFor(x => x.PerOrderLimit).InclusiveBetween(0, 100).WithMessage("每单限购次数不合法");
            RuleFor(x => x.TotalQuantity).InclusiveBetween(0, 10_000_000).WithMessage("参与人次上限不合法");
            RuleFor(x => x.Status).Must(a => a is 1 or 2).WithMessage("状态不正确");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户 Id 不能为负数");
        }
    }

    /// <summary>编辑校验。规则与新建一致，外加活动 Id 必须为正。</summary>
    private sealed class UpdatePromotionValidator : AbstractValidator<UpdatePromotionActivityCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdatePromotionValidator()
        {
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("活动 Id 必须为正数");
            RuleFor(x => x.ActivityName).NotEmpty().MinimumLength(2).MaximumLength(128)
                .WithMessage("活动名 2-128 个字符");

            // 下面每条 RuleFor 都必须写成 `x => x.某个属性` 的**具体属性表达式**。
            //
            // 踩过的坑：原本抽了一个共用的泛型辅助方法，内部是
            // `RuleFor(x => amount(x))` 这种**方法调用表达式**。FluentValidation
            // 从表达式里提取属性名，方法调用提不出来，于是错误键变成空字符串，
            // 响应变成 {"errors":{"":["满减活动必须填写优惠金额，且大于 0"]}}。
            // 前端拿到空键就没法把这句提示挂到对应输入框下面——而那正是需求里
            // 「提交校验失败时输入框下方备注失败原因」要的东西。
            // 校验失败但用户不知道是哪一栏错了，比不校验还糟。
            // 括号是必需的：关系模式（>= A and <= B）与相等模式（== C）之间要用 or 连接时，
            // 必须把关系模式整体括起来，否则编译器报 CS9135「应为 int 类型的常量值」。
            RuleFor(x => x.ActivityType).Must(a =>
                    a is (>= ActivityTypes.FullReduction and <= ActivityTypes.Discount) or ActivityTypes.Gift)
                .WithMessage("活动类型不正确");

            RuleFor(x => x.ActivityType).Must(a => a != ActivityTypes.Seckill)
                .WithMessage("限时抢购请通过秒杀场次入口维护，不要用普通活动入口");

            RuleFor(x => x.ThresholdAmount).InclusiveBetween(0m, 9_999_999.99m)
                .WithMessage("门槛金额超出范围");

            // 满减必须给金额、满折必须给折扣率、满赠必须给券模板。
            // 不校验就会出现「建了个满减但没填金额」的活动：它会命中却什么也不减，
            // 用户看到「已参与活动」却没便宜——这是最容易被投诉的一种配置错误。
            When(x => x.ActivityType == ActivityTypes.FullReduction, () =>
            {
                RuleFor(x => x.DiscountAmount).InclusiveBetween(0.01m, 9_999_999.99m)
                    .WithMessage("满减活动必须填写优惠金额，且大于 0");
            });

            When(x => x.ActivityType == ActivityTypes.Discount, () =>
            {
                RuleFor(x => x.DiscountRate).InclusiveBetween(0.01m, 10m)
                    .WithMessage("满折折扣率必须在 0.01 ~ 10 之间，8.5 表示 85 折");
            });

            When(x => x.ActivityType == ActivityTypes.Gift, () =>
            {
                RuleFor(x => x.GiftTemplateId).GreaterThan(0)
                    .WithMessage("满赠活动必须选择赠送的券模板");
            });

            RuleFor(x => x.TargetType).Must(a => a is >= TargetTypes.All and <= TargetTypes.BySku)
                .WithMessage("适用范围类型不正确");
            RuleFor(x => x.StartTime).NotEqual(default(DateTime)).WithMessage("请填写活动开始时间");
            RuleFor(x => x.EndTime).NotEqual(default(DateTime)).WithMessage("请填写活动结束时间");
            RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("活动结束时间必须晚于开始时间");
            RuleFor(x => x.Status).Must(a => a is 1 or 2).WithMessage("状态不正确");
        }
    }

    /// <summary>分页查询校验。</summary>
    private sealed class QueryPromotionValidator : AbstractValidator<QueryPromotionActivitiesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPromotionValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数必须在 1 ~ 100 之间");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键字最多 64 个字符");
        }
    }

    /// <summary>启停校验。</summary>
    private sealed class SetPromotionStatusValidator : AbstractValidator<SetPromotionStatusCommand>
    {
        /// <summary>构造校验器。</summary>
        public SetPromotionStatusValidator()
        {
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("活动 Id 必须为正数");
            RuleFor(x => x.Status).Must(a => a is 1 or 2).WithMessage("状态只能是启用或停用");
        }
    }

    /// <summary>删除校验。</summary>
    private sealed class DeletePromotionValidator : AbstractValidator<DeletePromotionActivityCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeletePromotionValidator()
        {
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("活动 Id 必须为正数");
        }
    }

    /// <summary>取详情校验。</summary>
    private sealed class GetPromotionValidator : AbstractValidator<GetPromotionActivityCommand>
    {
        /// <summary>构造校验器。</summary>
        public GetPromotionValidator()
        {
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("活动 Id 必须为正数");
        }
    }

    /// <summary>分组批量试算校验。</summary>
    private sealed class FinalPriceBatchValidator : AbstractValidator<CalculateFinalPriceBatchCommand>
    {
        /// <summary>构造校验器。</summary>
        public FinalPriceBatchValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.Groups).NotEmpty().WithMessage("请至少传一组商品");
            RuleFor(x => x.Groups).Must(a => a.Count <= MaxLinesPerRequest)
                .WithMessage($"单次最多 {MaxLinesPerRequest} 组，请分批请求");
            RuleForEach(x => x.Groups).Must(g => g is not null && g.Count > 0)
                .WithMessage("每一组至少要有一行商品");
        }
    }

    /// <summary>到手价试算校验。</summary>
    private sealed class FinalPriceValidator : AbstractValidator<CalculateFinalPriceCommand>
    {
        /// <summary>构造校验器。</summary>
        public FinalPriceValidator()
        {
            // CustomerId 允许 0：0 就是游客，只算活动不计券（BUSINESS.md 11.5）。
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.Lines).NotEmpty().WithMessage("请至少传一行商品");
            RuleFor(x => x.Lines).Must(a => a.Count <= MaxLinesPerRequest)
                .WithMessage($"单次试算最多 {MaxLinesPerRequest} 行，请分批请求");

            RuleForEach(x => x.Lines).ChildRules(line =>
            {
                line.RuleFor(a => a.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
                line.RuleFor(a => a.SpuId).GreaterThan(0).WithMessage("SPU Id 必须为正数");
                line.RuleFor(a => a.Amount).InclusiveBetween(0m, 9_999_999.99m).WithMessage("商品金额不正确");
            });
        }
    }
}
