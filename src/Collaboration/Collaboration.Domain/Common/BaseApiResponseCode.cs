namespace Collaboration.Domain.Common;

/// <summary>
/// 公共响应码。所有微服务共用，保证前端能按统一规则分支处理。
/// </summary>
/// <remarks>
/// 约定：成功为 0；4xx 为客户端错误；5xx 为服务端错误。业务错误从 4000 起按段分配。
/// </remarks>
public enum BaseApiResponseCode
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>通用参数错误。</summary>
    BadRequest = 400,

    /// <summary>未登录或令牌无效。</summary>
    Unauthorized = 401,

    /// <summary>无权限。</summary>
    Forbidden = 403,

    /// <summary>资源不存在。注意：越权访问他人资源应返回 404 而非 403，避免泄露资源是否存在（TEST_CASES 6.2）。</summary>
    NotFound = 404,

    /// <summary>请求冲突，例如重复提交、唯一键冲突。</summary>
    Conflict = 409,

    /// <summary>服务端错误。</summary>
    InternalError = 500,

    /// <summary>业务规则不满足。消息中必须说明具体原因，不允许只回「操作失败」。</summary>
    BusinessError = 4000,

    /// <summary>库存不足。</summary>
    StockNotEnough = 4001,

    /// <summary>超出可退余额。</summary>
    RefundExceeds = 4002,

    /// <summary>订单状态不允许该操作。</summary>
    OrderStateInvalid = 4003,

    /// <summary>额度不足（券库存、积分余额、限购次数等）。</summary>
    QuotaNotEnough = 4004,

    /// <summary>跨服务调用失败。用于 gRPC 调用超时的归类。</summary>
    RemoteCallFailed = 4005
}

