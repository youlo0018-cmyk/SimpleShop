using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>订单超时关单配置。</summary>
/// <remarks>
/// 阈值放在配置里而不是写死：不同业务（生鲜 / 3C）需要的等待时长不一样，
/// 而线上要临时调长时，改配置比重新发版快得多。
/// </remarks>
public sealed class OrderTimeoutOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Orders";

    /// <summary>支付超时阈值（分钟）。BUSINESS.md 7.3 规定 30 分钟。</summary>
    public int PaymentTimeoutMinutes { get; set; } = 30;

    /// <summary>单次扫描的最大张数。</summary>
    /// <remarks>
    /// 卡了半小时的订单可能一次性积压几百张（服务重启、数据库抖动之后尤其明显）。
    /// 不限张数的话一轮会把下游打满，库存、积分、券三个服务同时收到几百个请求，
    /// 关单本身又超时，下一轮继续堵。每轮限量把积压摊平更稳。
    /// </remarks>
    public int ScanBatchSize { get; set; } = 200;

    /// <summary>超过这个阈值就认为本轮异常，停止处理剩下的（防雪崩）。</summary>
    public int MaxClosePerRun { get; set; } = 500;
}

/// <summary>关闭支付超时订单的处理器。</summary>
/// <remarks>
/// <para><b>扫描规则归订单服务，定时归 ScheduledService</b>（BUSINESS.md 7.3）：
/// 「哪些单超时了」是业务规则，订单表也只有订单服务能查；
/// 「什么时候扫」是基础设施，交给独立进程，两者职责不混。</para>
///
/// <para>候选按 <c>created_at</c> 升序取：先关最早的那批。
/// 不排序的话 LIMIT 会随机取一批积压单，可能出现「关了新的、留了更老的」，
/// 而老的那些正是用户等最久、最可能在投诉的。</para>
/// </remarks>
public sealed class CloseTimeoutOrdersHandler
    : MediatR.IRequestHandler<CloseTimeoutOrdersCommand, ApiResponse<CloseTimeoutResult>>
{
    private readonly IOrderStore _store;
    private readonly OrderCancellationService _cancellation;
    private readonly OrderTimeoutOptions _options;
    private readonly ILogger<CloseTimeoutOrdersHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="store">落单端口。</param>
    /// <param name="cancellation">取消服务。</param>
    /// <param name="options">超时配置。</param>
    /// <param name="logger">日志器。</param>
    public CloseTimeoutOrdersHandler(
        IOrderStore store, OrderCancellationService cancellation,
        IOptions<OrderTimeoutOptions> options, ILogger<CloseTimeoutOrdersHandler> logger)
    {
        _store = store;
        _cancellation = cancellation;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>执行关单。</summary>
    /// <param name="request">关单命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>关单统计。</returns>
    public async Task<ApiResponse<CloseTimeoutResult>> Handle(
        CloseTimeoutOrdersCommand request, CancellationToken ct)
    {
        var details = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(request.OrderNo))
        {
            return await CloseOneAsync(request.OrderNo.Trim(), details, ct).ConfigureAwait(false);
        }

        var limit = request.Limit > 0 ? Math.Min(request.Limit, _options.ScanBatchSize) : _options.ScanBatchSize;
        var deadlineUtc = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.PaymentTimeoutMinutes));

        var candidates = await _store
            .FindTimeoutCandidatesAsync(deadlineUtc, limit, ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return ApiResults.Ok(new CloseTimeoutResult(0, 0, 0, details), "没有超时未支付的订单");
        }

        var closed = 0;
        var skipped = 0;

        foreach (var order in candidates)
        {
            // 单轮处理量硬上限：积压到几千张时，宁可多跑几轮，
            // 也不要一轮把三个下游服务一起打挂然后整体超时——那会进入越处理越堵的死循环
            if (closed >= _options.MaxClosePerRun)
            {
                details[order.OrderNo] = "本轮处理量已达上限，留到下一轮";
                continue;
            }

            var result = await TryCloseAsync(order, ct).ConfigureAwait(false);

            if (result.Changed) closed++;
            else skipped++;

            details[order.OrderNo] = result.Changed ? "已关单" : "状态已变更，跳过";
        }

        _logger.LogInformation(
            "超时关单完成：扫描 {Scanned} 张，关单 {Closed} 张，跳过 {Skipped} 张", candidates.Count, closed, skipped);

        return ApiResults.Ok(
            new CloseTimeoutResult(candidates.Count, closed, skipped, details),
            $"扫描 {candidates.Count} 张，关单 {closed} 张");
    }

    /// <summary>手工指定单号时走这条路。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <param name="details">逐张结果字典。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>关单统计。</returns>
    private async Task<ApiResponse<CloseTimeoutResult>> CloseOneAsync(
        string orderNo, Dictionary<string, string> details, CancellationToken ct)
    {
        var order = await _store.FindByOrderNoAsync(orderNo, ct).ConfigureAwait(false);
        if (order is null)
        {
            details[orderNo] = "订单不存在";
            return ApiResults.Ok(new CloseTimeoutResult(0, 0, 0, details), "订单不存在");
        }

        var result = await TryCloseAsync(order, ct).ConfigureAwait(false);
        details[orderNo] = result.Changed ? "已关单" : $"状态是「{OrderStatusMachine.NameOf(order.Status)}」，不关";

        return ApiResults.Ok(
            new CloseTimeoutResult(1, result.Changed ? 1 : 0, result.Changed ? 0 : 1, details),
            details[orderNo]);
    }

    /// <summary>关一张单，异常一律吃掉（定时任务不能因为一张坏单整轮失败）。</summary>
    /// <param name="order">候选订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>取消结果。</returns>
    private async Task<OrderCancelResult> TryCloseAsync(Order order, CancellationToken ct)
    {
        try
        {
            return await _cancellation.CancelAsync(order, "支付超时关单", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "超时关单失败：订单 {OrderNo}，留到下一轮", order.OrderNo);
            return OrderCancelResult.NotChanged();
        }
    }
}