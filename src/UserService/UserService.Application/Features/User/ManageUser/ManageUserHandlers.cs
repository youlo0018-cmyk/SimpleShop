using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Collaboration.Domain.Security;
using MediatR;
using Microsoft.Extensions.Logging;
using UserService.Application.Services;
using UserService.Domain.IRepository;
using UserEntity = UserService.Domain.Entities.User;

namespace UserService.Application.Features.User.ManageUser;

/// <summary>新建后台账号处理器。</summary>
/// <remarks>
/// 两条不变量在这里落地：<b>租户锁定</b>（目标账号必须落在调用方租户范围内）
/// 与<b>角色作用域匹配</b>（角色 AllowedScopes 必须与目标租户类型一致，由权限中心执行）。
/// 前者挡提权，后者挡「把商户角色绑给平台账号」这类跨域配置。
/// </remarks>
public sealed class CreateUserHandler : IRequestHandler<CreateUserCommand, ApiResponse<long>>
{
    private readonly IUserRepository _users;
    private readonly IUserRoleClient _roles;
    private readonly ILogger<CreateUserHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="roles">账号-角色绑定客户端。</param>
    /// <param name="logger">日志器。越权尝试要留痕。</param>
    public CreateUserHandler(IUserRepository users, IUserRoleClient roles, ILogger<CreateUserHandler> logger)
    {
        _users = users;
        _roles = roles;
        _logger = logger;
    }

    /// <summary>执行建号。</summary>
    /// <param name="request">建号命令，格式已由 CreateUserValidator 校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新账号 Id；越权 403；重名重号 400。</returns>
    public async Task<ApiResponse<long>> Handle(CreateUserCommand request, CancellationToken ct)
    {
        var ctx = TenantContextHolder.Current;

        if (!UserTenantScope.CanManage(ctx, request.TenantType, request.PlatformId, request.MerchantId))
        {
            _logger.LogWarning(
                "越权建号被拒：调用方 平台 {CallerPlatformId} / 商户 {CallerMerchantId} / 账号 {CallerUserId}，"
                + "目标 租户类型 {TenantType} / 平台 {PlatformId} / 商户 {MerchantId}",
                ctx.PlatformId, ctx.MerchantId, ctx.UserId,
                request.TenantType, request.PlatformId, request.MerchantId);

            return ApiResults.Fail<long>(BaseApiResponseCode.Forbidden, UserTenantScope.DeniedMessage);
        }

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

        // 插库之前先让权限中心确认角色可用（存在 + AllowedScopes 匹配）。
        // 放在这里而不是等绑定失败：绑定失败不回滚账号，那时请求已经回不了头了。
        var roleCheck = await _roles.ValidateScopesAsync(
            request.RoleIds ?? Array.Empty<long>(), request.TenantType, ct);

        if (!roleCheck.Ok)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, roleCheck.Message);
        }

        var user = new UserEntity
        {
            UserName = name,
            // 密码走 Collaboration 的 PasswordHasher（PBKDF2 + 每用户随机盐）
            PasswordHash = PasswordHasher.Hash(request.Password),
            Phone = phone,
            Email = request.Email?.Trim() ?? string.Empty,
            NickName = request.NickName.Trim(),
            Avatar = request.Avatar?.Trim() ?? string.Empty,
            TenantType = request.TenantType,
            PlatformId = request.PlatformId,
            MerchantId = request.MerchantId,
            Status = request.Status
        };

        var id = await _users.InsertAsync(user, ct);

        // 角色绑定在权限中心，账号表不存角色（fail-closed）。
        // 绑定失败只告警不回滚：没角色的账号是「什么都做不了」的可恢复状态，
        // 而回滚会留下一个建到一半的账号，更难收拾。
        await _roles.ReplaceAsync(
            id, request.RoleIds ?? Array.Empty<long>(), request.PlatformId, request.TenantType, ct);

        return ApiResults.Ok(id, "创建成功");
    }
}

/// <summary>编辑后台账号处理器。租户身份（类型 / 平台 / 商户）不可在这里改。</summary>
public sealed class UpdateUserHandler : IRequestHandler<UpdateUserCommand, ApiResponse>
{
    private readonly IUserRepository _users;
    private readonly IUserRoleClient _roles;
    private readonly ILogger<UpdateUserHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="roles">账号-角色绑定客户端。</param>
    /// <param name="logger">日志器。</param>
    public UpdateUserHandler(IUserRepository users, IUserRoleClient roles, ILogger<UpdateUserHandler> logger)
    {
        _users = users;
        _roles = roles;
        _logger = logger;
    }

    /// <summary>执行编辑。未传的字段保持原值；RoleIds 传了就全量覆盖。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；账号不存在或不在范围内返回 404。</returns>
    public async Task<ApiResponse> Handle(UpdateUserCommand request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(request.UserId, ct);
        if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

        var ctx = TenantContextHolder.Current;
        if (!UserTenantScope.CanManage(ctx, user))
        {
            _logger.LogWarning(
                "越权改号被拒：调用方 平台 {CallerPlatformId} / 商户 {CallerMerchantId} / 账号 {CallerUserId}，"
                + "目标账号 {TargetUserId}",
                ctx.PlatformId, ctx.MerchantId, ctx.UserId, request.UserId);

            // 按 TEST_CASES 6.2：越权访问他人资源回 404，不回 403，避免泄露资源是否存在
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");
        }

        if (request.UserName is not null)
        {
            var name = request.UserName.Trim();
            if (await _users.ExistsByNameAsync(name, user.Id, ct))
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该登录名已被其他账号占用");
            }

            user.UserName = name;
        }

        if (request.Phone is not null)
        {
            var phone = request.Phone.Trim();
            if (await _users.ExistsByPhoneAsync(phone, user.Id, ct))
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该手机号已被其他账号占用");
            }

            user.Phone = phone;
        }

        if (request.NickName is not null) user.NickName = request.NickName.Trim();
        if (request.Email is not null) user.Email = request.Email.Trim();
        if (request.Avatar is not null) user.Avatar = request.Avatar.Trim();
        if (request.Status.HasValue) user.Status = request.Status.Value;

        // 与建号同理：先把角色校验做完再落库，避免「保存成功但绑定没生效」
        if (request.RoleIds is not null)
        {
            var roleCheck = await _roles.ValidateScopesAsync(request.RoleIds, user.TenantType, ct);
            if (!roleCheck.Ok)
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, roleCheck.Message);
            }
        }

        await _users.UpdateAsync(user, ct);

        // RoleIds 传了才动绑定：null = 保持原绑定，非 null = 全量覆盖（校验器已保证至少 1 个）
        if (request.RoleIds is not null)
        {
            await _roles.ReplaceAsync(user.Id, request.RoleIds, user.PlatformId, user.TenantType, ct);
        }

        return ApiResponseFactory.Ok("保存成功");
    }
}

/// <summary>重置密码处理器。后台直接设置新密码，不需要旧密码。</summary>
public sealed class ResetPasswordHandler : IRequestHandler<ResetPasswordCommand, ApiResponse>
{
    private readonly IUserRepository _users;
    private readonly IAdminSessionRevoker _sessions;
    private readonly ILogger<ResetPasswordHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="sessions">会话吊销器，重置密码后把该账号的存量令牌全部作废。</param>
    /// <param name="logger">日志器。</param>
    public ResetPasswordHandler(IUserRepository users, IAdminSessionRevoker sessions, ILogger<ResetPasswordHandler> logger)
    {
        _users = users;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>执行重置。</summary>
    /// <param name="request">重置命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；账号不存在或不在范围内返回 404。</returns>
    public async Task<ApiResponse> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(request.UserId, ct);
        if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

        var ctx = TenantContextHolder.Current;
        if (!UserTenantScope.CanManage(ctx, user))
        {
            _logger.LogWarning(
                "越权重置密码被拒：调用方 平台 {CallerPlatformId} / 商户 {CallerMerchantId} / 账号 {CallerUserId}，"
                + "目标账号 {TargetUserId}",
                ctx.PlatformId, ctx.MerchantId, ctx.UserId, request.UserId);

            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");
        }

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        await _users.UpdateAsync(user, ct);

        // DATA_SPEC 5.20 副作用：把该账号已签发的令牌主动踢下线。
        // 只改哈希是不够的——存量 access token 仍然有效到过期（默认 2 小时），
        // 而「重置密码」在真实场景里往往正是因为怀疑账号被盗。
        await _sessions.RevokeAsync(user.Id, ct);

        return ApiResponseFactory.Ok("密码已重置");
    }
}

/// <summary>启用 / 停用账号处理器。停用只挡新登录，已签发令牌仍有效到过期。</summary>
public sealed class ChangeUserStatusHandler : IRequestHandler<ChangeUserStatusCommand, ApiResponse>
{
    private readonly IUserRepository _users;
    private readonly ILogger<ChangeUserStatusHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="logger">日志器。</param>
    public ChangeUserStatusHandler(IUserRepository users, ILogger<ChangeUserStatusHandler> logger)
    {
        _users = users;
        _logger = logger;
    }

    /// <summary>执行状态变更。</summary>
    /// <param name="request">状态变更命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；账号不存在或不在范围内返回 404。</returns>
    public async Task<ApiResponse> Handle(ChangeUserStatusCommand request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(request.UserId, ct);
        if (user is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");

        var ctx = TenantContextHolder.Current;
        if (!UserTenantScope.CanManage(ctx, user))
        {
            _logger.LogWarning(
                "越权改状态被拒：调用方 平台 {CallerPlatformId} / 商户 {CallerMerchantId} / 账号 {CallerUserId}，"
                + "目标账号 {TargetUserId}",
                ctx.PlatformId, ctx.MerchantId, ctx.UserId, request.UserId);

            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "账号不存在");
        }

        user.Status = request.Status;
        await _users.UpdateAsync(user, ct);
        return ApiResponseFactory.Ok("状态已更新");
    }
}

/// <summary>分页查询后台账号处理器。</summary>
public sealed class QueryUsersHandler : IRequestHandler<QueryUsersCommand, ApiResponse<List<UserListItem>>>
{
    private readonly IUserRepository _users;
    private readonly IPlatformNameClient _names;

    /// <summary>构造处理器。</summary>
    /// <param name="users">账号仓储。</param>
    /// <param name="names">平台 / 商户名称客户端。</param>
    public QueryUsersHandler(IUserRepository users, IPlatformNameClient names)
    {
        _users = users;
        _names = names;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>账号列表；无租户身份返回 403。</returns>
    /// <remarks>
    /// <b>裁剪来自上下文，不来自查询参数</b>。User 实体刻意不继承 AdminEntityBase，
    /// 所以 AOP 不会替它加租户条件；如果这里直接把请求里的 platformId 透给仓储，
    /// 平台账号传 platformId=0 就能把全部平台的账号（含超管）列出来。
    /// 非超管一律用上下文里的平台 / 商户覆盖入参。
    /// </remarks>
    public async Task<ApiResponse<List<UserListItem>>> Handle(QueryUsersCommand request, CancellationToken ct)
    {
        var ctx = TenantContextHolder.Current;

        long platformId;
        long merchantId;

        if (ctx.IsSuperAdmin)
        {
            platformId = request.PlatformId;
            merchantId = request.MerchantId;
        }
        else if (ctx.IsPlatform || ctx.IsMerchant)
        {
            platformId = ctx.PlatformId;
            merchantId = ctx.MerchantId;
        }
        else
        {
            return ApiResults.Fail<List<UserListItem>>(BaseApiResponseCode.Forbidden, "无权查看账号列表");
        }

        var (items, _) = await _users.QueryPagedAsync(
            request.Page, request.PageSize, request.Keyword,
            platformId, merchantId, request.Status, ct);

        // 列表要显示平台名 / 店铺名而不是雪花 Id（DATA_SPEC 4.3、用户要求「显示 name 不显示 id」）。
        // 一次批量取当前页用到的 Id，不做逐行查询；取不到时回落成 Id（降级但信息不丢）。
        var names = await _names.GetNamesAsync(
            items.Where(a => a.PlatformId > 0).Select(a => a.PlatformId).Distinct().ToArray(),
            items.Where(a => a.MerchantId > 0).Select(a => a.MerchantId).Distinct().ToArray(),
            ct);

        var result = items.Select(u => new UserListItem(
            u.Id.ToString(), u.UserName, u.NickName, u.Phone, u.Email, u.Avatar,
            u.TenantType == TenantTypes.Merchant ? "商户" : "平台",
            u.PlatformId > 0
                ? names.Platforms.GetValueOrDefault(u.PlatformId, u.PlatformId.ToString())
                : "全部平台",
            u.MerchantId > 0
                ? names.Merchants.GetValueOrDefault(u.MerchantId, u.MerchantId.ToString())
                : "—",
            u.Status,
            u.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")).ToList();

        return ApiResults.Ok(result);
    }
}
