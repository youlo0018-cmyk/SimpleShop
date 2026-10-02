using Collaboration.Domain.Repository;
using FreeSql;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>CrudRepository 的写库行为测试（需要本地 PostgreSQL）。</summary>
/// <remarks>
/// 这组用例守的是一个真实的 P0 缺陷：
/// 原实现用 Db.Update&lt;T&gt;(entity) 更新，FreeSql 3.5 在雪花主键实体上生成**空 SET 子句**，
/// 一条 SQL 都不发就返回 0 —— 接口回「成功」，数据却纹丝不动。
/// 排查入口是环境变量 SIMPLESHOP_SQL_TRACE=1 打印实际 SQL（见 FreeSqlServiceCollectionExtensions）。
/// </remarks>
public class CrudRepositoryTests : IAsyncLifetime
{
    /// <summary>应用角色的连接串。CRUD 走它，顺带验证建表脚本里的 GRANT 确实生效。</summary>
    private const string ConnectionString =
        "Host=127.0.0.1;Port=5432;Database=simpleshopuser;Username=simpleshop_app;Password=simpleshop_dev_2026";

    /// <summary>
    /// 超级用户连接串，只用来建测试表。
    /// </summary>
    /// <remarks>
    /// 必须分开：simpleshop_app 在 public schema 上没有 CREATE 权限——
    /// 这是刻意的，表结构由 deploy/sql 下的脚本管理，运行期不允许应用改 schema（DATA_SPEC 2.9）。
    /// 所以建表用 postgres，CRUD 用应用角色，顺带证明 GRANT 没漏。
    /// </remarks>
    private const string AdminConnectionString =
        "Host=127.0.0.1;Port=5432;Database=simpleshopuser;Username=postgres;Password=simpleshop_dev_2026";

    private const string TableName = "crud_test_entity";

    private IFreeSql? _db;

    /// <summary>准备连接并建测试表。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            // 建表 + 授权用超级用户；应用角色没有 DDL 权限，这是设计如此
            using (var admin = new FreeSqlBuilder()
                       .UseConnectionString(DataType.PostgreSQL, AdminConnectionString)
                       .UseAutoSyncStructure(false)
                       .Build())
            {
                await admin.Ado.ExecuteNonQueryAsync(
                    "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
                    "id bigint NOT NULL PRIMARY KEY," +
                    "created_at timestamp NOT NULL," +
                    "updated_at timestamp NULL," +
                    "is_deleted boolean NOT NULL DEFAULT false," +
                    "deleted_at timestamp NULL," +
                    "user_name varchar(64) NOT NULL," +
                    "nick_name varchar(64) NOT NULL," +
                    "status int NOT NULL DEFAULT 0)");

                await admin.Ado.ExecuteNonQueryAsync(
                    "GRANT ALL PRIVILEGES ON TABLE " + TableName + " TO simpleshop_app");
                await admin.Ado.ExecuteNonQueryAsync("DELETE FROM " + TableName);
            }

            // 真正的 CRUD 走应用角色，和线上走的是同一套权限
            _db = new FreeSqlBuilder()
                .UseConnectionString(DataType.PostgreSQL, ConnectionString)
                .UseAutoSyncStructure(false)
                .Build();
        }
        catch (Exception)
        {
            // 本地没起数据库时用例直接跳过，不让整个测试套件变红
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>清理本次写下的行。</summary>
    public async Task DisposeAsync()
    {
        try { if (_db is not null) await _db.Ado.ExecuteNonQueryAsync("DELETE FROM " + TableName); }
        catch { /* 清理失败不影响断言结论 */ }
        _db?.Dispose();
    }

    /// <summary>测试专用实体。</summary>
    [FreeSql.DataAnnotations.Table(Name = TableName)]
    private sealed class TestEntity : Collaboration.Domain.Entities.EntityBase
    {
        /// <summary>登录名。</summary>
        [FreeSql.DataAnnotations.Column(Name = "user_name", StringLength = 64)]
        public string UserName { get; set; } = string.Empty;

        /// <summary>昵称。</summary>
        [FreeSql.DataAnnotations.Column(Name = "nick_name", StringLength = 64)]
        public string NickName { get; set; } = string.Empty;

        /// <summary>状态。</summary>
        [FreeSql.DataAnnotations.Column(Name = "status")]
        public int Status { get; set; }
    }

    /// <summary>测试专用仓储，直接用基类的公开方法。</summary>
    private sealed class TestRepository : CrudRepository<TestEntity>
    {
        /// <summary>构造。</summary>
        public TestRepository(IFreeSql freeSql) : base(freeSql) { }
    }

    /// <summary>造一条测试数据。</summary>
    /// <remarks>
    /// 用裸 SQL 插，不走 InsertAsync：InsertAsync 内部要 SnowflakeId.NewId()，
    /// 而 SnowflakeId 是进程级单例，由 SnowflakeIdTests 那个类负责 Configure。
    /// 本组用例要证的缺陷在 UPDATE 上，不该对单例的初始化顺序形成依赖。
    /// </remarks>
    private static async Task<(TestRepository Repo, TestEntity Row)> SeedAsync(IFreeSql db)
    {
        var repo = new TestRepository(db);
        var row = new TestEntity
        {
            Id = DateTime.UtcNow.Ticks,
            CreatedAt = DateTime.UtcNow,
            UserName = "seed_user",
            NickName = "seed_nick",
            Status = 1
        };

        await db.Insert(row).ExecuteAffrowsAsync();
        return (repo, row);
    }

    [Fact]
    public async Task UpdateAsync_ActuallyPersists_AndReportsAffectedRows()
    {
        Assert.NotNull(_db);
        var (repo, row) = await SeedAsync(_db!);

        row.NickName = "updated_nick";
        row.Status = 2;
        int affected = await repo.UpdateAsync(row);

        // 回归点：原实现这里返回 0，而且一条 SQL 都不发
        Assert.Equal(1, affected);

        var reloaded = await repo.GetByIdAsync(row.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("updated_nick", reloaded!.NickName);
        Assert.Equal(2, reloaded.Status);
    }

    [Fact]
    public async Task UpdateAsync_FillsUpdatedAt()
    {
        Assert.NotNull(_db);
        var (repo, row) = await SeedAsync(_db!);

        Assert.Null(row.UpdatedAt);
        await repo.UpdateAsync(row);

        Assert.NotNull(row.UpdatedAt);
        Assert.True((await repo.GetByIdAsync(row.Id))!.UpdatedAt is not null);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotChangePrimaryKeyOrCreatedAt()
    {
        Assert.NotNull(_db);
        var (repo, row) = await SeedAsync(_db!);
        var originalId = row.Id;

        // 先从库里读一次再改：PostgreSQL 的 timestamp 只到微秒，
        // .NET DateTime 到 100 纳秒，直接拿内存里的值比对会因为精度截断而误报。
        var before = (await repo.GetByIdAsync(originalId))!;

        row.NickName = "changed";
        await repo.UpdateAsync(row);

        var reloaded = (await repo.GetByIdAsync(originalId))!;
        Assert.Equal(originalId, reloaded.Id);
        Assert.Equal(before.CreatedAt, reloaded.CreatedAt);
        Assert.True(reloaded.UpdatedAt is not null, "UpdatedAt 应被补上");
    }

    [Fact]
    public async Task UpdateAsync_CanWriteEmptyString()
    {
        Assert.NotNull(_db);
        var (repo, row) = await SeedAsync(_db!);

        // 清空可选字段是真实业务动作（编辑资料时把备注清掉），不能被当成「没传」而跳过
        row.NickName = string.Empty;
        Assert.Equal(1, await repo.UpdateAsync(row));
        Assert.Equal(string.Empty, (await repo.GetByIdAsync(row.Id))!.NickName);
    }

    [Fact]
    public async Task UpdateAsync_NonExistentRow_ReturnsZero()
    {
        Assert.NotNull(_db);
        var repo = new TestRepository(_db!);

        var ghost = new TestEntity { Id = DateTime.UtcNow.Ticks + 12345, UserName = "ghost" };
        Assert.Equal(0, await repo.UpdateAsync(ghost));
    }

    [Fact]
    public async Task UpdateColumnsAsync_OnlyTouchesGivenColumns()
    {
        Assert.NotNull(_db);
        var (repo, row) = await SeedAsync(_db!);

        // 局部 DTO：只带 NickName，UserName 没传。
        // 回归点：原实现直接 SetDto，会把 user_name 一起写成 C# 默认值（空串），静默抹掉数据。
        var dto = new PartialDto { Id = row.Id, NickName = "only_nick" };
        int affected = await repo.UpdateColumnsAsync(row.Id, dto);

        Assert.Equal(1, affected);
        var reloaded = (await repo.GetByIdAsync(row.Id))!;
        Assert.Equal("only_nick", reloaded.NickName);
        Assert.Equal("seed_user", reloaded.UserName);
    }

    [Fact]
    public async Task UpdateColumnsAsync_NonExistentRow_ReturnsZero()
    {
        Assert.NotNull(_db);
        var repo = new TestRepository(_db!);
        var dto = new PartialDto { Id = DateTime.UtcNow.Ticks + 999, NickName = "x" };

        Assert.Equal(0, await repo.UpdateColumnsAsync(dto.Id, dto));
    }

    /// <summary>局部更新用的 DTO，只有部分字段有值。</summary>
    private sealed class PartialDto
    {
        /// <summary>主键，仅用于定位。</summary>
        public long Id { get; set; }

        /// <summary>昵称。</summary>
        public string NickName { get; set; } = string.Empty;
    }
}