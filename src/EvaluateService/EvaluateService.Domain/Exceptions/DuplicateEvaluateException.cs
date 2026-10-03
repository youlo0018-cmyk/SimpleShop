namespace EvaluateService.Domain.Exceptions;

/// <summary>同一订单已对某个 SPU 发表过首评。</summary>
/// <remarks>
/// 抛这个异常而不是返回失败码，是因为它本质是**幂等命中**而不是业务错误：
/// 调用方（小程序）通常是网络超时后重试，重试拿到「你已评价过」是正确结果。
/// 由 Handler 捕获后转成一条友好提示。
/// </remarks>
public sealed class DuplicateEvaluateException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="spuId">SPU Id。</param>
    public DuplicateEvaluateException(string orderNo, long spuId)
        : base($"订单 {orderNo} 已对商品 {spuId} 发表过评价")
    {
        OrderNo = orderNo;
        SpuId = spuId;
    }

    /// <summary>订单号。</summary>
    public string OrderNo { get; }

    /// <summary>SPU Id。</summary>
    public long SpuId { get; }
}

/// <summary>追评数量已达上限。</summary>
public sealed class AppendLimitReachedException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="limit">上限条数。</param>
    public AppendLimitReachedException(int limit)
        : base($"每条评价最多追评 {limit} 条")
    {
        Limit = limit;
    }

    /// <summary>上限条数。</summary>
    public int Limit { get; }
}

/// <summary>追评已过有效期（首评后 30 天）。</summary>
public sealed class AppendWindowExpiredException : Exception
{
    /// <summary>构造异常。</summary>
    public AppendWindowExpiredException()
        : base("已超过追评有效期，如需补充请重新评价其他商品")
    {
    }
}

/// <summary>该评价已被回复过（同一主体只能回复 1 次）。</summary>
public sealed class DuplicateReplyException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="replyType">回复主体。</param>
    public DuplicateReplyException(int replyType)
        : base($"该评价已回复过了（{Entities.EvaluateReplyTypes.NameOf(replyType)}），回复不可编辑只能追加新评价")
    {
        ReplyType = replyType;
    }

    /// <summary>回复主体。</summary>
    public int ReplyType { get; }
}
