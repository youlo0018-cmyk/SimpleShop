using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ProductService.Application.Features.Shop;

/// <summary>
/// 商品搜索索引对账（补偿任务）。
/// </summary>
/// <param name="PageSize">每批处理多少个商品，1 ~ 500。</param>
/// <param name="DeleteOrphans">是否清理「索引里有、库里已经没有」的孤儿文档。</param>
/// <remarks>
/// <para><b>为什么不「删了重建」</b>：重建期间索引是空的，那段时间里用户搜索会得到
/// **零结果**，而且越是热门商品越容易触发重建。差集对账只补该补的、删该删的，
/// 整个过程索引始终可搜。</para>
///
/// <para>存在的意义：上一轮落地搜索时，索引写失败是**只记日志不阻塞业务**的
/// （这是对的——商品保存是主链路）。代价就是索引会慢慢和库不一致。
/// 这个任务就是那条代价的兜底。</para>
/// </remarks>
public record SyncSearchIndexCommand(int PageSize = 200, bool DeleteOrphans = true)
    : IRequest<ApiResponse<SearchIndexSyncResult>>;

/// <summary>索引对账结果。</summary>
/// <param name="DbProducts">库里的商品总数。</param>
/// <param name="IndexedBefore">对账前索引里的商品数。</param>
/// <param name="IndexedAfter">对账后索引里的商品数。</param>
/// <param name="Missing">本轮补写进索引的商品数。</param>
/// <param name="OrphansRemoved">本轮从索引里清掉的孤儿文档数。</param>
/// <param name="Failed">补写失败的商品数（下一轮会再试）。</param>
public sealed record SearchIndexSyncResult(
    int DbProducts, int IndexedBefore, int IndexedAfter, int Missing, int OrphansRemoved, int Failed);

/// <summary>索引对账命令的校验器注册。</summary>
public static class SearchIndexSyncValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddSearchIndexSyncValidators(IServiceCollection services)
        => services.AddScoped<IValidator<SyncSearchIndexCommand>, SyncSearchIndexValidator>();

    /// <summary>对账参数校验。</summary>
    private sealed class SyncSearchIndexValidator : AbstractValidator<SyncSearchIndexCommand>
    {
        /// <summary>构造校验器。</summary>
        public SyncSearchIndexValidator()
        {
            RuleFor(x => x.PageSize).InclusiveBetween(1, 500)
                .WithMessage("每批处理量在 1 ~ 500 之间");
        }
    }
}