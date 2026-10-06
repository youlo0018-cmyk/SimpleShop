using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Application.Features.Reports;
using MarketingService.Domain.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Coupon;

/// <summary>领券。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="ActivityId">券活动 Id。</param>
/// <param name="Quantity">领取张数。</param>
public record ClaimCouponCommand(long CustomerId, long ActivityId, int Quantity = 1)
    : IRequest<ApiResponse<ClaimCouponResult>>;

/// <summary>查询当前可领取的券活动。</summary>
public record QueryAvailableCouponsCommand() : IRequest<ApiResponse<List<CouponActivityItem>>>;

/// <summary>领券结果。</summary>
public sealed record ClaimCouponResult(IReadOnlyList<string> CouponCodes, string Message);

/// <summary>占券（下单时锁定）。CouponId 传 0 表示由服务端自动选最优券。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CouponId">指定券 Id，0 = 自动选最优。</param>
/// <param name="Lines">订单行，用于计算优惠。</param>
public record OccupyCouponCommand(
    long CustomerId, string OrderNo, long CouponId, IReadOnlyList<CouponOrderLine> Lines)
    : IRequest<ApiResponse<CouponOccupyResult>>;

/// <summary>占券结果。</summary>
/// <param name="CouponId">实际占用的券 Id，0 表示没占到。</param>
/// <param name="DiscountAmount">整单券优惠额。</param>
/// <param name="AlreadyApplied">是否命中幂等（重复请求）。</param>
/// <param name="Message">提示文案。</param>
public sealed record CouponOccupyResult(
    long CouponId, decimal DiscountAmount, bool AlreadyApplied, string Message)
{
    /// <summary>
    /// 券优惠的**逐行分摊额**（与结算页同一份算法），长度与订单行一致；未覆盖的行是 0。
    /// </summary>
    /// <remarks>
    /// 订单侧必须**原样使用**：券有作用域（全场 / 指定 SPU / 指定 SKU），
    /// 只有营销侧知道它覆盖了哪几行。订单侧按「全部行原价比例」自己分会把优惠
    /// 摊到作用域外的行上（实测：只减 SKU A 的券被摊成 A、B 各 10 元），
    /// 而部分退款是按行应付退的。
    /// </remarks>
    public IReadOnlyList<decimal> LineDiscounts { get; init; } = [];
}

/// <summary>核销券（支付成功）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="OrderNo">订单号。</param>
public record ConsumeCouponCommand(long CustomerId, string OrderNo) : IRequest<ApiResponse<CouponOccupyResult>>;

/// <summary>回退占券（取消 / 超时关单）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="OrderNo">订单号。</param>
public record ReleaseCouponCommand(long CustomerId, string OrderNo) : IRequest<ApiResponse<CouponOccupyResult>>;

/// <summary>结算试算：列出可用券并标出最优。</summary>
/// <param name="CustomerId">客户 Id，0 表示游客。</param>
/// <param name="Lines">订单行。</param>
/// <param name="CouponId">
/// 客户端**当前选中**的券 Id，0 表示还没选。传了的话会额外回一份这张券的逐行分摊，
/// 让结算页的逐行优惠与下单后的逐行优惠逐分一致（券有作用域，分摊只有营销侧算得对）。
/// </param>
public record SettleCouponsCommand(long CustomerId, IReadOnlyList<CouponOrderLine> Lines, long CouponId = 0)
    : IRequest<ApiResponse<SettleCouponResult>>;

/// <summary>结算试算结果。</summary>
/// <param name="HasCoupon">是否存在可用券。</param>
/// <param name="Best">最优券；无可用券时为 null。</param>
/// <param name="Options">全部可用券及各自优惠额。</param>
public sealed record SettleCouponResult(bool HasCoupon, SettleCouponOption? Best, IReadOnlyList<SettleCouponOption> Options)
{
    /// <summary>
    /// 命令里指定的那张券的**逐行分摊额**，长度与订单行一致；未覆盖的行是 0。
    /// </summary>
    /// <remarks>
    /// 空数组表示「没指定券」或「指定的券不可用」。结算页必须原样使用，
    /// 否则它自己按全行比例分会把优惠摊到券作用域外的行上（下单侧不再这么算了，两边就对不上）。
    /// </remarks>
    public IReadOnlyList<decimal> ChosenLineDiscounts { get; init; } = [];
}

/// <summary>一张可用券在当前订单下的试算结果。</summary>
public sealed record SettleCouponOption(
    long CouponId, string CouponCode, string CouponTypeName, decimal DiscountAmount, string ExpireAt, bool IsBest);

/// <summary>券命令的校验器注册。</summary>
public static class CouponValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCouponValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<ClaimCouponCommand>, ClaimCouponValidator>();
        services.AddScoped<IValidator<OccupyCouponCommand>, OccupyCouponValidator>();
        services.AddScoped<IValidator<ConsumeCouponCommand>, CouponOrderValidator>();
        services.AddScoped<IValidator<ReleaseCouponCommand>, ReleaseCouponValidator>();
        services.AddScoped<IValidator<SettleCouponsCommand>, SettleCouponsValidator>();
        services.AddScoped<IValidator<QueryMarketingReportCommand>, MarketingReportValidator>();
        services.AddScoped<IValidator<IssueGiftGrantsCommand>, IssueGiftGrantsValidator>();
    }

    /// <summary>营销效果报表校验。</summary>
    private sealed class MarketingReportValidator : AbstractValidator<QueryMarketingReportCommand>
    {
        /// <summary>构造校验器。</summary>
        public MarketingReportValidator()
        {
            // 只认 1~4 四档。不校验的话非法档位会在区间换算里抛异常变成 500，
            // 而它本来就是个参数错误，应该返回带中文原因的 400。
            RuleFor(x => x.Range).InclusiveBetween(1, 4).WithMessage("报表时间范围不正确");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户信息不正确");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台信息不正确");
        }
    }

    /// <summary>领券校验。</summary>
    private sealed class ClaimCouponValidator : AbstractValidator<ClaimCouponCommand>
    {
        /// <summary>构造校验器。</summary>
        public ClaimCouponValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("券活动 Id 必须为正数");
            RuleFor(x => x.Quantity).InclusiveBetween(1, 10).WithMessage("单次领取张数必须在 1 ~ 10 之间");
        }
    }

    /// <summary>占券校验。</summary>
    private sealed class OccupyCouponValidator : AbstractValidator<OccupyCouponCommand>
    {
        /// <summary>构造校验器。</summary>
        public OccupyCouponValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
            RuleFor(x => x.CouponId).GreaterThanOrEqualTo(0).WithMessage("券 Id 不能为负数（0 表示自动选最优）");
            // CascadeMode.Stop：默认级联是 Continue，NotNull 失败后 Must 照样执行，
            // 而 l!.Count 只是编译期断言，运行时 null 会直接抛 NullReferenceException
            // ——接口回 500 而不是 400。详见 CODING_STANDARD 第 68 条。
            RuleFor(x => x.Lines).Cascade(CascadeMode.Stop)
                .NotNull().Must(l => l!.Count > 0).WithMessage("订单行不能为空");
        }
    }

    /// <summary>核销 / 回退校验。</summary>
    private sealed class CouponOrderValidator : AbstractValidator<ConsumeCouponCommand>
    {
        /// <summary>构造校验器。</summary>
        public CouponOrderValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
        }
    }

    /// <summary>回退占券校验。形状与核销一致，但类型不同，要单独写一个校验器。</summary>
    private sealed class ReleaseCouponValidator : AbstractValidator<ReleaseCouponCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReleaseCouponValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
        }
    }

    /// <summary>结算试算校验。</summary>
    private sealed class SettleCouponsValidator : AbstractValidator<SettleCouponsCommand>
    {
        /// <summary>构造校验器。</summary>
        public SettleCouponsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不能为负数（0 表示游客）");
            RuleFor(x => x.CouponId).GreaterThanOrEqualTo(0).WithMessage("券 Id 不能为负数（0 表示还没选）");
            RuleFor(x => x.Lines).Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("订单行不能为空")
                .Must(l => l!.Count > 0).WithMessage("订单行不能为空")
                // 列表页整页商品合并成一次批量试算，≤50 行一批。
                // 必须挂在同一条链上（而不是单独 RuleFor(x => x.Lines.Count)）：
                // 后者是**另一个** Rule，级联模式管不到它，Cascade(CascadeMode.Stop)
                // 也救不了——Lines 为 null 时它照样会在取值那一步就抛 NullReferenceException。
                .Must(l => l!.Count <= 50).WithMessage("单次试算最多 50 个订单行");
        }
    }
}
