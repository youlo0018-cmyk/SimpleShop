using Collaboration.Domain.Common;
using MediatR;
using MerchantPlatformService.Domain.Entities;
using MerchantPlatformService.Domain.Exceptions;
using MerchantPlatformService.Domain.IRepository;
using MerchantPlatformService.Domain.Services;
// `Features/Platform` 这个命名空间段会遮蔽同名实体 `Platform`（CODING_STANDARD §6 第 1 条）
using PlatformEntity = MerchantPlatformService.Domain.Entities.Platform;

namespace MerchantPlatformService.Application.Features.Platform;

/// <summary>创建平台处理器。</summary>
public sealed class CreatePlatformHandler
    : IRequestHandler<CreatePlatformCommand, ApiResponse<long>>
{
    private readonly IPlatformRepository _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    public CreatePlatformHandler(IPlatformRepository platforms) => _platforms = platforms;

    /// <summary>执行创建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台 Id。</returns>
    public async Task<ApiResponse<long>> Handle(CreatePlatformCommand request, CancellationToken ct)
    {
        var platform = new PlatformEntity
        {
            PlatformName = request.PlatformName.Trim(),
            PlatformCode = request.PlatformCode.Trim().ToUpperInvariant(),
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            Logo = request.Logo.Trim(),
            MallName = request.MallName.Trim(),
            Notice = request.Notice.Trim(),
            PrimaryColor = request.PrimaryColor.Trim(),
            TabColor = request.TabColor.Trim(),
            BackgroundColor = request.BackgroundColor.Trim(),
            ShippingFee = decimal.Round(request.ShippingFee, 2, MidpointRounding.AwayFromZero),
            FreeShippingThreshold = decimal.Round(request.FreeShippingThreshold, 2, MidpointRounding.AwayFromZero),
            Status = request.Status,
            Remark = request.Remark.Trim()
        };

        try
        {
            var id = await _platforms.InsertAsync(platform, ct);
            return ApiResults.Ok(id, "平台创建成功");
        }
        catch (PlatformCodeTakenException ex)
        {
            // 名称与编码各有唯一索引，要把冲突的字段名回给前端，
            // 否则只能提示「已存在」，用户不知道该改哪个框
            return ApiResults.Fail<long>(
                BaseApiResponseCode.BusinessError, ex.Message,
                new Dictionary<string, string[]> { [ex.FieldName] = [ex.Message] });
        }
    }
}

/// <summary>编辑平台处理器。</summary>
public sealed class UpdatePlatformHandler : IRequestHandler<UpdatePlatformCommand, ApiResponse>
{
    private readonly IPlatformRepository _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    public UpdatePlatformHandler(IPlatformRepository platforms) => _platforms = platforms;

    /// <summary>执行编辑。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(UpdatePlatformCommand request, CancellationToken ct)
    {
        var platform = await _platforms.GetByIdAsync(request.PlatformId, ct);
        if (platform is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "平台不存在");
        }

        // 一律用 T() 而不是 request.X.Trim()：契约里这些字段的**默认值是空串**，
        // 但 JSON 里显式传 null 会覆盖默认值 —— 于是 .Trim() 抛 NullReferenceException，
        // 接口回 500 而不是 400。前端从列表页回填时最容易踩：列表 DTO 少一个字段
        // （黑色幽默：平台列表 DTO 恰恰就没有 logo / notice / remark），
        // 前端读出来就是 undefined，提交时成了 null。
        platform.PlatformName = T(request.PlatformName);
        // 🔴 PlatformCode **不赋值**：小程序用 PLATFORM_CODE 锁死它，改了等于让
        // 已发布的小程序找不到对应平台。后台表单通常把 Code 一起提交回来，
        // 这里静默忽略而不是报错，运营就不会以为自己改坏了什么（规格 5.1「复制编码」）。
        platform.ContactName = T(request.ContactName);
        platform.ContactPhone = T(request.ContactPhone);
        platform.Logo = T(request.Logo);
        platform.MallName = T(request.MallName);
        platform.Notice = T(request.Notice);
        platform.PrimaryColor = T(request.PrimaryColor);
        platform.TabColor = T(request.TabColor);
        platform.BackgroundColor = T(request.BackgroundColor);
        platform.ShippingFee = decimal.Round(request.ShippingFee, 2, MidpointRounding.AwayFromZero);
        platform.FreeShippingThreshold =
            decimal.Round(request.FreeShippingThreshold, 2, MidpointRounding.AwayFromZero);
        platform.Status = request.Status;
        platform.Remark = T(request.Remark);

        try
        {
            var ok = await _platforms.UpdateAsync(platform, ct);
            return ok
                ? ApiResponseFactory.Ok("保存成功")
                : ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "保存失败，请重试");
        }
        catch (PlatformCodeTakenException ex)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BusinessError, ex.Message,
                new Dictionary<string, string[]> { [ex.FieldName] = [ex.Message] });
        }
    }

    /// <summary>把可空字符串安全地 trim 成非空字符串。</summary>
    /// <param name="value">原始值，可能为 null。</param>
    /// <returns>trim 后的字符串；null 返回空串。</returns>
    /// <remarks>
    /// 存在的理由：契约默认值是空串，但 **JSON 里显式传 null 会覆盖默认值**，
    /// 于是 <c>request.X.Trim()</c> 抛 NullReferenceException，接口回 500。
    /// 前端从列表页回填时最容易踩 —— 列表 DTO 少一个字段，前端读出来是 undefined，
    /// 提交时就成了 null。
    /// </remarks>
    private static string T(string? value) => (value ?? string.Empty).Trim();
}

/// <summary>删除平台处理器。</summary>
public sealed class DeletePlatformHandler : IRequestHandler<DeletePlatformCommand, ApiResponse>
{
    private readonly IPlatformRepository _platforms;
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    /// <param name="merchants">商户仓储。</param>
    public DeletePlatformHandler(IPlatformRepository platforms, IMerchantRepository merchants)
    {
        _platforms = platforms;
        _merchants = merchants;
    }

    /// <summary>执行删除。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(DeletePlatformCommand request, CancellationToken ct)
    {
        var platform = await _platforms.GetByIdAsync(request.PlatformId, ct);
        if (platform is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "平台不存在");
        }

        // 有商户就不让删：删掉平台会让这些商户变成「没有平台的孤儿」，
        // 而它们的商品与订单还在。规格 5.1 明确「只能停用」。
        var merchantCount = await _merchants.CountByPlatformAsync(request.PlatformId, ct);
        if (merchantCount > 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BusinessError,
                $"该平台下还有 {merchantCount} 个商户，不能删除，只能停用");
        }

        var ok = await _platforms.SoftDeleteAsync(request.PlatformId, ct);
        return ok
            ? ApiResponseFactory.Ok("删除成功")
            : ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "删除失败，请重试");
    }
}

/// <summary>平台列表处理器。</summary>
public sealed class QueryPlatformsHandler
    : IRequestHandler<QueryPlatformsCommand, ApiResponse<PagedPlatformDtos>>
{
    private readonly IPlatformRepository _platforms;
    private readonly IMerchantRepository _merchants;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    /// <param name="merchants">商户仓储（统计商户数）。</param>
    public QueryPlatformsHandler(IPlatformRepository platforms, IMerchantRepository merchants)
    {
        _platforms = platforms;
        _merchants = merchants;
    }

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>平台分页。</returns>
    public async Task<ApiResponse<PagedPlatformDtos>> Handle(
        QueryPlatformsCommand request, CancellationToken ct)
    {
        var page = await _platforms.PageAsync(
            request.Keyword, request.Status, request.Page, request.PageSize, ct);

        // 商户数一次性批量统计：逐个查的话一页 100 条就是 100 次查询
        var counts = await _merchants.CountGroupByPlatformAsync(
            page.Items.Select(a => a.Id).ToList(), ct);

        var items = page.Items.Select(a => new PlatformListDto(
            a.Id,
            a.PlatformName,
            a.PlatformCode,
            a.MallName,
            a.ContactName,
            a.ContactPhone,
            a.PrimaryColor,
            a.TabColor,
            a.BackgroundColor,
            a.ShippingFee,
            a.FreeShippingThreshold,
            a.Status,
            PlatformStatuses.NameOf(a.Status),
            counts.TryGetValue(a.Id, out var count) ? count : 0L,
            PlatformAssembler.FormatTime(a.CreatedAt),
            a.CreatedByName,
            a.UpdatedAt is null ? string.Empty : PlatformAssembler.FormatTime(a.UpdatedAt.Value),
            a.OperationName)).ToList();

        return ApiResults.Ok(new PagedPlatformDtos(items, page.Total, page.Page, page.PageSize));
    }
}

/// <summary>平台 / 商户通用的展示时间格式化。</summary>
public static class PlatformAssembler
{
    /// <summary>展示时间格式：本地时区，不带时区后缀。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>把 UTC 时间转成展示字符串。</summary>
    /// <param name="utc">UTC 时间。</param>
    /// <returns>展示用字符串。</returns>
    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(TimeFormat);
}

/// <summary>平台下拉框处理器。</summary>
public sealed class QueryPlatformOptionsHandler
    : IRequestHandler<QueryPlatformOptionsQuery, ApiResponse<List<PlatformOptionDto>>>
{
    private readonly IPlatformRepository _platforms;

    /// <summary>构造处理器。</summary>
    /// <param name="platforms">平台仓储。</param>
    public QueryPlatformOptionsHandler(IPlatformRepository platforms) => _platforms = platforms;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全部启用平台的下拉项。</returns>
    /// <remarks>
    /// 只返回<b>启用</b>的平台：停用平台意味着小程序不可见、不可交易，
    /// 让它出现在「新建商户」的下拉里，选了就是个永远运营不了的店铺。
    /// </remarks>
    public Task<ApiResponse<List<PlatformOptionDto>>> Handle(
        QueryPlatformOptionsQuery request, CancellationToken ct)
    {
        var items = _platforms.ListEnabled(ct)
            .Select(a => new PlatformOptionDto(a.Id.ToString(), a.PlatformName, a.PlatformCode))
            .ToList();

        return Task.FromResult(ApiResults.Ok(items));
    }
}
