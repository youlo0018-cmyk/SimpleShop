using Microsoft.EntityFrameworkCore;

namespace AuthService.Infrastructure;

/// <summary>
/// 认证中心的 EF Core 上下文，只存 OpenIddict 自己的表（应用、授权、作用域、令牌）。
/// </summary>
/// <remarks>
/// 为什么 AuthService 用 EF Core 而不是全项目的 FreeSql（BUSINESS 1.3）：
/// OpenIddict 的官方存储实现就是 EF Core，换成别的 ORM 就得自己实现一遍令牌存储，
/// 令牌是安全边界最敏感的一块，优先用上游维护的实现。
/// 注意边界：<b>这里不存任何业务数据</b>。后台账号在 UserService，角色权限在 PermissionService，
/// 本库只管令牌生命周期。绝不能把账号密码哈希搬进来，那会让两个库同时持有凭据。
/// 表结构由 deploy/sql/auth 下的 SQL 脚本建立，不在运行期 EnsureCreated（DATA_SPEC 2.9）。
/// </remarks>
public sealed class AuthDbContext : DbContext
{
    /// <summary>构造上下文。</summary>
    /// <param name="options">上下文选项，由 DI 注入。</param>
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options)
    {
    }

    /// <summary>已注册的客户端（含公开客户端 admin-app）。</summary>
    public DbSet<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication> Applications
        => Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>();

    /// <summary>授权记录。</summary>
    public DbSet<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization> Authorizations
        => Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>();

    /// <summary>作用域定义。</summary>
    public DbSet<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreScope> Scopes
        => Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreScope>();

    /// <summary>已签发的令牌。</summary>
    public DbSet<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken> Tokens
        => Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>();

    /// <summary>装配 OpenIddict 的实体到本上下文。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    /// <remarks>
    /// <b>这一步不能省。</b>OpenIddict 5 之后，只在 AddDbContext 的 UseDbContextBuilder 上调
    /// UseOpenIddict() 并不会把 OpenIddict 的实体加进 EF 模型——生成的迁移会是空的，一张表都没有，
    /// 而运行期一访问就报「relation does not exist」。必须在这里再显式调一次。
    /// 踩过的坑：迁移脚本里只生成了一张 __EFMigrationsHistory。
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();
    }
}