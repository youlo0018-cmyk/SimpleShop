using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Domain.Services;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>读平台装修处理器。</summary>
public sealed class QueryPlatformDesignHandler
    : IRequestHandler<QueryPlatformDesignCommand, ApiResponse<DesignResult>>
{
    private readonly IDesignRepository _design;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    public QueryPlatformDesignHandler(IDesignRepository design) => _design = design;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>装修配置。</returns>
    public async Task<ApiResponse<DesignResult>> Handle(
        QueryPlatformDesignCommand request, CancellationToken ct)
    {
        var config = await _design.GetPlatformAsync(request.PlatformId, ct);
        if (config is null) return ApiResults.Ok(DesignResultFactory.Empty());

        return ApiResults.Ok(
            DesignResultFactory.FromDraft(config.DraftJson, config.PublishedJson, config.Version));
    }
}

/// <summary>读商户装修处理器。</summary>
public sealed class QueryMerchantDesignHandler
    : IRequestHandler<QueryMerchantDesignCommand, ApiResponse<DesignResult>>
{
    private readonly IDesignRepository _design;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    public QueryMerchantDesignHandler(IDesignRepository design) => _design = design;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>装修配置。</returns>
    public async Task<ApiResponse<DesignResult>> Handle(
        QueryMerchantDesignCommand request, CancellationToken ct)
    {
        var config = await _design.GetMerchantAsync(request.MerchantId, ct);
        // 商户装修只有店铺页：返回平台那套会让搭建器把首页 / 我的页也画出来，
        // 运营会发现「我改不动的页面居然在这儿」
        if (config is null) return ApiResults.Ok(DesignResultFactory.EmptyMerchant());

        return ApiResults.Ok(
            DesignResultFactory.FromDraft(config.DraftJson, config.PublishedJson, config.Version));
    }
}

/// <summary>取组件库处理器。</summary>
public sealed class QueryComponentLibraryHandler
    : IRequestHandler<QueryComponentLibraryCommand, ApiResponse<List<ComponentDefDto>>>
{
    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用组件列表。</returns>
    public Task<ApiResponse<List<ComponentDefDto>>> Handle(
        QueryComponentLibraryCommand request, CancellationToken ct)
    {
        var defs = request.ForMerchant
            ? DesignComponentRegistry.ForMerchant()
            : DesignComponentRegistry.ForPlatformPage(request.Page);

        var items = defs.Select(a => new ComponentDefDto(a.Type, a.Name, a.Category)).ToList();
        return Task.FromResult(ApiResults.Ok(items));
    }
}
