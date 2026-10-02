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

        if (string.IsNullOrWhiteSpace(bootstrap.AppId) || string.IsNullOrWhiteSpace(bootstrap.AppSecret))
        {
            throw new ConfigSourceUnavailableException(
                "Bootstrap.Source 设为 AgileConfig，但没配 Bootstrap:AppId 或 Bootstrap:AppSecret。");
        }

        var baseAddress = bootstrap.AgileConfigAddress.EndsWith('/')
            ? bootstrap.AgileConfigAddress
            : bootstrap.AgileConfigAddress + "/";

        var http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };
        var configEnv = string.IsNullOrWhiteSpace(bootstrap.Env) ? environment : bootstrap.Env;
        return new AgileConfigConfigSource(http, bootstrap.AppId, bootstrap.AppSecret, configEnv);
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

