using System.Text.Json;
using MerchantPlatformService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>装修配置校验的单元测试（规格 16.4 / TEST_CASES API-DSG-005）。</summary>
/// <remarks>
/// 重点是<b>商户不能改配色</b>这条：它错的方向很隐蔽 —— 不报错、页面照常打开，
/// 只是颜色悄悄变成了平台的，运营会以为「这破页面不理我」而不是「我没权限改」。
/// </remarks>
public class DesignValidatorTests
{
    /// <summary>把字符串包成组件 props 里的一个 JSON 值。</summary>
    /// <param name="value">字符串值。</param>
    /// <returns>JsonElement。</returns>
    private static JsonElement Prop(string value) => JsonSerializer.SerializeToElement(value);

    /// <summary>造一份只有一个店铺组件的商户装修配置。</summary>
    /// <param name="primary">主题色。</param>
    /// <returns>配置对象。</returns>
    private static DesignConfig MerchantConfig(string primary = "#0071e3")
    {
        var config = new DesignConfig
        {
            Theme = new DesignTheme { Primary = primary, TabColor = "#0071e3" },
        };
        config.Pages["store"] = new DesignPage
        {
            Components =
            [
                new DesignComponent
                {
                    Id = "c1",
                    Type = "shopHeader",
                    Span = 12,
                    Height = 90,
                    Props = new Dictionary<string, JsonElement> { ["shopName"] = Prop("测试店") },
                },
            ],
        };
        return config;
    }

    [Fact]
    public void 商户装修_主题三档色与组件配色都被剔除并告警()
    {
        var config = MerchantConfig();
        config.Theme.TabColor = "#00FF00";
        config.Theme.Background = "#0000FF";
        config.Pages["store"].Components[0].Props["primaryColor"] = Prop("#ABCDEF");

        var result = DesignValidator.Validate(config, forMerchant: true);

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, config.Theme.Primary);
        Assert.Equal(string.Empty, config.Theme.TabColor);
        Assert.Equal(string.Empty, config.Theme.Background);
        // 组件上的配色字段同样要剔（这条原本就有，别被主题的修复挤掉）
        Assert.False(config.Pages["store"].Components[0].Props.ContainsKey("primaryColor"));

        // 三档主题色 + 一个组件字段 = 4 条告警
        Assert.Equal(4, result.Warnings.Count);
        Assert.Contains(result.Warnings, a => a.Contains("已剔除"));
    }

    [Fact]
    public void 商户装修_没传配色时不产生告警()
    {
        var config = MerchantConfig(primary: string.Empty);
        config.Theme.TabColor = string.Empty;

        var result = DesignValidator.Validate(config, forMerchant: true);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void 平台装修_配色保留不被剔除()
    {
        var config = new DesignConfig
        {
            Theme = new DesignTheme { Primary = "#FF0000" },
        };
        config.Pages["index"] = new DesignPage();
        config.Pages["profile"] = new DesignPage();

        var result = DesignValidator.Validate(config, forMerchant: false);

        Assert.True(result.IsValid);
        // 平台装修本来就能改配色，剔了就等于把功能做没了
        Assert.Equal("#FF0000", config.Theme.Primary);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void 商户装修_非法配色值先判错而不是当成已剔除()
    {
        var config = MerchantConfig(primary: "不是颜色");

        var result = DesignValidator.Validate(config, forMerchant: true);

        // 「悄悄剔除掉」会让运营以为自己填对了色；
        // 传了个乱码进来必须明确报错。
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, a => a.Contains("主题色"));
    }

    [Fact]
    public void 商户装修_配置首页画布被拒()
    {
        var config = MerchantConfig();
        config.Pages["index"] = new DesignPage();

        var result = DesignValidator.Validate(config, forMerchant: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, a => a.Contains("只能配置店铺页"));
    }
}
