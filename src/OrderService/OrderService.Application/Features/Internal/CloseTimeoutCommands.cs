using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace OrderService.Application.Features.Internal;

/// <summary>扫描并关闭支付超时的订单。供 ScheduledService 每 30 秒调一次。</summary>
/// <param name="OrderNo">
/// 指定订单号，只关这一张。<b>留空表示扫描全部超时单</b>——定时任务走的就是留空这条路；
/// 传订单号是为了排障与运营手工触发（「这单一直卡在待支付，帮我关掉」）。
/// </param>
/// <param name="Limit">单次最多关多少张，0 表示用配置里的批量上限。防止一批积压把下游打爆。</param>
public record CloseTimeoutOrdersCommand(string OrderNo = "", int Limit = 0)
    : IRequest<ApiResponse<CloseTimeoutResult>>;

/// <summary>关单结果。</summary>
/// <param name="Scanned">本次扫到的候选单数（只含仍处于待支付的）。</param>
/// <param name="Closed">真正关掉的张数。</param>
/// <param name="Skipped">已被别人处理掉（状态已不是待支付）的张数。</param>
/// <param name="Details">逐张的处理结果，键是订单号，值是中文说明。排障时直接看这个。</param>
public sealed record CloseTimeoutResult(int Scanned, int Closed, int Skipped, IReadOnlyDictionary<string, string> Details);

/// <summary>关单命令的校验器。</summary>
public static class CloseTimeoutValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCloseTimeoutValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CloseTimeoutOrdersCommand>, CloseTimeoutOrdersValidator>();

    /// <summary>关单命令校验。</summary>
    private sealed class CloseTimeoutOrdersValidator : AbstractValidator<CloseTimeoutOrdersCommand>
    {
        /// <summary>构造校验器。</summary>
        public CloseTimeoutOrdersValidator()
        {
            RuleFor(x => x.OrderNo).MaximumLength(64).WithMessage("订单号不正确");
            RuleFor(x => x.Limit).InclusiveBetween(0, 1000).WithMessage("单次关单上限在 0 ~ 1000 之间");
        }
    }
}