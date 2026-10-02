using System.Linq.Expressions;
using System.Reflection;
using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using FreeSql.Internal;

namespace Collaboration.Domain.Infrastructure;

/// <summary>查询过滤器注册器：用 FreeSql GlobalFilter 统一注入软删 / 租户 / 客户 / 公开可见性。</summary>
/// <remarks>
/// 为什么不用 Aop.ParseExpression：它的 Result 是**替换**整个 WHERE，不是追加。
/// 实测踩坑：不存在的账号 not_exist_user 能登录成功并返回库里第一条记录，
/// 业务条件 customer_name 被注入的 is_deleted=false 整个顶掉。
/// 那样部署等于租户隔离全线失效、可越权（REVIEW 里的 P0 级风险）。
/// GlobalFilter.ApplyIf 才是 FreeSql 为这类场景设计的原语，它是 AND 进查询的。
/// 依据：DATA_SPEC.md 3.2、3.2.1；BUSINESS.md 1.3、1.4。
/// </remarks>
public static class FilterRegistrar
{
    private static readonly MethodInfo ApplyIfMethod = typeof(GlobalFilter)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == "ApplyIf" && m.IsGenericMethodDefinition);

    /// <summary>为指定程序集里的全部实体注册全局过滤。</summary>
    /// <param name="freeSql">已构建的 FreeSql 实例。</param>
    /// <param name="entityAssemblies">实体所在程序集，通常是本服务的 Xxx.Domain。</param>
    public static void Register(IFreeSql freeSql, params Assembly[] entityAssemblies)
    {
        var ctx = TenantContextHolder.Current;

        foreach (var assembly in entityAssemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract) continue;
                if (!typeof(EntityBase).IsAssignableFrom(type)) continue;
                RegisterFor(freeSql.GlobalFilter, type, ctx);
            }
        }
    }

    private static void RegisterFor(GlobalFilter filter, Type type, TenantContext ctx)
    {
        Apply(filter, type, "soft_delete", BuildSoftDelete(type));

        if (typeof(AdminEntityBase).IsAssignableFrom(type) && !ctx.IsSuperAdmin && !ctx.IsCustomer && !ctx.IsAnonymous)
        {
            long? merchantId = ctx.IsMerchant ? ctx.MerchantId : null;
            Apply(filter, type, "tenant", BuildTenant(type, ctx.PlatformId, merchantId));
        }

        if (typeof(CustomerEntityBase).IsAssignableFrom(type) && ctx.IsCustomer)
        {
            Apply(filter, type, "customer", BuildCustomer(type, ctx.UserId));
        }

        if (ctx.ShouldFilterPublicVisibility) RegisterPublicVisibility(filter, type);
    }

    private static void RegisterPublicVisibility(GlobalFilter filter, Type type)
    {
        var iface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPublicVisible<>));
        if (iface is null) return;

        var instance = Activator.CreateInstance(type);
        if (instance is null) return;

        var method = iface.GetMethod("BuildPublicCondition");
        if (method?.Invoke(instance, new object[] { DateTime.UtcNow }) is LambdaExpression lambda)
        {
            ApplyLambda(filter, type, "public_visibility", lambda);
        }
    }

    private static void Apply(GlobalFilter filter, Type type, string name, LambdaExpression where)
        => ApplyLambda(filter, type, name, where);

    private static void ApplyLambda(GlobalFilter filter, Type type, string name, LambdaExpression where)
    {
        var closed = ApplyIfMethod.MakeGenericMethod(type);
        closed.Invoke(filter, new object?[] { name, null, where, true });
    }

    private static LambdaExpression BuildSoftDelete(Type type)
    {
        var p = Expression.Parameter(type, "x");
        var body = Expression.Not(Expression.Property(p, nameof(EntityBase.IsDeleted)));
        return Expression.Lambda(body, p);
    }

    private static LambdaExpression BuildTenant(Type type, long platformId, long? merchantId)
    {
        var p = Expression.Parameter(type, "x");
        var body = Expression.Equal(
            Expression.Property(p, nameof(AdminEntityBase.PlatformId)),
            Expression.Constant(platformId, typeof(long)));

        if (merchantId.HasValue)
        {
            body = Expression.AndAlso(body, Expression.Equal(
                Expression.Property(p, nameof(AdminEntityBase.MerchantId)),
                Expression.Constant(merchantId.Value, typeof(long))));
        }

        return Expression.Lambda(body, p);
    }

    private static LambdaExpression BuildCustomer(Type type, long customerId)
    {
        var p = Expression.Parameter(type, "x");
        var body = Expression.Equal(
            Expression.Property(p, nameof(CustomerEntityBase.CustomerId)),
            Expression.Constant(customerId, typeof(long)));
        return Expression.Lambda(body, p);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}



