using Collaboration.Domain.Context;
using FreeSql;
using FreeSql.PostgreSQL;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Collaboration.Domain.Infrastructure;

/// <summary>FreeSql 注册入口。16 个服务共用（DATA_SPEC 3.1 通用注册流程）。</summary>
public static class FreeSqlServiceCollectionExtensions
{
    /// <summary>注册 FreeSql 单例并挂上四类 AOP。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">本服务的 PostgreSQL 连接串，必须由配置源提供。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppFreeSql(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("FreeSql 连接串为空，配置校验被绕过了。");
        }

        services.AddSingleton(_ =>
        {
            // FreeSql 3.5 的 FreeSqlBuilder 只有三参的 UseConnectionString，
            // 单串重载是旧版本 API。必须显式给出 provider 类型。
            var freeSql = new FreeSqlBuilder()
                .UseConnectionString(DataType.PostgreSQL, connectionString, typeof(PostgreSQLProvider<NpgsqlConnection>))
                .UseAutoSyncStructure(false)
                .Build();

            FreeSqlAopRegistrar.Register(freeSql);
            return freeSql;
        });

        services.AddScoped<TenantContext>();
        return services;
    }
}

