using Collaboration.Domain.Common;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MediatR;

namespace MarketingService.Application.Features.Coupon;

/// <summary>分页查询券模板处理器。</summary>
public sealed class QueryCouponTemplatesHandler
    : IRequestHandler<QueryCouponTemplatesCommand, ApiResponse<PagedResult<CouponTemplateItem>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryCouponTemplatesHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    /// <remarks>
    /// 平台范围用命令里的 <c>PlatformId</c>（0 = 不限），**不从令牌取**：
    /// 后台超管要能跨平台查看，范围收窄由网关的租户上下文在更上层决定。
    /// </remarks>
    public async Task<ApiResponse<PagedResult<CouponTemplateItem>>> Handle(
        QueryCouponTemplatesCommand request, CancellationToken ct)
    {
        var page = await _coupons.PageTemplatesAsync(
            request.Page, request.PageSize, request.Keyword,
            request.CouponType, request.Status, request.PlatformId, ct).ConfigureAwait(false);

        var items = page.Items.Select(a => new CouponTemplateItem(
            a.Id, a.TemplateName, a.CouponType, CouponTypes.NameOf(a.CouponType),
            a.ThresholdAmount, a.DiscountAmount, a.DiscountRate,
            a.GiftTemplateId, a.ValidDays, a.TotalQuantity, a.IssuedQuantity,
            a.PerUserLimit, a.PerOrderLimit, a.SortOrder,
            a.Status, EnableStatuses.NameOf(a.Status), a.PlatformId)).ToList();

        return ApiResults.Ok(new PagedResult<CouponTemplateItem>(items, page.Total, request.Page, request.PageSize));
    }
}

/// <summary>编辑券模板处理器。</summary>
public sealed class UpdateCouponTemplateHandler
    : IRequestHandler<UpdateCouponTemplateCommand, ApiResponse>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public UpdateCouponTemplateHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    /// <remarks>
    /// <b>允许把发行量调小，但不校验「不小于已发放数」是刻意的取舍</b>：
    /// 已发放的券有快照，不受影响；而运营确实需要「这个券别再发了」的能力
    /// （把 TotalQuantity 改成当前已发放数即可停发）。所以这里**不拦**，
    /// 而是把 <c>IssuedQuantity</c> 从更新列里排掉——不让后台改它才是关键。
    ///
    /// <para>满赠券要校验赠送的模板存在且不是自己（自己送自己是死循环）。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdateCouponTemplateCommand request, CancellationToken ct)
    {
        var template = await _coupons.GetTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        if (template is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券模板不存在");
        }

        if (request.CouponType == CouponTypes.Gift)
        {
            if (request.GiftTemplateId == template.Id)
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, "赠送的券模板不能是自己");
            }

            var gift = await _coupons.GetTemplateAsync(request.GiftTemplateId, ct).ConfigureAwait(false);
            if (gift is null)
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "赠送的券模板不存在");
            }
        }

        template.TemplateName = request.TemplateName.Trim();
        template.CouponType = request.CouponType;
        template.ThresholdAmount = request.ThresholdAmount;
        template.DiscountAmount = request.DiscountAmount;
        template.DiscountRate = request.DiscountRate;
        template.GiftTemplateId = request.GiftTemplateId;
        template.ValidDays = request.ValidDays;
        template.TotalQuantity = request.TotalQuantity;
        template.PerUserLimit = request.PerUserLimit;
        template.PerOrderLimit = request.PerOrderLimit;
        template.SortOrder = request.SortOrder;
        template.Status = request.Status;

        await _coupons.UpdateTemplateAsync(template, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("券模板已更新（不影响已发出的券）");
    }
}

/// <summary>删除券模板处理器。</summary>
public sealed class DeleteCouponTemplateHandler
    : IRequestHandler<DeleteCouponTemplateCommand, ApiResponse>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public DeleteCouponTemplateHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    /// <remarks>
    /// <b>不校验「是否已发放」</b>：已发出的券有完整快照，模板删掉不影响它们计算与展示。
    /// 如果这里改成「有发放记录就禁止删除」，运营会遇到一个无法解决的困境——
    /// 想清理一个滥发的券模板，却因为「它已经发出去了」而永远删不掉。
    /// 正确的停发手段是把状态改成「停用」。
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteCouponTemplateCommand request, CancellationToken ct)
    {
        var template = await _coupons.GetTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        if (template is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券模板不存在");
        }

        await _coupons.DeleteTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("券模板已删除");
    }
}

/// <summary>分页查询券活动处理器。</summary>
public sealed class QueryCouponActivitiesHandler
    : IRequestHandler<QueryCouponActivitiesCommand, ApiResponse<PagedResult<CouponActivityItem>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryCouponActivitiesHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    /// <remarks>
    /// <b>一次 IN 查询补齐模板名</b>，不在循环里逐行查（那是 N+1，20 行就是 20 次查询）。
    /// 模板名是运营看列表时的第一判断依据（券活动名字常年起得很随意），
    /// 只显示 Id 的列表等于让人去猜。
    /// </remarks>
    public async Task<ApiResponse<PagedResult<CouponActivityItem>>> Handle(
        QueryCouponActivitiesCommand request, CancellationToken ct)
    {
        var page = await _coupons.PageActivitiesAsync(
            request.Page, request.PageSize, request.Keyword,
            request.Status, request.PlatformId, ct).ConfigureAwait(false);

        var templateIds = page.Items
            .Where(a => a.TemplateId > 0)
            .Select(a => a.TemplateId)
            .Distinct()
            .ToArray();

        var names = templateIds.Length == 0
            ? new Dictionary<long, string>()
            : await LoadTemplateNamesAsync(templateIds, ct).ConfigureAwait(false);

        var items = page.Items.Select(a => new CouponActivityItem(
            a.Id, a.ActivityName, a.TemplateId,
            names.TryGetValue(a.TemplateId, out var name) ? name : "（模板已删除）",
            a.ClaimStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ClaimEndTime.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ClaimQuantity, a.ClaimedQuantity,
            a.PerUserLimit, a.TargetType, TargetTypes.NameOf(a.TargetType),
            a.SortOrder, a.Status, EnableStatuses.NameOf(a.Status), a.PlatformId)).ToList();

        return ApiResults.Ok(new PagedResult<CouponActivityItem>(items, page.Total, request.Page, request.PageSize));
    }

    private async Task<Dictionary<long, string>> LoadTemplateNamesAsync(
        IReadOnlyCollection<long> templateIds, CancellationToken ct)
    {
        var rows = await _coupons.ListTemplatesByIdsAsync(templateIds, ct).ConfigureAwait(false);
        return rows.ToDictionary(a => a.Id, a => a.TemplateName);
    }
}

/// <summary>编辑券活动处理器。</summary>
public sealed class UpdateCouponActivityHandler
    : IRequestHandler<UpdateCouponActivityCommand, ApiResponse>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public UpdateCouponActivityHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 <b>发放量不许改到小于已领取数</b>。活动池子是独立的计数
    /// （DATA_SPEC 5.13「ClaimQuantity 不得超过模板剩余可发量」），
    /// 把它调到比已领取还小，报表上就会凭空多出「已领取 - 总量」这么多张不存在的券。
    /// 这是唯一一条后台<b>不能</b>自由修改的字段，所以在这里硬拦。
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdateCouponActivityCommand request, CancellationToken ct)
    {
        var activity = await _coupons.GetActivityAsync(request.ActivityId, ct).ConfigureAwait(false);
        if (activity is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券活动不存在");
        }

        if (request.ClaimQuantity < activity.ClaimedQuantity)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BadRequest,
                $"发放量不能小于已领取数（当前已领取 {activity.ClaimedQuantity} 张）");
        }

        var template = await _coupons.GetTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        if (template is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "关联的券模板不存在");
        }

        activity.ActivityName = request.ActivityName.Trim();
        activity.TemplateId = request.TemplateId;
        activity.ClaimStartTime = ToUtc(request.ClaimStartTime);
        activity.ClaimEndTime = ToUtc(request.ClaimEndTime);
        activity.ClaimQuantity = request.ClaimQuantity;
        activity.PerUserLimit = request.PerUserLimit;
        activity.TargetType = request.TargetType;
        activity.Targets = request.Targets ?? "[]";
        activity.SortOrder = request.SortOrder;
        activity.Status = request.Status;

        await _coupons.UpdateActivityAsync(activity, ct).ConfigureAwait(false);
        return ApiResponseFactory.Ok("券活动已更新");
    }

    /// <summary>把客户端传来的时间归一到 UTC。</summary>
    /// <param name="value">原始时间。</param>
    /// <returns>UTC 时间。</returns>
    /// <remarks>
    /// 踩过的坑：从 JSON 反序列化带偏移量的字符串（<c>...+08:00</c>）会得到
    /// <b>Kind=Local 且时钟值已是本地时间</b>，直接存进 <c>timestamp</c> 列就差一个时区。
    /// 症状是「刚改完活动时间，活动立刻不在领取时间内」，代码看着完全没问题。
    /// </remarks>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>分页查询券核销记录处理器。</summary>
public sealed class QueryCouponRecordsHandler
    : IRequestHandler<QueryCouponRecordsCommand, ApiResponse<PagedResult<CouponRecordItem>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryCouponRecordsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <inheritdoc />
    public async Task<ApiResponse<PagedResult<CouponRecordItem>>> Handle(
        QueryCouponRecordsCommand request, CancellationToken ct)
    {
        var page = await _coupons.PageUserCouponsAsync(
            request.Page, request.PageSize, request.Status,
            request.TemplateId, request.OrderNo, request.Keyword, 0, ct).ConfigureAwait(false);

        var templateIds = page.Items
            .Where(a => a.TemplateId > 0)
            .Select(a => a.TemplateId)
            .Distinct()
            .ToArray();

        var names = templateIds.Length == 0
            ? new Dictionary<long, string>()
            : (await _coupons.ListTemplatesByIdsAsync(templateIds, ct).ConfigureAwait(false))
                .ToDictionary(a => a.Id, a => a.TemplateName);

        var items = page.Items.Select(a => new CouponRecordItem(
            a.Id, a.CouponCode, a.CustomerId, a.TemplateId,
            names.TryGetValue(a.TemplateId, out var name) ? name : "（模板已删除）",
            a.CouponType, CouponTypes.NameOf(a.CouponType),
            a.ThresholdAmount, a.DiscountAmount, a.DiscountRate,
            a.OrderNo ?? string.Empty,
            a.Status, CouponStatuses.NameOf(a.Status),
            a.ReceiveAt.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ExpireAt.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ConsumeAt?.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<CouponRecordItem>(items, page.Total, request.Page, request.PageSize));
    }
}

/// <summary>查询当前客户自己的券包。</summary>
public sealed class QueryMyCouponsHandler
    : IRequestHandler<QueryMyCouponsCommand, ApiResponse<PagedResult<CouponRecordItem>>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public QueryMyCouponsHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前客户自己的券包分页。</returns>
    public async Task<ApiResponse<PagedResult<CouponRecordItem>>> Handle(
        QueryMyCouponsCommand request, CancellationToken ct)
    {
        var ctx = Collaboration.Domain.Context.TenantContextHolder.Current;
        var customerId = ctx.IsCustomer ? ctx.UserId : request.CustomerId;
        if (customerId <= 0)
        {
            return ApiResults.Fail<PagedResult<CouponRecordItem>>(
                BaseApiResponseCode.Unauthorized,
                "请先登录后再查看券包");
        }

        var page = await _coupons.PageUserCouponsAsync(
            request.Page, request.PageSize, request.Status,
            0, string.Empty, string.Empty, customerId, ct).ConfigureAwait(false);

        var templateIds = page.Items
            .Where(a => a.TemplateId > 0)
            .Select(a => a.TemplateId)
            .Distinct()
            .ToArray();

        var names = templateIds.Length == 0
            ? new Dictionary<long, string>()
            : (await _coupons.ListTemplatesByIdsAsync(templateIds, ct).ConfigureAwait(false))
                .ToDictionary(a => a.Id, a => a.TemplateName);

        var items = page.Items.Select(a => new CouponRecordItem(
            a.Id, a.CouponCode, a.CustomerId, a.TemplateId,
            names.TryGetValue(a.TemplateId, out var name) ? name : "（模板已删除）",
            a.CouponType, CouponTypes.NameOf(a.CouponType),
            a.ThresholdAmount, a.DiscountAmount, a.DiscountRate,
            a.OrderNo ?? string.Empty,
            a.Status, CouponStatuses.NameOf(a.Status),
            a.ReceiveAt.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ExpireAt.ToString("yyyy-MM-dd HH:mm:ss"),
            a.ConsumeAt?.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(new PagedResult<CouponRecordItem>(
            items, page.Total, request.Page, request.PageSize));
    }
}
