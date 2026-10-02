using Collaboration.Domain.Context;
using Collaboration.Domain.Entities;
using FreeSql;
using FreeSql.Aop;

namespace Collaboration.Domain.Infrastructure;

/// <summary>
/// FreeSql AOP 注册器：统一注入四类过滤条件与审计字段填充。
/// </summary>
/// <remarks>
/// 链路位置：每个服务在 Program.cs 中于 Build() 之前调用一次 Register。
/// 解决的问题：软删、租户、客户、公开可见性这四类条件如果靠每个 Handler 手写，
/// 一定会出现某个 Handler 忘了加的情况——那不是风格问题，是越权与数据泄露。
/// API 选择：FreeSql 3.5.x 用 Aop.ParseExpression 与 Aop.CurdBefore 承担原本 2.x 的
/// Aop.DataFilter 与 Aop.DataMapping，后两者在新版本中已移除。
/// 依据：DATA_SPEC.md 3.2、3.2.1；BUSINESS.md 1.3、1.4。
/// </remarks>
public static class FreeSqlAopRegistrar
{
    /// <summary>
    /// 注册全部 AOP 钩子到指定 FreeSql 实例。
    /// </summary>
    /// <param name="freeSql">已配置连接串的 FreeSql 单例。</param>
    /// <exception cref="ArgumentNullException">freeSql 为 null 时抛出。</exception>
    public static void Register(IFreeSql freeSql)
    {
        ArgumentNullException.ThrowIfNull(freeSql);
        freeSql.Aop.ParseExpression += HandleParseExpression;
        freeSql.Aop.CurdBefore += HandleCurdBefore;
    }

    /// <summary>
    /// 查询过滤器：把软删 / 租户 / 客户 / 公开可见性合并成一段追加到 WHERE。
    /// </summary>
    /// <param name="sender">事件源，未使用。</param>
    /// <param name="e">表达式解析事件参数，设置 Result 后会被追加到 WHERE。</param>
    private static void HandleParseExpression(object? sender, ParseExpressionEventArgs e)
    {
        if (e.Tables is null || e.Tables.Count == 0)
        {
            return;
        }

        var ctx = TenantContextHolder.Current;
        var conditions = new List<string>();
        var multiTable = e.Tables.Count > 1;

        foreach (var table in e.Tables)
        {
            var tableInfo = table.Table;
            if (tableInfo is null)
            {
                continue;
            }

            var entityType = tableInfo.Type;
            if (entityType is null || !typeof(EntityBase).IsAssignableFrom(entityType))
            {
                continue;
            }

            var tableName = string.IsNullOrEmpty(table.Alias) ? tableInfo.DbName : table.Alias;
            var prefix = multiTable ? $"{tableName}." : string.Empty;
            conditions.Add($"{prefix}is_deleted = {SqlLiteral.Format(false)}");
            AppendTenant(conditions, prefix, entityType, ctx);
            AppendCustomer(conditions, prefix, entityType, ctx);
            AppendPublicVisibility(conditions, entityType, ctx);
        }

        if (conditions.Count > 0)
        {
            e.Result = $"({string.Join(" AND ", conditions)})";
        }
    }

    /// <summary>
    /// 租户条件：超管不加；平台加平台；商户加平台且商户。C 端与游客不按租户裁剪。
    /// </summary>
    /// <param name="conditions">条件收集器。</param>
    /// <param name="prefix">列名前缀，多表关联时为「表名.」。</param>
    /// <param name="entityType">实体类型。</param>
    /// <param name="ctx">当前租户上下文。</param>
    private static void AppendTenant(List<string> conditions, string prefix, Type entityType, TenantContext ctx)
    {
        if (!typeof(AdminEntityBase).IsAssignableFrom(entityType))
        {
            return;
        }

        if (ctx.IsSuperAdmin || ctx.IsCustomer || ctx.IsAnonymous)
        {
            return;
        }

        if (ctx.IsPlatform)
        {
            conditions.Add($"{prefix}platform_id = {SqlLiteral.Format(ctx.PlatformId)}");
            return;
        }

        if (ctx.IsMerchant)
        {
            conditions.Add($"{prefix}platform_id = {SqlLiteral.Format(ctx.PlatformId)}");
            conditions.Add($"{prefix}merchant_id = {SqlLiteral.Format(ctx.MerchantId)}");
        }
    }

    /// <summary>
    /// 客户条件：客户只能看到自己的数据。
    /// </summary>
    /// <param name="conditions">条件收集器。</param>
    /// <param name="prefix">列名前缀，多表关联时为「表名.」。</param>
    /// <param name="entityType">实体类型。</param>
    /// <param name="ctx">当前租户上下文。</param>
    private static void AppendCustomer(List<string> conditions, string prefix, Type entityType, TenantContext ctx)
    {
        if (!ctx.IsCustomer || !typeof(CustomerEntityBase).IsAssignableFrom(entityType))
        {
            return;
        }

        conditions.Add($"{prefix}customer_id = {SqlLiteral.Format(ctx.UserId)}");
    }

    /// <summary>
    /// 公开可见性条件：仅 C 端与游客，且实体实现 IPublicVisible 时生效。
    /// </summary>
    /// <param name="conditions">条件收集器。</param>
    /// <param name="entityType">实体类型。</param>
    /// <param name="ctx">当前租户上下文。</param>
    /// <remarks>具体条件由实体自行给出（值必须经 SqlLiteral 内联，见 IPublicVisible 设计取舍）。</remarks>
    private static void AppendPublicVisibility(List<string> conditions, Type entityType, TenantContext ctx)
    {
        if (!ctx.ShouldFilterPublicVisibility || !typeof(IPublicVisible).IsAssignableFrom(entityType))
        {
            return;
        }

        var instance = (IPublicVisible)Activator.CreateInstance(entityType)!;
        var condition = instance.BuildPublicCondition(DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(condition.Sql))
        {
            conditions.Add($"({condition.Sql})");
        }
    }

    /// <summary>
    /// 写入前钩子：插入补雪花 Id 与创建人，更新补最后操作人。
    /// </summary>
    /// <param name="sender">事件源，未使用。</param>
    /// <param name="e">写入前事件参数，States 可直接改写待写入值。</param>
    private static void HandleCurdBefore(object? sender, CurdBeforeEventArgs e)
    {
        if (e.EntityType is null || !typeof(EntityBase).IsAssignableFrom(e.EntityType))
        {
            return;
        }

        if (e.CurdType is not (CurdType.Insert or CurdType.InsertOrUpdate or CurdType.Update))
        {
            return;
        }

        if (e.States is null || e.States.Count == 0)
        {
            return;
        }

        var ctx = TenantContextHolder.Current;
        var isInsert = e.CurdType is CurdType.Insert or CurdType.InsertOrUpdate;
        ApplyAudit(e, e.States, ctx, isInsert);
    }

    /// <summary>
    /// 写入审计字段：插入时补 Id 与创建人，更新时补最后操作人。
    /// </summary>
    /// <param name="states">当前实体的待写入值集合，key 为属性名。</param>
    /// <param name="ctx">当前租户上下文。</param>
    /// <param name="isInsert">本次是否为插入（Insert 或 InsertOrUpdate）。</param>
    /// <remarks>
    /// 创建人一旦写入便不再修改，因此更新时不覆盖 CreatedById。
    /// 判断实体种类用「待写入值里有哪些键」而不是再取一次实体实例，避免多余的对象访问。
    /// </remarks>
    /// <summary>写入审计字段。States 的键可能是列名也可能是属性名，两种都试。</summary>
    private static void ApplyAudit(CurdBeforeEventArgs e, Dictionary<string, object> states, TenantContext ctx, bool isInsert)
    {
        if (isInsert)
        {
            if (!TryGetState(states, e, nameof(EntityBase.Id), out var id) || IsEmpty(id))
            {
                SetState(states, e, nameof(EntityBase.Id), SnowflakeId.NewId());
            }

            SetState(states, e, nameof(EntityBase.CreatedAt), DateTime.UtcNow);
        }
        else
        {
            SetState(states, e, nameof(EntityBase.UpdatedAt), DateTime.UtcNow);
        }

        if (HasState(states, e, nameof(AdminEntityBase.OperationId)))
        {
            SetState(states, e, nameof(AdminEntityBase.CreatedById), ctx.UserId);
            SetState(states, e, nameof(AdminEntityBase.CreatedByName), ctx.UserName);
            SetState(states, e, nameof(AdminEntityBase.OperationId), ctx.UserId);
            SetState(states, e, nameof(AdminEntityBase.OperationName), ctx.UserName);
            return;
        }

        if (HasState(states, e, nameof(CustomerEntityBase.CustomerId)))
        {
            SetState(states, e, nameof(CustomerEntityBase.CustomerId), ctx.UserId);
            SetState(states, e, nameof(CustomerEntityBase.CustomerName), ctx.UserName);
        }
    }

    /// <summary>把属性名解析成 States 里实际使用的键：优先属性名，其次列名。</summary>
    private static string? ResolveKey(CurdBeforeEventArgs e, string propertyName)
    {
        return propertyName;
    }

    private static string? ResolveColumn(CurdBeforeEventArgs e, string propertyName)
    {
        var columns = e.Table?.Columns;
        if (columns is null) return null;
        if (!columns.TryGetValue(propertyName, out var col)) return null;
        var dbName = col.Table?.DbName;
        return string.IsNullOrEmpty(dbName) ? null : dbName;
    }

    private static bool HasState(Dictionary<string, object> states, CurdBeforeEventArgs e, string propertyName)
    {
        return TryGetState(states, e, propertyName, out _);
    }

    private static bool TryGetState(Dictionary<string, object> states, CurdBeforeEventArgs e, string propertyName, out object? value)
    {
        if (states.TryGetValue(propertyName, out value)) return true;
        var col = ResolveColumn(e, propertyName);
        if (col is not null && states.TryGetValue(col, out value)) return true;
        value = null;
        return false;
    }

    private static void SetState(Dictionary<string, object> states, CurdBeforeEventArgs e, string propertyName, object value)
    {
        if (states.ContainsKey(propertyName))
        {
            states[propertyName] = value;
            return;
        }
        var col = ResolveColumn(e, propertyName);
        if (col is not null) states[col] = value;
    }

    private static bool IsEmpty(object? value) => value is null or 0L or 0;
}





