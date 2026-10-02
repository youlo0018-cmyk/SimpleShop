using System.Linq.Expressions;

namespace Collaboration.Domain.Entities;

/// <summary>公开可见性标记：实现本接口的实体，在 C 端与游客上下文中会被自动追加可见性条件。</summary>
/// <typeparam name="TSelf">实现该接口的实体自身类型。</typeparam>
/// <remarks>
/// 背景：租户过滤解决「谁能看到谁的数据」，可见性过滤解决「哪些数据允许对外」。
/// 两者是并列的独立维度——漏掉后者会把未审核商户、下架商品直接暴露给顾客（BUSINESS.md 1.4）。
/// 启用范围：仅 Customer / Anonymous 上下文；Admin 上下文不注入，因为运营必须能看到待审核与下架数据。
/// 实现示例：Merchant 返回 x =&gt; x.AuditStatus == 20 &amp;&amp; x.Status == 1；
/// 活动返回 x =&gt; x.Status == 1 &amp;&amp; now &gt;= x.StartTime &amp;&amp; now &lt;= x.EndTime。
/// </remarks>
public interface IPublicVisible<TSelf> where TSelf : class
{
    /// <summary>构造该实体的公开可见条件。</summary>
    /// <param name="now">当前时间 UTC。用于按时间窗判断的活动、券、秒杀场次等。</param>
    /// <returns>可见条件表达式；返回 null 表示该实体当前不做可见性约束。</returns>
    Expression<Func<TSelf, bool>>? BuildPublicCondition(DateTime now);
}

