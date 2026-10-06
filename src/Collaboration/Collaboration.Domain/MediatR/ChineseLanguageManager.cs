using System.Globalization;
using FluentValidation.Resources;

namespace Collaboration.Domain.MediatR;

/// <summary>FluentValidation 默认文案的中文实现。</summary>
/// <remarks>
/// 只覆盖框架内置校验器；业务显式写的 <c>WithMessage</c> 不受影响。
/// 未知键统一回退为「填写内容不正确」，确保任何漏配文案都不会把英文提示直接发给前端。
/// </remarks>
public sealed class ChineseLanguageManager : ILanguageManager
{
    /// <summary>是否启用本地化。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>默认文化，固定为简体中文。</summary>
    public CultureInfo? Culture { get; set; } = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>按键取中文文案。</summary>
    /// <param name="key">FluentValidation 的消息键。</param>
    /// <param name="culture">调用方请求的文化，当前实现忽略它并始终返回中文。</param>
    /// <returns>中文消息模板。</returns>
    public string GetString(string key, CultureInfo culture) => key switch
    {
        "NotEmptyValidator" => "不能为空",
        "NotNullValidator" => "不能为空",
        "NullValidator" => "必须为空",
        "EmptyValidator" => "必须为空",
        "LengthValidator" => "长度必须为 {MinLength} ~ {MaxLength} 个字符",
        "ExactLengthValidator" => "长度必须为 {MaxLength} 个字符",
        "MinimumLengthValidator" => "长度不能少于 {MinLength} 个字符",
        "MaximumLengthValidator" => "长度不能超过 {MaxLength} 个字符",
        "RegularExpressionValidator" => "格式不正确",
        "EmailValidator" => "邮箱格式不正确",
        "PredicateValidator" => "填写内容不正确",
        "AsyncPredicateValidator" => "填写内容不正确",
        "InclusiveBetweenValidator" => "必须在 {From} ~ {To} 之间",
        "ExclusiveBetweenValidator" => "必须大于 {From} 且小于 {To}",
        "GreaterThanOrEqualValidator" => "必须大于或等于 {ComparisonValue}",
        "GreaterThanValidator" => "必须大于 {ComparisonValue}",
        "LessThanOrEqualValidator" => "必须小于或等于 {ComparisonValue}",
        "LessThanValidator" => "必须小于 {ComparisonValue}",
        "EqualValidator" => "必须等于 {ComparisonValue}",
        "NotEqualValidator" => "不能等于 {ComparisonValue}",
        "EnumValidator" => "取值不在允许范围内",
        "CreditCardValidator" => "银行卡号格式不正确",
        "ScalePrecisionValidator" => "小数位数不正确",
        _ => "填写内容不正确"
    };
}
