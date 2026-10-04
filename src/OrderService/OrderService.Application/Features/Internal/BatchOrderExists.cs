using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Ports;

namespace OrderService.Application.Features.Internal;

/// <summary>批量判断哪些订单号**确实不存在**（孤儿预留对账用）。</summary>
/// <param name="OrderNos">订单号集合，最多 200 个。</param>
public record BatchOrderExistsCommand(IReadOnlyList<string> OrderNos) : IRequest<ApiResponse<BatchOrderExistsResult>>;

/// <summary>批量查询结果。</summary>
/// <param name="Existing">**确实存在**的订单号。</param>
/// <param name="Missing">**不存在**的订单号——这些才是可以安全释放的孤儿锁定。</param>
public sealed record BatchOrderExistsResult(IReadOnlyList<string> Existing, IReadOnlyList<string> Missing);

/// <summary>命令校验器注册。</summary>
public static class BatchOrderExistsValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddBatchOrderExistsValidators(IServiceCollection services)
        => services.AddScoped<IValidator<BatchOrderExistsCommand>, BatchOrderExistsValidator>();

    /// <summary>校验规则。</summary>
    private sealed class BatchOrderExistsValidator : AbstractValidator<BatchOrderExistsCommand>
    {
        /// <summary>构造校验器。</summary>
        public BatchOrderExistsValidator()
        {
            RuleFor(x => x.OrderNos).NotEmpty().WithMessage("请提供订单号")
                .Must(a => a.Count <= 200).WithMessage("一次最多查 200 个订单号");
            RuleForEach(x => x.OrderNos).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
        }
    }
}

/// <summary>批量判断订单是否存在的处理器。</summary>
/// <remarks>
/// <b>为什么返回「存在 / 不存在」两个列表，而不是布尔数组</b>：调用方（定时任务）要拿
/// 「不存在的」那一批去释放库存。给布尔数组它还得自己再映射一次，而映射错了的后果是
/// <b>释放真实存在订单所占的库存</b>——直接超卖。两个列表让「要用的那个」一目了然。
/// </remarks>
public sealed class BatchOrderExistsHandler
    : IRequestHandler<BatchOrderExistsCommand, ApiResponse<BatchOrderExistsResult>>
{
    private readonly IOrderStore _store;

    /// <summary>构造处理器。</summary>
    /// <param name="store">订单存储端口。</param>
    public BatchOrderExistsHandler(IOrderStore store) => _store = store;

    /// <summary>执行查询。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存在与不存在的订单号。</returns>
    public async Task<ApiResponse<BatchOrderExistsResult>> Handle(
        BatchOrderExistsCommand request, CancellationToken ct)
    {
        var wanted = request.OrderNos
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (wanted.Count == 0)
        {
            return ApiResults.Ok(new BatchOrderExistsResult([], []), "没有要查询的订单号");
        }

        var existing = new List<string>(wanted.Count);
        foreach (var orderNo in wanted)
        {
            var order = await _store.FindByOrderNoAsync(orderNo, ct).ConfigureAwait(false);
            if (order is not null) existing.Add(orderNo);
        }

        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(a => !existingSet.Contains(a)).ToList();

        return ApiResults.Ok(new BatchOrderExistsResult(existing, missing),
            $"存在 {existing.Count} 个，不存在 {missing.Count} 个");
    }
}
