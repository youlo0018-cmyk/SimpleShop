using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.Infrastructure;

/// <summary>
/// 设计时工厂，只给 <c>dotnet ef</c> 生成迁移脚本用。
/// </summary>
/// <remarks>
/// 运行期不加载这个类。dotnet ef 不会启动整个应用，所以拿不到 AgileConfig，
/// 只能从环境变量或默认值拿一条连接串。默认值只针对本地开发库 simpleshopauth。
/// 生成的迁移脚本提交进 deploy/sql/auth/，运行期只跑 SQL，不再碰迁移（DATA_SPEC 2.9）。
/// </remarks>
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    /// <summary>本地开发库名。</summary>
    private const string DevDatabase = "simpleshopauth";

    /// <summary>创建上下文。</summary>
    /// <param name="args">dotnet ef 传入的参数，这里不用。</param>
    /// <returns>可设计时使用的上下文。</returns>
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("SIMPLESHOP_AUTH_CONNECTION")
            ?? $"Host=127.0.0.1;Port=5432;Database={DevDatabase};Username=simpleshop_app;Password=simpleshop_dev_2026";

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString)
            .UseOpenIddict()
            .Options;

        return new AuthDbContext(options);
    }
}