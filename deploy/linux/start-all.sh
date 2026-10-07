#!/usr/bin/env bash
# 一键启动 SimpleShop（Linux Mint / 16G 低内存档）。
#
#   ./start-all.sh              # minimal：核心中间件 + 全部服务 + 两个前端
#   ./start-all.sh standard     # + RabbitMQ（事件总线）
#   ./start-all.sh full         # + ES / Kibana / Fluentd / Consul + LogService
#
# 启动顺序：中间件 → 微服务（串行）→ 前端。顺序不是仪式感：
# 服务启动第一步就 fail-fast 拉 AgileConfig、连 DB/Redis，中间件没就绪必失败。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

TIER="${1:-minimal}"
WITH_LOG=""

case "$TIER" in
  minimal)  ;;
  standard) ;;
  full)     WITH_LOG="--with-log" ;;
  *) fail "未知档位：$TIER（可选：minimal / standard / full）"; exit 1 ;;
esac

AVAIL="$(mem_available_mb)"
gray "可用内存：${AVAIL} MB"
case "$TIER" in
  minimal)
    [ "$AVAIL" -lt 3500 ] && warn "minimal 档建议 ≥3.5G 可用内存，当前偏低。" ;;
  standard)
    [ "$AVAIL" -lt 4000 ] && warn "standard 档建议 ≥4G 可用内存，当前偏低。" ;;
  full)
    [ "$AVAIL" -lt 7000 ] && warn "full 档建议 ≥7G 可用内存（ES+Kibana 是大头），当前偏低。" ;;
esac

started_at=$(date +%s)

bash "$LINUX_DIR/start-infra.sh" "$TIER"
svc_args=()
[ -n "$WITH_LOG" ] && svc_args+=("$WITH_LOG")
bash "$LINUX_DIR/start-services.sh" "${svc_args[@]}"
bash "$LINUX_DIR/start-web.sh"

echo
ok "启动完成（档位 $TIER，耗时 $(( $(date +%s) - started_at ))s）"
gray "后台管理端  http://127.0.0.1:5173"
gray "商城 H5     http://127.0.0.1:5174"
gray "查看状态    ./status.sh"
