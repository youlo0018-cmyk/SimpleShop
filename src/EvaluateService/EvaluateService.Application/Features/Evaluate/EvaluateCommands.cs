using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace EvaluateService.Application.Features.Evaluate;

// ==================== C 端：商品评价列表 ====================

/// <summary>查某商品的评价列表（商品详情页）。</summary>
/// <remarks>
/// 按规格 14.1：<c>skuId</c> 不传（0）时展示整个 SPU 的全部评价，
/// 传了则只展示标记了该 SKU 的评价——「红色」的评价不该出现在只看「蓝色」的用户面前。
/// </remarks>
/// <param name="SpuId">商品 SPU Id。</param>
/// <param name="SkuId">按 SKU 过滤，0 表示不过滤。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QuerySpuEvaluatesCommand(long SpuId, long SkuId = 0, int Page = 1, int PageSize = 10)
    : IRequest<ApiResponse<EvaluatePageResult>>;

// ==================== C 端：我的评价 ====================

/// <summary>查我的评价列表。</summary>
/// <remarks>包含已被后台隐藏的评价——客户得能看到「我评了但被隐藏了」，否则只会以为评价丢了。</remarks>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryMyEvaluatesCommand(long CustomerId, int Page = 1, int PageSize = 10)
    : IRequest<ApiResponse<EvaluatePageResult>>;

// ==================== C 端：可评价商品 ====================

/// <summary>查某订单下还没评价的商品（结算后「去评价」入口）。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="OrderNo">订单号，空表示查所有已完成且未评价的商品。</param>
public record QueryEvaluableCommand(long CustomerId, string OrderNo = "")
    : IRequest<ApiResponse<List<EvaluableItemDto>>>;

// ==================== C 端：发表首评 ====================

/// <summary>发表首评（SPU 级）。</summary>
/// <remarks>
/// 只提交 SPU 与星级 / 内容 / 图，<b>SKU 标记由服务端从订单自动推导</b>——
/// 让前端传 SKU 列表的话，它可能只传一个、也可能传一堆不属于这单的 SKU，
/// 两种都会让「这条评价覆盖了哪些规格」变成前端说了算。服务端按订单算出来的才是事实。
/// </remarks>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="SpuId">商品 SPU Id。</param>
/// <param name="StarScore">星级 1~5。</param>
/// <param name="Content">评价文字，可空（配图也算一条）。</param>
/// <param name="Images">图片 URL 列表，最多 9 张。</param>
/// <param name="IsAnonymous">是否匿名。</param>
public record PublishEvaluateCommand(
    long CustomerId,
    string OrderNo,
    long SpuId,
    int StarScore,
    string Content = "",
    IReadOnlyList<string>? Images = null,
    bool IsAnonymous = false) : IRequest<ApiResponse<PublishEvaluateResult>>;

// ==================== C 端：追评 ====================

/// <summary>追评。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="EvaluateId">首评 Id。</param>
/// <param name="StarScore">追评星级，0 表示不打分。</param>
/// <param name="Content">追评内容，可空（配图也算）。</param>
/// <param name="Images">图片 URL 列表，最多 9 张。</param>
public record AppendEvaluateCommand(
    long CustomerId,
    long EvaluateId,
    int StarScore,
    string Content = "",
    IReadOnlyList<string>? Images = null) : IRequest<ApiResponse<long>>;

// ==================== 后台：列表 / 隐藏 / 回复 ====================

/// <summary>后台评价列表。</summary>
/// <param name="SpuId">SPU Id，0 表示不限。</param>
/// <param name="MerchantId">商户 Id，0 表示不限。</param>
/// <param name="StarScore">星级，0 表示不限。</param>
/// <param name="OnlyHidden">只看已隐藏。</param>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryAdminEvaluatesCommand(
    long SpuId = 0, long MerchantId = 0, int StarScore = 0,
    bool OnlyHidden = false, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<EvaluatePageResult>>;

/// <summary>隐藏 / 取消隐藏评价（后台）。</summary>
/// <param name="EvaluateId">评价 Id。</param>
/// <param name="IsHidden">是否隐藏。</param>
/// <param name="HiddenReason">隐藏原因，隐藏时必填。</param>
/// <remarks>
/// <b>刻意不接受操作人字段</b>：操作人由 Handler 从令牌租户上下文取。
/// 这里曾经有 <c>OperatorId</c> 且 Handler <b>直接采信请求体</b> ——
/// 也就是说任何登录用户都能把「谁隐藏了这条评价」这条审计记录伪造成别人。
/// 隐藏原因是写进后台审计的凭证，审计人可伪造等于审计失效。
/// </remarks>
public record HideEvaluateCommand(long EvaluateId, bool IsHidden, string HiddenReason)
    : IRequest<ApiResponse>;

/// <summary>回复评价（后台）。</summary>
/// <param name="EvaluateId">首评 Id。</param>
/// <param name="AppendId">被回复的追评 Id，0 表示回复首评。</param>
/// <param name="ReplyContent">回复内容。</param>
/// <param name="ReplyType">1 商户 / 2 平台。</param>
/// <remarks>
/// <b>刻意不接受操作人字段</b>：Handler 已改成从令牌租户上下文取，
/// 但字段留在契约里就是静默伪造入口 —— 不传时行为正确，
/// 一旦有人传了，回复记录里的「谁回的」就被改写而接口照常返回成功。
/// </remarks>
public record ReplyEvaluateCommand(
    long EvaluateId, long AppendId, string ReplyContent, int ReplyType)
    : IRequest<ApiResponse<long>>;
