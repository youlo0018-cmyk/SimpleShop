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
    /// 商户账号查 <c>merchant</c> 表（商户维度的租户根）时，条件必须是 <c>Id = 自己</c>，
    /// 而**不是** <c>merchant_id = 自己</c>（TEST_CASES API-TEN-002）。
    /// </summary>
    /// <remarks>
    /// <para><b>这条断言改过一次，因为原断言把一个缺陷写成了期望。</b>
    /// <c>merchant</c> 表是商户维度的租户根：它的 <c>merchant_id</c> 列恒为 0
    /// （商户不隶属于另一个商户），身份在 <c>Id</c> 上。
    /// 原断言要求 SQL 里出现 <c>"merchant_id" = 自己</c>，而那正是「商户查不到自己」的原因 ——
    /// 实测后果是商户账号保存店铺装修永远回「商户不存在」，而 E2E 一直用超管令牌，所以没被发现。</para>
    ///
    /// <para>真正要防的仍然是「商户 A 看到商户 B」：判据换成 <c>Id = 自己</c> 之后，
    /// 同平台兄弟商户照样被挡住，而自己的那一条能查到了。
    /// 端到端对应 <c>design-regression.ps1</c> 的 API-DS-102 / API-DS-103。</para>
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

            // 条件各出现且**只出现一次**：重复叠加同样是有问题的 SQL
            Assert.Equal(1, CountOccurrences(merchantSql, $"\"id\" = {MerchantId}"));
            // 租户根表不能再叠加 merchant_id 条件：那一列恒为 0，加了就一条都查不到。
            // 注意判据要带上 " = "：SELECT 列表里本来就会出现 merchant_id 这一列名，
            // 只匹配列名会把「有过滤条件」误判成「没有」。
            Assert.Equal(0, CountOccurrences(merchantSql, "\"merchant_id\" = "));
            // 同理，平台条件也换成了 Id：租户根表不看 platform_id 归属
            Assert.Equal(0, CountOccurrences(merchantSql, "\"platform_id\" = "));
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
