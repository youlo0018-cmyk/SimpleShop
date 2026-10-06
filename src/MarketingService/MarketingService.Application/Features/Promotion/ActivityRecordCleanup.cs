using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Promotion;

/// <summary>列出活动参与记录里的孤儿候选（定时任务调用）。</summary>
/// <param name="OlderThanMinutes">只看创建超过多少分钟的记录；0 表示不设时长门槛。</param>
/// <param name="Limit">单轮最多返回多少条。</param>
public record ListActivityRecordOrphansCommand(int OlderThanMinutes = 30, int Limit = 200)
    : IRequest<ApiResponse<List<ActivityRecordOrphanItem>>>;

/// <summary>孤儿参与记录候选。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="ActivityId">活动 Id。</param>
/// <param name="ActivityName">活动名快照。</param>
public sealed record ActivityRecordOrphanItem(string OrderNo, long ActivityId, string ActivityName);

/// <summary>按订单号软删活动参与记录（定时任务确认订单不存在后调用）。</summary>
/// <param name="OrderNos">订单号集合。</param>
public record DiscardActivityRecordOrphansCommand(IReadOnlyList<string> OrderNos)
    : IRequest<ApiResponse<int>>;

/// <summary>清理命令的校验器注册。</summary>
public static class ActivityRecordCleanupValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddActivityRecordCleanupValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<ListActivityRecordOrphansCommand>, ListActivityRecordOrphansValidator>();
        services.AddScoped<IValidator<DiscardActivityRecordOrphansCommand>, DiscardActivityRecordOrphansValidator>();
    }

    /// <summary>候选查询校验。</summary>
    private sealed class ListActivityRecordOrphansValidator
        : AbstractValidator<ListActivityRecordOrphansCommand>
    {
        /// <summary>构造校验器。</summary>
        public ListActivityRecordOrphansValidator()
        {
            RuleFor(x => x.OlderThanMinutes)
                .InclusiveBetween(0, 1440)
                .WithMessage("候选时长必须在 0 ~ 1440 分钟之间");
            RuleFor(x => x.Limit)
                .InclusiveBetween(1, 1000)
                .WithMessage("单轮条数必须在 1 ~ 1000 之间");
        }
    }

    /// <summary>清理命令校验。</summary>
    private sealed class DiscardActivityRecordOrphansValidator
        : AbstractValidator<DiscardActivityRecordOrphansCommand>
    {
        /// <summary>构造校验器。</summary>
        public DiscardActivityRecordOrphansValidator()
        {
            RuleFor(x => x.OrderNos).NotEmpty().WithMessage("请提供要清理的订单号");
            RuleFor(x => x.OrderNos)
                .Must(a => a.Count <= 1000)
                .WithMessage("一次最多清理 1000 个订单号");
            RuleForEach(x => x.OrderNos).NotEmpty().WithMessage("订单号不能为空");
            RuleForEach(x => x.OrderNos).MaximumLength(64).WithMessage("订单号不能超过 64 个字符");
        }
    }
}

/// <summary>列出孤儿候选的处理器。</summary>
public sealed class ListActivityRecordOrphansHandler
    : IRequestHandler<ListActivityRecordOrphansCommand, ApiResponse<List<ActivityRecordOrphanItem>>>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public ListActivityRecordOrphansHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>孤儿候选清单；是否真的不存在由调用方去订单服务确认。</returns>
    public async Task<ApiResponse<List<ActivityRecordOrphanItem>>> Handle(
        ListActivityRecordOrphansCommand request, CancellationToken ct)
    {
        var createdBefore = DateTime.UtcNow.AddMinutes(-request.OlderThanMinutes);
        var records = await _promotions
            .ListParticipationOrphansAsync(createdBefore, request.Limit, ct)
            .ConfigureAwait(false);

        var items = records
            .Select(a => new ActivityRecordOrphanItem(a.OrderNo, a.ActivityId, a.ActivityName))
            .ToList();

        return ApiResults.Ok(items);
    }
}

/// <summary>软删孤儿参与记录的处理器。</summary>
public sealed class DiscardActivityRecordOrphansHandler
    : IRequestHandler<DiscardActivityRecordOrphansCommand, ApiResponse<int>>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public DiscardActivityRecordOrphansHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行清理。</summary>
    /// <param name="request">清理命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    public async Task<ApiResponse<int>> Handle(
        DiscardActivityRecordOrphansCommand request, CancellationToken ct)
    {
        var affected = await _promotions
            .SoftDeleteParticipationByOrderNosAsync(request.OrderNos, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(affected, $"已清理 {affected} 条孤儿参与记录");
    }
}
