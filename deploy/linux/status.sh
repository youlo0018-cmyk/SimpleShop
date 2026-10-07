#!/usr/bin/env bash
# 查看 SimpleShop 运行状态：内存 → 容器 → 微服务 → 前端。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

echo "===== 内存 ====="
free -h
echo

echo "===== 中间件容器 ====="
if docker info >/dev/null 2>&1; then
  compose ps --format 'table {{.Name}}\t{{.Status}}' 2>/dev/null || compose ps
  echo
  docker stats --no-stream --format 'table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}' 2>/dev/null \
    | grep -E 'NAME|simpleshop' || true
else
  fail "docker 不可用"
fi
echo

echo "===== 微服务 ====="
while IFS=$'\t' read -r name port _project; do
  [ -n "$name" ] || continue
  if [ "$port" -gt 0 ]; then
    if health_ok "$port"; then
      printf "  ${C_GREEN}✓${C_RESET} %-22s http://127.0.0.1:%s\n" "$name" "$port"
    else
      printf "  ${C_RED}✗${C_RESET} %-22s 未运行\n" "$name"
    fi
  else
    if pid_alive "$LOG_DIR/$name.pid"; then
      printf "  ${C_GREEN}✓${C_RESET} %-22s 运行中（无端口）\n" "$name"
    else
      printf "  ${C_GRAY}-${C_RESET} %-22s 未运行\n" "$name"
    fi
  fi
done < <(load_services)
echo

echo "===== 前端 ====="
for pair in "web-admin:5173" "web-h5:5174"; do
  label="${pair%%:*}"; port="${pair##*:}"
  if curl -fsS --max-time 2 "http://127.0.0.1:$port/" >/dev/null 2>&1; then
    printf "  ${C_GREEN}✓${C_RESET} %-10s http://127.0.0.1:%s\n" "$label" "$port"
  else
    printf "  ${C_RED}✗${C_RESET} %-10s 未运行\n" "$label"
  fi
done
