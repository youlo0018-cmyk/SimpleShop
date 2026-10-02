using Xunit;

// SnowflakeId 与 TenantContextHolder 都是进程级单例，xUnit 默认并行跑不同测试类，
// 会让「未配置应抛异常」这类断言被别的类先 Configure 过而失效。
// 这个项目目前用例不多，整体串行比引入集合划分更简单可靠。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

