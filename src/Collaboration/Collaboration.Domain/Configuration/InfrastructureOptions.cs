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

/// <summary>雪花 Id 配置。workerId 由 Redis INCR 原子自增分配（DATA_SPEC 3.4）。</summary>
public sealed class SnowflakeOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Snowflake";

    /// <summary>Redis 中分配 workerId 的 key 前缀，最终 key 为 {前缀}:{服务名}。</summary>
    public string WorkerIdKeyPrefix { get; set; } = "snowflake:worker";

    /// <summary>workerId 上限（不含）。超过则启动失败并打印已分配到的最大值，不做回绕复用。</summary>
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

