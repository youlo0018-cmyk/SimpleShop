using Collaboration.Domain.Context;
using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Collaboration.Domain.Infrastructure;

/// <summary>FreeSql 注册入口。16 个服务共用（DATA_SPEC 3.1 通用注册流程）。</summary>
public static class FreeSqlServiceCollectionExtensions
{
    /// <summary>注册 FreeSql 单例并挂上四类 AOP。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">本服务的 PostgreSQL 连接串，必须由配置源提供。</param>
    /// <param name="entityAssemblies">实体所在程序集，通常是本服务的 Xxx.Domain，用于注册全局过滤。</param>
    /// <returns>原集合，便于链式调用。</returns>
    public static IServiceCollection AddAppFreeSql(
        this IServiceCollection services,
        string connectionString,
        params Assembly[] entityAssemblies)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("FreeSql 连接串为空，配置校验被绕过了。");
        }

        services.AddSingleton(_ =>
        {
            // FreeSql 3.5 的 UseConnectionString 第三参 providerType 是**可选**的，
            // 由 DataType 自动解析 provider。手动传 typeof(PostgreSQLProvider<NpgsqlConnection>)
            // 反而会让 Build() 抛 NullReferenceException——不要多传。
            var freeSql = new FreeSqlBuilder()
                .UseConnectionString(DataType.PostgreSQL, connectionString)
                // 关闭 CodeFirst，表结构一律由 deploy/sql 管（DATA_SPEC 2.9）
                .UseAutoSyncStructure(false)
                .Build();

            FilterRegistrar.Register(freeSql, entityAssemblies);
            return freeSql;
        });

        services.AddScoped<TenantContext>();
        return services;
    }
}

