namespace PaymentService.Application.Services;

/// <summary>平台 / 商户显示名的跨服务契约。</summary>
/// <remarks>
/// 名称只存在于 MerchantPlatformService。退款单详情要冗余返回 PlatformName / MerchantName
/// （DATA_SPEC 4.3），不解析的话后台看到的是雪花 Id。
/// </remarks>
public interface IPlatformNameClient
{
    /// <summary>按 Id 集合取平台与商户的显示名。</summary>
    /// <param name="platformIds">平台 Id 集合。</param>
    /// <param name="merchantIds">商户 Id 集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Id → 名称；查不到或服务不可用时缺项。</returns>
    Task<PlatformNames> GetNamesAsync(
        IReadOnlyCollection<long> platformIds,
        IReadOnlyCollection<long> merchantIds,
        CancellationToken ct = default);
}

/// <summary>名称查询结果。</summary>
/// <param name="Platforms">平台 Id → 平台名。</param>
/// <param name="Merchants">商户 Id → 店铺名。</param>
public sealed record PlatformNames(
    IReadOnlyDictionary<long, string> Platforms,
    IReadOnlyDictionary<long, string> Merchants);
