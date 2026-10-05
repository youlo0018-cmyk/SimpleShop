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

        // 🔴 必须是 Scoped，不能是 Singleton。
        //
        // 原因：FilterRegistrar.Register 会读取 TenantContextHolder.Current 来构造
        // 「租户过滤」条件（PlatformId = 我的平台）。这个注册动作发生在
        // IFreeSql **第一次被解析**的时候：
        //   - Singleton 下它只发生一次，条件被**永久锁定**成第一个请求的租户；
        //   - 如果第一个请求恰好是匿名/探活请求，结果就是**永远不注册租户过滤**。
        // 实测后果：一个平台账号能看到全部 50 个平台，也能改别人的平台。
        // 这是一个静默的、跨全部 16 个服务的多租户隔离失效。
        //
        // 代价说明：FreeSql 实例改为每请求一个。它本身只是个对象图，
        // 真正的连接由 Npgsql 连接池复用，所以这不是「每请求开一个连接」。
        // 用一点对象分配换回租户隔离，这个交换是必须的。
        services.AddScoped(_ =>
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

            // 排障用 SQL 追踪：设 SIMPLESHOP_SQL_TRACE=1 才开，默认关。
            // FreeSql 默认不打印任何 SQL，出了「接口返回成功但数据没变」这类问题时
            // 没有语句可看，只能靠猜；这里给出开关，但不默认打开（量级很大）。
            if (string.Equals(Environment.GetEnvironmentVariable("SIMPLESHOP_SQL_TRACE"), "1", StringComparison.Ordinal))
            {
                freeSql.Aop.CommandBefore += (_, e) =>
                    Console.WriteLine($"[sql] {e.Command.CommandText}");
            }

            return freeSql;
        });

        services.AddScoped<TenantContext>();
        return services;
    }
}

