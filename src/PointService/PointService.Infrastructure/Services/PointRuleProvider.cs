using System.Text.Json;
using FreeSql;
using Microsoft.Extensions.Logging;
using PointService.Domain.Entities;
using PointService.Domain.Services;

namespace PointService.Infrastructure.Services;

/// <summary>积分规则提供器。读 point_rule_config 覆盖值，缺项回落代码默认。</summary>
/// <remarks>
/// 缓存 30 秒，与网关的 RBAC 缓存一致：规则改动不该要求「等所有实例都过期」才生效，
/// 但也不该每发一笔积分就查一次库。
/// 解析失败（值不是数字、JSON 坏了）时记警告并回落默认值，
/// 绝不抛异常把下单链路打断——一份配置写坏了不该让所有人下不了单。
/// </remarks>
public sealed class PointRuleProvider : IPointRuleProvider
{
    /// <summary>缓存有效期（秒）。</summary>
    private const int CacheSeconds = 30;

    private readonly IFreeSql _db;
    private readonly ILogger<PointRuleProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile PointRuleSnapshot? _cached;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>构造提供器。</summary>
    /// <param name="db">FreeSql 实例。</param>
    /// <param name="logger">日志器。</param>
    public PointRuleProvider(IFreeSql db, ILogger<PointRuleProvider> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PointRuleSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (IsFresh()) return _cached!;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh()) return _cached!;

            var rows = await _db.Select<PointRuleConfig>()
                .Where(a => a.RuleValue != null && a.RuleValue != string.Empty)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            _cached = Build(rows);
            _loadedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            // 表不存在（首次部署没跑 SQL）或查询失败：回落默认值并继续服务。
            _logger.LogError(ex, "读取积分规则配置失败，本次按默认规则处理");
            return PointRuleSnapshot.Defaults();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private bool IsFresh()
        => _cached is not null
           && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(CacheSeconds);

    /// <summary>按覆盖值组装规则快照。</summary>
    /// <param name="rows">配置行。</param>
    /// <returns>规则快照。</returns>
    private PointRuleSnapshot Build(List<PointRuleConfig> rows)
    {
        var d = PointRuleSnapshot.Defaults();
        var map = rows.ToDictionary(a => a.RuleKey, a => a.RuleValue, StringComparer.OrdinalIgnoreCase);

        return new PointRuleSnapshot(
            ReadLong(map, PointRuleKeys.BalanceCap, d.BalanceCap, 1),
            (int)ReadLong(map, PointRuleKeys.ValidDays, d.ValidDays, 1),
            ReadLong(map, PointRuleKeys.RegisterGift, d.RegisterGift, 0),
            ReadLong(map, PointRuleKeys.FirstEvaluateGift, d.FirstEvaluateGift, 0),
            ReadLong(map, PointRuleKeys.PointsPerYuan, d.PointsPerYuan, 1),
            ReadLong(map, PointRuleKeys.EarnPointsPerYuan, d.EarnPointsPerYuan, 0),
            ReadRewards(map) ?? d.SignInRewards);
    }

    private long ReadLong(
        Dictionary<string, string> map, string key, long fallback, long minValue)
    {
        if (!map.TryGetValue(key, out var raw)) return fallback;

        if (!long.TryParse(raw, out var value) || value < minValue)
        {
            _logger.LogWarning(
                "积分规则 {Key} 的值 {Raw} 不合法（要求不小于 {Min}），本次按默认值 {Fallback} 处理",
                key, raw, minValue, fallback);
            return fallback;
        }

        return value;
    }

    private IReadOnlyList<long>? ReadRewards(Dictionary<string, string> map)
    {
        if (!map.TryGetValue(PointRuleKeys.SignInRewards, out var raw)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<long[]>(raw);
            if (parsed is null || parsed.Length == 0) return null;

            // 负数奖励会让「签到」变成扣分，必须挡掉。
            if (parsed.Any(x => x < 0))
            {
                _logger.LogWarning("积分规则 sign_in_rewards 含负数，本次按默认值处理");
                return null;
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "积分规则 sign_in_rewards 不是合法 JSON 数组，本次按默认值处理");
            return null;
        }
    }
}
