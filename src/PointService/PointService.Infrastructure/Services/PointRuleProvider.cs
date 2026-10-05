using System.Text.Json;
using FreeSql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PointRuleProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile PointRuleSnapshot? _cached;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>构造提供器。</summary>
    /// <param name="scopes">作用域工厂。用来在需要查库时开一个短作用域取 IFreeSql。</param>
    /// <param name="logger">日志器。</param>
    /// <remarks>
    /// <b>为什么注入作用域工厂而不是直接注入 IFreeSql</b>：本类是 Singleton（规则要跨请求缓存 30 秒），
    /// 而 IFreeSql 是 Scoped —— 因为它要按「当前请求的租户」构造全局过滤条件（见
    /// FreeSqlServiceCollectionExtensions 的说明）。单例直接持有 Scoped 依赖会被 DI 校验拒绝
    /// （fail-fast，正是我们想要的）。所以这里只在**真正要查库**的那一刻开一个短作用域，
    /// 用完即弃，缓存仍在单例上。
    /// </remarks>
    public PointRuleProvider(IServiceScopeFactory scopes, ILogger<PointRuleProvider> logger)
    {
        _scopes = scopes;
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

            // 开一个短作用域取 IFreeSql：积分规则是**全局**配置（不按租户分），
            // 所以这里用哪个租户的上下文查都一样。真正要紧的是别把 Scoped 的
            // IFreeSql 关在单例里 —— 那等于让第一个请求的租户条件永久生效。
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IFreeSql>();

            var rows = await db.Select<PointRuleConfig>()
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
