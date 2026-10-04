using Collaboration.Domain.Common;
using FluentValidation;
using InventoryService.Domain.Entities;
using InventoryService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryService.Application.Features.Internal;

/// <summary>重试补偿表里到期的库存释放（ScheduledService 每轮调用）。</summary>
/// <param name="Limit">单轮最多处理多少条。</param>
public record CompensateStockReleasesCommand(int Limit = 200)
    : IRequest<ApiResponse<CompensateStockReleasesResult>>;

/// <summary>补偿重试结果。</summary>
/// <param name="Scanned">本轮取到的待处理条数。</param>
/// <param name="Succeeded">重试成功条数。</param>
/// <param name="Failed">仍失败条数（会退避后重试）。</param>
/// <param name="GaveUp">达到重试上限转人工的条数。</param>
public sealed record CompensateStockReleasesResult(int Scanned, int Succeeded, int Failed, int GaveUp);

/// <summary>命令校验器注册。</summary>
public static class CompensateStockReleasesValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddCompensateStockReleasesValidators(IServiceCollection services)
        => services.AddScoped<IValidator<CompensateStockReleasesCommand>, CompensateStockReleasesValidator>();

    /// <summary>校验规则。</summary>
    private sealed class CompensateStockReleasesValidator
        : AbstractValidator<CompensateStockReleasesCommand>
    {
        /// <summary>构造校验器。</summary>
        public CompensateStockReleasesValidator()
            => RuleFor(x => x.Limit).InclusiveBetween(1, 500).WithMessage("单轮处理条数不正确");
    }
}

/// <summary>库存释放补偿重试处理器。</summary>
/// <remarks>
/// <para><b>为什么要有这张补偿表</b>：释放库存是「取消订单 / 超时关单 / 下单失败回滚」的兜底动作。
/// 兜底动作自己失败时，库存就<b>永久锁住</b>——商品一直卖不出去，而且没有任何痕迹说明发生过什么。
/// 记一条待补偿、交给定时任务重试，是唯一能让这类库存自己恢复回来的办法。</para>
///
/// <para><b>重试的 bizNo 必须加后缀</b>（<c>原单号#R3</c>）：幂等键是 <c>{biz_no}:{action}</c>，
/// 用原单号重试会命中首次那条流水、被判成「已处理过」而<b>什么都没做</b>。</para>
///
/// <para><b>退避 + 次数上限</b>：失败后按 2 的幂次推后下次重试时间，超过 8 次转「人工」——
/// 一条坏数据被无限重试会一直占着每轮的额度，真正的失败反而排不上。</para>
/// </remarks>
public sealed class CompensateStockReleasesHandler
    : IRequestHandler<CompensateStockReleasesCommand, ApiResponse<CompensateStockReleasesResult>>
{
    /// <summary>单条补偿最多重试几次。</summary>
    private const int MaxRetries = 8;

    private readonly IStockRepository _stocks;
    private readonly ILogger<CompensateStockReleasesHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    /// <param name="logger">日志器。</param>
    public CompensateStockReleasesHandler(IStockRepository stocks, ILogger<CompensateStockReleasesHandler> logger)
    {
        _stocks = stocks;
        _logger = logger;
    }

    /// <summary>执行重试。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本轮处理统计。</returns>
    public async Task<ApiResponse<CompensateStockReleasesResult>> Handle(
        CompensateStockReleasesCommand request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await _stocks.GetDuePendingReleasesAsync(now, request.Limit, ct).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return ApiResults.Ok(new CompensateStockReleasesResult(0, 0, 0, 0), "没有待补偿的释放");
        }

        var succeeded = 0;
        var failed = 0;
        var gaveUp = 0;

        foreach (var pending in due)
        {
            var attempt = pending.RetryCount + 1;
            var retryBizNo = $"{pending.BizNo}#R{attempt}";
            var operation = new StockOperation(
                pending.SkuId, StockActions.Release, pending.Quantity, retryBizNo,
                $"补偿释放（原单号 {pending.BizNo}）", pending.PlatformId, pending.MerchantId);

            try
            {
                var outcome = await _stocks.ApplyAsync(operation, 0, "system", ct).ConfigureAwait(false);
                if (outcome.Succeeded)
                {
                    pending.Status = PendingReleaseStatus.Done;
                    pending.RetryCount = attempt;
                    pending.LastError = string.Empty;
                    succeeded++;
                    _logger.LogInformation("库存释放补偿成功：{SkuId} × {Qty}，原单号 {BizNo}",
                        pending.SkuId, pending.Quantity, pending.BizNo);
                }
                else
                {
                    failed++;
                    if (MarkFailed(pending, attempt, outcome.Error, now)) gaveUp++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                if (MarkFailed(pending, attempt, ex.Message, now)) gaveUp++;
            }

            await _stocks.UpdatePendingReleaseAsync(pending, ct).ConfigureAwait(false);
        }

        return ApiResults.Ok(new CompensateStockReleasesResult(due.Count, succeeded, failed, gaveUp),
            $"扫描 {due.Count} 条，成功 {succeeded}，失败 {failed}，转人工 {gaveUp}");
    }

    /// <summary>标记一次失败：退避重排，超过上限转人工。</summary>
    /// <param name="pending">补偿记录。</param>
    /// <param name="attempt">本次是第几次尝试。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>true 表示已转人工，不再重试。</returns>
    private bool MarkFailed(PendingStockRelease pending, int attempt, string error, DateTime now)
    {
        pending.RetryCount = attempt;
        pending.LastError = error.Length > 500 ? error[..500] : error;

        if (attempt >= MaxRetries)
        {
            pending.Status = PendingReleaseStatus.Failed;
            pending.NextRetryAt = now;
            _logger.LogError("库存释放补偿重试 {Attempt} 次仍失败，转人工：{SkuId} × {Qty}，{Error}",
                attempt, pending.SkuId, pending.Quantity, pending.LastError);
            return true;
        }

        // 指数退避：1min / 2min / 4min …，最多约 2 小时
        var delaySeconds = Math.Min(60 * (int)Math.Pow(2, attempt - 1), 7200);
        pending.NextRetryAt = now.AddSeconds(delaySeconds);
        return false;
    }
}
