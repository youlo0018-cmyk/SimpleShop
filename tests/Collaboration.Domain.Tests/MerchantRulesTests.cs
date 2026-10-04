using MerchantPlatformService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>平台 / 商户纯规则的单元测试（依据 DATA_SPEC 5.1、5.2、5.31）。</summary>
public class MerchantRulesTests
{
    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("aBcDeF")]     // 大小写都合法
    [InlineData("  QHRQDB  ")]
    public void ValidatePlatformCode_六位字母_通过(string code)
        => Assert.Null(MerchantRules.ValidatePlatformCode(code));

    [Theory]
    [InlineData("ABCDE")]     // 5 位
    [InlineData("ABCDEFG")]   // 7 位
    [InlineData("P12345")]    // 含数字：规格要的是 6 位**字母**
    [InlineData("")]
    public void ValidatePlatformCode_非法_给出提示(string code)
        => Assert.NotNull(MerchantRules.ValidatePlatformCode(code));

    [Theory]
    [InlineData("13800138000")]
    [InlineData("19912345678")]
    public void ValidatePhone_合法_通过(string phone)
        => Assert.Null(MerchantRules.ValidatePhone(phone));

    [Theory]
    [InlineData("12345678901")]  // 第二位是 1，规则要求 [3-9]
    [InlineData("1380013800")]   // 10 位
    [InlineData("abcdefghijk")]
    public void ValidatePhone_非法_给出提示(string phone)
        => Assert.NotNull(MerchantRules.ValidatePhone(phone));

    [Theory]
    [InlineData("#0071e3")]
    [InlineData("#FFFFFF")]
    [InlineData("")]            // 空 = 用默认值，放行
    public void ValidateColor_合法或为空_通过(string color)
        => Assert.Null(MerchantRules.ValidateColor(color));

    [Theory]
    [InlineData("red")]
    [InlineData("#GGG")]
    [InlineData("#0071E")]      // 5 位
    public void ValidateColor_非法_给出提示(string color)
        => Assert.NotNull(MerchantRules.ValidateColor(color));

    [Fact]
    public void BuildMerchantNo_平台编码加雪花Id()
    {
        // 编号带平台前缀，客服收到就能判断归属，不用再查库
        Assert.Equal("DEMOPL13755080881608709",
            MerchantRules.BuildMerchantNo("demopl", 13755080881608709));
    }

    [Fact]
    public void ValidateRegionsJson_空串视为恢复默认_通过()
        => Assert.Null(MerchantRules.ValidateRegionsJson(""));

    [Theory]
    [InlineData("{不是数组")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ValidateRegionsJson_非法结构_给出提示(string json)
        => Assert.NotNull(MerchantRules.ValidateRegionsJson(json));

    [Fact]
    public void ValidateRegionsJson_顶层缺name_给出提示()
        => Assert.NotNull(MerchantRules.ValidateRegionsJson("[{\"code\":\"3301\"}]"));

    [Fact]
    public void ValidateRegionsJson_孙节点缺name_给出提示()
    {
        // 递归检查：这个坑出过一次——只查顶层时，缺 name 的子节点能通过校验，
        // 到了前端才渲染出一行空白，用户只会以为系统坏了
        const string json = "[{\"name\":\"浙江省\",\"children\":[{\"name\":\"杭州市\"},{\"code\":\"3302\"}]}]";
        Assert.NotNull(MerchantRules.ValidateRegionsJson(json));
    }

    [Fact]
    public void ValidateRegionsJson_每级都有name_通过()
    {
        // 🔴 这条同时守住「[JsonPropertyName] 必须显式标注」那条规则：
        // 地区 JSON 的键是小写 name / children，而 JsonSerializer.Deserialize<T>(json)
        // 默认**区分大小写**，没标注时小写 name 绑不上 PascalCase 的 Name，
        // 校验会报「每一级地区都必须填写名称」——而用户明明每一级都填了名称。
        // 症状（校验报缺名称）与病因（键名没绑上）隔了三层，几乎不可能往回找。
        const string json =
            "[{\"name\":\"浙江省\",\"children\":[{\"name\":\"杭州市\",\"children\":[{\"name\":\"西湖区\"}]}]}]";
        Assert.Null(MerchantRules.ValidateRegionsJson(json));
    }

    [Fact]
    public void ValidateRegionsJson_超过2MB_给出提示()
    {
        // 构造一个体积超限但结构合法的 JSON，验证体积上限先于结构被拦下
        var filler = new string('a', MerchantRules.MaxRegionsJsonBytes + 1024);
        Assert.Contains("2MB", MerchantRules.ValidateRegionsJson($"[{filler}]"));
    }
}
