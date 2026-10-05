namespace Collaboration.Domain.Infrastructure;

/// <summary>标记「这张表本身就是租户根」的实体。</summary>
/// <remarks>
/// <para>租户过滤的默认规则是 <c>PlatformId == 我的平台Id</c> —— 回答的是
/// 「这一行归哪个平台所有」。但 <c>platform</c> 表是个例外：**这一行就是平台本身**，
/// 它的 <c>PlatformId</c> 恒为 0（平台不隶属于另一个平台）。</para>
///
/// <para>不特殊处理的话，平台账号按默认规则查自己的平台会得到
/// <c>PlatformId(0) == 我的平台Id</c> → 假 → <b>一条都查不到</b>，
/// 连自己的信息都看不见、更谈不上编辑。</para>
///
/// <para>用标记接口而不是「在过滤器里写 if (type.Name == "Platform")」：
/// 名字判据会在重命名或新增同类表时静默失效，而标记接口漏标会在编译/评审时被看见。</para>
/// </remarks>
public interface ITenantRoot
{
}
