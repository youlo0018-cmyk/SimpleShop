#!/usr/bin/env bash
# 停止 SimpleShop：前端 → 微服务 → 中间件容器。
#
#   ./stop-all.sh                # 全停（容器只 stop，不删，数据都在）
#   ./stop-all.sh --keep-infra   # 只停前端与服务，容器继续跑
#
# 停进程以**端口**为准（pid 文件只兜底无端口进程）：pid 文件可能指向包装进程，
# 杀它可能留下子进程占着端口，下次启动就报「地址已在使用」。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

KEEP_INFRA=0
[ "${1:-}" = "--keep-infra" ] && KEEP_INFRA=1

kill_pid_gracefully() {
  local pid="$1"
  kill "$pid" 2>/dev/null || true
  for _ in 1 2 3 4 5; do
    kill -0 "$pid" 2>/dev/null || return 0
    sleep 1
  done
  warn "进程 $pid 未响应 TERM，强制结束"
  kill -9 "$pid" 2>/dev/null || true
}

stop_by_port() {
  local port="$1" label="$2"
  local pids
  pids="$(port_pids "$port")"
  if [ -z "$pids" ]; then
    gray "$label 未在运行（端口 $port）"
    return 0
  fi
  for pid in $pids; do
    kill_pid_gracefully "$pid"
  done
  ok "$label 已停止（端口 $port）"
}

stop_by_pidfile() {
  local name="$1" label="$2"
  local pidf="$LOG_DIR/$name.pid"
  if pid_alive "$pidf"; then
    kill_pid_gracefully "$(cat "$pidf" | tr -d '[:space:]')"
    ok "$label 已停止"
  else
    gray "$label 未在运行"
  fi
  rm -f "$pidf"
}

info "停止前端"
stop_by_port 5173 "后台管理端"
stop_by_port 5174 "商城 H5"
rm -f "$LOG_DIR/web-admin.pid" "$LOG_DIR/web-h5.pid"

info "停止微服务"
while IFS=$'\t' read -r name port _project; do
  [ -n "$name" ] || continue
  if [ "$port" -gt 0 ]; then
    stop_by_port "$port" "$name"
  else
    stop_by_pidfile "$name" "$name"
  fi
done < <(load_services)

if [ "$KEEP_INFRA" -eq 0 ]; then
  info "停止中间件容器（stop 不删，数据保留）"
  compose stop
  ok "容器已停止"
fi

ok "全部停止完成"
