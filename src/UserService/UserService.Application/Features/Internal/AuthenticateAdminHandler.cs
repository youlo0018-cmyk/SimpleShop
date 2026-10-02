using Collaboration.Domain.Common;
using Collaboration.Domain.Security;
using MediatR;
using UserService.Domain.IRepository;

namespace UserService.Application.Features.Internal;

/// <summary>后台账号凭据校验处理器。</summary>
public sealed class AuthenticateAdminHandler
    : IRequestHandler<AuthenticateAdminCommand, ApiResponse<AdminIdentity>>
{
    /// <summary>登录失败时统一使用的消息。</summary>
    /// <remarks>
    /// 「账号不存在」与「密码错误」必须返回同一句话。若分开返回，
    /// 攻击者就能用登录名枚举批量探测哪些账号存在，这是真实的信息泄露。
    /// </remarks>
    private const string InvalidCredentialsMessage = "登录名或密码错误";

    /// <summary>
    /// 「账号不存在」分支用的哑元哈希，用于拉平耗时。
    /// </summary>
    /// <remarks>
    /// PBKDF2 21 万次迭代在正常机器上约几十毫秒。账号不存在时直接返回，
    /// 比密码错误快一个数量级，攻击者能用响应时间差异判断某个登录名是否存在。
    /// 拿一个固定哈希跑一遍同样的计算把耗时拉平。该哈希不对应任何真实密码。
    /// </remarks>
    private const string DummyHash =
        "pbkdf2$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly IUserRepository _users;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    public AuthenticateAdminHandler(IUserRepository users) => _users = users;

    /// <summary>执行凭据校验。</summary>
    /// <param name="request">校验命令，形状已由校验器保证。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回账号身份；失败返回 400，且不区分「账号不存在」与「密码错误」。</returns>
    public async Task<ApiResponse<AdminIdentity>> Handle(AuthenticateAdminCommand request, CancellationToken ct)
    {
        var user = await _users.GetByNameAsync(request.UserName.Trim(), ct);

        if (user is null)
        {
            PasswordHasher.Verify(request.Password, DummyHash);
            return ApiResults.Fail<AdminIdentity>(BaseApiResponseCode.BadRequest, InvalidCredentialsMessage);
        }

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return ApiResults.Fail<AdminIdentity>(BaseApiResponseCode.BadRequest, InvalidCredentialsMessage);
        }

        // 密码正确后才提示停用，否则「已停用」会成为账号存在性的旁路
        if (user.Status != 1)
        {
            return ApiResults.Fail<AdminIdentity>(BaseApiResponseCode.Forbidden, "账号已被停用，请联系管理员");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user, ct);

        var identity = new AdminIdentity(
            user.Id, user.UserName, user.NickName, user.Avatar,
            user.TenantType, user.PlatformId, user.MerchantId);

        return ApiResults.Ok(identity, "校验通过");
    }
}