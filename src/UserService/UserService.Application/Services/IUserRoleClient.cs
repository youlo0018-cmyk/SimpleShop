namespace UserService.Application.Services;

/// <summary>账号-角色绑定的跨服务契约。角色数据在权限中心（PermissionService），账号表不存角色。</summary>
/// <remarks>
/// 这是权限 fail-closed 的一部分：账号表**不得**有任何角色字段兜底，
/// 权限只认权限中心 user_role 表的显式绑定（BUSINESS 5.3）。
/// </remarks>
public interface IUserRoleClient
{
    /// <summary>重绑账号的角色集合。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="roleIds">目标角色 Id 集合，可为空表示解绑全部。</param>
    /// <param name="platformId">平台 Id，冗余存储用于按平台裁剪。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已绑定；false 表示绑定失败，调用方应告警但不因此让建号失败。</returns>
    Task<bool> ReplaceAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default);
}