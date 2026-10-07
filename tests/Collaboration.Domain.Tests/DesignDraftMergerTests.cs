using System.Text.Json;
using MerchantPlatformService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>装修草稿按页合并的单元测试（依据 DATA_SPEC 5.29「保存契约」）。</summary>
/// <remarks>
/// 背景：搭建器一次只提交当前编辑的那一页，后端必须把它合并到已存草稿上再校验。
/// 不合并的话平台装修存草稿会报「缺少「profile」页面画布」；
/// 整份覆盖又会把另一页、主题与 TabBar 洗掉。两种情况都不会在 API 层暴露 ——
/// e2e 一直是拿完整配置测的，所以这里用单元测试把边界钉死。
/// </remarks>
public class DesignDraftMergerTests
{
    private const string Stored = """
    {
      "platformCode": "demo",
      "theme": { "primary": "#0071e3", "tabColor": "", "background": "" },
      "tabBar": [ { "pagePath": "pages/index/index", "text": "首页" } ],
      "pages": {
        "index": { "components": [ { "id": "a", "type": "banner", "span": 12, "height": 80, "props": {} } ] },
        "profile": { "components": [ { "id": "b", "type": "memberCard", "span": 12, "height": 80, "props": {} } ] }
      }
    }
    """;

    [Fact]
    public void 只提交一页时_另一页原样保留()
    {
        var merged = DesignDraftMerger.Merge(Stored, """{ "pages": { "index": { "components": [] } } }""");
        var config = DesignConfig.FromJson(merged);

        Assert.Empty(config.Pages["index"].Components);
        Assert.Single(config.Pages["profile"].Components);
        Assert.Equal("b", config.Pages["profile"].Components[0].Id);
    }

    [Fact]
    public void 提交里出现的页_整页替换而不是逐字段合并()
    {
        // 逐字段合并会让「删掉一个组件」永远删不掉：已存里还有它，合并后又被带回来。
        var merged = DesignDraftMerger.Merge(Stored, """
        { "pages": { "index": { "components": [ { "id": "c", "type": "title", "span": 12, "height": 40, "props": {} } ] } } }
        """);
        var config = DesignConfig.FromJson(merged);

        Assert.Single(config.Pages["index"].Components);
        Assert.Equal("c", config.Pages["index"].Components[0].Id);
    }

    [Fact]
    public void 没带主题与TabBar时_保留已存值()
    {
        // 搭建器不编辑主题与 TabBar。不带就覆盖的话，每存一次草稿都会把它们清空。
        var merged = DesignDraftMerger.Merge(Stored, """{ "pages": { "index": { "components": [] } } }""");
        var config = DesignConfig.FromJson(merged);

        Assert.Equal("#0071e3", config.Theme.Primary);
        Assert.Single(config.TabBar);
        Assert.Equal("demo", config.PlatformCode);
    }

    [Fact]
    public void 显式带了主题与TabBar时_覆盖已存值()
    {
        var merged = DesignDraftMerger.Merge(Stored, """
        { "theme": { "primary": "#ff0000" }, "tabBar": [], "pages": { "index": { "components": [] } } }
        """);
        var config = DesignConfig.FromJson(merged);

        Assert.Equal("#ff0000", config.Theme.Primary);
        Assert.Empty(config.TabBar);
    }

    [Fact]
    public void 没有已存配置时_原样返回请求()
    {
        var merged = DesignDraftMerger.Merge(null, """{ "pages": { "index": { "components": [] } } }""");
        var config = DesignConfig.FromJson(merged);

        Assert.True(config.Pages.ContainsKey("index"));
        Assert.False(config.Pages.ContainsKey("profile"));
    }

    [Fact]
    public void 请求为空时_返回已存配置()
    {
        var merged = DesignDraftMerger.Merge(Stored, null);
        var config = DesignConfig.FromJson(merged);

        Assert.Equal(2, config.Pages.Count);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void 非法JSON或非对象_抛JsonException由调用方转业务错误(string bad)
    {
        // ThrowsAny 而不是 Throws：语法错误抛的是 JsonReaderException，
        // 它是 JsonException 的子类，而 Assert.Throws 要求类型**完全相等**。
        // 调用方（SaveAsync）catch 的是 JsonException，两种都会被转成业务错误。
        Assert.ThrowsAny<JsonException>(() => DesignDraftMerger.Merge(Stored, bad));
    }
}
