using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using MediatR;
using Microsoft.Extensions.Logging;
using PointService.Domain.Entities;
using PointService.Domain.IRepository;
using PointService.Domain.Services;

namespace PointService.Application.Features.Operations;

/// <summary>发放积分处理器。</summary>
public sealed class EarnPointsHandler : IRequestHandler<EarnPointsCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    /// <param name="rules">积分规则提供器。</param>
    public EarnPointsHandler(IPointRepository points, IPointRuleProvider rules)
    {
        _points = points;
        _rules = rules;
    }

    /// <summary>执行发放。</summary>
    /// <param name="request">发放命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(EarnPointsCommand request, CancellationToken ct)
    {
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);

        var outcome = await _points.EarnAsync(
            request.CustomerId, request.Source, request.Quantity, request.BizNo,
            request.Action, request.Remark, rules.ValidDays, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var message = outcome.AlreadyApplied ? "该业务单已发放过积分"
            : outcome.Capped ? "发放成功，但超出余额上限的部分未入账"
            : "发放成功";

        return ApiResults.Ok(ToBalance(request.CustomerId, outcome), message);
    }

    internal static PointBalance ToBalance(long customerId, PointOutcome o)
        => new(customerId, o.Available, o.Frozen, 0, 0, o.AlreadyApplied);
}

/// <summary>下单冻结积分处理器。</summary>
public sealed class LockPointsHandler : IRequestHandler<LockPointsCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public LockPointsHandler(IPointRepository points) => _points = points;

    /// <summary>执行冻结。</summary>
    /// <param name="request">冻结命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额；积分不足返回业务错误。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(LockPointsCommand request, CancellationToken ct)
    {
        var outcome = await _points.LockAsync(
            request.CustomerId, request.BizNo, request.Quantity, request.Remark, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome),
            outcome.AlreadyApplied ? "该订单已冻结过积分" : "冻结成功");
    }
}

/// <summary>按订单发放积分处理器（实付每满 1 元 1 积分）。</summary>
public sealed class EarnByOrderHandler : IRequestHandler<EarnByOrderCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;
    private readonly IPointRuleProvider _rules;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    /// <param name="rules">积分规则提供器。</param>
    public EarnByOrderHandler(IPointRepository points, IPointRuleProvider rules)
    {
        _points = points;
        _rules = rules;
    }

    /// <summary>执行发放。</summary>
    /// <param name="request">发放命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额；实付不足 1 元时返回成功但未发放。</returns>
    /// <remarks>
    /// <para><b>向下取整</b>，不是四舍五入：实付 0.99 元得 0 积分。
    /// 四舍五入的话 0.40 元也能得 0 分……差得不多，但规则一旦说不清，
    /// 用户就会来问「为什么我这单没给积分」，而那时没人能答上来。</para>
    ///
    /// <para>实付不足 1 元时<b>不报错</b>：0 元单（全额抵扣）是完全正常的单，
    /// 它只是恰好拿不到积分。回错误会让订单服务把「订单完成」当成失败。</para>
    /// </remarks>
    public async Task<ApiResponse<PointBalance>> Handle(EarnByOrderCommand request, CancellationToken ct)
    {
        // 规则来自配置（后台可改），不是常量。
        var rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        var earned = (long)Math.Floor(request.PaidAmount * rules.EarnPointsPerYuan);

        if (earned <= 0)
        {
            // 不足 1 元只查余额就够了：不写流水（没有积分变动，写一条流水只会让人以为发生过什么）
            var account = await _points.GetAccountAsync(request.CustomerId, ct);
            return ApiResults.Ok(new PointBalance(
                account?.CustomerId ?? request.CustomerId,
                account?.Available ?? 0, account?.Frozen ?? 0,
                account?.TotalEarned ?? 0, account?.TotalUsed ?? 0, false),
                "订单实付不足 1 元，本次不发放积分");
        }

        var outcome = await _points.EarnAsync(
            request.CustomerId, PointSources.OrderCompleted, earned, request.BizNo,
            "earn", request.Remark, rules.ValidDays, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var msg = outcome.AlreadyApplied ? "该订单已发放过积分"
            : outcome.Capped ? $"发放成功 {earned} 积分，超出余额上限的部分未入账"
            : $"订单完成，发放 {earned} 积分";

        return ApiResults.Ok(EarnPointsHandler.ToBalance(request.CustomerId, outcome), msg);
    }
}

/// <summary>解冻积分处理器。</summary>
public sealed class UnfreezePointsHandler : IRequestHandler<UnfreezePointsCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public UnfreezePointsHandler(IPointRepository points) => _points = points;

    /// <summary>执行解冻。</summary>
    /// <param name="request">解冻命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(UnfreezePointsCommand request, CancellationToken ct)
    {
        var outcome = await _points.UnfreezeAsync(request.CustomerId, request.BizNo, request.Remark, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome),
            outcome.AlreadyApplied ? "该订单已解冻过" : "解冻成功");
    }
}

/// <summary>实扣积分处理器。</summary>
public sealed class ConsumePointsHandler : IRequestHandler<ConsumePointsCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public ConsumePointsHandler(IPointRepository points) => _points = points;

    /// <summary>执行实扣。</summary>
    /// <param name="request">实扣命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(ConsumePointsCommand request, CancellationToken ct)
    {
        var outcome = await _points.ConsumeAsync(request.CustomerId, request.BizNo, request.Remark, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome),
            outcome.AlreadyApplied ? "该订单已扣减过积分" : "扣减成功");
    }
}

/// <summary>退款回收积分处理器。</summary>
public sealed class RefundPointsHandler : IRequestHandler<RefundPointsCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public RefundPointsHandler(IPointRepository points) => _points = points;

    /// <summary>执行回收。</summary>
    /// <param name="request">回收命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回最新余额。</returns>
    public async Task<ApiResponse<PointBalance>> Handle(RefundPointsCommand request, CancellationToken ct)
    {
        var outcome = await _points.RefundAsync(
            request.CustomerId, request.BizNo, request.RefundRatio, request.Remark, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointBalance>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        return ApiResults.Ok(
            EarnPointsHandler.ToBalance(request.CustomerId, outcome),
            outcome.AlreadyApplied ? "该订单已回收过积分" : "回收成功");
    }
}

/// <summary>每日签到处理器。</summary>
public sealed class SignInPointsHandler : IRequestHandler<SignInPointsCommand, ApiResponse<PointSignInResult>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public SignInPointsHandler(IPointRepository points) => _points = points;

    /// <summary>执行签到。</summary>
    /// <param name="request">签到命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回签到结果。</returns>
    public async Task<ApiResponse<PointSignInResult>> Handle(SignInPointsCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        // 跨天判定以**服务端本地日期**为准，不做时区换算（BUSINESS.md 13.6）。
        // 直接用 UtcNow.Date 会让东八区凌晨 0~8 点的用户「签到到昨天」，
        // 必须先转成 Asia/Shanghai 再取日期。
        var today = TimeZoneHelper.GetShanghaiToday();

        var (outcome, streak, reward, already) =
            await _points.SignInAsync(customerId, today, ct);

        if (!outcome.Succeeded)
        {
            return ApiResults.Fail<PointSignInResult>(BaseApiResponseCode.BusinessError, outcome.Error);
        }

        var result = new PointSignInResult(
            customerId, streak, reward, outcome.Available, already,
            already ? "今日已签到" : $"签到成功，获得 {reward} 积分");

        return ApiResults.Ok(result, result.Message);
    }
}

/// <summary>查询积分账户处理器。</summary>
public sealed class QueryPointAccountHandler : IRequestHandler<QueryPointAccountCommand, ApiResponse<PointBalance>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public QueryPointAccountHandler(IPointRepository points) => _points = points;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>积分余额。没发放过也返回全 0，不报 404。</returns>
    /// <remarks>
    /// 从没领过积分的客户也要能看到「0 分」，而不是「查不到」。
    /// 否则小程序积分中心对新用户会显示空白或报错。
    /// </remarks>
    public async Task<ApiResponse<PointBalance>> Handle(QueryPointAccountCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var account = await _points.GetAccountAsync(customerId, ct);

        var balance = account is null
            ? new PointBalance(customerId, 0, 0, 0, 0, false)
            : new PointBalance(
                account.CustomerId, account.Available, account.Frozen,
                account.TotalEarned, account.TotalUsed, false);

        return ApiResults.Ok(balance);
    }
}

/// <summary>分页查询积分流水处理器。</summary>
public sealed class QueryPointRecordsHandler : IRequestHandler<QueryPointRecordsCommand, ApiResponse<List<PointRecordItem>>>
{
    private readonly IPointRepository _points;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    public QueryPointRecordsHandler(IPointRepository points) => _points = points;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流水列表。</returns>
    public async Task<ApiResponse<List<PointRecordItem>>> Handle(QueryPointRecordsCommand request, CancellationToken ct)
    {
        var customerId = CustomerScope.Require(request.CustomerId);
        var (items, _) = await _points.QueryRecordsAsync(
            customerId, Math.Max(1, request.Page), Math.Clamp(request.PageSize, 1, 100), ct);

        var list = items.Select(a => new PointRecordItem(
            a.Id.ToString(), a.BizNo, a.Action, a.Quantity,
            a.BeforeAvailable, a.AfterAvailable, a.BeforeFrozen, a.AfterFrozen,
            a.Remark, a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();

        return ApiResults.Ok(list);
    }
}

/// <summary>时区辅助。</summary>
public static class TimeZoneHelper
{
    /// <summary>签到等「按天」判定的时区。文档指定 Asia/Shanghai，不做配置化。</summary>
    public static readonly TimeZoneInfo BusinessZone =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    /// <summary>取业务时区下的今天。</summary>
    /// <returns>业务时区的当天日期。</returns>
    public static DateOnly GetShanghaiToday()
    {
        // 兜底：某些精简 Linux 镜像没有 tzdata，会抛 TimeZoneNotFoundException。
        // 这时退回 UTC+8 定长偏移——中国不实行夏令时，定长偏移与 IANA 时区等价。
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        }
    }
}
/// <summary>过期扣减处理器。</summary>
public sealed class ExpirePointsHandler : IRequestHandler<ExpirePointsCommand, ApiResponse<ExpireResult>>
{
    private readonly IPointRepository _points;
    private readonly ILogger<ExpirePointsHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="points">积分仓储。</param>
    /// <param name="logger">日志器。</param>
    public ExpirePointsHandler(IPointRepository points, ILogger<ExpirePointsHandler> logger)
    {
        _points = points;
        _logger = logger;
    }

    /// <summary>执行过期处理。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>处理统计。</returns>
    /// <remarks>
    /// <para><b>单个批次失败不中断整批</b>：一个客户的账户异常不该让后面几千个批次都过期不了。
    /// 失败的记下来继续，下轮还会扫到它（批次没清零就一直会被扫到），所以不会丢。</para>
    ///
    /// <para>没有扫到东西时返回成功而不是失败：每天 02:00 大多数时候本来就没什么可过期的，
    /// 把它算成失败会让监控上天天飘红，久而久之就没人看这个告警了。</para>
    /// </remarks>
    public async Task<ApiResponse<ExpireResult>> Handle(ExpirePointsCommand request, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var lots = await _points.GetExpiredLotsAsync(nowUtc, request.Limit, ct);

        if (lots.Count == 0)
        {
            return ApiResults.Ok(new ExpireResult(0, 0, 0, 0), "没有到期积分");
        }

        var expired = 0;
        var skipped = 0;
        long deducted = 0;

        foreach (var lot in lots)
        {
            try
            {
                var outcome = await _points.ExpireLotAsync(lot.Id, ct);

                if (!outcome.Succeeded)
                {
                    skipped++;
                    continue;
                }

                if (outcome.AlreadyApplied || lot.Remaining <= 0)
                {
                    skipped++;
                    continue;
                }

                expired++;
                deducted += lot.Remaining;
            }
            catch (Exception ex)
            {
                // 单个批次异常不让整批中断：批次没清零，下轮还会扫到它
                _logger.LogError(ex, "过期批次 {LotId}（客户 {CustomerId}）处理失败，本轮跳过", lot.Id, lot.CustomerId);
                skipped++;
            }
        }

        return ApiResults.Ok(
            new ExpireResult(lots.Count, expired, skipped, deducted),
            $"扫描 {lots.Count} 个批次，过期 {expired} 个，扣减 {deducted} 积分");
    }
}
