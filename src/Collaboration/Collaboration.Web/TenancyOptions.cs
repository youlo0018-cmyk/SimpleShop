namespace Collaboration.Web;

/// <summary>租户上下文相关的配置，对应 AgileConfig 的 Tenancy 节。</summary>
public sealed class TenancyOptions
{
    /// <summary>配置文件节名。</summary>
    public const string SectionName = "Tenancy";

    /// <summary>
    /// 网关与下游服务之间的内部共享口令。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：<b>不能无条件信任 X-Claim-* 请求头</b>。
    /// 网关会从令牌解析出这些头再转发给下游，但服务端口本身也可能被直接访问——
    /// 同机部署、容器网络误配、内网扫描都能绕过网关直接打到 5011/5022。
    /// 那样任何人只要发一个 <c>X-Claim-PlatformId: 0</c> 就是平台超管。
    /// 用共享口令把「这些头确实是网关写的」这件事变成可验证的，而不是默认相信。
    /// 口令不入库（deploy/.env + AgileConfig），也不写死在代码里。
    /// </remarks>
    public string InternalToken { get; set; } = string.Empty;

    /// <summary>网关注入请求头的名称常量。</summary>
    public static class Headers
    {
        /// <summary>内部共享口令。</summary>
        public const string InternalToken = "X-Internal-Token";

        /// <summary>操作人 Id。</summary>
        public const string UserId = "X-Claim-UserId";

        /// <summary>操作人姓名。</summary>
        public const string UserName = "X-Claim-UserName";

        /// <summary>租户类型。1 平台 / 2 商户 / 3 客户。</summary>
        public const string TenantType = "X-Claim-TenantType";

        /// <summary>平台 Id，超管为 0。</summary>
        public const string PlatformId = "X-Claim-PlatformId";

        /// <summary>商户 Id，平台账号为 0。</summary>
        public const string MerchantId = "X-Claim-MerchantId";

        /// <summary>权限点集合，可重复出现或在单值内用逗号分隔。</summary>
        public const string Permissions = "X-Claim-Permissions";

        /// <summary>角色集合，可重复出现或在单值内用逗号分隔。</summary>
        public const string Roles = "X-Claim-Roles";
    }
}