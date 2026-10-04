using Collaboration.Domain.Infrastructure;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.IRepository;

namespace MerchantPlatformService.Infrastructure.Repository;

/// <summary>装修配置仓储实现。</summary>
public sealed class DesignRepository : IDesignRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="db">已注册全局过滤的 FreeSql 单例。</param>
    public DesignRepository(IFreeSql db) => _db = db;

    /// <inheritdoc />
    public async Task<PlatformAppConfig?> GetPlatformAsync(long platformId, CancellationToken ct = default)
        => await _db.Select<PlatformAppConfig>()
            .Where(a => a.PlatformId == platformId)
            .FirstAsync(ct);

    /// <inheritdoc />
    public async Task<MerchantAppConfig?> GetMerchantAsync(long merchantId, CancellationToken ct = default)
        => await _db.Select<MerchantAppConfig>()
            .Where(a => a.MerchantId == merchantId)
            .FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> SavePlatformDraftAsync(
        long platformId, string draftJson, CancellationToken ct = default)
    {
        // 先 UPDATE 再 INSERT：并发下两个「保存草稿」都会查到「不存在」然后都去插，撞唯一索引。
        // 直接走 upsert 就没这个问题
        var affected = await _db.Update<PlatformAppConfig>()
            .Where(a => a.PlatformId == platformId)
            .Set(a => new PlatformAppConfig { DraftJson = draftJson, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

        if (affected > 0)
        {
            var existing = await GetPlatformAsync(platformId, ct);
            return existing?.Id ?? 0;
        }

        var config = new PlatformAppConfig { PlatformId = platformId, DraftJson = draftJson };
        config.Id = SnowflakeId.NewId();
        config.CreatedAt = DateTime.UtcNow;
        await _db.Insert(config).ExecuteAffrowsAsync(ct);

        return config.Id;
    }

    /// <inheritdoc />
    public async Task<long> SaveMerchantDraftAsync(
        long merchantId, long platformId, string draftJson, CancellationToken ct = default)
    {
        var affected = await _db.Update<MerchantAppConfig>()
            .Where(a => a.MerchantId == merchantId)
            .Set(a => new MerchantAppConfig { DraftJson = draftJson, UpdatedAt = DateTime.UtcNow })
            .ExecuteAffrowsAsync(ct);

        if (affected > 0)
        {
            var existing = await GetMerchantAsync(merchantId, ct);
            return existing?.Id ?? 0;
        }

        var config = new MerchantAppConfig
        {
            MerchantId = merchantId,
            PlatformId = platformId,
            DraftJson = draftJson
        };
        config.Id = SnowflakeId.NewId();
        config.CreatedAt = DateTime.UtcNow;
        await _db.Insert(config).ExecuteAffrowsAsync(ct);

        return config.Id;
    }

    /// <inheritdoc />
    public async Task<int> PublishPlatformAsync(long platformId, CancellationToken ct = default)
    {
        var config = await GetPlatformAsync(platformId, ct);

        // 没有草稿可发布：返回 0 让调用方提示「请先保存草稿」，
        // 而不是把线上那份原地覆盖成空
        if (config is null || string.IsNullOrWhiteSpace(config.DraftJson)) return 0;

        config.PublishedJson = config.DraftJson;
        config.Version += 1;
        config.UpdatedAt = DateTime.UtcNow;

        await _db.Update<PlatformAppConfig>()
            .Where(a => a.Id == config.Id)
            .Set(a => new PlatformAppConfig
            {
                PublishedJson = config.PublishedJson,
                Version = config.Version,
                UpdatedAt = config.UpdatedAt
            })
            .ExecuteAffrowsAsync(ct);

        return config.Version;
    }

    /// <inheritdoc />
    public async Task<int> PublishMerchantAsync(long merchantId, CancellationToken ct = default)
    {
        var config = await GetMerchantAsync(merchantId, ct);
        if (config is null || string.IsNullOrWhiteSpace(config.DraftJson)) return 0;

        config.PublishedJson = config.DraftJson;
        config.Version += 1;
        config.UpdatedAt = DateTime.UtcNow;

        await _db.Update<MerchantAppConfig>()
            .Where(a => a.Id == config.Id)
            .Set(a => new MerchantAppConfig
            {
                PublishedJson = config.PublishedJson,
                Version = config.Version,
                UpdatedAt = config.UpdatedAt
            })
            .ExecuteAffrowsAsync(ct);

        return config.Version;
    }
}
