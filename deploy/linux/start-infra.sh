#!/usr/bin/env bash
# 启动中间件容器（按内存档位）。
#
#   ./start-infra.sh                # 默认 minimal：只起核心三件套
#   ./start-infra.sh standard       # + RabbitMQ
#   ./start-infra.sh full           # + Consul / ES / Kibana / Fluentd
#
# 档位与内存（16G 设备实测口径，见 README.md 的预算表）：
#   minimal  ~0.4G   数据库 + 缓存 + 配置中心，业务全部可用（事件/搜索/日志降级）
#   standard ~0.6G   + 事件总线（RabbitMQ）
#   full     ~2.6G   + ES(512m 堆) / Kibana(384m 堆) / Fluentd / Consul
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

TIER="${1:-minimal}"

case "$TIER" in
  minimal)
    SERVICES=(postgres redis agileconfig)
    ;;
  standard)
    SERVICES=(postgres redis agileconfig rabbitmq)
    ;;
  full)
    SERVICES=(postgres redis agileconfig rabbitmq consul elasticsearch kibana fluentd)
    ;;
  *)
    fail "未知档位：$TIER（可选：minimal / standard / full）"
    exit 1
    ;;
esac

require_docker

if [ "$TIER" = "full" ]; then
  ensure_es_sysctl || {
    warn "先修好 vm.max_map_count 再起 full 档；本次仍继续（ES 可能起不来）。"
  }
fi

info "启动中间件（档位 $TIER：${SERVICES[*]}）"
compose up -d "${SERVICES[@]}"

info "等待健康检查（最长 300s）"
CONTAINERS=()
for s in "${SERVICES[@]}"; do
  CONTAINERS+=("simpleshop-$s")
done
wait_healthy 300 "${CONTAINERS[@]}"

ok "中间件全部 healthy（${#CONTAINERS[@]} 个）"
gray "当前容器内存占用："
docker stats --no-stream --format '      {{.Name}}  {{.MemUsage}}' "${CONTAINERS[@]}" 2>/dev/null || true
