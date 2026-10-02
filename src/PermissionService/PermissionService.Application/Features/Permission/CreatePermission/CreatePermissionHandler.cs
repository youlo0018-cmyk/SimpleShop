using Collaboration.Domain.Common;
using MediatR;
using PermissionService.Domain.IRepository;
using PermissionEntity = PermissionService.Domain.Entities.Permission;

namespace PermissionService.Application.Features.Permission.CreatePermission;

/// <summary>新建权限点处理器：校验唯一性与层级后落库。</summary>
/// <remarks>
/// 层级固定四层（DATA_SPEC 5.22），Level 由父链推导而不是让调用方传，
/// 否则会出现「叶子下面又挂模块」的越界结构。
/// 新建权限点固定 IsBuiltin = false，内置标记不可由外部设置。
/// </remarks>
public sealed class CreatePermissionHandler
    : IRequestHandler<CreatePermissionCommand, ApiResponse<long>>
{
    private readonly IPermissionRepository _permissions;

    /// <summary>构造处理器。</summary>
    /// <param name="permissions">权限点仓储。</param>
    public CreatePermissionHandler(IPermissionRepository permissions) => _permissions = permissions;

    /// <summary>执行新建。</summary>
    /// <param name="request">新建命令，格式已由 CreatePermissionValidator 校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新权限点 Id；冲突返回 400。</returns>
    public async Task<ApiResponse<long>> Handle(CreatePermissionCommand request, CancellationToken ct)
    {
        var name = request.Name.Trim();

        if (await _permissions.ExistsByNameAsync(name, request.ParentId, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "同一父节点下已存在同名权限");
        }

        var code = request.Code?.Trim();
        if (!string.IsNullOrEmpty(code) && await _permissions.ExistsByCodeAsync(code, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "该权限编码已存在");
        }

        var level = await ResolveLevelAsync(request.ParentId, ct);
        if (level < 0) return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "上级权限点不存在");
        if (level > 3) return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "权限树固定四层，不能在权限点下继续新增");

        var entity = new PermissionEntity
        {
            Name = name,
            Code = code ?? string.Empty,
            ApiPath = request.ApiPath?.Trim() ?? string.Empty,
            ParentId = request.ParentId,
            Level = level,
            SortOrder = request.SortOrder,
            Status = 1,
            IsBuiltin = false,
            Description = request.Description ?? string.Empty
        };

        var id = await _permissions.InsertAsync(entity, ct);
        return ApiResults.Ok(id, "创建成功");
    }

    /// <summary>由父链推导新节点的层级。</summary>
    /// <param name="parentId">父节点 Id，0 表示一级。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新节点应处的层级；父节点不存在返回 -1；父节点是叶子返回 4（超上限）。</returns>
    private async Task<int> ResolveLevelAsync(long parentId, CancellationToken ct)
    {
        if (parentId == 0) return 1;

        var parent = await _permissions.GetByIdAsync(parentId, ct);
        if (parent is null) return -1;
        if (parent.Level >= 3) return 4;

        return parent.Level + 1;
    }
}

