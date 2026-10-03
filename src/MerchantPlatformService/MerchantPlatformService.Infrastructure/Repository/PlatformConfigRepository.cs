using Collaboration.Domain.Infrastructure;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.IRepository;

namespace MerchantPlatformService.Infrastructure.Repository;

/// <summary>平台配置仓储实现（地区地址）。</summary>
public sealed class PlatformConfigRepository : IPlatformConfigRepository
{
    private readonly IFreeSql _db;

    /// <summary>构造仓储。</summary>
    /// <param name="db">已注册全局过滤的 FreeSql 单例。</param>
    public PlatformConfigRepository(IFreeSql db) => _db = db;

    /// <inheritdoc />
    public async Task<PlatformConfig?> GetByPlatformAsync(long platformId, CancellationToken ct = default)
        => await _db.Select<PlatformConfig>()
            .Where(a => a.PlatformId == platformId)
            .FirstAsync(ct);

    /// <inheritdoc />
    public async Task<long> SaveAsync(PlatformConfig config, CancellationToken ct = default)
    {
        // 配置是「一个平台一份」，所以这里做 upsert 而不是让调用方判断存不存在：
        // 并发下两个请求都会查到「不存在」，然后都去插，撞唯一索引。
        // 直接 UPDATE ... WHERE platform_id = x，命中 0 行再 INSERT。
        var affected = await _db.Update<PlatformConfig>()
            .Where(a => a.PlatformId == config.PlatformId)
            .Set(a => new PlatformConfig
            {
                RegionsJson = config.RegionsJson,
                UpdatedAt = DateTime.UtcNow
            })
            .ExecuteAffrowsAsync(ct);

        if (affected > 0) return config.Id;

        config.Id = SnowflakeId.NewId();
        config.CreatedAt = DateTime.UtcNow;
        await _db.Insert(config).ExecuteAffrowsAsync(ct);

        return config.Id;
    }
}
