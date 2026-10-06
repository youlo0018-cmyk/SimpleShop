namespace UserService.Application.Services;

/// <summary>后台账号的会话吊销器：把某账号已签发的令牌全部作废。</summary>
/// <remarks>
/// 抽成接口是为了让 Handler 不必知道吊销是走 Redis 还是别的存储，
/// 也为了单测能塞一个不连 Redis 的替身。机制见 <see cref="Collaboration.Domain.Security.AdminSessionRevocation"/>。
/// </remarks>
public interface IAdminSessionRevoker
{
    /// <summary>吊销某账号当前全部已签发的后台令牌。</summary>
    /// <param name="userId">后台账号 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入完成的任务。</returns>
    Task RevokeAsync(long userId, CancellationToken ct = default);
}
