namespace MerchantPlatformService.Domain.Exceptions;

/// <summary>平台名称或编码已被占用。</summary>
/// <remarks>
/// 抛异常而不是返回失败码，是因为它本质是**唯一索引冲突**：
/// 仓储靠 <c>PostgresErrors</c> 沿异常链识别（FreeSql 会把驱动异常包一层，
/// 直接按类型 catch 会漏掉），再翻译成带字段名的校验错误返回给前端。
/// </remarks>
public sealed class PlatformCodeTakenException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="fieldName">冲突的字段名（platformName / platformCode）。</param>
    public PlatformCodeTakenException(string fieldName)
        : base(fieldName == "platformCode" ? "平台编码已被占用" : "平台名称已存在")
    {
        FieldName = fieldName;
    }

    /// <summary>冲突的字段名。</summary>
    public string FieldName { get; }
}

/// <summary>同平台内商户名已被占用。</summary>
public sealed class MerchantNameTakenException : Exception
{
    /// <summary>构造异常。</summary>
    public MerchantNameTakenException()
        : base("该平台下已存在同名商户")
    {
    }
}
