using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;
using MerchantPlatformService.Application.Services;
using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MerchantPlatformService.Application.Features.Design;

/// <summary>草稿保存的结果。</summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="ConfigJson">规范化后的配置 JSON；校验失败时为空。</param>
/// <param name="Error">失败原因（面向运营的中文完整句）；通过时为空。</param>
/// <param name="Warnings">
/// 告警清单（如「商户配色已剔除」）。<b>必须回给调用方</b>：
/// 只写日志的话运营永远不知道自己传的东西被改了，
/// 下次还会再传一遍，然后觉得「这破页面怎么不理我」。
/// </param>
public sealed record DesignSaveResult(
    bool IsValid, string ConfigJson, string Error, IReadOnlyList<string> Warnings);

/// <summary>保存装修草稿的公共逻辑。</summary>
internal static class DesignDraftSaver
{
    /// <summary>校验并保存草稿。</summary>
    /// <param name="configJson">配置 JSON。</param>
    /// <param name="forMerchant">是否按商户装修校验。</param>
    /// <param name="platformId">限定平台，0 表示不限。</param>
    /// <param name="merchantId">限定商户，0 表示不限。</param>
    /// <param name="products">商品服务端口。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存结果。</returns>
    /// <remarks>
    /// 校验分两步，缺一不可：
    /// ① <b>结构校验</b>——组件类型 / 栅格 / 高度 / 配色，本地就能判；
    /// ② <b>商品可见性</b>——要问商品服务「这些 Id 是不是本平台 / 本商户的、
    /// 审核通过、已上架」。
    ///
    /// <para><b>商品服务不可用时必须拒绝保存</b>，不能当成「全部通过」——
    /// 那是把「查不到」当成「没问题」，审核机制就形同虚设了。</para>
    /// </remarks>
    public static async Task<DesignSaveResult> SaveAsync(
        string configJson,
        bool forMerchant,
        long platformId,
        long merchantId,
        IProductPort products,
        ILogger logger,
        CancellationToken ct)
    {
        DesignConfig config;
        try
        {
            config = DesignConfig.FromJson(configJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return new DesignSaveResult(false, string.Empty, "配置不是合法的 JSON", []);
        }

        var check = DesignValidator.Validate(config, forMerchant);

        foreach (var warning in check.Warnings)
        {
            logger.LogWarning("装修配置告警：{Warning}", warning);
        }

        if (!check.IsValid)
        {
            return new DesignSaveResult(
                false, string.Empty, string.Join("；", check.Errors), check.Warnings);
        }

        if (check.ProductIds.Count == 0)
        {
            return new DesignSaveResult(true, config.ToJson(), string.Empty, check.Warnings);
        }

        var rejected = await products.CheckForDesignAsync(
            check.ProductIds, platformId, merchantId, ct).ConfigureAwait(false);

        if (rejected is null)
        {
            // 查不到 ≠ 没问题。这里必须拦住，否则装修页就成了绕过审核的后门
            return new DesignSaveResult(
                false, string.Empty, "商品服务暂时不可用，无法校验所选商品，请稍后重试",
                check.Warnings);
        }

        return rejected.Count == 0
            ? new DesignSaveResult(true, config.ToJson(), string.Empty, check.Warnings)
            : new DesignSaveResult(
                false, string.Empty,
                string.Join("；", rejected.Select(a => $"商品 {a.ProductId}：{a.Reason}")),
                check.Warnings);
    }
}

/// <summary>保存平台装修草稿处理器。</summary>
public sealed class SavePlatformDraftHandler
    : IRequestHandler<SavePlatformDraftCommand, ApiResponse<DesignResult>>
{
    private readonly IDesignRepository _design;
    private readonly IProductPort _products;
    private readonly ILogger<SavePlatformDraftHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    /// <param name="products">商品服务端口。</param>
    /// <param name="logger">日志器。</param>
    public SavePlatformDraftHandler(
        IDesignRepository design, IProductPort products, ILogger<SavePlatformDraftHandler> logger)
    {
        _design = design;
        _products = products;
        _logger = logger;
    }

    /// <summary>执行保存。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存后的装修配置。</returns>
    public async Task<ApiResponse<DesignResult>> Handle(
        SavePlatformDraftCommand request, CancellationToken ct)
    {
        // platformId 来自请求体，必须按租户身份再判一次：
        // AOP 过滤只挡查询 / 更新，越权请求会走到 INSERT 分支给别的平台插一份配置。
        if (!DesignTenantScope.CanUsePlatform(TenantContextHolder.Current, request.PlatformId))
        {
            return ApiResults.Fail<DesignResult>(
                BaseApiResponseCode.NotFound, DesignTenantScope.NotFoundMessage);
        }

        var saved = await DesignDraftSaver.SaveAsync(
            request.ConfigJson, forMerchant: false,
            request.PlatformId, merchantId: 0, _products, _logger, ct).ConfigureAwait(false);

        if (!saved.IsValid)
        {
            return ApiResults.Fail<DesignResult>(BaseApiResponseCode.BusinessError, saved.Error);
        }

        await _design.SavePlatformDraftAsync(request.PlatformId, saved.ConfigJson, ct).ConfigureAwait(false);
        return ApiResults.Ok(
            DesignResultFactory.FromDraft(saved.ConfigJson, string.Empty, 0, saved.Warnings),
            "草稿已保存，发布后生效");
    }
}

/// <summary>保存商户装修草稿处理器。</summary>
public sealed class SaveMerchantDraftHandler
    : IRequestHandler<SaveMerchantDraftCommand, ApiResponse<DesignResult>>
{
    private readonly IDesignRepository _design;
    private readonly IMerchantRepository _merchants;
    private readonly IProductPort _products;
    private readonly ILogger<SaveMerchantDraftHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="design">装修仓储。</param>
    /// <param name="merchants">商户仓储（取所属平台）。</param>
    /// <param name="products">商品服务端口。</param>
    /// <param name="logger">日志器。</param>
    public SaveMerchantDraftHandler(
        IDesignRepository design,
        IMerchantRepository merchants,
        IProductPort products,
        ILogger<SaveMerchantDraftHandler> logger)
    {
        _design = design;
        _merchants = merchants;
        _products = products;
        _logger = logger;
    }

    /// <summary>执行保存。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存后的装修配置。</returns>
    public async Task<ApiResponse<DesignResult>> Handle(
        SaveMerchantDraftCommand request, CancellationToken ct)
    {
        // 商户账号只能装修自己的店铺页（DATA_SPEC 5.30：MerchantId「只读；锁定本商户」）
        if (!DesignTenantScope.CanUseMerchant(TenantContextHolder.Current, request.MerchantId))
        {
            return ApiResults.Fail<DesignResult>(
                BaseApiResponseCode.NotFound, DesignTenantScope.NotFoundMessage);
        }

        var merchant = await _merchants.GetByIdAsync(request.MerchantId, ct).ConfigureAwait(false);
        if (merchant is null)
        {
            return ApiResults.Fail<DesignResult>(BaseApiResponseCode.NotFound, "商户不存在");
        }

        // 商户只能装修**自己的**商品：平台维度传 0、商户维度传自己，
        // 否则就能把别人家的商品挂到自己的店铺页上
        var saved = await DesignDraftSaver.SaveAsync(
            request.ConfigJson, forMerchant: true,
            platformId: 0, merchantId: request.MerchantId, _products, _logger, ct).ConfigureAwait(false);

        if (!saved.IsValid)
        {
            return ApiResults.Fail<DesignResult>(BaseApiResponseCode.BusinessError, saved.Error);
        }

        await _design.SaveMerchantDraftAsync(
            request.MerchantId, merchant.PlatformId, saved.ConfigJson, ct).ConfigureAwait(false);

        return ApiResults.Ok(
            DesignResultFactory.FromDraft(saved.ConfigJson, string.Empty, 0, saved.Warnings),
            "草稿已保存，发布后生效");
    }
}
