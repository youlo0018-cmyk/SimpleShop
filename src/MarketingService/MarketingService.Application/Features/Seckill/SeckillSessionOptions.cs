using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Seckill;

/// <summary>秒杀场次下拉（DATA_SPEC 4.2）：只返回**未开始 / 进行中**的场次。</summary>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QuerySeckillSessionOptionsCommand(int Limit = 200)
    : IRequest<ApiResponse<List<SeckillSessionOption>>>;

/// <summary>秒杀场次下拉项。</summary>
/// <param name="Id">场次 Id，字符串下发。</param>
/// <param name="Name">场次名 + 时间窗，前端直接显示。</param>
/// <param name="Status">状态码。</param>
/// <param name="StatusName">状态中文名，**后端下发**（4.5）。</param>
/// <remarks>下拉项统一形状 <c>{ id, name }</c>（DATA_SPEC 4.7），按需追加字段。</remarks>
public sealed record SeckillSessionOption(
    string Id, string Name, int Status, string StatusName);

/// <summary>场次下拉的校验器注册。</summary>
public static class SeckillSessionOptionValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddSeckillSessionOptionValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QuerySeckillSessionOptionsCommand>, QuerySeckillSessionOptionsValidator>();

    /// <summary>下拉查询校验。</summary>
    private sealed class QuerySeckillSessionOptionsValidator
        : AbstractValidator<QuerySeckillSessionOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QuerySeckillSessionOptionsValidator()
            => RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
    }
}

/// <summary>秒杀场次下拉处理器。</summary>
public sealed class QuerySeckillSessionOptionsHandler
    : IRequestHandler<QuerySeckillSessionOptionsCommand, ApiResponse<List<SeckillSessionOption>>>
{
    private readonly ISeckillRepository _seckill;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    public QuerySeckillSessionOptionsHandler(ISeckillRepository seckill) => _seckill = seckill;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>未开始 + 进行中的场次下拉项。</returns>
    /// <remarks>
    /// 分两次查（状态 10 与 20）再合并：列表接口的状态筛选是**单值**，而这里要的是两个状态。
    /// 已结束（30）与已取消（40）不进下拉，往它们里面加商品加不进去，选到只会白填一次表单。
    /// 平台范围由 AOP 租户过滤注入（4.2 通用约定）。
    /// </remarks>
    public async Task<ApiResponse<List<SeckillSessionOption>>> Handle(
        QuerySeckillSessionOptionsCommand request, CancellationToken ct)
    {
        var result = new List<SeckillSessionOption>(request.Limit);

        foreach (var status in new[] { SeckillSessionStatuses.NotStarted, SeckillSessionStatuses.Running })
        {
            var (items, _) = await _seckill
                .ListSessionsAsync(status, 1, request.Limit, ct).ConfigureAwait(false);

            result.AddRange(items.Select(a => new SeckillSessionOption(
                a.Id.ToString(),
                $"{a.SessionName}（{a.StartTime:MM-dd HH:mm} ~ {a.EndTime:MM-dd HH:mm}）",
                a.Status,
                SeckillNames.StatusName(a.Status))));
        }

        var ordered = result
            .OrderByDescending(a => a.Status == SeckillSessionStatuses.Running)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Take(request.Limit)
            .ToList();

        return ApiResults.Ok(ordered);
    }
}
