namespace UserService.Application.Services;

/// <summary>角色作用域预检结果。</summary>
/// <param name="Ok">是否通过。</param>
/// <param name="Message">不通过时的原因，直接回给调用方。</param>
public readonly record struct RoleScopeCheckResult(bool Ok, string Message);

/// <summary>账号-角色绑定的跨服务契约。角色数据在权限中心（PermissionService），账号表不存角色。</summary>
/// <remarks>
/// 这是权限 fail-closed 的一部分：账号表**不得**有任何角色字段兜底，
/// 权限只认权限中心 user_role 表的显式绑定（BUSINESS 5.3）。
/// </remarks>
public interface IUserRoleClient
{
    /// <summary>建号 / 改号前预检角色集合是否可用于该租户类型。</summary>
    /// <param name="roleIds">目标角色 Id 集合。</param>
    /// <param name="tenantType">目标账号租户类型，1 平台 / 2 商户。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>通过返回 Ok=true；角色不存在或作用域不匹配返回 Ok=false 与原因。</returns>
    /// <remarks>
    /// 必须在**插库之前**调用：建号是「先插账号再绑角色」两步，绑定失败不回滚账号，
    /// 只在这里拦才能避免「接口说创建成功、账号其实没角色」。
    /// </remarks>
    Task<RoleScopeCheckResult> ValidateScopesAsync(
        IReadOnlyCollection<long> roleIds, int tenantType, CancellationToken ct = default);

    /// <summary>重绑账号的角色集合。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="roleIds">目标角色 Id 集合，可为空表示解绑全部。</param>
    /// <param name="platformId">平台 Id，冗余存储用于按平台裁剪。</param>
    /// <param name="tenantType">目标账号租户类型，1 平台 / 2 商户。权限中心据此校验角色 AllowedScopes 是否匹配。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已绑定；false 表示绑定失败，调用方应告警但不因此让建号失败。</returns>
    Task<bool> ReplaceAsync(
        long userId,
        IReadOnlyCollection<long> roleIds,
        long platformId,
        int tenantType,
        CancellationToken ct = default);
}
