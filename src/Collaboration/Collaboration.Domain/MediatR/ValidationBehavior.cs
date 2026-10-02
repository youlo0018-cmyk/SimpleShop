using FluentValidation;
using MediatR;

namespace Collaboration.Domain.MediatR;

/// <summary>MediatR 管道校验器：发送前跑 FluentValidation，失败抛 ValidationException。</summary>
/// <remarks>
/// 链路位置：每个服务的 AddAppServices 里注册一次，所有 Command 自动生效。
/// 失败由全局异常中间件转成 400 + { code, message, errors }（CODING_STANDARD 3.3）。
/// 每个写入口与列表查询都必须有对应 Validator，缺 Validator 视为未完成。
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    /// <summary>构造校验器集合。</summary>
    /// <param name="validators">该请求对应的全部 Validator，可为空集合。</param>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;

    /// <summary>执行校验后放行。</summary>
    /// <param name="request">待发送请求。</param>
    /// <param name="next">下一段管道。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下一段的执行结果。</returns>
    /// <exception cref="ValidationException">任一规则不通过时抛出。</exception>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var validators = _validators as IValidator<TRequest>[] ?? _validators.ToArray();
        if (validators.Length == 0) return await next();

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToArray();

        if (failures.Length > 0) throw new ValidationException(failures);
        return await next();
    }
}

