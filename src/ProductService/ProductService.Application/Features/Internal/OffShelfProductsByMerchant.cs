using Collaboration.Domain.Common;
using FluentValidation;
using FreeSql;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductService.Application.Services;
using ProductService.Domain.Entities;
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Internal;

/// <summary>批量下架某商户的全部已上架商品（供商户审核 / 停用时调用）。</summary>
/// <param name="MerchantId">商户 Id。</param>
public record OffShelfProductsByMerchantCommand(long MerchantId)
    : IRequest<ApiResponse<OffShelfByMerchantResult>>;

/// <summary>批量下架结果。</summary>
/// <param name="OffShelved">实际被下架的商品数。</param>
/// <param name="IndexSynced">成功同步索引的数量。</param>
/// <param name="IndexFailed">索引同步失败的数量（商品已下架，只是索引还搜得到）。</param>
public sealed record OffShelfByMerchantResult(int OffShelved, int IndexSynced, int IndexFailed);

/// <summary>批量下架命令的校验器注册。</summary>
public static class OffShelfProductsByMerchantValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddOffShelfProductsByMerchantValidators(IServiceCollection services)
        => services.AddScoped<IValidator<OffShelfProductsByMerchantCommand>, OffShelfByMerchantValidator>();

    /// <summary>校验规则。</summary>
    private sealed class OffShelfByMerchantValidator : AbstractValidator<OffShelfProductsByMerchantCommand>
    {
        /// <summary>构造校验器。</summary>
        public OffShelfByMerchantValidator()
            => RuleFor(x => x.MerchantId).GreaterThan(0).WithMessage("商户 Id 必须为正数");
    }
}

/// <summary>批量下架某商户全部已上架商品。</summary>
/// <remarks>
/// <para><b>为什么要连带下架</b>：商品资质依赖商户资质（BUSINESS.md 14 / 1.4）。
/// 商户审核被拒或被停用，它的商品就不该继续对外销售。</para>
///
/// <para><b>为什么必须同步 ES 索引</b>：搜索走索引、<b>不走 C 端可见性过滤</b>。
/// 不同步就会出现「商品页看不到但搜索搜得到，点进去才发现下架了」——
/// 搜索是漏斗最上层，它比商品页更早暴露问题，用户观感最差。</para>
///
/// <para><b>索引同步失败不回滚下架</b>：商品已经在库里下架了，C 端已经看不到；
/// 索引是加速手段，它失败只会让「搜索搜到已下架商品」，严重程度远低于「违规商品还在卖」。
/// 失败计数返回给调用方并记警告，交给对账任务兜底。</para>
/// </remarks>
public sealed class OffShelfProductsByMerchantHandler
    : IRequestHandler<OffShelfProductsByMerchantCommand, ApiResponse<OffShelfByMerchantResult>>
{
    private readonly IFreeSql _db;
    private readonly IProductSearchIndex _search;
    private readonly ILogger<OffShelfProductsByMerchantHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="search">搜索索引。</param>
    /// <param name="logger">日志器。</param>
    public OffShelfProductsByMerchantHandler(
        IFreeSql db, IProductSearchIndex search, ILogger<OffShelfProductsByMerchantHandler> logger)
    {
        _db = db;
        _search = search;
        _logger = logger;
    }

    /// <summary>执行批量下架。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下架与索引同步统计。</returns>
    public async Task<ApiResponse<OffShelfByMerchantResult>> Handle(
        OffShelfProductsByMerchantCommand request, CancellationToken ct)
    {
        // 先捞出「已上架」的：已经下架的再下一遍是空操作，
        // 而把它们也算进 affected 会让调用方以为改动了东西
        var listed = await _db.Select<ProductEntity>()
            .Where(a => a.MerchantId == request.MerchantId
                && a.Status == ListingStatuses.OnShelf
                && a.IsDeleted == false)
            .ToListAsync(a => new { a.Id, a.AuditStatus }, ct)
            .ConfigureAwait(false);

        if (listed.Count == 0)
        {
            return ApiResults.Ok(new OffShelfByMerchantResult(0, 0, 0), "没有需要下架的商品");
        }

        // 条件更新带上「当前仍是已上架」，避免与运营的上下架操作互相覆盖
        var affected = await _db.Update<ProductEntity>()
            .Where(a => a.MerchantId == request.MerchantId
                && a.Status == ListingStatuses.OnShelf
                && a.IsDeleted == false)
            .Set(a => new ProductEntity
            {
                Status = ListingStatuses.OffShelf,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct)
            .ConfigureAwait(false);

        var synced = 0;
        var failed = 0;

        foreach (var product in listed)
        {
            try
            {
                if (await _search.UpdateStatusAsync(
                    product.Id, product.AuditStatus, ListingStatuses.OffShelf, ct).ConfigureAwait(false))
                {
                    synced++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "下架后同步商品 {ProductId} 的索引失败", product.Id);
            }
        }

        if (failed > 0)
        {
            _logger.LogError(
                "商户 {MerchantId} 有 {Failed} 个商品下架后索引同步失败，搜索里可能还能搜到，" +
                "将由搜索索引对账任务兜底",
                request.MerchantId, failed);
        }

        return ApiResults.Ok(
            new OffShelfByMerchantResult(affected, synced, failed),
            $"已下架 {affected} 个商品，同步索引 {synced} 个");
    }
}
