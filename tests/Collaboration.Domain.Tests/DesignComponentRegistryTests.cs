using MerchantPlatformService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>装修组件注册表的单元测试（依据 BUSINESS.md 16.3、16.4）。</summary>
public class DesignComponentRegistryTests
{
    /// <summary>取某页面在平台装修里可用的组件类型。</summary>
    /// <param name="page">页面标识。</param>
    /// <returns>组件类型集合。</returns>
    private static HashSet<string> PlatformTypes(string page)
        => DesignComponentRegistry.ForPlatformPage(page).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void 首页_可用全部通用组件()
    {
        var types = PlatformTypes(DesignPages.Index);
        Assert.Contains("banner", types);
        Assert.Contains("productGrid", types);
        Assert.Contains("searchBar", types);
        Assert.Contains("spacer", types);
    }

    [Fact]
    public void 我的页_额外可用会员与服务宫格()
    {
        var types = PlatformTypes(DesignPages.Profile);
        Assert.Contains("memberCard", types);
        Assert.Contains("serviceGrid", types);
        Assert.Contains("banner", types);
        Assert.DoesNotContain("seckillZone", types);
    }

    [Fact]
    public void 会员组件不在首页_规格说的是我的页额外可用()
    {
        // 「我的页额外可用会员与服务宫格」的「额外」意味着它不在首页。
        // 一开始把会员组件也注册到了首页，于是首页能摆一张会员卡——看着像 bug
        Assert.DoesNotContain("memberCard", PlatformTypes(DesignPages.Index));
        Assert.DoesNotContain("benefits", PlatformTypes(DesignPages.Index));
    }

    [Fact]
    public void 店铺类组件只在店铺页_店铺类不属于通用组件()
    {
        var store = PlatformTypes(DesignPages.Store);
        var index = PlatformTypes(DesignPages.Index);
        Assert.Contains("shopHeader", store);
        Assert.Contains("shopEvaluate", store);
        Assert.DoesNotContain("shopHeader", index);
        Assert.DoesNotContain("shopEvaluate", index);
    }

    [Fact]
    public void 店铺页_只有店铺类与通用组件()
    {
        var store = PlatformTypes(DesignPages.Store);
        Assert.DoesNotContain("memberCard", store);
        Assert.DoesNotContain("serviceGrid", store);
        Assert.DoesNotContain("seckillZone", store);
        Assert.Contains("banner", store);
    }

    [Fact]
    public void 商户组件库只有11个_与平台组件库互不干扰()
    {
        // 用户明确要求商户装修与平台装修组件库独立
        Assert.Equal(11, DesignComponentRegistry.MerchantTypes.Count);
        Assert.Contains("shopHeader", DesignComponentRegistry.MerchantTypes);
        Assert.Contains("productGrid", DesignComponentRegistry.MerchantTypes);
        Assert.Contains("banner", DesignComponentRegistry.MerchantTypes);
        Assert.DoesNotContain("memberCard", DesignComponentRegistry.MerchantTypes);
        Assert.DoesNotContain("searchBar", DesignComponentRegistry.MerchantTypes);
    }

    [Fact]
    public void 组件类型不能重复注册()
    {
        // 重复注册会让「按页面过滤」的语义不可预测：
        // 同一 type 出现在两条规则里时，取哪条取决于遍历顺序
        Assert.Equal(DesignComponentRegistry.All.Count, DesignComponentRegistry.AllTypes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(12)]
    public void 允许的栅格列数(int span)
        => Assert.Contains(span, DesignComponentRegistry.AllowedSpans);

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void 不允许的栅格列数(int span)
        => Assert.DoesNotContain(span, DesignComponentRegistry.AllowedSpans);
}
