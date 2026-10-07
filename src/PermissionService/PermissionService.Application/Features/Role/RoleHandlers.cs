using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.Entities;
using PermissionService.Domain.IRepository;
// Features.Role 命名空间会遮蔽 Role 实体（CS0118），按 CODING_STANDARD 陷阱 1 用别名绕开。
using RoleEntity = PermissionService.Domain.Entities.Role;

namespace PermissionService.Application.Features.Role;

/// <summary>新建角色处理器。</summary>
public sealed class CreateRoleHandler : IRequestHandler<CreateRoleCommand, ApiResponse<long>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public CreateRoleHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行新建。</summary>
    /// <param name="request">新建命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新角色 Id；重名或重码返回 400。</returns>
    public async Task<ApiResponse<long>> Handle(CreateRoleCommand request, CancellationToken ct)
    {
        if (await _roles.ExistsByNameAsync(request.RoleName.Trim(), ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该角色名已存在");
        }

        if (await _roles.ExistsByCodeAsync(request.Code.Trim(), ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该角色编码已存在");
        }

        var role = new RoleEntity
        {
            RoleName = request.RoleName.Trim(),
            Code = request.Code.Trim(),
            AllowedScopes = request.AllowedScopes,
            DataScope = request.DataScope,
            Status = 1,
            IsBuiltin = false,
            Remark = request.Remark ?? string.Empty
        };

        var id = await _roles.InsertAsync(role, ct);
        return ApiResults.Ok(id, "创建成功");
    }
}

/// <summary>编辑角色处理器。内置管理员角色**禁止编辑**（BUSINESS 5.4）。</summary>
public sealed class UpdateRoleHandler : IRequestHandler<UpdateRoleCommand, ApiResponse>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public UpdateRoleHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行编辑。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；内置角色返回 400。</returns>
    public async Task<ApiResponse> Handle(UpdateRoleCommand request, CancellationToken ct)
    {
        var role = await _roles.GetByIdAsync(request.RoleId, ct);
        if (role is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "角色不存在");

        if (role.IsBuiltin)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "内置管理员角色不可修改");
        }

        var name = request.RoleName.Trim();
        if (await _roles.ExistsByNameAsync(name, ct))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "该角色名已存在");
        }

        role.RoleName = name;
        role.AllowedScopes = request.AllowedScopes;
        role.DataScope = request.DataScope;
        role.Remark = request.Remark ?? string.Empty;

        await _roles.UpdateAsync(role, ct);
        return ApiResponseFactory.Ok("保存成功");
    }
}

/// <summary>删除角色处理器。内置角色拒绝删除，并级联清理角色绑定与账号绑定。</summary>
public sealed class DeleteRoleHandler : IRequestHandler<DeleteRoleCommand, ApiResponse>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public DeleteRoleHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行删除。</summary>
    /// <param name="request">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；内置角色返回 400。</returns>
    public async Task<ApiResponse> Handle(DeleteRoleCommand request, CancellationToken ct)
    {
        var role = await _roles.GetByIdAsync(request.RoleId, ct);
        if (role is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "角色不存在");

        if (role.IsBuiltin)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "内置管理员角色不可删除");
        }

        await _roles.DeleteAsync(request.RoleId, ct);

        // 级联清理：角色的权限绑定 + 账号绑定
        await _roles.ReplaceRolePermissionsAsync(request.RoleId, Array.Empty<long>(), ct);
        await _roles.UnbindRoleAsync(request.RoleId, ct);

        return ApiResponseFactory.Ok("删除成功");
    }
}

/// <summary>重绑角色权限处理器。内置管理员角色权限**锁定**，禁止重绑。</summary>
public sealed class BindRolePermissionsHandler : IRequestHandler<BindRolePermissionsCommand, ApiResponse>
{
    private readonly IRoleRepository _roles;
    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    /// <param name="permissions">权限点仓储（校验只绑叶子用）。</param>
    public BindRolePermissionsHandler(IRoleRepository roles, IPermissionRepository permissions)
        => (_roles, _permissions) = (roles, permissions);

    /// <summary>执行重绑。</summary>
    /// <param name="request">重绑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；内置角色或非法权限点 Id 返回 400。</returns>
    /// <remarks>
    /// 🔴 <b>只接受叶子权限点</b>（有 <c>Code</c> 的节点）。业务大类 / 功能模块 / 虚拟根
    /// 「全部权限」都是勾选用的容器，不是权限点本身 —— BUSINESS.md 5.4「半选节点不保存，
    /// 只保存叶子权限点」。不拦的话，前端一旦把容器 Id（含虚拟根 0 和没有权限点的空模块）
    /// 一并提交，role_permission 里就会多出一批查不到 code 的绑定：权限不会多出来，
    /// 但角色详情的「已绑定 N 项」与回显的勾选状态全部失真，而且界面上看不出来。
    /// </remarks>
    public async Task<ApiResponse> Handle(BindRolePermissionsCommand request, CancellationToken ct)
    {
        var role = await _roles.GetByIdAsync(request.RoleId, ct);
        if (role is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "角色不存在");

        if (role.IsBuiltin)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, "内置管理员角色的权限已锁定，不可修改");
        }

        if (request.PermissionIds.Count > 0)
        {
            var all = await _permissions.QueryAllAsync(onlyEnabled: false, ct).ConfigureAwait(false);
            var leafIds = all
                .Where(p => !string.IsNullOrEmpty(p.Code))
                .Select(p => p.Id)
                .ToHashSet();

            var invalid = request.PermissionIds.Where(id => !leafIds.Contains(id)).Distinct().ToArray();
            if (invalid.Length > 0)
            {
                return ApiResponseFactory.Fail(
                    BaseApiResponseCode.BadRequest,
                    $"只能绑定叶子权限点，收到 {invalid.Length} 个容器或不存在的 Id（{string.Join(",", invalid.Take(5))}）");
            }
        }

        await _roles.ReplaceRolePermissionsAsync(request.RoleId, request.PermissionIds, ct);
        return ApiResponseFactory.Ok("权限已保存");
    }
}

/// <summary>分页查询角色处理器。</summary>
public sealed class QueryRolesHandler : IRequestHandler<QueryRolesCommand, ApiResponse<List<RoleListItem>>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public QueryRolesHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色列表，每项带已绑定权限点数。</returns>
    public async Task<ApiResponse<List<RoleListItem>>> Handle(QueryRolesCommand request, CancellationToken ct)
    {
        var (items, _) = await _roles.QueryPagedAsync(request.Page, request.PageSize, request.Keyword, ct);

        var result = new List<RoleListItem>(items.Count);
        foreach (var role in items)
        {
            var ids = await _roles.GetPermissionIdsAsync(role.Id, ct);
            result.Add(new RoleListItem(
                role.Id.ToString(), role.RoleName, role.Code, role.AllowedScopes,
                role.DataScope, role.Status, role.IsBuiltin, ids.Count, role.Remark));
        }

        return ApiResults.Ok(result);
    }
}

/// <summary>查询角色详情与已绑定权限点。</summary>
public sealed class QueryRoleDetailHandler : IRequestHandler<QueryRoleDetailCommand, ApiResponse<RoleDetailDto>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public QueryRoleDetailHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>角色详情；不存在返回 404。</returns>
    public async Task<ApiResponse<RoleDetailDto>> Handle(
        QueryRoleDetailCommand request, CancellationToken ct)
    {
        var role = await _roles.GetByIdAsync(request.RoleId, ct);
        if (role is null)
        {
            return ApiResults.Fail<RoleDetailDto>(BaseApiResponseCode.NotFound, "角色不存在");
        }

        var permissionIds = await _roles.GetPermissionIdsAsync(request.RoleId, ct);
        return ApiResults.Ok(new RoleDetailDto(
            role.Id.ToString(),
            role.RoleName,
            role.Code,
            role.AllowedScopes,
            role.DataScope,
            role.Status,
            role.IsBuiltin,
            role.Remark,
            permissionIds.Select(id => id.ToString()).ToList()));
    }
}
