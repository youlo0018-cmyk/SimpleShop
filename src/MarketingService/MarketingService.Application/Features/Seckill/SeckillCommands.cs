using Collaboration.Domain.Common;
using FluentValidation;
using MarketingService.Domain.Entities;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Seckill;

/// <summary>新建秒杀场次。</summary>
/// <param name="SessionName">场次名，2-128 字符。</param>
/// <param name="StartTime">开始时间（UTC）。</param>
/// <param name="EndTime">结束时间（UTC）。</param>
/// <param name="SortOrder">排序。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id，0 表示平台自营。</param>
public record CreateSessionCommand(
    string SessionName,
    DateTime StartTime,
    DateTime EndTime,
    int SortOrder = 0,
    long PlatformId = 0,
    long MerchantId = 0) : IRequest<ApiResponse<long>>;

/// <summary>编辑秒杀场次（<b>不含库存划转</b>，划转由「发布」动作单独触发）。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="SessionName">场次名。</param>
/// <param name="StartTime">开始时间（UTC）。</param>
/// <param name="EndTime">结束时间（UTC）。</param>
/// <param name="SortOrder">排序。</param>
public record UpdateSessionCommand(
    long SessionId, string SessionName, DateTime StartTime, DateTime EndTime, int SortOrder = 0)
    : IRequest<ApiResponse>;

/// <summary>场次分页（后台）。</summary>
/// <param name="Status">场次状态，0 表示不限。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QuerySessionsCommand(int Status = 0, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<SeckillSessionPage>>;

/// <summary>发布场次：<b>把库存划出到秒杀池子</b>，并置为进行中。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="Force">场次已经处于进行中时是否强制重发。<b>默认不允许</b>——重复划转会扣两遍常规库存。</param>
public record PublishSessionCommand(long SessionId, bool Force = false) : IRequest<ApiResponse<SeckillPublishResult>>;

/// <summary>结束 / 中止场次：<b>剩余库存立即回补常规库存</b>。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="Cancel">true 表示手动中止（40），false 表示正常结束（30）。</param>
public record FinishSessionCommand(long SessionId, bool Cancel = false) : IRequest<ApiResponse<SeckillFinishResult>>;

/// <summary>场次内新增商品。<b>只能在场次发布（库存已划出）之前添加。</b></summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SeckillPrice">秒杀价。</param>
/// <param name="SeckillStock">要划出的库存量。</param>
/// <param name="PerUserLimit">每人限购，1 起。</param>
/// <param name="SortOrder">排序。</param>
public record AddSessionItemCommand(
    long SessionId,
    long SkuId,
    decimal SeckillPrice,
    int SeckillStock,
    int PerUserLimit = 1,
    int SortOrder = 0) : IRequest<ApiResponse<long>>;

/// <summary>场次商品分页（后台）。</summary>
/// <param name="SessionId">场次 Id。</param>
public record QuerySessionItemsCommand(long SessionId) : IRequest<ApiResponse<List<SessionItemDto>>>;

/// <summary>删除场次商品（只能在场次发布前删除）。</summary>
/// <param name="ItemId">商品 Id。</param>
public record DeleteSessionItemCommand(long ItemId) : IRequest<ApiResponse>;

/// <summary>前台：当前可抢购的场次与商品。</summary>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
/// <param name="SessionId">指定场次，0 表示取当前进行中的场次。</param>
public record QueryPublicSessionsCommand(long PlatformId = 0, long SessionId = 0)
    : IRequest<ApiResponse<List<PublicSessionDto>>>;


/// <summary>发布结果。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="ItemCount">划出的商品数。</param>
/// <param name="ReservedTotal">从常规库存划出的总件数。</param>
/// <param name="FailedSkus">划出失败的 SKU（库存不足等）。</param>
public sealed record SeckillPublishResult(
    long SessionId, int ItemCount, int ReservedTotal, IReadOnlyList<string> FailedSkus);

/// <summary>结束 / 中止结果。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="ReleasedTotal">回补常规库存的总件数。</param>
/// <param name="FailedSkus">回补失败的 SKU。</param>
/// <param name="Status">结束后的场次状态。</param>
public sealed record SeckillFinishResult(
    long SessionId, int ReleasedTotal, IReadOnlyList<string> FailedSkus, int Status);

/// <summary>场次分页结果。</summary>
/// <param name="Items">当页场次。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record SeckillSessionPage(
    IReadOnlyList<SessionDto> Items, long Total, int Page, int PageSize);

/// <summary>场次列表项（后台）。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="SessionName">场次名。</param>
/// <param name="StartTime">开始时间。</param>
/// <param name="EndTime">结束时间。</param>
/// <param name="Status">场次状态。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="StockTransferred">库存是否已划出。</param>
/// <param name="ItemCount">场次商品数。</param>
public sealed record SessionDto(
    string SessionId, string SessionName, string StartTime, string EndTime,
    int Status, string StatusName, bool StockTransferred, int ItemCount);

/// <summary>场次商品（后台）。</summary>
/// <param name="ItemId">商品 Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名。</param>
/// <param name="SkuSpecText">规格文本。</param>
/// <param name="SeckillPrice">秒杀价。</param>
/// <param name="SeckillStock">划出库存。</param>
/// <param name="SoldCount">已抢数量。</param>
/// <param name="Remaining">剩余。</param>
/// <param name="PerUserLimit">每人限购。</param>
/// <param name="Status">状态。</param>
public sealed record SessionItemDto(
    string ItemId, string SkuId, string ProductName, string SkuSpecText,
    decimal SeckillPrice, int SeckillStock, int SoldCount, int Remaining,
    int PerUserLimit, int Status);

/// <summary>前台场次。</summary>
/// <param name="SessionId">场次 Id。</param>
/// <param name="SessionName">场次名。</param>
/// <param name="StartTime">开始时间（ISO 8601 UTC）。</param>
/// <param name="EndTime">结束时间。</param>
/// <param name="Status">场次状态。</param>
/// <param name="SecondsToStart">距离开场还有多少秒；已开场为 0。前端直接拿它做倒计时。</param>
/// <param name="SecondsToEnd">距离结束还有多少秒。</param>
/// <param name="Items">场次商品。</param>
public sealed record PublicSessionDto(
    string SessionId, string SessionName, string StartTime, string EndTime,
    int Status, long SecondsToStart, long SecondsToEnd, IReadOnlyList<PublicSeckillItemDto> Items);

/// <summary>前台秒杀商品。</summary>
/// <param name="ItemId">商品 Id。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="ProductName">商品名。</param>
/// <param name="SkuSpecText">规格文本。</param>
/// <param name="Image">商品图。</param>
/// <param name="OriginalPrice">划线原价。</param>
/// <param name="SeckillPrice">秒杀价大字。</param>
/// <param name="Remaining">剩余库存。</param>
/// <param name="PerUserLimit">每人限购。</param>
public sealed record PublicSeckillItemDto(
    string ItemId, string SpuId, string SkuId, string ProductName, string SkuSpecText,
    string Image, decimal OriginalPrice, decimal SeckillPrice, int Remaining, int PerUserLimit);


/// <summary>秒杀命令的校验器注册。</summary>
public static class SeckillValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddSeckillValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateSessionCommand>, CreateSessionValidator>();
        services.AddScoped<IValidator<UpdateSessionCommand>, UpdateSessionValidator>();
        services.AddScoped<IValidator<QuerySessionsCommand>, QuerySessionsValidator>();
        services.AddScoped<IValidator<PublishSessionCommand>, PublishSessionValidator>();
        services.AddScoped<IValidator<FinishSessionCommand>, FinishSessionValidator>();
        services.AddScoped<IValidator<AddSessionItemCommand>, AddSessionItemValidator>();
        services.AddScoped<IValidator<QuerySessionItemsCommand>, QuerySessionItemsValidator>();
        services.AddScoped<IValidator<DeleteSessionItemCommand>, DeleteSessionItemValidator>();
        services.AddScoped<IValidator<QueryPublicSessionsCommand>, QueryPublicSessionsValidator>();
        services.AddScoped<IValidator<GrabSeckillCommand>, GrabSeckillValidator>();
        services.AddScoped<IValidator<QueryGrabResultCommand>, QueryGrabResultValidator>();
    }

    /// <summary>发布场次校验。</summary>
    private sealed class PublishSessionValidator : AbstractValidator<PublishSessionCommand>
    {
        /// <summary>构造校验器。</summary>
        public PublishSessionValidator()
        {
            RuleFor(x => x.SessionId).GreaterThan(0).WithMessage("场次 Id 必须为正数");
        }
    }

    /// <summary>场次商品查询校验。</summary>
    private sealed class QuerySessionItemsValidator : AbstractValidator<QuerySessionItemsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QuerySessionItemsValidator()
        {
            RuleFor(x => x.SessionId).GreaterThan(0).WithMessage("场次 Id 必须为正数");
        }
    }

    /// <summary>删除场次商品校验。</summary>
    private sealed class DeleteSessionItemValidator : AbstractValidator<DeleteSessionItemCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteSessionItemValidator()
        {
            RuleFor(x => x.ItemId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        }
    }

    /// <summary>新建场次校验。</summary>
    private sealed class CreateSessionValidator : AbstractValidator<CreateSessionCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateSessionValidator()
        {
            RuleFor(x => x.SessionName).NotEmpty().MinimumLength(2).MaximumLength(128)
                .WithMessage("场次名 2-128 个字符");
            RuleFor(x => x.StartTime).NotEqual(default(DateTime)).WithMessage("请填写场次开始时间");
            RuleFor(x => x.EndTime).NotEqual(default(DateTime)).WithMessage("请填写场次结束时间");
            RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("场次结束时间必须晚于开始时间");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户 Id 不能为负数");
        }
    }

    /// <summary>编辑场次校验。</summary>
    private sealed class UpdateSessionValidator : AbstractValidator<UpdateSessionCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateSessionValidator()
        {
            RuleFor(x => x.SessionId).GreaterThan(0).WithMessage("场次 Id 必须为正数");
            RuleFor(x => x.SessionName).NotEmpty().MinimumLength(2).MaximumLength(128)
                .WithMessage("场次名 2-128 个字符");
            RuleFor(x => x.StartTime).NotEqual(default(DateTime)).WithMessage("请填写场次开始时间");
            RuleFor(x => x.EndTime).NotEqual(default(DateTime)).WithMessage("请填写场次结束时间");
            RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("场次结束时间必须晚于开始时间");
        }
    }

    /// <summary>场次分页校验。</summary>
    private sealed class QuerySessionsValidator : AbstractValidator<QuerySessionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QuerySessionsValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数必须在 1 ~ 100 之间");
            // 0 = 不限（命令自身的约定），必须放行。
            // 早先写成 `a is >= NotStarted and <= Cancelled`，结果「查全部」的请求直接被自己的校验器拒了——
            // 而 List 最常用的就是不带状态查全部
            RuleFor(x => x.Status).Must(a => a == 0
                                            || (a >= SeckillSessionStatuses.NotStarted && a <= SeckillSessionStatuses.Cancelled))
                .WithMessage("场次状态不正确");
        }
    }

    /// <summary>结束 / 中止场次校验。</summary>
    private sealed class FinishSessionValidator : AbstractValidator<FinishSessionCommand>
    {
        /// <summary>构造校验器。</summary>
        public FinishSessionValidator()
        {
            RuleFor(x => x.SessionId).GreaterThan(0).WithMessage("场次 Id 必须为正数");
        }
    }

    /// <summary>新增场次商品校验。</summary>
    private sealed class AddSessionItemValidator : AbstractValidator<AddSessionItemCommand>
    {
        /// <summary>构造校验器。</summary>
        public AddSessionItemValidator()
        {
            RuleFor(x => x.SessionId).GreaterThan(0).WithMessage("场次 Id 必须为正数");
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
            RuleFor(x => x.SeckillPrice).InclusiveBetween(0.01m, 9_999_999.99m)
                .WithMessage("秒杀价必须大于 0");
            RuleFor(x => x.SeckillStock).InclusiveBetween(1, 100_000)
                .WithMessage("秒杀库存必须在 1 ~ 100000 之间");
            RuleFor(x => x.PerUserLimit).InclusiveBetween(1, 100)
                .WithMessage("每人限购必须在 1 ~ 100 之间");
        }
    }

    /// <summary>前台场次查询校验。</summary>
    private sealed class QueryPublicSessionsValidator : AbstractValidator<QueryPublicSessionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPublicSessionsValidator()
        {
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台 Id 不正确");
            RuleFor(x => x.SessionId).GreaterThanOrEqualTo(0).WithMessage("场次 Id 不正确");
        }
    }

    /// <summary>抢购校验。</summary>
    private sealed class GrabSeckillValidator : AbstractValidator<GrabSeckillCommand>
    {
        /// <summary>构造校验器。</summary>
        public GrabSeckillValidator()
        {
            // 游客不能抢：限购额度要挂在人身上，游客没有身份
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.ItemId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        }
    }

    /// <summary>轮询抢购结果校验。</summary>
    private sealed class QueryGrabResultValidator : AbstractValidator<QueryGrabResultCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryGrabResultValidator()
        {
            RuleFor(x => x.RequestId).NotEmpty().MaximumLength(64).WithMessage("请求 Id 不正确");
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
        }
    }
}
