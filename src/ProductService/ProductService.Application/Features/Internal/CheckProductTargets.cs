using Collaboration.Domain.Common;
using FluentValidation;
using FreeSql;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ProductEntity = ProductService.Domain.Entities.Product;
using SkuEntity = ProductService.Domain.Entities.Sku;

namespace ProductService.Application.Features.Internal;

/// <summary>
/// 校验一批 SPU / SKU 能否作为营销活动的适用目标（DATA_SPEC 5.32「仅后端验证」）。
/// </summary>
/// <param name="SpuIds">指定 SPU 的目标 Id 集合（TargetType = 2 时传）。</param>
/// <param name="SkuIds">指定 SKU 的目标 Id 集合（TargetType = 3 时传）。</param>
/// <param name="PlatformId">限定平台，0 表示不限。</param>
/// <param name="MerchantId">限定商户，0 表示不限（平台级活动传 0）。</param>
public record CheckProductTargetsCommand(
    IReadOnlyList<long> SpuIds,
    IReadOnlyList<long> SkuIds,
    long PlatformId = 0,
    long MerchantId = 0) : IRequest<ApiResponse<CheckProductTargetsResult>>;

/// <summary>校验结果。</summary>
/// <param name="Rejected">不通过的目标及原因，按传入顺序。</param>
public sealed record CheckProductTargetsResult(IReadOnlyList<RejectedTarget> Rejected);

/// <summary>不通过的目标及原因。</summary>
/// <param name="TargetId">SPU 或 SKU Id。</param>
/// <param name="TargetType">2 SPU / 3 SKU，与活动的 TargetType 同码。</param>
/// <param name="Reason">中文原因，后台要原样展示给运营。</param>
public sealed record RejectedTarget(long TargetId, int TargetType, string Reason);

/// <summary>校验命令的校验器注册。</summary>
public static class CheckProductTargetsValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCheckProductTargetsValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CheckProductTargetsCommand>, CheckProductTargetsValidator>();

    /// <summary>校验规则。</summary>
    private sealed class CheckProductTargetsValidator : AbstractValidator<CheckProductTargetsCommand>
    {
        /// <summary>构造校验器。</summary>
        public CheckProductTargetsValidator()
        {
            // 一次最多 200 个：与 DATA_SPEC 5.11 的 Targets 上限一致
            RuleFor(x => x.SpuIds).Must(ids => ids.Count <= 200).WithMessage("一次最多校验 200 个商品");
            RuleFor(x => x.SkuIds).Must(ids => ids.Count <= 200).WithMessage("一次最多校验 200 个规格");
            RuleForEach(x => x.SpuIds).GreaterThan(0).WithMessage("商品 Id 必须为正数");
            RuleForEach(x => x.SkuIds).GreaterThan(0).WithMessage("规格 Id 必须为正数");
        }
    }
}

/// <summary>营销活动目标的归属校验处理器。</summary>
/// <remarks>
/// <para><b>只校验「存在 + 归属」，刻意不校验审核状态与上架状态</b>：
/// 运营常常先把活动配好，等商品审核上架后自动生效；
/// 强行要求已上架会打断这个工作流（见 CheckProductsForDesignHandler 的同一段说明）。</para>
///
/// <para><b>为什么要校验归属</b>：不校验的话，商户 A 的活动可以把目标写成商户 B 的商品，
/// 用户在 B 的商品上看到 A 的满减 —— 那是跨租户改价。
/// 而目标写成不存在的 Id 时活动对谁都不生效，运营却以为配好了。</para>
/// </remarks>
public sealed class CheckProductTargetsHandler
    : IRequestHandler<CheckProductTargetsCommand, ApiResponse<CheckProductTargetsResult>>
{
    private readonly IFreeSql _db;

    /// <summary>构造处理器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    public CheckProductTargetsHandler(IFreeSql db) => _db = db;

    /// <summary>执行校验。</summary>
    /// <param name="request">校验命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不通过的目标清单；全部通过时为空。</returns>
    public async Task<ApiResponse<CheckProductTargetsResult>> Handle(
        CheckProductTargetsCommand request, CancellationToken ct)
    {
        var rejected = new List<RejectedTarget>();

        var spuIds = request.SpuIds.Distinct().ToList();
        var skuIds = request.SkuIds.Distinct().ToList();

        // ---- SPU：直接按 product 表判归属 ----
        if (spuIds.Count > 0)
        {
            var products = await _db.Select<ProductEntity>()
                .Where(a => spuIds.Contains(a.Id))
                .ToListAsync(a => new { a.Id, a.PlatformId, a.MerchantId }, ct)
                .ConfigureAwait(false);

            var byId = products.ToDictionary(a => a.Id);

            foreach (var id in spuIds)
            {
                if (!byId.TryGetValue(id, out var product))
                {
                    rejected.Add(new RejectedTarget(id, TargetTypes.Spu, "商品不存在"));
                    continue;
                }

                var reason = CheckOwnership(product.PlatformId, product.MerchantId, request, "商品");
                if (reason is not null) rejected.Add(new RejectedTarget(id, TargetTypes.Spu, reason));
            }
        }

        // ---- SKU：归属跟着它的 SPU 走 ----
        if (skuIds.Count > 0)
        {
            var skus = await _db.Select<SkuEntity>()
                .Where(a => skuIds.Contains(a.Id))
                .ToListAsync(a => new { a.Id, a.ProductId }, ct)
                .ConfigureAwait(false);

            var productIds = skus.Select(a => a.ProductId).Distinct().ToList();
            var owners = productIds.Count == 0
                ? new Dictionary<long, (long PlatformId, long MerchantId)>()
                : (await _db.Select<ProductEntity>()
                        .Where(a => productIds.Contains(a.Id))
                        .ToListAsync(a => new { a.Id, a.PlatformId, a.MerchantId }, ct)
                        .ConfigureAwait(false))
                    .ToDictionary(a => a.Id, a => (a.PlatformId, a.MerchantId));

            var skuById = skus.ToDictionary(a => a.Id);

            foreach (var id in skuIds)
            {
                if (!skuById.TryGetValue(id, out var sku))
                {
                    rejected.Add(new RejectedTarget(id, TargetTypes.Sku, "规格不存在"));
                    continue;
                }

                if (!owners.TryGetValue(sku.ProductId, out var owner))
                {
                    // SKU 存在但它的商品没了：数据不一致，按不可用处理并说清楚
                    rejected.Add(new RejectedTarget(id, TargetTypes.Sku, "规格所属商品不存在"));
                    continue;
                }

                var reason = CheckOwnership(owner.PlatformId, owner.MerchantId, request, "规格");
                if (reason is not null) rejected.Add(new RejectedTarget(id, TargetTypes.Sku, reason));
            }
        }

        return ApiResults.Ok(new CheckProductTargetsResult(rejected));
    }

    /// <summary>判定单个目标的归属，通过返回 null。</summary>
    /// <param name="platformId">目标所属平台。</param>
    /// <param name="merchantId">目标所属商户。</param>
    /// <param name="request">校验命令（带限定条件）。</param>
    /// <param name="label">「商品」或「规格」，用于拼提示。</param>
    /// <returns>不通过时的中文原因。</returns>
    private static string? CheckOwnership(
        long platformId, long merchantId, CheckProductTargetsCommand request, string label)
    {
        if (request.PlatformId > 0 && platformId != request.PlatformId)
        {
            return $"{label}不属于当前平台";
        }

        if (request.MerchantId > 0 && merchantId != request.MerchantId)
        {
            return $"{label}不属于当前商户";
        }

        return null;
    }
}

/// <summary>活动目标类型，与 MarketingService 的 TargetTypes 同码。</summary>
public static class TargetTypes
{
    /// <summary>指定 SPU。</summary>
    public const int Spu = 2;

    /// <summary>指定 SKU。</summary>
    public const int Sku = 3;
}
