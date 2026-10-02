using Collaboration.Domain.Common;
using Collaboration.Domain.Security;
using MediatR;
using UserService.Application.Services;
using UserService.Domain.Entities;
using UserService.Domain.IRepository;
using UserEntity = UserService.Domain.Entities.User;

namespace UserService.Application.Features.User.ManageUser;

/// <summary>新建后台账号处理器。</summary>
public sealed class CreateUserHandler : IRequestHandler<CreateUserCommand, ApiResponse<long>>
{
    private readonly IUserRepository _users;
    private readonly IUserRoleClient _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="roles">账号-角色绑定客户端。</param>
    public CreateUserHandler(IUserRepository users, IUserRoleClient roles)
    {
        _users = users;
        _roles = roles;
    }

    /// <summary>执行建号。</summary>
    /// <param name="request">建号命令，格式已由 CreateUserValidator 校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新账号 Id；重名重号返回 400。</returns>
    public async Task<ApiResponse<long>> Handle(CreateUserCommand request, CancellationToken ct)
    {
        var name = request.UserName.Trim();
        var phone = request.Phone.Trim();

        if (await _users.ExistsByNameAsync(name, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该登录名已被占用");
        }

        if (await _users.ExistsByPhoneAsync(phone, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该手机号已被占用");
        }

        var user = new UserEntity
        {
            UserName = name,
            // 密码走 Collaboration 的 PasswordHasher（PBKDF2 + 每用户随机盐）
            PasswordHash = PasswordHasher.Hash(request.Password),
            Phone = phone,
            Email = request.Email?.Trim() ?? string.Empty,
            NickName = request.NickName.Trim(),
            TenantType = request.TenantType,
            PlatformId = request.PlatformId,
            MerchantId = request.MerchantId,
            Status = 1
        };

        var id = await _users.InsertAsync(user, ct);

        // 角色绑定在权限中心，账号表不存角色（fail-closed）
        await _roles.ReplaceAsync(id, request.RoleIds ?? Array.Empty<long>(), request.PlatformId, ct);

        return ApiResults.Ok(id, "创建成功");
    }

}

    /// <summary>编辑后台账号基本信息处理器。租户身份不可在这里改。</summary>
public sealed class UpdateUserHandler : IRequestHandler<UpdateUserCommand, ApiResponse>
{
    private readonly IUserRepository _users;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    public UpdateUserHandler(IUserRepository users) => _users = users;

    /// <summary>执行编辑。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(UpdateUserCommand request, CancellationToken ct)
  {
        var user = await _users.GetByIdAsync(request.UserId, ct);
        if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

        var phone = request.Phone.Trim();
   if (await _users.ExistsByPhoneAsync(phone, user.Id, ct))
     {
       return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该手机号已被其他账号占用");
        }

        user.Phone = phone;
        user.NickName = request.NickName.Trim();
        user.Email = request.Email?.Trim() ?? string.Empty;
        user.Avatar = request.Avatar?.Trim() ?? user.Avatar;

        await _users.UpdateAsync(user, ct);
        return ApiResponseFactory.Ok("保存成功");
    }
}

/// <summary>重置密码处理器。</summary>
public sealed class ResetPasswordHandler : IRequestHandler<ResetPasswordCommand, ApiResponse>
{
    private readonly IUserRepository _users;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    public ResetPasswordHandler(IUserRepository users) => _users = users;

    /// <summary>执行重置。</summary>
    /// <param name="request">重置命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(request.UserId, ct);
   if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

      user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        await _users.UpdateAsync(user, ct);
        return ApiResponseFactory.Ok("密码已重置");
    }
}

/// <summary>启用 / 停用账号处理器。</summary>
public sealed class ChangeUserStatusHandler : IRequestHandler<ChangeUserStatusCommand, ApiResponse>
{
    private readonly IUserRepository _users;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    public ChangeUserStatusHandler(IUserRepository users) => _users = users;

    /// <summary>执行状态变更。</summary>
    /// <param name="request">状态变更命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(ChangeUserStatusCommand request, CancellationToken ct)
  {
        var user = await _users.GetByIdAsync(request.UserId, ct);
      if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

  user.Status = request.Status;
        await _users.UpdateAsync(user, ct);
     return ApiResponseFactory.Ok("状态已更新");
 }
}

/// <summary>分页查询后台账号处理器。</summary>
public sealed class QueryUsersHandler : IRequestHandler<QueryUsersCommand, ApiResponse<List<UserListItem>>>
{
    private readonly IUserRepository _users;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    public QueryUsersHandler(IUserRepository users) => _users = users;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号列表。</returns>
    public async Task<ApiResponse<List<UserListItem>>> Handle(QueryUsersCommand request, CancellationToken ct)
    {
        var (items, _) = await _users.QueryPagedAsync(
            request.Page, request.PageSize, request.Keyword,
            request.PlatformId, request.MerchantId, request.Status, ct);

    var result = items.Select(u => new UserListItem(
            u.Id.ToString(), u.UserName, u.NickName, u.Phone, u.Email, u.Avatar,
            u.TenantType == TenantTypes.Merchant ? "商户" : "平台",
            u.PlatformId > 0 ? u.PlatformId.ToString() : "全部平台",
    u.MerchantId > 0 ? u.MerchantId.ToString() : "—",
       u.Status,
     u.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")).ToList();

        return ApiResults.Ok(result);
    }
}
