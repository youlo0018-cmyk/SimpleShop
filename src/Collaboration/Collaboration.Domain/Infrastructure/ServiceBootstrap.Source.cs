using Collaboration.Domain.Configuration;
using Microsoft.Extensions.Configuration;

namespace Collaboration.Domain.Infrastructure;

public static partial class ServiceBootstrap
{
    private static IConfigSource CreateSource(
        BootstrapOptions bootstrap,
        IConfiguration configuration,
        string environment)
    {
        if (!string.Equals(bootstrap.Source, "AgileConfig", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalFileConfigSource(configuration, environment);
        }

        if (string.IsNullOrWhiteSpace(bootstrap.AgileConfigAddress))
        {
            throw new ConfigSourceUnavailableException(
                "Bootstrap.Source 设为 AgileConfig，但没配 Bootstrap:AgileConfigAddress。");
        }

        // 显式失败而不是静默回退到本地文件：宁可服务起不来，也不要「以为连着配置中心其实没有」。
        // AgileConfig 官方 registry 当前网络不可达（PLAN.md 2.6），网络恢复后在此接入即可。
        throw new NotSupportedException(
            "AgileConfig 配置源尚未接入。请把 Bootstrap.Source 改回 LocalFile，"
            + "或在网络可达后补上 AgileConfigConfigSource 实现。");
    }

    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        int maxRetry,
        string sourceName,
        CancellationToken ct)
    {
        Exception? last = null;

        for (var i = 0; i < maxRetry; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                last = ex;
                var delaySeconds = 1 << i;
                Console.WriteLine(
                    $"[bootstrap] 配置源 {sourceName} 第 {i + 1} 次失败：{ex.Message}，{delaySeconds}s 后重试");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
        }

        throw new ConfigSourceUnavailableException(
            $"配置源 {sourceName} 重试 {maxRetry} 次仍失败，服务拒绝启动。", last!);
    }
}

