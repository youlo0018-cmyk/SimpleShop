namespace Collaboration.Domain.Context;

/// <summary>
/// 租户上下文的异步持有器。
/// </summary>
/// <remarks>
/// 存在的理由：FreeSql 的 IFreeSql 实例通常是单例，而 TenantContext 是每请求一个（Scoped）。
/// AOP 回调在单例的 IFreeSql 上执行，拿不到作用域注入的实例。
/// 用 AsyncLocal 承载后，AOP 回调可在同一异步流内读到当前请求的上下文，天然避免跨请求串号。
/// 链路位置：Api 层在请求入口调用 Set，下游 AOP 用 Current 读取。
/// </remarks>
public static class TenantContextHolder
{
    private static readonly AsyncLocal<TenantContext?> Storage = new();

    /// <summary>
    /// 当前请求的租户上下文。
    /// </summary>
    /// <remarks>未设置时返回匿名上下文（Access = Anonymous），保证 AOP 在任何时候都有确定行为。</remarks>
    public static TenantContext Current => Storage.Value ?? Anonymous();

    /// <summary>
    /// 是否已设置上下文。
    /// </summary>
    /// <remarks>后台任务、定时任务没有请求上下文，此值为 false。</remarks>
    public static bool HasContext => Storage.Value is not null;

    /// <summary>
    /// 设置当前请求的租户上下文。
    /// </summary>
    /// <param name="context">上下文实例，不可为 null。</param>
    public static void Set(TenantContext context)
        => Storage.Value = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// 清空当前上下文。
    /// </summary>
    /// <remarks>请求结束时必须调用，防止线程池复用导致上下文串号。</remarks>
    public static void Clear() => Storage.Value = null;

    private static TenantContext Anonymous() => new() { Access = AccessContext.Anonymous };
}

