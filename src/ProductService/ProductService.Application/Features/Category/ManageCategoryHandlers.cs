using Collaboration.Domain.Common;
using MediatR;
using ProductService.Domain.IRepository;
using CategoryEntity = ProductService.Domain.Entities.Category;

namespace ProductService.Application.Features.Category;

/// <summary>新建分类处理器。</summary>
public sealed class CreateCategoryHandler : IRequestHandler<CreateCategoryCommand, ApiResponse<long>>
{
    private readonly ICategoryRepository _categories;

    /// <summary>构造处理器。</summary>
    /// <param name="categories">分类仓储。</param>
    public CreateCategoryHandler(ICategoryRepository categories) => _categories = categories;

    /// <summary>执行新建。</summary>
    /// <param name="request">新建命令，格式已由校验器校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新分类 Id。</returns>
    /// <remarks>
    /// Level **不取前端传值**，而是按 ParentId 链算出来：
    /// 一级固定 1，其余是父级 +1。父级不存在、或父级已经是第 3 级，都直接拒绝——
    /// 前端传 level=1 建到三级下面是最典型的越界入口。
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(CreateCategoryCommand request, CancellationToken ct)
    {
        var name = request.CategoryName.Trim();

        if (await _categories.ExistsByNameAsync(request.ParentId, name, 0, ct))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "同级下已存在同名分类");
        }

        var level = 1;
        if (request.ParentId > 0)
        {
            var parent = await _categories.GetByIdAsync(request.ParentId, ct);
            if (parent is null)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "上级分类不存在");
            }

            if (parent.Level >= CategoryLevels.Max)
            {
                return ApiResults.Fail<long>(
                    BaseApiResponseCode.BadRequest,
                    $"分类最多 {CategoryLevels.Max} 级，不能在第 {parent.Level} 级下再建子分类");
            }

            level = parent.Level + 1;
        }

        var category = new CategoryEntity
        {
            ParentId = request.ParentId,
            CategoryName = name,
            CategoryCode = request.CategoryCode?.Trim() ?? string.Empty,
            Icon = request.Icon?.Trim() ?? string.Empty,
            Image = request.Image?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder,
            Level = level,
            Status = request.Status,
            PlatformId = request.PlatformId
        };

        var id = await _categories.InsertAsync(category, ct);
        return ApiResults.Ok(id, "创建成功");
    }
}

/// <summary>编辑分类处理器。</summary>
public sealed class UpdateCategoryHandler : IRequestHandler<UpdateCategoryCommand, ApiResponse>
{
    private readonly ICategoryRepository _categories;

    /// <summary>构造处理器。</summary>
    /// <param name="categories">分类仓储。</param>
    public UpdateCategoryHandler(ICategoryRepository categories) => _categories = categories;

    /// <summary>执行编辑。</summary>
    /// <param name="request">编辑命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(UpdateCategoryCommand request, CancellationToken ct)
    {
        var category = await _categories.GetByIdAsync(request.CategoryId, ct);
        if (category is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "分类不存在");
        }

        var name = request.CategoryName.Trim();
        if (await _categories.ExistsByNameAsync(category.ParentId, name, category.Id, ct))
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "同级下已存在同名分类");
        }

        category.CategoryName = name;
        category.CategoryCode = request.CategoryCode?.Trim() ?? string.Empty;
        category.Icon = request.Icon?.Trim() ?? string.Empty;
        category.Image = request.Image?.Trim() ?? string.Empty;
        category.SortOrder = request.SortOrder;
        category.Status = request.Status;

        await _categories.UpdateAsync(category, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>删除分类处理器。</summary>
public sealed class DeleteCategoryHandler : IRequestHandler<DeleteCategoryCommand, ApiResponse>
{
    private readonly ICategoryRepository _categories;

    /// <summary>构造处理器。</summary>
    /// <param name="categories">分类仓储。</param>
    public DeleteCategoryHandler(ICategoryRepository categories) => _categories = categories;

    /// <summary>执行删除。</summary>
    /// <param name="request">删除命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；有子分类或商品时返回 400。</returns>
    /// <remarks>
    /// 有子分类或有商品时**只允许停用，不允许删除**（DATA_SPEC 5.4）。
    /// 直接软删会让这些商品变成「挂在一个不存在的分类下」，前台取不到分类名，
    /// 而后台也已经改不回去了——这种数据没法修复。
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteCategoryCommand request, CancellationToken ct)
    {
        var category = await _categories.GetByIdAsync(request.CategoryId, ct);
        if (category is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "分类不存在");
        }

        var childCount = await _categories.CountChildrenAsync(category.Id, ct);
        if (childCount > 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, $"该分类下还有 {childCount} 个子分类，请先处理子分类或改为停用");
        }

        var productCount = await _categories.CountProductsAsync(category.Id, ct);
        if (productCount > 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, $"该分类下还有 {productCount} 个商品，请先处理商品或改为停用");
        }

        await _categories.DeleteAsync(category.Id, ct);
        return ApiResponseFactory.Ok();
    }
}

/// <summary>查询分类树处理器。</summary>
public sealed class QueryCategoryTreeHandler : IRequestHandler<QueryCategoryTreeCommand, ApiResponse<List<CategoryNodeDto>>>
{
    private readonly ICategoryRepository _categories;

    /// <summary>构造处理器。</summary>
    /// <param name="categories">分类仓储。</param>
    public QueryCategoryTreeHandler(ICategoryRepository categories) => _categories = categories;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分类树。</returns>
    public async Task<ApiResponse<List<CategoryNodeDto>>> Handle(QueryCategoryTreeCommand request, CancellationToken ct)
    {
        var all = await _categories.ListAllAsync(ct);
        if (!request.IncludeDisabled)
        {
            all = all.Where(a => a.Status == 1).ToList();
        }

        var tree = CategoryTreeBuilder.Build(all);
        return ApiResults.Ok(tree);
    }
}

/// <summary>由扁平分类列表构建树。</summary>
public static class CategoryTreeBuilder
{
    /// <summary>构建分类树。</summary>
    /// <param name="all">扁平分类列表。</param>
    /// <returns>树形列表。</returns>
    /// <remarks>
    /// 一次性把所有节点建好再连父子，避免在循环里反复查库（N+1）。
    /// 父节点被过滤掉的情况（比如父级停用了但子级启用）也处理了：
    /// 子节点会被提升成根节点，而不是整棵丢弃——否则停用一个父分类
    /// 会连带让它下面的分类在前台一起消失。
    /// </remarks>
    public static List<CategoryNodeDto> Build(List<CategoryEntity> all)
    {
        var nodes = all.ToDictionary(a => a.Id, a => new CategoryNodeDto
        {
            Id = a.Id.ToString(),
            ParentId = a.ParentId,
            CategoryName = a.CategoryName,
            CategoryCode = a.CategoryCode,
            Icon = a.Icon,
            Image = a.Image,
            SortOrder = a.SortOrder,
            Level = a.Level,
            Status = a.Status
        });

        var roots = new List<CategoryNodeDto>();

        foreach (var entity in all)
        {
            var node = nodes[entity.Id];

            // 父节点不在集合里（不存在或被过滤）→ 当作根节点
            if (entity.ParentId == 0 || !nodes.TryGetValue(entity.ParentId, out var parent))
            {
                roots.Add(node);
                continue;
            }

            parent.Children.Add(node);
            parent.HasChildren = true;
        }

        return Sort(roots);
    }

    private static List<CategoryNodeDto> Sort(List<CategoryNodeDto> nodes)
    {
        var sorted = nodes.OrderBy(a => a.SortOrder).ThenBy(a => a.CategoryName, StringComparer.Ordinal).ToList();
        foreach (var node in sorted)
        {
            node.Children = Sort(node.Children);
        }

        return sorted;
    }
}