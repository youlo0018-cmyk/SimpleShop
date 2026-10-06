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
        // 同一个校验器可能被注册多次：`AddValidatorsFromAssembly` 会扫到 public 的校验器，
        // 而各服务又习惯再显式 AddScoped 一次「确保不漏注册」。结果同一个校验器跑 4 遍，
        // 响应里每条错误重复 4 次 —— 前端要在 errors 里看到 4 条一模一样的中文提示。
        // 这里按**具体类型**去重：注册几次都只跑一次，显式注册的保险仍然有效。
        var validators = (_validators as IValidator<TRequest>[] ?? _validators.ToArray())
            .GroupBy(v => v.GetType())
            .Select(g => g.First())
            .ToArray();

        if (validators.Length == 0) return await next();

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct)));

        // 不同的校验器也可能对同一字段给出同一句提示（例如共用规则集 + 专用规则集），
        // 按「字段 + 文案」去重，保留不同措辞的提示。
        var failures = results
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .GroupBy(f => (f.PropertyName, f.ErrorMessage))
            .Select(g => g.First())
            .ToArray();

        if (failures.Length > 0) throw new ValidationException(failures);
        return await next();
    }
}

