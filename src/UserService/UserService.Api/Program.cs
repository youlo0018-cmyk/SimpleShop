using System.Reflection;
using Collaboration.Domain.MediatR;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UserService.Api;
using UserService.Application.Features.User.ManageUser;
using UserService.Application.Services;
using UserService.Domain.Entities;
using UserService.Infrastructure;
using StackExchange.Redis;
using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;

var builder = WebApplication.CreateBuilder(args);

// S0~S3：先取配置再连任何依赖（DATA_SPEC 1.2）
var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "UserService", environment, new List<string> { "Consul:ServiceName" });

var overlay = new ConfigurationBuilder().AddInMemoryCollection(loaded).Build();
builder.Configuration.AddConfiguration(overlay);

// S4~S5：Redis 连通后分配雪花 workerId
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var snowflakeOptions = builder.Configuration.GetSection(SnowflakeOptions.SectionName).Get<SnowflakeOptions>()!;

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
Console.WriteLine($"[bootstrap] redis 已连接 db={redisOptions.Database}");

var workerId = await ServiceBootstrap.AllocateWorkerIdAsync(
    redis.GetDatabase(redisOptions.Database),
    snowflakeOptions.WorkerIdKeyPrefix,
    "UserService",
    snowflakeOptions.WorkerIdUpperBound);

SnowflakeId.Configure(workerId);

builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

builder.Services.AddAppFreeSql(
    builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!.Default,
    typeof(User).Assembly);

builder.Services.AddAppServices(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

builder.Services.AddAppEventLogging(builder.Configuration);

var app = builder.Build();
app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.UseAppRequestLogging();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();
