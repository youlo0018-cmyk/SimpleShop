using Collaboration.Domain.Common;
using FluentValidation;
using FreeSql;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ProductService.Domain.Entities;
using ProductEntity = ProductService.Domain.Entities.Product;

namespace ProductService.Application.Features.Internal;

/// <summary>校验一批商品能否被装修配置引用（装修页手动选品用）。</summary>
/// <param name="ProductIds">商品 Id 集合，最多 200 个。</param>
/// <param name="PlatformId">限定平台，0 表示不限（平台装修传它、商户装修传 0）。</param>
/// <param name="MerchantId">限定商户，0 表示不限（商户装修传它、平台装修传 0）。</param>
public record CheckProductsForDesignCommand(
    IReadOnlyList<long> ProductIds, long PlatformId = 0, long MerchantId = 0)
    : IRequest<ApiResponse<CheckProductsForDesignResult>>;

/// <summary>校验结果。</summary>
/// <param name="Checkable">全部可用的商品 Id。</param>
/// <param name="Rejected">不可用的商品及原因，按传入顺序。</param>
public sealed record CheckProductsForDesignResult(
    IReadOnlyList<long> Checkable,
    IReadOnlyList<RejectedProduct> Rejected);

/// <summary>不可用的商品及原因。</summary>
/// <param name="ProductId">商品 Id。</param>
/// <param name="Reason">中文原因，后台要原样展示给运营。</param>
public sealed record RejectedProduct(long ProductId, string Reason);

/// <summary>校验命令的校验器注册。</summary>
public static class CheckProductsForDesignValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCheckProductsForDesignValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CheckProductsForDesignCommand>, CheckProductsForDesignValidator>();

    /// <summary>校验规则。</summary>
    private sealed class CheckProductsForDesignValidator
        : AbstractValidator<CheckProductsForDesignCommand>
    {
        /// <summary>构造校验器。</summary>
        public CheckProductsForDesignValidator()
        {
            RuleFor(x => x.ProductIds).NotEmpty().WithMessage("请选择商品")
                .Must(ids => ids.Count <= 200).WithMessage("一次最多校验 200 个商品");
            RuleForEach(x => x.ProductIds).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        }
    }
}

/// <summary>装修选品可见性校验处理器。</summary>
/// <remarks>
/// <b>为什么装修必须校验</b>：装修是<b>直接面向顾客展示</b>的界面，
/// 手动指定商品的组件如果能挂未审核 / 未上架的商品，审核机制就被装修页绕过了——
/// 运营自己就能把没过审的内容摆到首页。
///
/// <para>判定条件（规格 16.4）：本平台（平台装修）或本商户（商户装修）
/// + <b>审核通过</b> + <b>已上架</b>。</para>
///
/// <para><b>把原因原样回给运营</b>，而不是只说「有商品不可用」：
/// 装修一次最多 20 个商品，运营需要知道<b>具体是哪几个、为什么</b>才能改。</para>
///
/// <para><b>注意范围</b>：本校验只针对<b>装修</b>。活动与券的适用商品（<c>Targets</c>）
/// <b>不受此限制</b>——运营常常先配好活动，等商品审核上架后自动生效；
/// 强行要求已上架会打断这个工作流（规格 16.4 明确写了）。</para>
/// </remarks>
public sealed class CheckProductsForDesignHandler
    : IRequestHandler<CheckProductsForDesignCommand, ApiResponse<CheckProductsForDesignResult>>
{
    private readonly IFreeSql _db;

    /// <summary>构造处理器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    public CheckProductsForDesignHandler(IFreeSql db) => _db = db;

    /// <summary>执行校验。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用与不可用的商品清单。</returns>
    public async Task<ApiResponse<CheckProductsForDesignResult>> Handle(
        CheckProductsForDesignCommand request, CancellationToken ct)
    {
        // 去重但保持传入顺序：后台回显时按用户勾选的顺序列出更容易对应
        var ids = request.ProductIds.Distinct().ToList();

        var rows = await _db.Select<ProductEntity>()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(a => new { a.Id, a.PlatformId, a.MerchantId, a.AuditStatus, a.Status }, ct)
            .ConfigureAwait(false);

        var byId = rows.ToDictionary(a => a.Id);
        var checkable = new List<long>();
        var rejected = new List<RejectedProduct>();

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var product))
            {
                rejected.Add(new RejectedProduct(id, "商品不存在"));
                continue;
            }

            if (request.PlatformId > 0 && product.PlatformId != request.PlatformId)
            {
                rejected.Add(new RejectedProduct(id, "商品不属于当前平台"));
                continue;
            }

            if (request.MerchantId > 0 && product.MerchantId != request.MerchantId)
            {
                rejected.Add(new RejectedProduct(id, "商品不属于当前商户"));
                continue;
            }

            if (product.AuditStatus != AuditStatuses.Approved)
            {
                rejected.Add(new RejectedProduct(id, "商品审核未通过"));
                continue;
            }

            if (product.Status != ListingStatuses.OnShelf)
            {
                rejected.Add(new RejectedProduct(id, "商品已下架"));
                continue;
            }

            checkable.Add(id);
        }

        return ApiResults.Ok(new CheckProductsForDesignResult(checkable, rejected));
    }
}
