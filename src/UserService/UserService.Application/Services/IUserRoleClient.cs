namespace UserService.Application.Services;

/// <summary>账号-角色绑定的跨服务契约。角色数据在权限中心（PermissionService），账号表不存角色。</summary>
public interface IUserRoleClient
{
    /// <summary>重绑账号的角色集合。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="roleIds">目标角色 Id 集合，可为空表示解绑全部。</param>
    /// <param name="platformId">平台 Id，冗余存储用于按平台裁剪。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示已绑定；false 表示未绑定（建号不因此失败，需补偿补绑）。</returns>
    Task<bool> ReplaceAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default);
}

/// <summary>S1 阶段的占位实现：权限中心 gRPC 契约尚未建立，此处只打通调用形状。</summary>
/// <remarks>返回 false 而不是 true，是为了让「角色未绑定」在日志里可见；建号本身不因此失败。</remarks>
public sealed class UnavailableUserRoleClient : IUserRoleClient
{
    /// <inheritdoc />
    public Task<bool> ReplaceAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default)
    {
        Console.WriteLine($"[user-role] 权限中心尚未接入，账号 {userId} 的角色未绑定（PLAN.md S1 补接）");
        return Task.FromResult(false);
    }
}