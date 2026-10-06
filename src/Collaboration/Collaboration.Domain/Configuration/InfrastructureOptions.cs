namespace Collaboration.Domain.Configuration;

/// <summary>PostgreSQL 连接配置。每个服务一个独立库（DATA_SPEC 3.3）。</summary>
public sealed class DatabaseOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "ConnectionStrings";

    /// <summary>本服务数据库连接串。</summary>
    public string Default { get; set; } = string.Empty;
}

/// <summary>Redis 连接配置。用于分布式锁、缓存、秒杀预扣（DATA_SPEC 1.4）。</summary>
public sealed class RedisOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Redis";

    /// <summary>连接串，例如 127.0.0.1:6379。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>逻辑库索引。不同用途建议错开。</summary>
    public int Database { get; set; }

    /// <summary>
    /// 跨服务共享的逻辑库索引，默认 0。
    /// </summary>
    /// <remarks>
    /// 各服务的 Redis 库是**独占**的（避免同名 key 撞车），但有一类键必须被多个服务读到：
    /// 后台账号的会话吊销键由 UserService 写、由网关在每个请求上读（DATA_SPEC 5.20）。
    /// 两边各用各的库号就永远读不到对方写的值，而且失败是静默的——令牌看起来「吊销了」却照样能用。
    /// 所以这类键统一走本库号。默认 0 与网关自身的库号一致，不配置也能对上。
    /// </remarks>
    public int SharedDatabase { get; set; }
}

/// <summary>Consul 连接配置。</summary>
public sealed class ConsulOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Consul";

    /// <summary>Consul 地址，例如 http://127.0.0.1:8500。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>本服务注册到 Consul 的服务名，默认等于服务名。</summary>
    public string ServiceName { get; set; } = string.Empty;
}

/// <summary>RabbitMQ 连接配置。</summary>
public sealed class RabbitMqOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>主机地址。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>端口，默认 5672。</summary>
    public int Port { get; set; } = 5672;

    /// <summary>用户名。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>虚拟主机。</summary>
    public string VirtualHost { get; set; } = "/";
}

/// <summary>雪花 Id 配置。workerId 由 Redis 租约槽位分配（DATA_SPEC 3.4）。</summary>
public sealed class SnowflakeOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Snowflake";

    /// <summary>Redis 中分配 workerId 的 key 前缀，最终 key 为 {前缀}:{服务名}:{槽位}。</summary>
    public string WorkerIdKeyPrefix { get; set; } = "snowflake:worker";

    /// <summary>
    /// 槽位总数（不含上界），即 workerId 可用范围。全部槽位都被未过期的租约占满时启动失败，
    /// 并打印每个槽位的占用情况。
    /// </summary>
    public ushort WorkerIdUpperBound { get; set; } = 64;
}

/// <summary>客户 JWT 配置（HS256）。后台令牌由 AuthService 的 OpenIddict 用 RS256 签发（DATA_SPEC 4）。</summary>
public sealed class JwtOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Jwt";

    /// <summary>签发者。</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>受众。</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>HS256 签名密钥。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>有效期，单位小时。</summary>
    public int ExpireHours { get; set; } = 12;
}

