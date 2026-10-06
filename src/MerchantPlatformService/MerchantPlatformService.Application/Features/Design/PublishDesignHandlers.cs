using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;
using MerchantPlatformService.Domain.IRepository;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>发布平台装修处理器。</summary>
public sealed class PublishPlatformDesignHandler
    : IRequestHandler<PublishPlatformDesignCommand, ApiResponse<int>>
{
    private readonly IDesignRepository _design;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    public PublishPlatformDesignHandler(IDesignRepository design) => _design = design;

    /// <summary>执行发布。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号。</returns>
    public async Task<ApiResponse<int>> Handle(
        PublishPlatformDesignCommand request, CancellationToken ct)
    {
        if (!DesignTenantScope.CanUsePlatform(TenantContextHolder.Current, request.PlatformId))
        {
            return ApiResults.Fail<int>(BaseApiResponseCode.NotFound, DesignTenantScope.NotFoundMessage);
        }

        var version = await _design.PublishPlatformAsync(request.PlatformId, ct).ConfigureAwait(false);
        // 版本号 0 = 没有草稿可发布。此时**不能**把线上那份覆盖成空，
        // 那是把线上页面清空的最快方式
        return version > 0
            ? ApiResults.Ok(version, $"发布成功，当前版本 v{version}")
            : ApiResults.Fail<int>(BaseApiResponseCode.BusinessError, "没有可发布的草稿，请先保存");
    }
}

/// <summary>发布商户装修处理器。</summary>
public sealed class PublishMerchantDesignHandler
    : IRequestHandler<PublishMerchantDesignCommand, ApiResponse<int>>
{
    private readonly IDesignRepository _design;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    public PublishMerchantDesignHandler(IDesignRepository design) => _design = design;

    /// <summary>执行发布。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号。</returns>
    public async Task<ApiResponse<int>> Handle(
        PublishMerchantDesignCommand request, CancellationToken ct)
    {
        if (!DesignTenantScope.CanUseMerchant(TenantContextHolder.Current, request.MerchantId))
        {
            return ApiResults.Fail<int>(BaseApiResponseCode.NotFound, DesignTenantScope.NotFoundMessage);
        }

        var version = await _design.PublishMerchantAsync(request.MerchantId, ct).ConfigureAwait(false);
        return version > 0
            ? ApiResults.Ok(version, $"发布成功，当前版本 v{version}")
            : ApiResults.Fail<int>(BaseApiResponseCode.BusinessError, "没有可发布的草稿，请先保存");
    }
}
