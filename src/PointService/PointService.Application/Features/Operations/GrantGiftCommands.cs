using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PointService.Domain.IRepository;
using PointService.Domain.Services;

namespace PointService.Application.Features.Operations;

/// <summary>发放注册赠送积分（金额由积分规则决定）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <remarks>
/// <para><b>为什么金额不由调用方传</b>：注册赠送的数额是<b>积分规则</b>里的一项
/// （<c>PointRuleConfig</c> 的 <c>register_gift</c>，后台可改）。让调用方传金额，
/// 等于把规则抄了一份到客户服务里 —— 运营把赠送从 100 改成 200，
/// 客户服务那边还是写死的 100，而且不会有任何报错，只是「改了不生效」。</para>
///
/// <para>幂等键固定为 <c>REG-{customerId}</c>：同一客户重复注册 / 重试只发一次。</para>
/// </remarks>
public record GrantRegisterGiftCommand(long CustomerId) : IRequest<ApiResponse<PointBalance>>;

/// <summary>发放发表首评赠送积分（金额由积分规则决定）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="EvaluateId">评价 Id，用作幂等键。</param>
/// <remarks>幂等键固定为 <c>EVL-{evaluateId}</c>：同一评价重复投递只发一次。</remarks>
public record GrantEvaluateGiftCommand(long CustomerId, long EvaluateId)
    : IRequest<ApiResponse<PointBalance>>;

/// <summary>两条赠送命令的校验器注册。</summary>
public static class GrantGiftValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddGrantGiftValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<GrantRegisterGiftCommand>, GrantRegisterGiftValidator>();
        services.AddScoped<IValidator<GrantEvaluateGiftCommand>, GrantEvaluateGiftValidator>();
    }

    /// <summary>注册赠送校验。</summary>
    private sealed class GrantRegisterGiftValidator : AbstractValidator<GrantRegisterGiftCommand>
    {
        /// <summary>构造校验规则。</summary>
        public GrantRegisterGiftValidator()
            => RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 不正确");
    }

    /// <summary>首评赠送校验。</summary>
    private sealed class GrantEvaluateGiftValidator : AbstractValidator<GrantEvaluateGiftCommand>
    {
        /// <summary>构造校验规则。</summary>
        public GrantEvaluateGiftValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.EvaluateId).GreaterThan(0).WithMessage("评价 Id 不正确");
        }
    }
}

/// <summary>发放注册赠送积分的处理器。</summary>
public sealed class GrantRegisterGiftHandler
    : IRequestHandler<GrantRegisterGiftCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    /// <param name="rules">积分规则提供器。</param>
    public GrantRegisterGiftHandler(IPointRepository points, IPointRuleProvider rules)
    {
        _points = points;
        _rules = rules;
    }

    /// <summary>执行发放。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(
        GrantRegisterGiftCommand request, CancellationToken ct)
    {
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);

        // 规则里配成 0 表示「不赠送」——照常返回成功（余额不变），
        // 不要报错：运营关掉赠送是合法配置，报错会让注册链路以为积分服务出了问题。
        if (rules.RegisterGift <= 0)
        {
            return ApiResults.Ok(
                new PointBalance(request.CustomerId, 0, 0, 0, 0, false), "注册赠送已关闭");
        }

        var outcome = await _points.EarnAsync(
            request.CustomerId, "注册赠送", rules.RegisterGift,
            $"REG-{request.CustomerId}", "earn", "新客户注册赠送",
            rules.ValidDays, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome), "注册赠送已发放");
    }
}

/// <summary>发放发表首评赠送积分的处理器。</summary>
public sealed class GrantEvaluateGiftHandler
    : IRequestHandler<GrantEvaluateGiftCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    /// <param name="rules">积分规则提供器。</param>
    public GrantEvaluateGiftHandler(IPointRepository points, IPointRuleProvider rules)
    {
        _points = points;
        _rules = rules;
    }

    /// <summary>执行发放。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(
        GrantEvaluateGiftCommand request, CancellationToken ct)
    {
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);

        if (rules.FirstEvaluateGift <= 0)
        {
            return ApiResults.Ok(
                new PointBalance(request.CustomerId, 0, 0, 0, 0, false), "首评赠送已关闭");
        }

        var outcome = await _points.EarnAsync(
            request.CustomerId, "发表首评", rules.FirstEvaluateGift,
            $"EVL-{request.EvaluateId}", "earn", "发表首评赠送",
            rules.ValidDays, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome), "首评赠送已发放");
    }
}

