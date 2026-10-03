using EvaluateService.Domain.Services;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>评价纯计算规则的单元测试（依据 BUSINESS.md 14）。</summary>
public class EvaluateCalculatorTests
{
    [Fact]
    public void FormatSpecs_不超过三个_按原顺序逗号连接()
    {
        Assert.Equal("红色 / M、蓝色 / L",
            EvaluateCalculator.FormatSpecs(["红色 / M", "蓝色 / L"]));
    }

    [Fact]
    public void FormatSpecs_恰好三个_不折叠()
    {
        Assert.Equal("A、B、C", EvaluateCalculator.FormatSpecs(["A", "B", "C"]));
    }

    [Fact]
    public void FormatSpecs_超过三个_折叠为等N个规格()
    {
        // 规格 14.1：最多列 3 个，超出显示「等 N 个规格」
        Assert.Equal("A、B、C 等 5 个规格",
            EvaluateCalculator.FormatSpecs(["A", "B", "C", "D", "E"]));
    }

    [Fact]
    public void FormatSpecs_有重复规格_去重后再算数量()
    {
        // 「红色 / M」买了两次只该算一个规格。
        // 不去重的话会显示「A、B 等 3 个规格」，而实际只有 2 个——很荒唐
        Assert.Equal("红色 / M、蓝色 / L",
            EvaluateCalculator.FormatSpecs(["红色 / M", "蓝色 / L", "红色 / M"]));
    }

    [Fact]
    public void FormatSpecs_去重后仍超三个_按去重后的数量折叠()
    {
        Assert.Equal("A、B、C 等 4 个规格",
            EvaluateCalculator.FormatSpecs(["A", "B", "C", "D", "A", "B"]));
    }

    [Fact]
    public void FormatSpecs_规格为空白_忽略不占位()
    {
        Assert.Equal("红色", EvaluateCalculator.FormatSpecs(["红色", "  ", ""]));
    }

    [Fact]
    public void FormatSpecs_全部为空白_返回空串()
        => Assert.Equal(string.Empty, EvaluateCalculator.FormatSpecs([" ", ""]));

    [Fact]
    public void FormatSpecs_空集合_返回空串()
        => Assert.Equal(string.Empty, EvaluateCalculator.FormatSpecs([]));

    [Fact]
    public void ValidateContentAndImages_有文字_通过()
    {
        Assert.True(EvaluateCalculator.ValidateContentAndImages("很好用", [], out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ValidateContentAndImages_有图无文字_通过()
    {
        // 规格 14.2：图片至少 1 张**或**文字至少 1 条，配图也算有效评价
        Assert.True(EvaluateCalculator.ValidateContentAndImages("", ["a.jpg"], out _));
    }

    [Fact]
    public void ValidateContentAndImages_全空白且无图_不通过并给出原因()
    {
        Assert.False(EvaluateCalculator.ValidateContentAndImages("   ", [], out var error));
        Assert.Contains("图片", error);
    }

    [Fact]
    public void AverageScore_整数均分_保留两位()
    {
        Assert.Equal(4.00m, EvaluateCalculator.AverageScore([4, 4]));
        Assert.Equal(3.00m, EvaluateCalculator.AverageScore([3, 3, 3]));
    }

    [Fact]
    public void AverageScore_除不尽_四舍五入而非银行家舍入()
    {
        // 3 星 + 4 星 + 4 星 = 3.67。ToEven 在 .5 上会进成偶数，
        // AwayFromZero 才符合中文语境的「四舍五入」
        Assert.Equal(3.67m, EvaluateCalculator.AverageScore([3, 4, 4]));
        Assert.Equal(3.67m, EvaluateCalculator.AverageScore([3, 4, 4]));
    }

    [Fact]
    public void AverageScore_中点值_按四舍五入进位()
    {
        // 5 星 + 4 星 = 4.5，正好是中点
        Assert.Equal(4.50m, EvaluateCalculator.AverageScore([5, 4]));
    }

    [Fact]
    public void AverageScore_无评价_返回0()
        => Assert.Equal(0m, EvaluateCalculator.AverageScore([]));

    [Fact]
    public void DisplayScore_无评价显示5星而不是0()
    {
        // 规格 14.4：0 分会被用户理解成「很差」，而「还没人评价」是中性的
        Assert.Equal(5.0m, EvaluateCalculator.DisplayScore(0m));
    }

    [Fact]
    public void DisplayScore_有评价显示真实均分()
        => Assert.Equal(3.67m, EvaluateCalculator.DisplayScore(3.67m));

    [Fact]
    public void MerchantRating_取有评价商品的均分均值()
    {
        Assert.Equal(4.00m, EvaluateCalculator.MerchantRating([4.00m, 4.00m]));
        Assert.Equal(3.67m, EvaluateCalculator.MerchantRating([4.00m, 3.34m]));
    }

    [Fact]
    public void MerchantRating_无商品_返回0()
        => Assert.Equal(0m, EvaluateCalculator.MerchantRating([]));

    [Fact]
    public void IsAppendWindowOpen_30天内_允许追评()
    {
        var first = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(EvaluateCalculator.IsAppendWindowOpen(first, first.AddDays(29)));
        Assert.True(EvaluateCalculator.IsAppendWindowOpen(first, first.AddDays(30)));
    }

    [Fact]
    public void IsAppendWindowOpen_超过30天_不允许追评()
    {
        var first = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(EvaluateCalculator.IsAppendWindowOpen(first, first.AddDays(30).AddSeconds(1)));
        Assert.False(EvaluateCalculator.IsAppendWindowOpen(first, first.AddDays(31)));
    }

    [Fact]
    public void 常量_与规格一致()
    {
        Assert.Equal(9, EvaluateCalculator.MaxImages);
        Assert.Equal(3, EvaluateCalculator.MaxDisplaySpecs);
        Assert.Equal(3, EvaluateCalculator.MaxAppends);
        Assert.Equal(30, EvaluateCalculator.AppendWindowDays);
    }
}
