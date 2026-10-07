# SimpleShop 运行时数据恢复说明（压缩包内随附）

这个压缩包是从开发机导出的**运行时数据**（不含源码、不含中间件镜像），
用于让下一台机器**不用重新初始化**就能以完全相同的状态启动。

## 包含什么

| 路径 | 内容 | 为什么必须单独拷 |
|---|---|---|
| `deploy/data/postgres/` | PostgreSQL 全部业务库（含权限树、角色、AgileConfig 的配置库） | 数据库文件不在 git 里 |
| `deploy/data/redis/` | Redis AOF（会话 / 锁 / 雪花 workerId 租约的历史） | 同上 |
| `deploy/data/agileconfig/` | AgileConfig 本地卷（配置正文在 PG，这里是运行数据） | 同上 |
| `deploy/data/elasticsearch/` | ES 索引（商品搜索 + 日志） | 同上 |
| `deploy/data/rabbitmq/`、`fluentd/` | 消息队列与日志缓冲的本地卷 | 同上 |
| `deploy/.env` | AgileConfig 管理台凭据 + 服务间内部令牌 | 被 gitignore，**不入库** |
| `deploy/keys/` | 自提取货码的 **RSA 私钥** | 绝不入库；丢了历史取货码全部失效 |
| `deploy/certs/` | JWT 自签证书（如有） | 同上 |
| `uploads/` | 上传的商品图 / 富文本图片 | 运行时产物，不入库 |

## 怎么用（Linux Mint）

```bash
# 1. 先拿源码（私有仓库，需要先登录 GitHub）
git clone https://github.com/youlo0018-cmyk/SimpleShop.git
cd SimpleShop

# 2. 把本压缩包解压到**仓库根目录**（覆盖）
tar -xzf simpleshop-runtime-data-*.tar.gz -C .

# 3. 按 deploy/linux/README.md 做「首次准备」（docker 组 / vm.max_map_count / swap），然后：
cd deploy/linux
chmod +x ./*.sh
./build.sh --fresh     # 首次必须 fresh：前端原生依赖（esbuild 等）按平台分发
./start-all.sh         # minimal 档；需要搜索/日志面板时 ./start-all.sh full

# 4. 验证
./status.sh            # 17 个服务 + 2 个前端全绿
# 浏览器打开 http://127.0.0.1:5173，账号 codexadmin / Admin123456
```

## 注意

- 数据卷是 **PostgreSQL 16 / Redis 7.2 / Elasticsearch 8.15** 的磁盘格式，
  只能给**同版本镜像**使用（`docker-compose.yml` 里已钉住版本，不要改大版本）。
- 覆盖前先确保目标机器上的容器已停止（`./stop-all.sh`），运行中覆盖数据目录会损坏数据。
- 压缩包内含**凭据与私钥**，不要上传到任何公开位置；通过 U 盘 / 内网传输即可。
- 如果只是想快速看效果、不在乎历史数据，也可以跳过本压缩包，
  按 `deploy/linux/README.md` 的「场景 B：全新初始化」从零初始化。
