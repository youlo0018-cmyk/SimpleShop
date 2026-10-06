using System.Text.RegularExpressions;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using Collaboration.Domain.Infrastructure;
using FreeSql;
using MerchantPlatformService.Domain.Entities;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>全局过滤器注册的防回归测试。</summary>
/// <remarks>
/// 这里守的是一个会静默破坏多租户隔离的缺陷：
/// FreeSql 的 <c>ApplyIf</c> 会把条件加到**所有**实体查询上，
/// 每个实体注册一遍后，同一张表会出现多遍租户条件；
/// 而租户根表 <c>platform</c> 的 <c>platform_id</c> 恒为 0，
/// 被套上普通租户条件后平台账号连自己的资料都查不到。
/// </remarks>
public sealed class FilterRegistrarTests
{
    private const long PlatformId = 123456789012345;
    private const long CustomerId = 987654321098765;

    /// <summary>平台根表不参与租户条件，普通后台实体只应用一次租户条件。</summary>
    [Fact]
    public void Register_ShouldApplyTenantFilterOnlyToNonRootAdminEntities()
    {
        TenantContextHolder.Set(new TenantContext
        {
            Access = AccessContext.Admin,
            TenantType = 1,
            PlatformId = PlatformId
        });

        try
        {
            using var db = BuildFreeSql();
            FilterRegistrar.Register(db, typeof(Platform).Assembly);

            var platformSql = db.Select<Platform>().ToSql();
            Assert.DoesNotContain($"\"platform_id\" = {PlatformId}", platformSql, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(platformSql, "\"is_deleted\" = 'f'"));

            var merchantSql = db.Select<Merchant>().ToSql();
            Assert.Equal(1, CountOccurrences(merchantSql, $"\"platform_id\" = {PlatformId}"));
            Assert.Equal(1, CountOccurrences(merchantSql, "\"is_deleted\" = 'f'"));
        }
        finally
        {
            TenantContextHolder.Clear();
        }
    }

    /// <summary>客户过滤只作用在客户实体上，不应串到后台实体。</summary>
    [Fact]
    public void Register_ShouldApplyCustomerFilterOnlyToCustomerEntities()
    {
        TenantContextHolder.Set(new TenantContext
        {
            Access = AccessContext.Customer,
            TenantType = 3,
            UserId = CustomerId
        });

        try
        {
            using var db = BuildFreeSql();
            FilterRegistrar.Register(db, typeof(FilterRegistrarTests).Assembly);

            var merchantSql = db.Select<Merchant>().ToSql();
            Assert.DoesNotContain($"\"customer_id\" = {CustomerId}", merchantSql, StringComparison.Ordinal);

            var customerSql = db.Select<CustomerProbe>().ToSql();
            Assert.Equal(1, CountOccurrences(customerSql, $"\"customer_id\" = {CustomerId}"));
            Assert.Equal(1, CountOccurrences(customerSql, "\"is_deleted\" = 'f'"));
        }
        finally
        {
            TenantContextHolder.Clear();
        }
    }

    /// <summary>
    /// 商户账号的租户条件必须**同时**带 platform_id 与 merchant_id（TEST_CASES API-TEN-002）。
    /// </summary>
    /// <remarks>
    /// 只加 platform_id 是最容易被漏掉的一种：商户 A 与商户 B 在同一个平台下，
    /// 少一个 merchant_id 条件，A 就能在列表里看到 B 的全部店铺数据。
    /// 端到端很难覆盖到这一层（要先建出平台维度的商户账号再比对列表），
    /// 所以在这里对着生成的 SQL 断言 —— 条件数量不对，测试立刻红。
    /// </remarks>
    [Fact]
    public void Register_ShouldScopeMerchantAccountToItsOwnMerchant()
    {
        const long MerchantId = 555666777888999;

        TenantContextHolder.Set(new TenantContext
        {
            Access = AccessContext.Admin,
            TenantType = 2,          // 商户账号
            PlatformId = PlatformId,
            MerchantId = MerchantId
        });

        try
        {
            using var db = BuildFreeSql();
            FilterRegistrar.Register(db, typeof(Merchant).Assembly);

            var merchantSql = db.Select<Merchant>().ToSql();

            // 两个条件各出现且**只出现一次**：重复叠加同样是有问题的 SQL
            Assert.Equal(1, CountOccurrences(merchantSql, $"\"platform_id\" = {PlatformId}"));
            Assert.Equal(1, CountOccurrences(merchantSql, $"\"merchant_id\" = {MerchantId}"));
            Assert.Equal(1, CountOccurrences(merchantSql, "\"is_deleted\" = 'f'"));
        }
        finally
        {
            TenantContextHolder.Clear();
        }
    }

    /// <summary>构造不连接数据库的 FreeSql，仅用于生成 SQL。</summary>
    private static IFreeSql BuildFreeSql()
        => new FreeSqlBuilder()
            .UseConnectionString(DataType.PostgreSQL, "test")
            .UseNoneCommandParameter(true)
            .UseAutoSyncStructure(false)
            .Build();

    /// <summary>统计片段出现次数。</summary>
    private static int CountOccurrences(string text, string value)
        => Regex.Matches(text, Regex.Escape(value), RegexOptions.CultureInvariant).Count;

    /// <summary>客户私有数据测试实体。</summary>
    [FreeSql.DataAnnotations.Table(Name = "filter_registrar_customer_probe")]
    private sealed class CustomerProbe : CustomerEntityBase
    {
    }
}
