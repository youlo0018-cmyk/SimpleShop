using Collaboration.Domain.Common;
using FluentValidation;
using FreeSql;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductService.Domain.Entities;
// `Features/Product` 这个命名空间段会遮蔽同名实体 `Product`（CODING_STANDARD §6 第 1 条）。
// 不加别名的话 `Update<Product>()` 报 CS0118「Product 是命名空间，但此处被当做类型」。
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Internal;

/// <summary>回写商品评价均分与条数（由 EvaluateService 每日重算后调用）。</summary>
/// <param name="Items">评分条目。</param>
public record SyncProductRatingsCommand(IReadOnlyList<ProductRatingItem> Items)
    : IRequest<ApiResponse<SyncProductRatingsResult>>;

/// <summary>单个商品的评分。</summary>
/// <param name="SpuId">SPU Id。</param>
/// <param name="Score">均分，两位小数。0 表示无评价。</param>
/// <param name="Count">首评条数。</param>
public sealed record ProductRatingItem(long SpuId, decimal Score, int Count);

/// <summary>回写结果。</summary>
/// <param name="Updated">成功更新条数。</param>
/// <param name="Skipped">跳过条数（商品不存在）。</param>
public sealed record SyncProductRatingsResult(int Updated, int Skipped);

/// <summary>评分回写的校验器注册。</summary>
public static class SyncProductRatingsValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddSyncProductRatingsValidators(IServiceCollection services)
        => services.AddScoped<IValidator<SyncProductRatingsCommand>, SyncProductRatingsValidator>();

    /// <summary>校验规则。</summary>
    private sealed class SyncProductRatingsValidator : AbstractValidator<SyncProductRatingsCommand>
    {
        /// <summary>构造校验器。</summary>
        public SyncProductRatingsValidator()
        {
            RuleFor(x => x.Items).NotEmpty().WithMessage("没有需要回写的评分");
            RuleFor(x => x.Items.Count).LessThanOrEqualTo(5000).WithMessage("单次回写条目过多");
            RuleForEach(x => x.Items).ChildRules(item =>
            {
                item.RuleFor(a => a.SpuId).GreaterThan(0).WithMessage("SPU Id 必须为正数");
                item.RuleFor(a => a.Score).InclusiveBetween(0m, 5m).WithMessage("评分必须在 0 ~ 5 之间");
                item.RuleFor(a => a.Count).GreaterThanOrEqualTo(0).WithMessage("评价条数不能为负数");
            });
        }
    }
}

/// <summary>回写商品评分的处理器。</summary>
/// <remarks>
/// <b>为什么这里是「跳过」而不是「报错」</b>：EvaluateService 重算时如果某个 SPU 在本服务
/// 已不存在（商品被硬删），抛错会让整批回写失败，导致**其他商品也更新不了**。
/// 记一条警告、跳过这一条，是「让 99% 成功」好过「让 0% 成功」。
/// </remarks>
public sealed class SyncProductRatingsHandler
    : IRequestHandler<SyncProductRatingsCommand, ApiResponse<SyncProductRatingsResult>>
{
    private readonly IFreeSql _db;
    private readonly ILogger<SyncProductRatingsHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="logger">日志器。</param>
    public SyncProductRatingsHandler(IFreeSql db, ILogger<SyncProductRatingsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>执行回写。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回写统计。</returns>
    public async Task<ApiResponse<SyncProductRatingsResult>> Handle(
        SyncProductRatingsCommand request, CancellationToken ct)
    {
        var updated = 0;
        var skipped = 0;

        foreach (var item in request.Items)
        {
            // 条件更新带上「当前值」，避免与后台的编辑操作互相覆盖
            var affected = await _db.Update<ProductEntity>()
                .Where(a => a.Id == item.SpuId)
                .Set(a => new ProductEntity
                {
                    EvaluationScore = item.Score,
                    EvaluationCount = item.Count,
                    UpdatedAt = DateTime.UtcNow
                })
                .ExecuteAffrowsAsync(ct);

            if (affected > 0)
            {
                updated++;
            }
            else
            {
                skipped++;
                _logger.LogWarning("回写评分时商品 {SpuId} 不存在，已跳过", item.SpuId);
            }
        }

        return ApiResults.Ok(
            new SyncProductRatingsResult(updated, skipped),
            $"已回写 {updated} 个商品评分，跳过 {skipped} 个");
    }
}
