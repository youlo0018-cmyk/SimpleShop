using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace LogService.Application;

/// <summary>三个日志查询命令共有的字段。</summary>
/// <remarks>
/// 用接口而不是让三个命令互相继承：继承会让「页面访问日志请求里带上 operatorId」
/// 这类不属于它的字段悄悄混进来。代价只是这里要多写一遍属性名。
/// </remarks>
public interface ILogQueryFields
{
    /// <summary>页码，从 1 起。</summary>
    int Page { get; }

    /// <summary>每页条数。</summary>
    int PageSize { get; }

    /// <summary>响应码下界，0 表示不过滤。</summary>
    int MinStatusCode { get; }

    /// <summary>开始时间 UTC（含），null 表示不限。</summary>
    DateTime? From { get; }

    /// <summary>结束时间 UTC（含），null 表示不限。</summary>
    DateTime? To { get; }

    /// <summary>服务名，空表示不过滤。</summary>
    string? Service { get; }
}

/// <summary>日志查询与重放命令的校验器注册。</summary>
/// <remarks>
/// 沿用全项目约定：校验器写成嵌套静态类里的私有类，代价是
/// <c>AddValidatorsFromAssembly</c> **扫不到**，必须在 <see cref="AddLogValidators"/> 里逐条注册。
/// 只写不注册等于完全没有校验。
/// </remarks>
public static class LogValidators
{
    /// <summary>每页条数上限。</summary>
    /// <remarks>
    /// 200 是权衡出来的：日志查询没有唯一键支撑，页数一大 ES 的 from/size 就开始退化，
    /// 而后台翻日志本来也很少一次要几百条。
    /// </remarks>
    private const int MaxPageSize = 200;

    /// <summary>注册全部日志校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddLogValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryPvLogsCommand>, QueryPvLogsValidator>();
        services.AddScoped<IValidator<QueryOperationLogsCommand>, QueryOperationLogsValidator>();
        services.AddScoped<IValidator<QueryExceptionLogsCommand>, QueryExceptionLogsValidator>();
        services.AddScoped<IValidator<GetExceptionStackCommand>, GetExceptionStackValidator>();
        services.AddScoped<IValidator<QueryDeadLettersCommand>, QueryDeadLettersValidator>();
        services.AddScoped<IValidator<ReplayDeadLetterCommand>, ReplayDeadLetterValidator>();
    }

    /// <summary>日志查询的公共规则。</summary>
    /// <remarks>
    /// 抽成基类是因为三条查询的约束完全一致；
    /// 写三遍的话，改了页数上限只改了两处，第三处就是个能拉到 10 万条的漏洞。
    /// </remarks>
    /// <typeparam name="T">命令类型。</typeparam>
    private abstract class LogQueryValidatorBase<T> : AbstractValidator<T>
        where T : ILogQueryFields
    {
        /// <summary>绑定共有字段上的规则。</summary>
        protected LogQueryValidatorBase()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.MinStatusCode).InclusiveBetween(0, 599).WithMessage("响应码不正确");
            RuleFor(x => x.Service).MaximumLength(64).WithMessage("服务名不正确");

            RuleFor(x => x.To)
                .Must((x, v) => !x.From.HasValue || !v.HasValue || v >= x.From)
                .WithMessage("结束时间不能早于开始时间");
        }
    }

    /// <summary>页面访问日志查询校验。</summary>
    private sealed class QueryPvLogsValidator : LogQueryValidatorBase<QueryPvLogsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryPvLogsValidator() { }
    }

    /// <summary>写操作日志查询校验。</summary>
    private sealed class QueryOperationLogsValidator : LogQueryValidatorBase<QueryOperationLogsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryOperationLogsValidator() { }
    }

    /// <summary>异常日志查询校验。</summary>
    private sealed class QueryExceptionLogsValidator : LogQueryValidatorBase<QueryExceptionLogsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryExceptionLogsValidator() { }
    }

    /// <summary>异常调用栈查询校验。</summary>
    private sealed class GetExceptionStackValidator : AbstractValidator<GetExceptionStackCommand>
    {
        /// <summary>构造校验器。</summary>
        public GetExceptionStackValidator()
        {
            // 这个 Id 会被拼进 ES 的文档地址，不限长就等于开了一条路径穿越的口子
            RuleFor(x => x.Id).NotEmpty().MaximumLength(64).WithMessage("日志记录不正确");
        }
    }

    /// <summary>死信列表查询校验。</summary>
    private sealed class QueryDeadLettersValidator : AbstractValidator<QueryDeadLettersCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryDeadLettersValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码不正确");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.EventType).MaximumLength(64).WithMessage("事件类型不正确");
        }
    }

    /// <summary>死信重放校验。</summary>
    private sealed class ReplayDeadLetterValidator : AbstractValidator<ReplayDeadLetterCommand>
    {
        /// <summary>构造校验器。</summary>
        public ReplayDeadLetterValidator()
        {
            RuleFor(x => x.EventId).NotEmpty().MaximumLength(64).WithMessage("死信记录不正确");
        }
    }
}
