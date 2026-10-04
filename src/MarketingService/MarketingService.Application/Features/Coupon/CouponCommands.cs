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
public sealed record CouponOccupyResult(long CouponId, decimal DiscountAmount, bool AlreadyApplied, string Message);

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
public record SettleCouponsCommand(long CustomerId, IReadOnlyList<CouponOrderLine> Lines)
    : IRequest<ApiResponse<SettleCouponResult>>;

/// <summary>结算试算结果。</summary>
/// <param name="HasCoupon">是否存在可用券。</param>
/// <param name="Best">最优券；无可用券时为 null。</param>
/// <param name="Options">全部可用券及各自优惠额。</param>
public sealed record SettleCouponResult(bool HasCoupon, SettleCouponOption? Best, IReadOnlyList<SettleCouponOption> Options);

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
        services.AddScoped<IValidator<QueryCouponReportCommand>, CouponReportValidator>();
    }

    /// <summary>营销效果报表校验。</summary>
    private sealed class CouponReportValidator : AbstractValidator<QueryCouponReportCommand>
    {
        /// <summary>构造校验器。</summary>
        public CouponReportValidator()
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
            RuleFor(x => x.Lines).NotNull().Must(l => l!.Count > 0).WithMessage("订单行不能为空");
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
            RuleFor(x => x.Lines).NotNull().Must(l => l!.Count > 0).WithMessage("订单行不能为空");
            // 列表页整页商品合并成一次批量试算，≤50 行一批
            RuleFor(x => x.Lines.Count).LessThanOrEqualTo(50).WithMessage("单次试算最多 50 个订单行");
        }
    }
}
