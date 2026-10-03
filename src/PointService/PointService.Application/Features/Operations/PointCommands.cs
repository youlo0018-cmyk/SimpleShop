using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace PointService.Application.Features.Operations;

/// <summary>发放积分（注册赠送 / 订单完成 / 首评 / 签到）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Source">来源，见 Domain 的 <c>PointSources</c>。</param>
/// <param name="Quantity">发放数量。</param>
/// <param name="BizNo">业务单号，幂等键的一部分。</param>
/// <param name="Remark">备注。</param>
/// <param name="Action">流水动作，默认 earn。</param>
public record EarnPointsCommand(
    long CustomerId,
    string Source,
    long Quantity,
    string BizNo,
    string Remark = "",
    string Action = "earn") : IRequest<ApiResponse<PointBalance>>;

/// <summary>下单冻结积分。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">订单号，幂等键的一部分。</param>
/// <param name="Quantity">冻结数量。</param>
/// <param name="Remark">备注。</param>
public record LockPointsCommand(
    long CustomerId, string BizNo, long Quantity, string Remark = "") : IRequest<ApiResponse<PointBalance>>;

/// <summary>取消 / 超时关单解冻。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">订单号。</param>
/// <param name="Remark">备注。</param>
public record UnfreezePointsCommand(
    long CustomerId, string BizNo, string Remark = "") : IRequest<ApiResponse<PointBalance>>;

/// <summary>支付成功实扣。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">订单号。</param>
/// <param name="Remark">备注。</param>
public record ConsumePointsCommand(
    long CustomerId, string BizNo, string Remark = "") : IRequest<ApiResponse<PointBalance>>;

/// <summary>退款按比例回收积分（向上取整）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">订单号。</param>
/// <param name="RefundRatio">退款比例 0~1，整单退款传 1。</param>
/// <param name="Remark">备注。</param>
public record RefundPointsCommand(
    long CustomerId, string BizNo, decimal RefundRatio, string Remark = "") : IRequest<ApiResponse<PointBalance>>;

/// <summary>按订单发放积分（订单完成时由 OrderService 调用）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">订单号，幂等键的一部分。</param>
/// <param name="PaidAmount">订单实付金额，两位小数。积分数由服务端按规则算，<b>不接受客户端传积分数</b>。</param>
/// <param name="Remark">备注。</param>
/// <remarks>
/// 单独开这个入口而不是让订单服务自己算好积分数再调 <c>Earn</c>，是为了让<b>规则只存在一处</b>。
/// 「实付每满 1 元 1 积分」这条规则改了，只改 PointService 一处；
/// 散到订单服务里算一遍的话，改了规则就会有两个服务算出不同的积分数。
/// </remarks>
public record EarnByOrderCommand(
    long CustomerId, string BizNo, decimal PaidAmount, string Remark = "")
    : IRequest<ApiResponse<PointBalance>>;

/// <summary>每日签到。</summary>
/// <param name="CustomerId">客户 Id。</param>
public record SignInPointsCommand(long CustomerId) : IRequest<ApiResponse<PointSignInResult>>;

/// <summary>查询积分账户。</summary>
/// <param name="CustomerId">客户 Id。</param>
public record QueryPointAccountCommand(long CustomerId) : IRequest<ApiResponse<PointBalance>>;

/// <summary>分页查询积分流水。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryPointRecordsCommand(
    long CustomerId, int Page = 1, int PageSize = 20) : IRequest<ApiResponse<List<PointRecordItem>>>;

/// <summary>积分余额。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Available">可用积分。</param>
/// <param name="Frozen">冻结积分。</param>
/// <param name="TotalEarned">累计发放。</param>
/// <param name="TotalUsed">累计消耗。</param>
/// <param name="AlreadyApplied">本次是否命中幂等（重复请求）。</param>
public record PointBalance(
    long CustomerId, long Available, long Frozen, long TotalEarned, long TotalUsed, bool AlreadyApplied);

/// <summary>签到结果。</summary>
public sealed record PointSignInResult(
    long CustomerId, int Streak, long Reward, long Available, bool AlreadySigned, string Message);

/// <summary>积分流水项。</summary>
public record PointRecordItem(
    string Id, string BizNo, string Action, long Quantity,
    long AvailableBefore, long AvailableAfter, long FrozenBefore, long FrozenAfter,
    string Remark, string CreatedAt);

/// <summary>积分命令的校验器注册。</summary>
public static class PointValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddPointValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<EarnPointsCommand>, EarnPointsValidator>();
        services.AddScoped<IValidator<LockPointsCommand>, LockPointsValidator>();
        services.AddScoped<IValidator<RefundPointsCommand>, RefundPointsValidator>();
        services.AddScoped<IValidator<EarnByOrderCommand>, EarnByOrderValidator>();
    }

    /// <summary>发放校验。</summary>
    private sealed class EarnPointsValidator : AbstractValidator<EarnPointsCommand>
    {
        /// <summary>构造校验器。</summary>
        public EarnPointsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("发放数量必须大于 0");
            RuleFor(x => x.BizNo).NotEmpty().MaximumLength(64).WithMessage("业务单号必填且不超过 64 个字符");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
        }
    }

    /// <summary>冻结校验。</summary>
    private sealed class LockPointsValidator : AbstractValidator<LockPointsCommand>
    {
        /// <summary>构造校验器。</summary>
        public LockPointsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("冻结数量必须大于 0");
            RuleFor(x => x.BizNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
        }
    }

    /// <summary>按订单发放的校验。</summary>
    private sealed class EarnByOrderValidator : AbstractValidator<EarnByOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public EarnByOrderValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.BizNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");

            // 实付为负说明上游算错了（退款回来说要发积分？）。
            // 这里必须挡住：0 元单实付就是 0，那是合法的（积分数算出来是 0，本次不发）。
            RuleFor(x => x.PaidAmount).InclusiveBetween(0m, 9999999.99m).WithMessage("实付金额不正确");
        }
    }

    /// <summary>退款比例校验。</summary>
    private sealed class RefundPointsValidator : AbstractValidator<RefundPointsCommand>
    {
        /// <summary>构造校验器。</summary>
        public RefundPointsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
            RuleFor(x => x.BizNo).NotEmpty().MaximumLength(64).WithMessage("订单号必填且不超过 64 个字符");
            RuleFor(x => x.RefundRatio).InclusiveBetween(0.0001m, 1.0m).WithMessage("退款比例必须在 0 ~ 1 之间");
        }
    }
}