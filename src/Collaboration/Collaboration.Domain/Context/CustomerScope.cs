using Collaboration.Domain.Common;

namespace Collaboration.Domain.Context;

/// <summary>
/// C 端客户范围解析。网关客户令牌存在时，以令牌里的客户 Id 为准。
/// </summary>
/// <remarks>
/// 这些接口的参数模型为了兼容服务直连与内部调用保留了 CustomerId，
/// 但经网关进入时必须防 IDOR：客户 A 改请求体里的 CustomerId 不能操作客户 B。
/// 没有客户上下文时仍允许显式传值，供内部任务与端到端测试直连使用。
/// </remarks>
public static class CustomerScope
{
    /// <summary>
    /// 解析当前请求实际可操作的客户 Id。
    /// </summary>
    /// <param name="requestedCustomerId">请求参数中的客户 Id；0 表示未显式指定。</param>
    /// <returns>最终使用的客户 Id。</returns>
    /// <exception cref="BaseApiException">请求客户与令牌客户不一致，或未登录且没有可用的显式客户。</exception>
    public static long Require(long requestedCustomerId)
    {
        var context = TenantContextHolder.Current;
        if (context.IsCustomer)
        {
            if (requestedCustomerId > 0 && requestedCustomerId != context.UserId)
            {
                throw new BaseApiException(BaseApiResponseCode.Forbidden, "不能操作其他客户的数据");
            }

            return context.UserId;
        }

        if (requestedCustomerId <= 0)
        {
            throw new BaseApiException(BaseApiResponseCode.Unauthorized, "请先登录");
        }

        return requestedCustomerId;
    }
}
