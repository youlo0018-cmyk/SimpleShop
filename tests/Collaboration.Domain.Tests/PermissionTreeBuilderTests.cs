using PermissionService.Application.Services;
using PermissionEntity = PermissionService.Domain.Entities.Permission;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>权限树构建的单元测试。纯函数，无需数据库。</summary>
public class PermissionTreeBuilderTests
{
    private static PermissionEntity P(long id, string name, long parent, int level, string code = "", int sort = 0)
        => new() { Id = id, Name = name, ParentId = parent, Level = level, Code = code, ApiPath = "/x", SortOrder = sort };

    [Fact]
    public void Build_空输入_仍返回虚拟根节点()
    {
        var root = Assert.Single(PermissionTreeBuilder.Build(Array.Empty<PermissionEntity>()));
        Assert.Equal("全部权限", root.Name);
        Assert.Equal("0", root.Id);
        Assert.Equal(0, root.Level);
        Assert.Empty(root.Children);
        Assert.True(root.Selectable);
    }

    [Fact]
    public void Build_四层结构_层级与父子关系正确()
    {
        var list = new List<PermissionEntity>
        {
            P(1, "系统管理", 0, 1),
            P(2, "账号", 1, 2),
            P(3, "账号列表", 2, 3, "user:read"),
        };
        var root = Assert.Single(PermissionTreeBuilder.Build(list));
        var group = Assert.Single(root.Children);
        var module = Assert.Single(group.Children);
        var leaf = Assert.Single(module.Children);
        Assert.Equal("系统管理", group.Name);
        Assert.Equal("账号", module.Name);
        Assert.Equal("账号列表", leaf.Name);
        Assert.Equal("user:read", leaf.Code);
    }

    [Fact]
    public void Build_容器无叶子则不可选()
    {
        var list = new List<PermissionEntity>
        {
            P(1, "系统管理", 0, 1),
            P(2, "账号", 1, 2),
            P(3, "账号列表", 2, 3, "user:read"),
            P(4, "品牌", 1, 2),
        };
        var root = Assert.Single(PermissionTreeBuilder.Build(list));
        var group = root.Children.Single(x => x.Name == "系统管理");
        Assert.True(group.Selectable);
        Assert.True(group.Children.Single(x => x.Name == "账号").Selectable);
        // 品牌模块无独立权限点（复用 product:*），必须不可选，
        // 否则前端会出现「勾了却什么都没选」的困惑
        Assert.False(group.Children.Single(x => x.Name == "品牌").Selectable);
    }

    [Fact]
    public void Build_乱序输入_按SortOrder再按Id稳定排序()
    {
        var list = new List<PermissionEntity>
        {
            P(100, "系统管理", 0, 1),
            P(3, "第三个", 100, 2, sort: 3),
            P(1, "第一个", 100, 2, sort: 1),
            P(2, "第二个", 100, 2, sort: 2),
        };
        var root = Assert.Single(PermissionTreeBuilder.Build(list));
        var group = Assert.Single(root.Children);
        Assert.Equal(new[] { "第一个", "第二个", "第三个" }, group.Children.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Build_同一SortOrder_按Id兜底保证顺序确定()
    {
        var list = new List<PermissionEntity>
        {
            P(100, "系统管理", 0, 1),
            P(20, "B", 100, 2, sort: 1),
            P(10, "A", 100, 2, sort: 1),
        };
        var root = Assert.Single(PermissionTreeBuilder.Build(list));
        var group = Assert.Single(root.Children);
        Assert.Equal(new[] { "A", "B" }, group.Children.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Build_容器节点Code为null_前端不会显示编码()
    {
        var list = new List<PermissionEntity> { P(1, "系统管理", 0, 1) };
        var root = Assert.Single(PermissionTreeBuilder.Build(list));
        Assert.Null(root.Code);
        Assert.Null(root.Children[0].Code);
    }

    [Fact]
    public void Build_空集合抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => PermissionTreeBuilder.Build(null!));
    }
}

