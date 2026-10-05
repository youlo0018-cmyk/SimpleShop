using EvaluateService.Domain.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace EvaluateService.Application.Features.Evaluate;

/// <summary>评价命令的校验器注册。</summary>
/// <remarks>
/// 刻意写成嵌套静态类里的私有类：这是本项目统一约定，但代价是
/// <c>AddValidatorsFromAssembly</c> **扫不到**，必须在 <c>AddEvaluateValidators</c> 里逐条注册。
/// 只写不注册 = 完全没有校验。
/// </remarks>
public static class EvaluateValidators
{
    /// <summary>注册全部评价校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddEvaluateValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QuerySpuEvaluatesCommand>, QuerySpuEvaluatesValidator>();
        services.AddScoped<IValidator<QueryMyEvaluatesCommand>, QueryMyEvaluatesValidator>();
        services.AddScoped<IValidator<QueryEvaluableCommand>, QueryEvaluableValidator>();
        services.AddScoped<IValidator<PublishEvaluateCommand>, PublishEvaluateValidator>();
        services.AddScoped<IValidator<AppendEvaluateCommand>, AppendEvaluateValidator>();
        services.AddScoped<IValidator<QueryAdminEvaluatesCommand>, QueryAdminEvaluatesValidator>();
        services.AddScoped<IValidator<HideEvaluateCommand>, HideEvaluateValidator>();
        services.AddScoped<IValidator<ReplyEvaluateCommand>, ReplyEvaluateValidator>();
    }

    /// <summary>商品评价列表校验。</summary>
    private sealed class QuerySpuEvaluatesValidator : AbstractValidator<QuerySpuEvaluatesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QuerySpuEvaluatesValidator()
        {
            RuleFor(x => x.SpuId).GreaterThan(0).WithMessage("商品信息不正确");
            RuleFor(x => x.SkuId).GreaterThanOrEqualTo(0).WithMessage("规格信息不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("每页条数不正确");
        }
    }

    /// <summary>我的评价列表校验。</summary>
    private sealed class QueryMyEvaluatesValidator : AbstractValidator<QueryMyEvaluatesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryMyEvaluatesValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("每页条数不正确");
        }
    }

    /// <summary>可评价商品校验。</summary>
    private sealed class QueryEvaluableValidator : AbstractValidator<QueryEvaluableCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryEvaluableValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.OrderNo).MaximumLength(64).WithMessage("订单号不正确");
        }
    }

    /// <summary>发表首评校验。</summary>
    private sealed class PublishEvaluateValidator : AbstractValidator<PublishEvaluateCommand>
    {
        /// <summary>构造校验器。</summary>
        public PublishEvaluateValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("缺少订单号");
            RuleFor(x => x.SpuId).GreaterThan(0).WithMessage("请选择要评价的商品");
            RuleFor(x => x.StarScore).InclusiveBetween(1, 5).WithMessage("请选择 1 到 5 星");
            RuleFor(x => x.Content).MaximumLength(1000).WithMessage("评价内容最多 1000 个字符");

            // 图片数量与「文字或图至少一项」放在同一处：它们的错误都指向同一次提交，
            // 分成两个 RuleFor 会让前端拿到两条互相矛盾的提示
            RuleFor(x => x)
                .Must(x => x.Images is null || x.Images.Count <= EvaluateCalculator.MaxImages)
                .WithMessage($"最多上传 {EvaluateCalculator.MaxImages} 张图片")
                .Must(x => EvaluateCalculator.ValidateContentAndImages(x.Content, x.Images ?? [], out _))
                .WithMessage("请至少填写评价内容或上传一张图片");
        }
    }

    /// <summary>追评校验。</summary>
    private sealed class AppendEvaluateValidator : AbstractValidator<AppendEvaluateCommand>
    {
        /// <summary>构造校验器。</summary>
        public AppendEvaluateValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.EvaluateId).GreaterThan(0).WithMessage("请选择要追评的评价");
            // 0 表示不打分，1~5 表示打分；超出这个范围就是非法值
            RuleFor(x => x.StarScore).InclusiveBetween(0, 5).WithMessage("星级不正确");
            RuleFor(x => x.Content).MaximumLength(1000).WithMessage("追评内容最多 1000 个字符");

            RuleFor(x => x)
                .Must(x => x.Images is null || x.Images.Count <= EvaluateCalculator.MaxImages)
                .WithMessage($"最多上传 {EvaluateCalculator.MaxImages} 张图片")
                .Must(x => EvaluateCalculator.ValidateContentAndImages(x.Content, x.Images ?? [], out _))
                .WithMessage("请至少填写追评内容或上传一张图片");
        }
    }

    /// <summary>后台评价列表校验。</summary>
    private sealed class QueryAdminEvaluatesValidator : AbstractValidator<QueryAdminEvaluatesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminEvaluatesValidator()
        {
            RuleFor(x => x.SpuId).GreaterThanOrEqualTo(0).WithMessage("商品 Id 不正确");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户 Id 不正确");
            RuleFor(x => x.StarScore).InclusiveBetween(0, 5).WithMessage("星级不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数不正确");
        }
    }

    /// <summary>隐藏评价校验。</summary>
    private sealed class HideEvaluateValidator : AbstractValidator<HideEvaluateCommand>
    {
        /// <summary>构造校验器。</summary>
        public HideEvaluateValidator()
        {
            RuleFor(x => x.EvaluateId).GreaterThan(0).WithMessage("评价信息不正确");
            // 不再有「请先登录」这条：操作人已从请求体移除，
            // 未登录的判断改由 Handler 读租户上下文来做（返回 401 而不是 400 ——
            // 「没登录」和「参数填错」是两件事，混在参数校验里会让前端提示错方向）。

            // 隐藏时必须写原因：后台要记审计（「为什么这条被下架」），
            // 没有原因的话出问题无从追溯。取消隐藏时不需要。
            RuleFor(x => x.HiddenReason)
                .Must((cmd, reason) => !cmd.IsHidden || !string.IsNullOrWhiteSpace(reason))
                .WithMessage("隐藏评价时必须填写隐藏原因")
                .Must((cmd, reason) => !cmd.IsHidden || (reason?.Trim().Length is >= 2 and <= 200))
                .WithMessage("隐藏原因需为 2 ~ 200 个字符");
        }
    }

    /// <summary>回复评价校验。</summary>
    private sealed class ReplyEvaluateValidator : AbstractValidator<ReplyEvaluateCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReplyEvaluateValidator()
        {
            RuleFor(x => x.EvaluateId).GreaterThan(0).WithMessage("评价信息不正确");
            RuleFor(x => x.AppendId).GreaterThanOrEqualTo(0).WithMessage("追评信息不正确");
            RuleFor(x => x.ReplyType).InclusiveBetween(1, 2).WithMessage("回复主体不正确");
            RuleFor(x => x.ReplyContent).NotEmpty().WithMessage("请填写回复内容")
                .MaximumLength(500).WithMessage("回复内容最多 500 个字符")
                .Must(c => c.Trim().Length >= 2).WithMessage("回复内容至少 2 个字符");
        }
    }
}
