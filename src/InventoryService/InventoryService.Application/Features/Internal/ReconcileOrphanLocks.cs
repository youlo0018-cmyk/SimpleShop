using Collaboration.Domain.Common;
using FluentValidation;
using InventoryService.Domain.Entities;
using InventoryService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryService.Application.Features.Internal;

/// <summary>列出疑似孤儿锁定：已超期、且从未被释放 / 扣减。</summary>
/// <param name="OlderThanMinutes">锁定超过多少分钟才算候选。</param>
/// <param name="Limit">单次最多返回多少条。</param>
public record QueryOrphanLocksCommand(int OlderThanMinutes = 30, int Limit = 500) : IRequest<ApiResponse<List<OrphanLockCandidate>>>;

/// <summary>释放一批已确认无对应订单的孤儿锁定。</summary>
/// <param name="Locks">候选清单。</param>
public record ReleaseOrphanLocksCommand(IReadOnlyList<OrphanLockInput> Locks) : IRequest<ApiResponse<ReleaseOrphanLocksResult>>;

/// <summary>待释放的孤儿锁定。</summary>
/// <param name="BizNo">业务单号，格式 <c>{订单号}:{skuId}</c>。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Quantity">要释放的数量。</param>
public sealed record OrphanLockInput(string BizNo, long SkuId, int Quantity);

/// <summary>释放结果。</summary>
/// <param name="Released">成功释放条数。</param>
/// <param name="Skipped">跳过条数（已被结算过 / 库存记录不存在）。</param>
/// <param name="Failed">释放失败条数。</param>
public sealed record ReleaseOrphanLocksResult(int Released, int Skipped, int Failed);

/// <summary>孤儿锁定候选查询处理器。</summary>
/// <remarks>
/// <b>库存服务只回答「有没有被结算过」，不回答「有没有订单」。</b>
/// 后者要问订单服务——如果库存服务自己反查，两个服务就耦在一起了。
/// 所以这里只出候选，由调用方（定时任务）拿着「确实没有订单」的结论再来请求释放。
/// </remarks>
public sealed class QueryOrphanLocksHandler
    : IRequestHandler<QueryOrphanLocksCommand, ApiResponse<List<OrphanLockCandidate>>>
{
    private readonly IStockRepository _stocks;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    public QueryOrphanLocksHandler(IStockRepository stocks) => _stocks = stocks;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>候选清单。</returns>
    public async Task<ApiResponse<List<OrphanLockCandidate>>> Handle(
        QueryOrphanLocksCommand request, CancellationToken ct)
    {
        var olderThan = DateTime.UtcNow.AddMinutes(-request.OlderThanMinutes);
        var candidates = await _stocks
            .GetOrphanLockCandidatesAsync(olderThan, request.Limit, ct)
            .ConfigureAwait(false);

        return ApiResults.Ok(candidates, $"找到 {candidates.Count} 条疑似孤儿锁定");
    }
}

/// <summary>孤儿锁定释放处理器。</summary>
/// <remarks>
/// <b>释放的 bizNo 同样要加后缀</b>，理由与补偿重试完全一样：
/// 幂等键是 <c>{biz_no}:{action}</c>，用锁定时的原单号释放会命中那条 <c>lock</c> 流水？——
/// 不会，因为 action 不同（lock vs release）。<b>但反过来说，如果这个单号后来又被正式释放过一次，
/// 重复释放就会被幂等键挡住并静默跳过</b>，于是这里必须让「跳过」算成功而不是算失败。
/// </remarks>
public sealed class ReleaseOrphanLocksHandler
    : IRequestHandler<ReleaseOrphanLocksCommand, ApiResponse<ReleaseOrphanLocksResult>>
{
    private readonly IStockRepository _stocks;
    private readonly ILogger<ReleaseOrphanLocksHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="stocks">库存仓储。</param>
    /// <param name="logger">日志器。</param>
    public ReleaseOrphanLocksHandler(IStockRepository stocks, ILogger<ReleaseOrphanLocksHandler> logger)
    {
        _stocks = stocks;
        _logger = logger;
    }

    /// <summary>执行释放。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>释放统计。</returns>
    public async Task<ApiResponse<ReleaseOrphanLocksResult>> Handle(
        ReleaseOrphanLocksCommand request, CancellationToken ct)
    {
        var released = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in request.Locks)
        {
            var operation = new StockOperation(
                item.SkuId, StockActions.Release, item.Quantity,
                $"{item.BizNo}#ORPHAN", "孤儿预留对账释放");

            try
            {
                var outcome = await _stocks.ApplyAsync(operation, 0, "system", ct).ConfigureAwait(false);
                if (outcome.Succeeded)
                {
                    if (outcome.AlreadyApplied)
                    {
                        // 幂等键挡住了重复释放：这说明已经有人（或上一轮自己）处理过了，算成功
                        skipped++;
                    }
                    else
                    {
                        released++;
                        _logger.LogWarning(
                            "孤儿预留已释放：{BizNo} {SkuId} × {Qty}（锁定后始终没有对应订单）",
                            item.BizNo, item.SkuId, item.Quantity);
                    }
                }
                else
                {
                    failed++;
                    _logger.LogError("孤儿预留释放失败：{BizNo}，{Error}", item.BizNo, outcome.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "孤儿预留释放异常：{BizNo}", item.BizNo);
            }
        }

        return ApiResults.Ok(new ReleaseOrphanLocksResult(released, skipped, failed),
            $"释放 {released} 条，跳过 {skipped} 条，失败 {failed} 条");
    }
}

/// <summary>孤儿对账命令的校验器注册。</summary>
public static class ReconcileOrphanLocksValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddReconcileOrphanLocksValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryOrphanLocksCommand>, QueryOrphanLocksValidator>();
        services.AddScoped<IValidator<ReleaseOrphanLocksCommand>, ReleaseOrphanLocksValidator>();
    }

    /// <summary>候选查询校验。</summary>
    private sealed class QueryOrphanLocksValidator : AbstractValidator<QueryOrphanLocksCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryOrphanLocksValidator()
        {
            RuleFor(x => x.OlderThanMinutes).InclusiveBetween(1, 1440).WithMessage("超时阈值不正确");
            RuleFor(x => x.Limit).InclusiveBetween(1, 1000).WithMessage("单次条数不正确");
        }
    }

    /// <summary>释放命令校验。</summary>
    private sealed class ReleaseOrphanLocksValidator : AbstractValidator<ReleaseOrphanLocksCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReleaseOrphanLocksValidator()
        {
            RuleFor(x => x.Locks).NotEmpty().WithMessage("没有要释放的锁定")
                .Must(l => l.Count <= 1000).WithMessage("单次最多释放 1000 条");
            RuleForEach(x => x.Locks).ChildRules(item =>
            {
                item.RuleFor(a => a.BizNo).NotEmpty().WithMessage("业务单号不能为空");
                item.RuleFor(a => a.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
                item.RuleFor(a => a.Quantity).GreaterThan(0).WithMessage("释放数量必须大于 0");
            });
        }
    }
}
