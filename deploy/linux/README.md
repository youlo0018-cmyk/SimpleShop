# SimpleShop Linux 启动方案（低内存档）

面向设备：**Linux Mint / Ubuntu，16G 内存（实际可用 ~13.5G）**。
目标：**先保证起得来，再谈起得快** —— 峰值内存优先、按档位裁剪、并发放到最后。

## 1. 三个档位

| 档位 | 启动内容 | 估算内存 | 什么时候用 |
|---|---|---|---|
| `minimal`（默认） | PostgreSQL + Redis + AgileConfig；15 个微服务（不含 LogService）；2 个前端 | **~2.4G** | 日常开发。事件总线、商品搜索、日志面板全部**自动降级**，业务功能可用 |
| `standard` | minimal + RabbitMQ | ~2.6G | 需要事件链路（日志事件、消息重放调试）时 |
| `full` | standard + Consul + Elasticsearch + Kibana + Fluentd + LogService | **~4.2G** | 需要商品搜索 / EFK 日志 / 死信重放时 |

> 加桌面环境（Cinnamon ~1.2-1.8G）后：minimal ≈ 4G、full ≈ 6G —— 13.5G 有充足余量。
> 预算来源：本机实测（ES 1.9G / Kibana 675M / 服务 ~140M×16）+ 覆盖层参数换算，见第 4 节。

**为什么默认 minimal 就够了**：服务对可选中间件是「降级」设计，不是「硬依赖」——

| 组件 | 缺失时的行为 | 依据 |
|---|---|---|
| RabbitMQ | 事件发布走空实现（丢弃），主流程不受影响 | `EventBusRegistration` 未配置时用空实现；`EventBus` 懒连接 |
| Elasticsearch | 商品搜索降级为数据库浏览；索引初始化失败不阻止启动 | `ProductService/Program.cs` 显式注释 |
| Consul | 当前网关用静态 `DownstreamHostAndPorts`，不依赖服务发现 | `GatewayHealthCheck` 注释 |
| LogService | 整个服务就是写 ES 的，没有 ES 它没有意义 —— 所以 minimal 档**不启动它** | `LogService/ServiceExtensions` |

## 2. 首次准备（一次性）

```bash
# ① 基础依赖
sudo apt install -y curl
# Docker Engine + compose v2（不要装 snap 版）
#   https://docs.docker.com/engine/install/ubuntu/
# .NET 10 SDK（构建用；只跑不建可只装运行时）
#   https://dotnet.microsoft.com/download
# Node.js 20+
#   https://nodejs.org/

# ② 把当前用户加入 docker 组（重登生效）
sudo usermod -aG docker "$USER"

# ③ ES 需要的内核参数（只有 full 档用得上；写入后永久生效）
echo 'vm.max_map_count=262144' | sudo tee /etc/sysctl.d/99-simpleshop-es.conf
sudo sysctl --system

# ④ 建议 swap ≥ 4G（Mint 安装时默认有；没有就补一个 swapfile）
free -h   # Swap 行不为 0 即可

# ⑤ 构建（后端 + 两个前端）
cd deploy/linux
chmod +x ./*.sh        # 首次 clone 后给脚本加执行位（git 不跨平台保存它）
./build.sh
```

### 2.1 首次初始化（全新设备）

**场景 A：从现有设备迁数据卷**（推荐，最省事）

把 `deploy/data/`（postgres / redis / agileconfig 的数据卷）整体拷到新设备同路径。
库、表、权限树、内置角色、配置中心内容都在里面，**直接跳到第 3 节**。

> **从 Windows 拷贝整个仓库的注意**：两个前端目录的 `node_modules` 不能跨平台复用
> （esbuild 等原生二进制按平台分发），后端 `bin/obj` 也建议重建。到 Linux 后执行：
> `./build.sh --fresh`（清理前端依赖重装 + 重建后端）。

**场景 B：全新初始化**

```bash
# ① 数据库：建库 → 建表 + SQL 种子（bash，幂等可重跑）
cd deploy/linux
./start-infra.sh minimal
./init-db.sh

# ② 权限树 / 内置角色 / AgileConfig 配置（用 pwsh 跑一次；仅首次需要）
#    Mint 22 基于 Ubuntu 24.04（Mint 21 把下面的 24.04 换成 22.04）
wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/pmp.deb
sudo dpkg -i /tmp/pmp.deb && sudo apt update && sudo apt install -y powershell

cd ../../scripts
pwsh ./seed-permissions.ps1     # 77 个权限点 + 权限树
pwsh ./seed-roles.ps1           # 6 个内置角色
pwsh ./seed-agileconfig.ps1     # 16 个服务的配置（凭据读 deploy/.env，见根 README「AgileConfig 管理台」）
```

> 这 3 个种子脚本数据量大（权限点 / 角色 / 每服务配置），**不做第二份 bash 实现**以免两套漂移；
> 它们只在全新初始化时用一次，**日常启动完全不需要 pwsh**。

## 3. 日常使用

```bash
cd deploy/linux

./start-all.sh              # 一键：minimal 档全套（中间件 → 服务 → 前端）
./start-all.sh full         # 需要搜索 / 日志面板时
./status.sh                 # 看内存、容器、服务、前端
./stop-all.sh               # 全停（容器只 stop，数据都在）
./stop-all.sh --keep-infra  # 只停服务和前端

# 也可以拆开用：
./start-infra.sh minimal    # 只起中间件
./start-services.sh         # 只起服务（串行，等前一个就绪再起下一个）
./start-web.sh              # 只起前端
./start-services.sh --only Order    # 单个服务
./start-services.sh --with-log      # 额外起 LogService（先确保 ES 已起）
```

启动后：后台 `http://127.0.0.1:5173`、商城 H5 `http://127.0.0.1:5174`、网关 `http://127.0.0.1:5008`。

## 4. 低内存做了什么（逐项）

全部改动都在**覆盖层 `docker-compose.linux.yml`** 与**启动脚本的环境变量**里，
不改基础 `docker-compose.yml`、不改任何服务代码：

| 项 | 默认 | 低内存档 | 说明 |
|---|---|---|---|
| Elasticsearch 堆 | 1g | **512m** | 实测常驻 1.9G → 约 0.9G；单机开发索引量级几万文档，够用 |
| Kibana 堆 | 自动（按物理内存推算） | **384m** | Node `--max-old-space-size`；实测 675M → 约 450M |
| RabbitMQ 内存水位 | 0.4（默认比例） | **0.25** | 超过水位堵生产者，而不是把整机拖进 swap |
| Redis 上限 | 无限制 | **384M + noeviction** | 里面是会话/锁/workerId 租约，**不能用 LRU 静默淘汰**；宁可写满报错 |
| PostgreSQL | 默认 | **shared_buffers=192M** | 显式钉住，避免大内存机器上自动调大 |
| 容器内存上限 | 无 | 每个容器 `mem_limit` | **上限不是预留**；防单个组件吃爆整机（ES 1.6G / Kibana 900M / MQ 640M…） |
| .NET GC | Server GC（按核数开堆） | **工作站 GC**（`DOTNET_gcServer=0`） | 每服务常驻从 ~140M 降到 ~80-110M，16 个服务省约 0.8G |
| 启动方式 | — | **串行**（一个就绪再起下一个） | 16 个进程同时 JIT/建连的瞬时峰值是最大杀手 |
| LogService | 随全量启动 | **默认不启** | 它强依赖 ES，属 full 档 |

## 5. 排障

| 症状 | 原因与处理 |
|---|---|
| ES 容器反复重启 | `vm.max_map_count` 没设置（第 2 节 ③）；`docker logs simpleshop-elasticsearch` 会看到 `max virtual memory areas` 报错 |
| 服务启动报「配置中心不可达」 | AgileConfig 没起：`./start-infra.sh minimal`；确认 `curl http://127.0.0.1:5000/` 有响应 |
| 服务启动报缺配置键 | AgileConfig 里对应 AppId 的配置被改坏：`deploy/.env` 里有管理台凭据（README 根目录「AgileConfig 管理台」小节） |
| 端口被占（`address already in use`） | `./stop-all.sh` 会按端口清理；顽固进程用 `ss -ltnp "sport = :<port>"` 找到 PID 再 kill |
| 内存告急 / 系统卡死 | 降档：`./stop-all.sh` 后只跑 `./start-all.sh minimal`；关掉浏览器/IDE；确认 swap 可用 |
| 前端白屏 | 必须 build + preview（路径含 `#` 时 dev-server 白屏）；`./build.sh --web` 后重试 |
| 登录接口 502 | 检查对应服务：`./status.sh`，再看 `logs/runtime/<服务名>.log` |

## 6. 与 Windows 脚本的关系

| 平台 | 脚本位置 | 说明 |
|---|---|---|
| Windows | `scripts/*.ps1` | 原有流程不变 |
| Linux | `deploy/linux/*.sh` | 本目录；用 bash 而不是 pwsh —— Linux 原生零依赖，装机即用 |

两套脚本共用同一份 `scripts/service-registry.json`（端口表只维护一处）与同一份数据库初始化 SQL。
