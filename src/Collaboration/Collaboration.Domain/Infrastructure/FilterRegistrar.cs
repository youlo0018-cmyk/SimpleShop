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
    private static readonly MethodInfo ApplyOnlyMethod = typeof(GlobalFilter)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == "ApplyOnly" && m.IsGenericMethodDefinition);

    /// <summary>为指定程序集里的全部实体注册全局过滤。</summary>
    /// <param name="freeSql">已构建的 FreeSql 实例。</param>
    /// <param name="entityAssemblies">实体所在程序集，通常是本服务的 Xxx.Domain。</param>
    public static void Register(IFreeSql freeSql, params Assembly[] entityAssemblies)
    {
        var ctx = TenantContextHolder.Current;
        var types = new List<Type>();

        foreach (var assembly in entityAssemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract) continue;
                if (!typeof(EntityBase).IsAssignableFrom(type)) continue;
                types.Add(type);
            }
        }

        // 软删是所有实体共有的规则。用 EntityBase 做 ApplyOnly，
        // FreeSql 会按「查询实体的类型是否可从 EntityBase 赋值」自动匹配，
        // 因此不需要为每个实体注册一遍，也不会给 platform 等根表叠加租户条件。
        ApplyOnly(freeSql.GlobalFilter, typeof(EntityBase), "soft_delete", BuildSoftDelete(typeof(EntityBase)));

        // 公开可见性与实体自身类型绑定，名字带类型名，避免不同接口实现互相覆盖。
        if (ctx.ShouldFilterPublicVisibility)
        {
            foreach (var type in types)
            {
                RegisterPublicVisibility(freeSql.GlobalFilter, type);
            }
        }

        RegisterTenantFilters(freeSql.GlobalFilter, types, ctx);
        RegisterCustomerFilter(freeSql.GlobalFilter, ctx);
    }

    /// <summary>注册后台租户过滤。</summary>
    /// <param name="filter">全局过滤器。</param>
    /// <param name="types">本服务的全部实体类型。</param>
    /// <param name="ctx">当前租户上下文。</param>
    /// <remarks>
    /// <para>FreeSql 的 <c>ApplyIf</c> 不是按实体隔离的：它会被加入所有查询，
    /// 再由表达式翻译去碰运气。多个实体注册同名或不同名的过滤器时，
    /// 每个查询都会拿到全部条件，最后在 SQL 里出现重复字段或串表条件。</para>
    ///
    /// <para><c>ApplyOnly&lt;TEntity&gt;</c> 才是按类型生效的原语：
    /// FreeSql 在拼接 SQL 前会检查 <c>Only</c>，只把表达式参数类型可赋值的实体套进去。
    /// 这里为每个非租户根的后台实体单独注册，既避免重复叠加，
    /// 又让 <c>platform</c> 这种租户根表完全不进入租户条件。
    /// 平台根表的可见性由 <c>PlatformRepository</c> 显式限定。</para>
    /// </remarks>
    private static void RegisterTenantFilters(GlobalFilter filter, List<Type> types, TenantContext ctx)
    {
        // Internal 也要跳过：内部调用没有平台/商户声明，套上「PlatformId == 0」
        // 会把所有行都过滤掉，内部接口直接查不到任何数据。
        if (ctx.IsSuperAdmin || ctx.IsCustomer || ctx.IsAnonymous || ctx.IsInternal) return;

        var merchantId = ctx.IsMerchant ? ctx.MerchantId : (long?)null;
        foreach (var type in types)
        {
            if (typeof(ITenantRoot).IsAssignableFrom(type))
            {
                continue;
            }

            // 判定依据是「有没有 PlatformId / MerchantId 这两列」，**不是**继承自哪个基类。
            //
            // 🔴 只认 AdminEntityBase 会漏掉 Order：订单表没有审计列（创建人 / 操作人），
            // 所以 Order 继承 EntityBase，但它照样有 platform_id / merchant_id，
            // 照样必须被租户裁剪。漏掉它的后果实测过 ——
            // 商户 B 的后台账号能按 id 读到**商户 A 的订单详情**（列表是收窄过的，
            // 详情没有），订单号与实付金额一览无余。
            //
            // 用属性名反射而不是基类判断，以后再有实体走 EntityBase + 租户列也能自动覆盖，
            // 不需要有人记得回来改这里。
            if (type.GetProperty(nameof(AdminEntityBase.PlatformId)) is null
                || type.GetProperty(nameof(AdminEntityBase.MerchantId)) is null)
            {
                continue;
            }

            ApplyOnly(filter, type, "tenant:" + type.Name, BuildTenant(type, ctx.PlatformId, merchantId));
        }
    }

    /// <summary>注册客户私有数据过滤。</summary>
    /// <param name="filter">全局过滤器。</param>
    /// <param name="ctx">当前租户上下文。</param>
    /// <remarks>客户令牌只能看自己的数据；该条件绑定到 CustomerEntityBase，不会串到后台实体。</remarks>
    private static void RegisterCustomerFilter(GlobalFilter filter, TenantContext ctx)
    {
        if (!ctx.IsCustomer) return;

        ApplyOnly(
            filter,
            typeof(CustomerEntityBase),
            "customer",
            BuildCustomer(typeof(CustomerEntityBase), ctx.UserId));
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
            ApplyOnly(filter, type, "public_visibility:" + type.Name, lambda);
        }
    }

    private static void ApplyOnly(GlobalFilter filter, Type type, string name, LambdaExpression where)
    {
        var closed = ApplyOnlyMethod.MakeGenericMethod(type);
        closed.Invoke(filter, new object?[] { name, where, true });
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



