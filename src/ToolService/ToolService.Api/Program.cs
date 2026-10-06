using Collaboration.Domain.Configuration;
using Collaboration.Domain.Infrastructure;
using Collaboration.Web;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using ToolService.Api;
using ToolService.Application.Features.Upload;
using ToolService.Domain.Entities;
using ToolService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var environment = builder.Environment.EnvironmentName;
var loaded = await ServiceBootstrap.LoadConfigurationAsync(
    builder.Configuration, "ToolService", environment, new List<string> { "Consul:ServiceName" });

var overlay = new ConfigurationBuilder().AddInMemoryCollection(loaded).Build();
builder.Configuration.AddConfiguration(overlay);

var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()!;
var snowflakeOptions = builder.Configuration.GetSection(SnowflakeOptions.SectionName).Get<SnowflakeOptions>()!;

var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
var workerId = await ServiceBootstrap.AllocateWorkerIdAsync(
    redis.GetDatabase(redisOptions.Database), snowflakeOptions.WorkerIdKeyPrefix, "ToolService", snowflakeOptions.WorkerIdUpperBound);
SnowflakeId.Configure(workerId);

builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton(_ => redis.GetDatabase(redisOptions.Database));

builder.Services.AddAppFreeSql(
    builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()!.Default,
    typeof(StoredFile).Assembly);

builder.Services.AddAppServices(builder.Configuration);
builder.Services.AddAppControllers();
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

builder.Services.AddAppEventLogging(builder.Configuration);

var app = builder.Build();
app.UseAppTenantContext();
app.UseAppExceptionHandling();
app.UseAppRequestLogging();
app.MapHealthChecks("/health");
app.MapControllers();
app.Run();
