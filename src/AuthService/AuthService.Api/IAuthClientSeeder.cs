using AuthService.Infrastructure;

namespace AuthService.Api;

/// <summary>公开客户端注册接口。</summary>
public interface IAuthClientSeeder
{
    /// <summary>把配置的客户端写入令牌库，幂等。</summary>
    /// <param name="options">认证中心配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task SeedAsync(AuthOptions options, CancellationToken ct = default);
}