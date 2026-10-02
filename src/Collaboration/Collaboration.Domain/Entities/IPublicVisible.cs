namespace Collaboration.Domain.Entities;

/// <summary>
/// 公开可见性标记接口：实现本接口的实体，在 C 端与游客上下文中会被 AOP 自动追加可见性条件。
/// </summary>
/// <remarks>
/// 链路位置：FreeSql AOP 的公开可见性过滤器（DATA_SPEC 3.2.1）。
/// 为什么需要：租户过滤解决「谁能看到谁的数据」，可见性过滤解决「哪些数据允许对外」。
/// 两者是并列的独立维度——漏掉后者会把未审核商户、下架商品直接暴露给顾客。
/// 使用约束：仅 C 端（Customer）与游客（Anonymous）上下文注入；后台（Admin）不注入，
/// 因为运营必须能看到待审核商户与下架商品。
/// 设计取舍：这里返回 SQL 片段而不是表达式。FreeSql 3.5.x 的 Aop.ParseExpression
/// 只接受字符串条件、没有参数通道，因此值必须内联。内联的安全性由 SqlLiteral 保证：
/// 它只接受令牌声明、枚举常量与服务端时钟这几类值，遇到其他类型直接抛异常。
/// 实体实现时必须通过 SqlLiteral.Format 生成值，**不要自己拼字符串**。
/// 依据：BUSINESS.md 1.4。
/// </remarks>
public interface IPublicVisible
{
    /// <summary>
    /// 构造该实体的公开可见条件。
    /// </summary>
    /// <param name="now">当前时间，UTC。用于按时间窗判断的活动、券、秒杀场次等。</param>
    /// <returns>可见条件。Sql 为空表示该实体当前不做可见性约束。</returns>
    PublicVisibilityCondition BuildPublicCondition(DateTime now);
}

/// <summary>
/// 公开可见条件的值对象：SQL 片段 + 顺序对应的参数。
/// </summary>
/// <param name="Sql">SQL 片段，如 "audit_status = 20 AND status = 1"。为空表示无约束。</param>
public sealed record PublicVisibilityCondition(string Sql)
{
    /// <summary>无约束的可见条件。</summary>
    public static PublicVisibilityCondition None { get; } = new(string.Empty);
}

