using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MarketingService.Application.Services;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MarketingService.Domain.Services;
using MediatR;

namespace MarketingService.Application.Features.Promotion;

/// <summary>活动类型与适用范围的中文名。</summary>
/// <remarks>
/// 展示中文名放在应用层而不是让前端各写一份：同一套枚举在 5 个页面出现，
/// 各写各的迟早会有一处写成「限时抢购」另一处写成「秒杀」。
/// </remarks>
public static class PromotionNames
{
    /// <summary>活动类型中文名。</summary>
    /// <param name="type">活动类型。</param>
    /// <returns>中文名。</returns>
    public static string TypeName(int type) => type switch
    {
        ActivityTypes.FullReduction => "满减",
        ActivityTypes.Discount => "满折",
        ActivityTypes.Gift => "满赠",
        ActivityTypes.Seckill => "限时抢购",
        _ => "未知活动"
    };

    /// <summary>活动状态中文名。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>中文名。</returns>
    public static string StatusName(int status) => status == 1 ? "启用" : status == 2 ? "停用" : "未知";

    /// <summary>适用范围中文名。</summary>
    /// <param name="targetType">适用范围类型。</param>
    /// <param name="targets">JSON 文本。</param>
    /// <returns>中文名。</returns>
    public static string TargetName(int targetType, string targets)
        => targetType switch
        {
            TargetTypes.All => "全场",
            TargetTypes.BySpu => $"指定商品（{PromotionCalculator.ParseTargets(targets).Count} 个 SPU）",
            TargetTypes.BySku => $"指定规格（{PromotionCalculator.ParseTargets(targets).Count} 个 SKU）",
            _ => "未知范围"
        };

    /// <summary>「优惠」列的展示文本：按活动类型给出满减金额 / 折扣 / 赠品张数。</summary>
    /// <param name="type">活动类型。</param>
    /// <param name="discountAmount">满减金额。</param>
    /// <param name="discountRate">折扣率（折）。</param>
    /// <param name="giftQuantity">满赠每单张数。</param>
    /// <returns>展示文本。</returns>
    /// <remarks>
    /// 列表的「优惠」列不能直接绑 <c>discountAmount</c>：满折活动那一列永远是 0.00，
    /// 满赠活动也没有金额可显示。前端的列格式化器只认固定字段（金额 / 计数 / 时间），
    /// 所以由服务端按类型算好一段文本下发；金额遵循「只显示数字、两位小数」的口径。
    /// </remarks>
    public static string DiscountValue(int type, decimal discountAmount, decimal discountRate, int giftQuantity)
        => type switch
        {
            ActivityTypes.FullReduction => discountAmount.ToString("0.00"),
            ActivityTypes.Discount => $"{discountRate:0.##} 折",
            ActivityTypes.Gift => $"赠 {giftQuantity} 张",
            _ => string.Empty
        };
}

/// <summary>营销活动 DTO 里时间字段的统一格式。</summary>
/// <remarks>
/// 必须带 <c>Z</c>：编辑页把这两个字段交给 `new Date(...)` 再按本地时区显示，
/// 没有时区标记时 JS 会当成**本地时间**解析 —— 界面上看到的是 UTC 原值，
/// 保存时又按本地转 UTC，每编辑一次活动时间就整体偏移一个时区（实测差 8 小时）。
/// </remarks>
internal static class PromotionTimeFormat
{
    /// <summary>把库里的 UTC 时间转成带 <c>Z</c> 的 ISO 字符串。</summary>
    /// <param name="value">数据库中的 UTC 时间（Kind 可能是 Unspecified）。</param>
    /// <returns>形如 <c>2026-10-07T07:29:00Z</c> 的字符串。</returns>
    public static string UtcIso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>新建营销活动的处理器。</summary>
public sealed class CreatePromotionActivityHandler
    : IRequestHandler<CreatePromotionActivityCommand, ApiResponse<long>>
{
    private readonly IPromotionRepository _promotions;
    private readonly IProductPort _products;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="products">商品端口，用于校验适用目标的存在性与归属。</param>
    public CreatePromotionActivityHandler(IPromotionRepository promotions, IProductPort products)
    {
        _promotions = promotions;
        _products = products;
    }

    /// <summary>执行新建。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回活动 Id；归属越界返回 403；目标不可用返回 400。</returns>
    /// <remarks>
    /// 两条校验都必须在插库之前：<c>PlatformId</c> / <c>MerchantId</c> 来自请求体（必须锁定），
    /// <c>Targets</c> 里的 SPU / SKU 必须真实存在且属于本租户
    /// （否则商户 A 的活动能把目标写成商户 B 的商品，那是跨租户改价）。
    /// </remarks>
    public async Task<ApiResponse<long>> Handle(CreatePromotionActivityCommand request, CancellationToken ct)
    {
        if (!PromotionActivityScope.TryResolve(
                TenantContextHolder.Current, request.PlatformId, request.MerchantId,
                out var platformId, out var merchantId, out var scopeError))
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.Forbidden, scopeError);
        }

        var targetError = await PromotionActivityTargets
            .ValidateAsync(request.TargetType, request.Targets, platformId, merchantId, _products, ct)
            .ConfigureAwait(false);
        if (targetError is not null)
        {
            return ApiResults.Fail<long>(BaseApiResponseCode.BadRequest, targetError);
        }

        var activity = new PromotionActivity
        {
            PlatformId = platformId,
            MerchantId = merchantId,
            ActivityName = request.ActivityName.Trim(),
            ActivityType = request.ActivityType,
            ThresholdAmount = PromotionCalculator.Round2(request.ThresholdAmount),
            DiscountAmount = PromotionCalculator.Round2(request.DiscountAmount),
            DiscountRate = PromotionCalculator.Round2(request.DiscountRate),
            GiftTemplateId = request.GiftTemplateId,
            GiftQuantity = request.GiftQuantity,
            SessionId = request.SessionId,
            TargetType = request.TargetType,
            Targets = (request.Targets ?? "[]").Trim(),
            StartTime = ToUtc(request.StartTime),
            EndTime = ToUtc(request.EndTime),
            SortOrder = request.SortOrder,
            Status = request.Status
        };

        var id = await _promotions.InsertAsync(activity, ct);
        return ApiResults.Ok(id, "活动创建成功");
    }

    /// <summary>把客户端传来的时间归一到 UTC。</summary>
    /// <param name="value">原始时间。</param>
    /// <returns>UTC 时间。</returns>
    /// <remarks>
    /// 与 <c>CouponAdminController</c> 同一套处理：带偏移量的字符串反序列化后
    /// Kind=Local 且时钟值已是本地时间，直接存进 UTC 列会差一个时区。
    /// 这条踩过一次（新建的活动立刻提示「不在领取时间内」），两处必须一起改。
    /// </remarks>
    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>编辑营销活动的处理器。</summary>
public sealed class UpdatePromotionActivityHandler
    : IRequestHandler<UpdatePromotionActivityCommand, ApiResponse>
{
    private readonly IPromotionRepository _promotions;
    private readonly IProductPort _products;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="products">商品端口，用于校验适用目标的存在性与归属。</param>
    public UpdatePromotionActivityHandler(IPromotionRepository promotions, IProductPort products)
    {
        _promotions = promotions;
        _products = products;
    }

    /// <summary>执行编辑。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应；活动不存在返回 404；目标不可用返回 400。</returns>
    /// <remarks>
    /// 编辑**不接收** PlatformId / MerchantId：归属在创建时锁定，事后改归属等于把一条活动
    /// 搬到另一个租户名下（已经产生的参与记录会跟着错位）。所以这里只用已存在实体的归属
    /// 去校验新的 Targets。
    /// </remarks>
    public async Task<ApiResponse> Handle(UpdatePromotionActivityCommand request, CancellationToken ct)
    {
        var existing = await _promotions.GetAsync(request.ActivityId, ct);
        if (existing is null) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "活动不存在");

        var targetError = await PromotionActivityTargets
            .ValidateAsync(
                request.TargetType, request.Targets,
                existing.PlatformId, existing.MerchantId, _products, ct)
            .ConfigureAwait(false);
        if (targetError is not null)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.BadRequest, targetError);
        }

        existing.ActivityName = request.ActivityName.Trim();
        existing.ActivityType = request.ActivityType;
        existing.ThresholdAmount = PromotionCalculator.Round2(request.ThresholdAmount);
        existing.DiscountAmount = PromotionCalculator.Round2(request.DiscountAmount);
        existing.DiscountRate = PromotionCalculator.Round2(request.DiscountRate);
        existing.GiftTemplateId = request.GiftTemplateId;
        existing.GiftQuantity = request.GiftQuantity;
        existing.TargetType = request.TargetType;
        existing.Targets = (request.Targets ?? "[]").Trim();
        existing.StartTime = CreatePromotionActivityHandler.ToUtc(request.StartTime);
        existing.EndTime = CreatePromotionActivityHandler.ToUtc(request.EndTime);
        existing.SortOrder = request.SortOrder;
        existing.Status = request.Status;

        var affected = await _promotions.UpdateAsync(existing, ct);
        if (affected == 0)
        {
            return ApiResponseFactory.Fail(BaseApiResponseCode.InternalError, "保存失败，请稍后重试");
        }

        return ApiResponseFactory.Ok("保存成功");
    }
}

/// <summary>活动分页处理器。</summary>
public sealed class QueryPromotionActivitiesHandler
    : IRequestHandler<QueryPromotionActivitiesCommand, ApiResponse<PagedPromotionResult>>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public QueryPromotionActivitiesHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行分页。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动分页结果。</returns>
    public async Task<ApiResponse<PagedPromotionResult>> Handle(
        QueryPromotionActivitiesCommand request, CancellationToken ct)
    {
        var (items, total) = await _promotions.ListAsync(
            request.ActivityType, request.Keyword, request.Status, request.Page, request.PageSize, ct);

        var dtos = items.Select(a => new PromotionActivityDto(
            a.Id, a.ActivityName, a.ActivityType, PromotionNames.TypeName(a.ActivityType),
            a.ThresholdAmount, a.DiscountAmount, a.DiscountRate,
            a.TargetType, PromotionNames.TargetName(a.TargetType, a.Targets),
            PromotionTimeFormat.UtcIso(a.StartTime), PromotionTimeFormat.UtcIso(a.EndTime),
            a.Status, PromotionNames.StatusName(a.Status),
            a.GiftTemplateId, a.GiftQuantity,
            a.SortOrder, a.Targets, a.PlatformId,
            PromotionNames.DiscountValue(a.ActivityType, a.DiscountAmount, a.DiscountRate, a.GiftQuantity)))
            .ToList();

        return ApiResults.Ok(new PagedPromotionResult(dtos, total, request.Page, request.PageSize));
    }
}

/// <summary>取单个活动的处理器。</summary>
public sealed class GetPromotionActivityHandler
    : IRequestHandler<GetPromotionActivityCommand, ApiResponse<PromotionActivityDto>>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public GetPromotionActivityHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>活动详情。</returns>
    public async Task<ApiResponse<PromotionActivityDto>> Handle(
        GetPromotionActivityCommand request, CancellationToken ct)
    {
        var a = await _promotions.GetAsync(request.ActivityId, ct);
        if (a is null) return ApiResults.Fail<PromotionActivityDto>(BaseApiResponseCode.NotFound, "活动不存在");

        return ApiResults.Ok(new PromotionActivityDto(
            a.Id, a.ActivityName, a.ActivityType, PromotionNames.TypeName(a.ActivityType),
            a.ThresholdAmount, a.DiscountAmount, a.DiscountRate,
            a.TargetType, PromotionNames.TargetName(a.TargetType, a.Targets),
            PromotionTimeFormat.UtcIso(a.StartTime), PromotionTimeFormat.UtcIso(a.EndTime),
            a.Status, PromotionNames.StatusName(a.Status),
            a.GiftTemplateId, a.GiftQuantity,
            a.SortOrder, a.Targets, a.PlatformId,
            PromotionNames.DiscountValue(a.ActivityType, a.DiscountAmount, a.DiscountRate, a.GiftQuantity)));
    }
}

/// <summary>启停活动的处理器。</summary>
public sealed class SetPromotionStatusHandler : IRequestHandler<SetPromotionStatusCommand, ApiResponse>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public SetPromotionStatusHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行启停。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    public async Task<ApiResponse> Handle(SetPromotionStatusCommand request, CancellationToken ct)
    {
        var affected = await _promotions.SetStatusAsync(request.ActivityId, request.Status, ct);
        if (affected == 0) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "活动不存在");

        return ApiResponseFactory.Ok(request.Status == 1 ? "已启用" : "已停用");
    }
}

/// <summary>删除活动的处理器。</summary>
public sealed class DeletePromotionActivityHandler : IRequestHandler<DeletePromotionActivityCommand, ApiResponse>
{
    private readonly IPromotionRepository _promotions;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    public DeletePromotionActivityHandler(IPromotionRepository promotions) => _promotions = promotions;

    /// <summary>执行删除。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回空响应。</returns>
    /// <remarks>
    /// 走<b>软删</b>：已经参与过这个活动的订单要能查到当时的活动配置，
    /// 物理删掉就对不上账了。
    /// </remarks>
    public async Task<ApiResponse> Handle(DeletePromotionActivityCommand request, CancellationToken ct)
    {
        var affected = await _promotions.SoftDeleteAsync(request.ActivityId, ct);
        if (affected == 0) return ApiResponseFactory.Fail(BaseApiResponseCode.NotFound, "活动不存在");

        return ApiResponseFactory.Ok("删除成功");
    }
}

/// <summary>到手价试算处理器（BUSINESS.md 11.5）。</summary>
/// <remarks>
/// 列表页整页商品合并成一次试算，所以这个接口是<b>全系统调用频次最高的业务接口</b>。
/// 一次调用只查两次库（活动 + 用户券），计算全在内存里做完，不做 N+1。
/// </remarks>
public sealed class CalculateFinalPriceHandler
    : IRequestHandler<CalculateFinalPriceCommand, ApiResponse<FinalPriceDto>>
{
    private readonly IPromotionRepository _promotions;
    private readonly ICouponRepository _coupons;
    private readonly IProductPort _products;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="coupons">券仓储。</param>
    /// <param name="products">商品端口：商户级活动要按行上的商户匹配。</param>
    public CalculateFinalPriceHandler(
        IPromotionRepository promotions, ICouponRepository coupons, IProductPort products)
    {
        _promotions = promotions;
        _coupons = coupons;
        _products = products;
    }

    /// <summary>执行试算。</summary>
    /// <param name="request">命令，CustomerId = 0 表示游客。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>到手价与逐行拆分。</returns>
    public async Task<ApiResponse<FinalPriceDto>> Handle(
        CalculateFinalPriceCommand request, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        var activities = await _promotions.ListActiveAsync(
            request.PlatformId, request.SessionId, nowUtc, ct);

        // 商户级活动要按「这一行属于哪个商户」匹配，而 C 端只传 SPU / SKU：
        // 归属只能回商品服务取（没有商户级活动时直接跳过，不给高频接口加负担）
        var merchants = await LineMerchantResolver
            .ResolveAsync(activities, request.Lines.Select(a => a.SkuId), _products, ct)
            .ConfigureAwait(false);

        var lines = request.Lines
            .Select(a => new PromotionLine(
                a.SpuId, a.SkuId, PromotionCalculator.Round2(a.Amount),
                merchants.GetValueOrDefault(a.SkuId)))
            .ToArray();

        // 游客只算活动价不计券：没有券包可查，直接传空集合。
        // 查一个 customer_id = 0 的券包既是无效查询，也让「游客」这个语义变得含糊。
        var coupons = request.CustomerId > 0
            ? await _coupons.ListAvailableAsync(request.CustomerId, nowUtc, ct)
            : new List<UserCoupon>();

        var priority = await _promotions.GetPriorityAsync(request.PlatformId, ct);

        var result = PromotionCalculator.Calculate(lines, activities, coupons, priority, nowUtc);

        var dto = new FinalPriceDto(
            result.OriginalTotal, result.ActivityDiscountTotal, result.CouponDiscountTotal,
            result.FinalPrice, result.CouponId, result.CouponCode,
            result.UsedActivity, request.CustomerId <= 0,
            result.Lines.Select(a => new FinalPriceLineDto(
                a.SpuId, a.SkuId, a.OriginalAmount,
                a.ActivityDiscount, a.CouponDiscount, a.PayableAmount,
                a.Source, a.SourceName)).ToList());

        return ApiResults.Ok(dto);
    }

}
/// <summary>分组批量试算处理器：每组独立算，组间互不影响。</summary>
/// <remarks>
/// 列表页要的就是这个语义——「这个商品我自己买，能便宜多少」，
/// 而不是「这一页所有商品合起来能便宜多少」。原因写在 <see cref="CalculateFinalPriceBatchCommand"/> 的注释里。
/// </remarks>
public sealed class CalculateFinalPriceBatchHandler
    : IRequestHandler<CalculateFinalPriceBatchCommand, ApiResponse<IReadOnlyList<FinalPriceDto>>>
{
    private readonly IPromotionRepository _promotions;
    private readonly ICouponRepository _coupons;
    private readonly IProductPort _products;

    /// <summary>构造处理器。</summary>
    /// <param name="promotions">活动仓储。</param>
    /// <param name="coupons">券仓储。</param>
    /// <param name="products">商品端口：商户级活动要按行上的商户匹配。</param>
    public CalculateFinalPriceBatchHandler(
        IPromotionRepository promotions, ICouponRepository coupons, IProductPort products)
    {
        _promotions = promotions;
        _coupons = coupons;
        _products = products;
    }

    /// <summary>执行批量试算。</summary>
    /// <param name="request">命令，Groups 每组一次独立试算。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>与 Groups 一一对应的试算结果，顺序一致。</returns>
    /// <remarks>
    /// 活动与用户券都<b>只查一次</b>然后复用：几十组共享同一批候选活动与同一张券包，
    /// 逐组查库才是真正的 N+1。结果的正确性来自「门槛只按组内行金额判定」，
    /// 不来自「查得更多」。
    /// </remarks>
    public async Task<ApiResponse<IReadOnlyList<FinalPriceDto>>> Handle(
        CalculateFinalPriceBatchCommand request, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        var activities = await _promotions.ListActiveAsync(
            request.PlatformId, request.SessionId, nowUtc, ct);

        // 整批只查一次归属（所有组共用同一张 SKU → 商户 表）
        var merchants = await LineMerchantResolver
            .ResolveAsync(
                activities,
                request.Groups.SelectMany(g => g).Select(a => a.SkuId),
                _products, ct)
            .ConfigureAwait(false);

        var coupons = request.CustomerId > 0
            ? await _coupons.ListAvailableAsync(request.CustomerId, nowUtc, ct)
            : new List<UserCoupon>();

        var priority = await _promotions.GetPriorityAsync(request.PlatformId, ct);

        var results = new List<FinalPriceDto>(request.Groups.Count);

        foreach (var group in request.Groups)
        {
            var lines = group
                .Select(a => new PromotionLine(
                    a.SpuId, a.SkuId, PromotionCalculator.Round2(a.Amount),
                    merchants.GetValueOrDefault(a.SkuId)))
                .ToArray();

            var r = PromotionCalculator.Calculate(lines, activities, coupons, priority, nowUtc);

            results.Add(new FinalPriceDto(
                r.OriginalTotal, r.ActivityDiscountTotal, r.CouponDiscountTotal,
                r.FinalPrice, r.CouponId, r.CouponCode,
                r.UsedActivity, request.CustomerId <= 0,
                r.Lines.Select(a => new FinalPriceLineDto(
                    a.SpuId, a.SkuId, a.OriginalAmount,
                    a.ActivityDiscount, a.CouponDiscount, a.PayableAmount,
                    a.Source, a.SourceName)).ToList()));
        }

        return ApiResults.Ok<IReadOnlyList<FinalPriceDto>>(results);
    }
}
