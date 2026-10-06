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
    Anonymous = 2,

    /// <summary>
    /// 服务间内部调用（路径以 <c>/internal</c> 开头）。<b>两个过滤都不注入。</b>
    /// </summary>
    /// <remarks>
    /// <para>内部接口不是「公开页面」，调用方（订单、购物车、营销）要的是**真实状态**，
    /// 再由它们自己决定怎么对外表达。典型例子：订单服务回查 SKU 定价时必须能区分
    /// 「SKU 不存在」与「SKU 已下架」—— 两者给用户的原因不一样，
    /// 而公开可见性过滤会把「已下架」变成「查不到」，原因就丢了。</para>
    ///
    /// <para>必须与 <see cref="Anonymous"/> 分开：网关的匿名白名单路径（逛商品、登录）
    /// 到达下游时同样「没有声明」。两者合并的话，要么内部调用被误加可见性过滤，
    /// 要么真正的游客拿不到该有的过滤 —— 两个方向都会出错。</para>
    /// </remarks>
    Internal = 3
}

