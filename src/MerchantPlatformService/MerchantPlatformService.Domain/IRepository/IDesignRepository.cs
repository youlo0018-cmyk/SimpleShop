using MerchantPlatformService.Domain.Entities;

namespace MerchantPlatformService.Domain.IRepository;

/// <summary>装修配置仓储。</summary>
public interface IDesignRepository
{
    /// <summary>取平台装修配置，不存在返回 null。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置实体或 null。</returns>
    Task<PlatformAppConfig?> GetPlatformAsync(long platformId, CancellationToken ct = default);

    /// <summary>取商户装修配置，不存在返回 null。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置实体或 null。</returns>
    Task<MerchantAppConfig?> GetMerchantAsync(long merchantId, CancellationToken ct = default);

    /// <summary>保存平台装修草稿（不影响线上）。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="draftJson">草稿 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置 Id。</returns>
    Task<long> SavePlatformDraftAsync(long platformId, string draftJson, CancellationToken ct = default);

    /// <summary>保存商户装修草稿（不影响线上）。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="platformId">所属平台 Id（冗余，便于按平台查）。</param>
    /// <param name="draftJson">草稿 JSON。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>配置 Id。</returns>
    Task<long> SaveMerchantDraftAsync(long merchantId, long platformId, string draftJson,
        CancellationToken ct = default);

    /// <summary>发布平台装修：草稿转已发布，版本号 +1。</summary>
    /// <param name="platformId">平台 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号；没有草稿返回 0。</returns>
    Task<int> PublishPlatformAsync(long platformId, CancellationToken ct = default);

    /// <summary>发布商户装修：草稿转已发布，版本号 +1。</summary>
    /// <param name="merchantId">商户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>发布后的版本号；没有草稿返回 0。</returns>
    Task<int> PublishMerchantAsync(long merchantId, CancellationToken ct = default);
}
