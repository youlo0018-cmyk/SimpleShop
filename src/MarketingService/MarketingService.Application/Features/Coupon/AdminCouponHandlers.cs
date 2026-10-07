using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using MediatR;

namespace MarketingService.Application.Features.Coupon;

/// <summary>券活动时间的归一化。</summary>
/// <remarks>
/// 踩过的坑：从 JSON 反序列化带偏移量的字符串（<c>...+08:00</c>）会得到
/// <b>Kind=Local 且时钟值已是本地时间</b>，直接存进 <c>timestamp</c> 列就差一个时区。
/// 症状是「刚建完 / 改完活动时间，活动立刻不在领取时间内」，代码看着完全没问题。
/// 新建与编辑两条路径共用这一份，避免只修好其中一条。
/// </remarks>
internal static class CouponTimeNormalizer
{
    /// <summary>把客户端传来的时间归一到 UTC。</summary>
    /// <param name="value">原始时间。</param>
    /// <returns>UTC 时间。</returns>
    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>把库里的 UTC 时间转成带 <c>Z</c> 的 ISO 字符串。</summary>
    /// <param name="value">数据库中的 UTC 时间（Kind 可能是 Unspecified）。</param>
    /// <returns>形如 <c>2026-10-07T07:29:00Z</c> 的字符串。</returns>
    /// <remarks>
    /// 券活动的「领取开始 / 结束时间」是**表单要编辑**的字段：不带 <c>Z</c> 时
    /// 前端 `new Date(...)` 会按本地时间解析，界面上显示 UTC 原值，
    /// 保存时再按本地转 UTC —— 每编辑一次就整体偏移一个时区。
    /// </remarks>
    internal static string ToUtcIso(DateTime value) => ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ");
}

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
public sealed class CreateCouponTemplateHandler
    : IRequestHandler<CreateCouponTemplateCommand, ApiResponse<long>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public CreateCouponTemplateHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行新建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新模板 Id。</returns>
    /// <remarks>
    /// <para><b>满赠券要校验赠送的模板存在</b>：只在「大于 0」上放行的话，
    /// 运营填一个不存在的 Id 也能保存，用户付完钱才发现赠品券发不出来。</para>
    ///
    /// <para><c>IssuedQuantity</c> <b>恒从 0 起</b>：它是发放流程累加出来的计数，
    /// 允许请求体带进来的话，报表上的「已发放」就能被随手编造 ——
    /// 编辑路径刻意把它排除在更新列之外，创建路径同样不能收。</para>
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(CreateCouponTemplateCommand request, CancellationToken ct)
    {
        // 归属由服务端按租户身份解析（DATA_SPEC 5.12，与 5.11 / 5.13 同一套规则）：
        // PlatformId / MerchantId 来自请求体，而 AOP 租户过滤只管查询 / 更新 / 删除、**不管插入**。
        if (!Promotion.PromotionActivityScope.TryResolve(
                TenantContextHolder.Current, request.PlatformId, request.MerchantId,
                out var platformId, out var merchantId, out var scopeError))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.Forbidden, scopeError);
        }

        if (request.CouponType == CouponTypes.Gift)
        {
            var gift = await _coupons.GetTemplateAsync(request.GiftTemplateId, ct).ConfigureAwait(false);
            if (gift is null)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "赠送的券模板不存在");
            }

            // 赠送的模板必须与本人同租户：跨租户引用等于把 A 的券挂到 B 的满赠活动上
            if (merchantId > 0 && gift.MerchantId > 0 && gift.MerchantId != merchantId)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "赠送的券模板不属于当前商户");
            }

            if (platformId > 0 && gift.PlatformId > 0 && gift.PlatformId != platformId)
            {
                return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "赠送的券模板不属于当前平台");
            }
        }

        var template = new CouponTemplate
        {
            TemplateName = request.TemplateName.Trim(),
            CouponType = request.CouponType,
            ThresholdAmount = PromotionCalculator.Round2(request.ThresholdAmount),
            DiscountAmount = PromotionCalculator.Round2(request.DiscountAmount),
            DiscountRate = PromotionCalculator.Round2(request.DiscountRate),
            GiftTemplateId = request.CouponType == CouponTypes.Gift ? request.GiftTemplateId : 0,
            ValidDays = request.ValidDays,
            TotalQuantity = request.TotalQuantity,
            IssuedQuantity = 0,
            PerUserLimit = request.PerUserLimit,
            PerOrderLimit = request.PerOrderLimit,
            SortOrder = request.SortOrder,
            Status = request.Status,
            PlatformId = platformId,
            MerchantId = merchantId
        };

        var id = await _coupons.InsertTemplateAsync(template, ct).ConfigureAwait(false);
        return ApiResults.Ok(id, "券模板已创建（不影响已发出的券）");
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
    ///
    /// <para>🔴 <b>但「还有券活动在用」时必须拦住</b>：模板一删，那个活动就成了悬空引用 ——
    /// 领券中心照样把它列出来（模板名退化成「券模板」），用户点「领取」却报
    /// 「券模板不存在或已停用」；更糟的是运营想停用这个活动也改不动，
    /// 因为编辑路径同样要校验模板存在。这与活动侧「有参与记录时禁止删除，只能停用」同一口径。</para>
    /// </remarks>
    public async Task<ApiResponse> Handle(DeleteCouponTemplateCommand request, CancellationToken ct)
    {
        var template = await _coupons.GetTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        if (template is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券模板不存在");
        }

        var referencing = await _coupons.CountActiveActivitiesByTemplateAsync(request.TemplateId, ct)
            .ConfigureAwait(false);
        if (referencing > 0)
        {
            return ApiResponseFactory.Fail(
                BaseApiResponseCode.BadRequest,
                $"还有 {referencing} 个启用中的券活动在用这个模板，请先停用或改绑那些活动；只停发的话把模板状态改成「停用」即可");
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

        var items = page.Items.Select(a =>
        {
            // 模板已被删的活动是「悬空引用」：能停用、不能启用（SetStatus 会拒），
            // 所以要把存在性单独告诉前端，由前端收起那个必报错的「启用」按钮。
            var exists = names.TryGetValue(a.TemplateId, out var name);
            return new CouponActivityItem(
                a.Id, a.ActivityName, a.TemplateId,
                exists ? name! : "（模板已删除）",
                CouponTimeNormalizer.ToUtcIso(a.ClaimStartTime),
                CouponTimeNormalizer.ToUtcIso(a.ClaimEndTime),
                a.ClaimQuantity, a.ClaimedQuantity,
                a.PerUserLimit, a.TargetType, TargetTypes.NameOf(a.TargetType),
                a.SortOrder, a.Status, EnableStatuses.NameOf(a.Status), a.PlatformId, exists);
        }).ToList();

        return ApiResults.Ok(new PagedResult<CouponActivityItem>(items, page.Total, request.Page, request.PageSize));
    }

    private async Task<Dictionary<long, string>> LoadTemplateNamesAsync(
        IReadOnlyCollection<long> templateIds, CancellationToken ct)
    {
        var rows = await _coupons.ListTemplatesByIdsAsync(templateIds, ct).ConfigureAwait(false);
        return rows.ToDictionary(a => a.Id, a => a.TemplateName);
    }
}

/// <summary>券活动的池子规则。</summary>
/// <remarks>
/// 抽出来是因为新建与编辑是同一个表单的两条路径，规则必须一模一样
/// （这个项目栽过一次：编辑路径少写一条规则，于是同样的配置「建不出来、却能改出来」）。
/// </remarks>
internal static class CouponActivityRules
{
    /// <summary>校验计划发放量是否超过模板剩余可发量。</summary>
    /// <param name="template">关联的券模板。</param>
    /// <param name="claimQuantity">计划发放量。</param>
    /// <returns>通过返回 null，否则返回中文原因。</returns>
    /// <remarks>
    /// <c>TotalQuantity = 0</c> 表示不限量，此时没有「剩余」可言，直接放行。
    ///
    /// <para>只比「本活动 vs 模板剩余」，不比「同模板下所有活动的合计」：
    /// 合计超发在**领取那一刻**由模板池子（<c>IssuedQuantity</c>）兜底拒掉，
    /// 而这里拦的是最明显的一种配置错误 —— 一次就要发得比模板能发的还多。</para>
    /// </remarks>
    public static string? CheckTemplateRemaining(CouponTemplate template, int claimQuantity)
    {
        if (template.TotalQuantity <= 0) return null;

        var remaining = template.TotalQuantity - template.IssuedQuantity;
        if (claimQuantity <= remaining) return null;

        return $"发放量超过模板剩余可发量（模板共 {template.TotalQuantity} 张，已发 {template.IssuedQuantity} 张，剩余 {remaining} 张）";
    }
}

/// <summary>编辑券活动处理器。</summary>
public sealed class CreateCouponActivityHandler
    : IRequestHandler<CreateCouponActivityCommand, ApiResponse<long>>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public CreateCouponActivityHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行新建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新活动 Id。</returns>
    /// <remarks>
    /// <para><b>关联模板必须存在</b>：只在「Id 大于 0」上放行的话，
    /// 运营填一个不存在的模板也能保存，用户点「领取」才发现领不到券。</para>
    ///
    /// <para><c>ClaimedQuantity</c>（已领取数）<b>恒从 0 起</b>：
    /// 它是领券流程累加出来的计数，允许请求体带进来的话，
    /// 报表上的「已领取」就能被随手编造。</para>
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(CreateCouponActivityCommand request, CancellationToken ct)
    {
        // 归属由服务端按租户身份解析（DATA_SPEC 5.13，与 5.11 活动同一套规则）：
        // PlatformId / MerchantId 来自请求体，而 AOP 租户过滤只管查询 / 更新 / 删除、
        // **不管插入** —— 不解析的话，商户账号能建出一条挂在别人平台 / 商户名下的券活动。
        if (!Promotion.PromotionActivityScope.TryResolve(
                TenantContextHolder.Current, request.PlatformId, request.MerchantId,
                out var platformId, out var merchantId, out var scopeError))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.Forbidden, scopeError);
        }

        var template = await _coupons.GetTemplateAsync(request.TemplateId, ct).ConfigureAwait(false);
        if (template is null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.NotFound, "关联的券模板不存在");
        }

        // 模板与活动的归属必须一致：跨租户引用等于把 A 的券发到 B 的活动上。
        // 只比「双方都有归属」的情况 —— 平台 0 / 商户 0 表示不限（历史数据与平台无关的模板）。
        if (merchantId > 0 && template.MerchantId > 0 && template.MerchantId != merchantId)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "关联的券模板不属于当前商户");
        }

        if (platformId > 0 && template.PlatformId > 0 && template.PlatformId != platformId)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, "关联的券模板不属于当前平台");
        }

        // 计划发放量不得超过模板剩余可发量（DATA_SPEC 5.13 / 5.32「仅后端验证」）。
        // 不拦的后果：领券中心挂着一个**点了就报错**的按钮 ——
        // 用户点「领取」才被告知「券模板剩余不足」，而那本该在配置时就挡住。
        var remainingError = CouponActivityRules.CheckTemplateRemaining(template, request.ClaimQuantity);
        if (remainingError is not null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, remainingError);
        }

        var activity = new CouponActivity
        {
            ActivityName = request.ActivityName.Trim(),
            TemplateId = request.TemplateId,
            ClaimStartTime = CouponTimeNormalizer.ToUtc(request.ClaimStartTime),
            ClaimEndTime = CouponTimeNormalizer.ToUtc(request.ClaimEndTime),
            ClaimQuantity = request.ClaimQuantity,
            ClaimedQuantity = 0,
            PerUserLimit = request.PerUserLimit,
            TargetType = request.TargetType,
            Targets = (request.Targets ?? "[]").Trim(),
            SortOrder = request.SortOrder,
            Status = request.Status,
            PlatformId = platformId,
            MerchantId = merchantId
        };

        var id = await _coupons.InsertActivityAsync(activity, ct).ConfigureAwait(false);
        return ApiResults.Ok(id, "券活动已创建");
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
            // 悬空引用（模板已被删）的**唯一**出路是停用：改绑到别的模板时那个模板必须存在，
            // 否则只是又造一条悬空引用。没有这个口子，这类活动会永远停在「启用」且改不动，
            // 用户在领券中心点「领取」只会拿到一句报错。
            var canDisable = request.Status == 2 && request.TemplateId == activity.TemplateId;
            if (!canDisable)
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "关联的券模板不存在");
            }
        }
        else
        {
            // 与新建同一条规则（DATA_SPEC 5.13）：编辑也不能把发放量改到超过模板剩余可发量。
            // 只在新建那侧拦等于「建不出来却能改出来」，这个项目已经栽过一次。
            var remainingError = CouponActivityRules.CheckTemplateRemaining(template, request.ClaimQuantity);
            if (remainingError is not null)
            {
                return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, remainingError);
            }
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
    private static DateTime ToUtc(DateTime value) => CouponTimeNormalizer.ToUtc(value);
}

/// <summary>启停券活动的处理器。</summary>
/// <remarks>
/// 启停是<b>独立接口</b>，不重跑编辑校验：活动创建时已经按模板剩余量校验过，
/// 之后再点「启用 / 停用」时若重跑，模板剩余量已被本活动占走，
/// 合法的活动会被自己挡住，运营只能看到一条莫名其妙的 400。
/// </remarks>
public sealed class SetCouponActivityStatusHandler
    : IRequestHandler<SetCouponActivityStatusCommand, ApiResponse>
{
    private readonly ICouponRepository _coupons;

    /// <summary>构造处理器。</summary>
    /// <param name="coupons">券仓储。</param>
    public SetCouponActivityStatusHandler(ICouponRepository coupons) => _coupons = coupons;

    /// <summary>执行启停。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 启用时仍要求关联模板存在；停用永远允许——模板已被删的悬空活动，
    /// 停用是唯一出路，不能因为模板不存在就把它永远锁死在启用状态。
    /// </remarks>
    public async Task<ApiResponse> Handle(
        SetCouponActivityStatusCommand request, CancellationToken ct)
    {
        var activity = await _coupons.GetActivityAsync(request.ActivityId, ct).ConfigureAwait(false);
        if (activity is null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券活动不存在");
        }

        if (request.Status == 1)
        {
            var template = await _coupons.GetTemplateAsync(activity.TemplateId, ct).ConfigureAwait(false);
            if (template is null)
            {
                return ApiResponseFactory.Fail(
                    BaseApiResponseCode.NotFound, "关联的券模板不存在，不能启用");
            }
        }

        var affected = await _coupons
            .SetActivityStatusAsync(request.ActivityId, request.Status, ct)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "券活动不存在");
        }

        return ApiResponseFactory.Ok(request.Status == 1 ? "券活动已启用" : "券活动已停用");
    }
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
            request.TemplateId, request.OrderNo, request.Keyword, request.CustomerId, ct).ConfigureAwait(false);

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
        // 防 IDOR：客户令牌存在时以令牌里的客户为准，与请求体不一致直接 403。
        // 之前是「静默改用令牌客户」，虽然没泄露数据，但客户端传错 Id 时界面照常显示，
        // bug 会一直藏着；而且与订单 / 积分 / 购物车的口径不一致（那三处都是 403）。
        var customerId = CustomerScope.Require(request.CustomerId);

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
