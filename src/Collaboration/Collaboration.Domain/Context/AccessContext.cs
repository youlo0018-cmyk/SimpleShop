namespace Collaboration.Domain.Context;

/// <summary>
/// 访问上下文：区分「谁在访问」，决定 AOP 注入哪些过滤条件。
/// </summary>
/// <remarks>
/// 链路位置：Api 层构造（由网关令牌决定），通过 DI 以 Scoped 生命周期注入下游服务。
/// 为什么需要它：租户过滤与公开可见性过滤的启用范围不同。
/// 后台（Admin）需要看到待审核商户与下架商品，所以不注入可见性过滤；C 端与游客必须注入。
/// 依据：DATA_SPEC.md 3.2.1。
/// </remarks>
public enum AccessContext
{
    /// <summary>后台（运营 / 商户 / 超管）。只注入租户过滤，不注入可见性过滤。</summary>
    Admin = 0,

    /// <summary>C 端已登录客户。注入租户过滤 + 公开可见性过滤。</summary>
    Customer = 1,

    /// <summary>游客（未登录）。注入租户过滤 + 公开可见性过滤。</summary>
    Anonymous = 2
}

