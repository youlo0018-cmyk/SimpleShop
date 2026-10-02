namespace Collaboration.Domain.Validation;

/// <summary>
/// 校验规则常量。由 scripts/generate-validation-rules.ps1 从
/// deploy/shared/validation-rules.json 生成，请勿手工修改。
/// </summary>
/// <remarks>单一来源见 CODING_STANDARD.md 3.5。改规则请改 JSON 后重跑生成脚本。</remarks>
public static class ValidationPatterns
{

    /// <summary>phone 规则的正则。</summary>
    public const string phonePattern = "^1[3-9]\\d{9}$";
    /// <summary>phone 规则的提示文案。</summary>
    public const string phoneMessage = "手机号格式不正确";

    /// <summary>email 规则的正则。</summary>
    public const string emailPattern = "^[^\\s@]+@[^\\s@]+\\.[^\\s@]{2,}$";
    /// <summary>email 规则的提示文案。</summary>
    public const string emailMessage = "邮箱格式不正确";

    /// <summary>amount 规则的正则。</summary>
    public const string amountPattern = "^\\d+(\\.\\d{1,2})?$";
    /// <summary>amount 规则的提示文案。</summary>
    public const string amountMessage = "金额最多保留两位小数";
    /// <summary>amount 规则的min 边界值。</summary>
    public const string amountMin = "0.01";

    /// <summary>quantity 规则的提示文案。</summary>
    public const string quantityMessage = "数量需为 1-99 的整数";
    /// <summary>quantity 规则的min 边界值。</summary>
    public const string quantityMin = "1";
    /// <summary>quantity 规则的max 边界值。</summary>
    public const string quantityMax = "99";

    /// <summary>password 规则的提示文案。</summary>
    public const string passwordMessage = "密码至少 8 位且包含字母和数字";
    /// <summary>password 规则的minLength 边界值。</summary>
    public const int passwordMinLength = 8;

    /// <summary>platformCode 规则的正则。</summary>
    public const string platformCodePattern = "^[A-Za-z]{6}$";
    /// <summary>platformCode 规则的提示文案。</summary>
    public const string platformCodeMessage = "平台编码需为 6 位字母";

    /// <summary>discountRate 规则的正则。</summary>
    public const string discountRatePattern = "^\\d+(\\.\\d{1,2})?$";
    /// <summary>discountRate 规则的提示文案。</summary>
    public const string discountRateMessage = "折扣率需为 0.01-10，最多两位小数";
    /// <summary>discountRate 规则的min 边界值。</summary>
    public const string discountRateMin = "0.01";
    /// <summary>discountRate 规则的max 边界值。</summary>
    public const string discountRateMax = "10";

    /// <summary>hexColor 规则的正则。</summary>
    public const string hexColorPattern = "^#[0-9a-fA-F]{6}$";
    /// <summary>hexColor 规则的提示文案。</summary>
    public const string hexColorMessage = "请使用 #RRGGBB 格式的颜色";

    /// <summary>url 规则的提示文案。</summary>
    public const string urlMessage = "链接长度不能超过 512";
    /// <summary>url 规则的maxLength 边界值。</summary>
    public const int urlMaxLength = 512;
}
