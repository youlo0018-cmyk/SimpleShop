#!/usr/bin/env bash
# 启动全部 .NET 微服务（低内存串行模式）。
#
#   ./start-services.sh                # 串行启动（默认，内存峰值最低）
#   ./start-services.sh --with-log     # 额外启动 LogService（需要 ES 已起）
#   ./start-services.sh --only Order   # 只启动名字含 Order 的服务
#
# 低内存三件事：
#   1) DOTNET_gcServer=0 —— 工作站 GC，实测每个服务常驻从 ~140M 降到 ~80-110M；
#   2) 串行启动 —— 16 个进程同时做 JIT/建连的瞬时峰值是最大杀手；
#   3) 不启 LogService（它强依赖 ES，属 full 档）。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ONLY=""
WITH_LOG=0
while [ $# -gt 0 ]; do
  case "$1" in
    --with-log) WITH_LOG=1 ;;
    --only) ONLY="${2:-}"; shift ;;
    *) fail "未知参数：$1"; exit 1 ;;
  esac
  shift
done

require_cmd dotnet "安装 .NET 10 运行时：https://dotnet.microsoft.com/download"
require_node

# 服务启动第一步就是连 AgileConfig（fail-fast），不通的话每个服务都要等超时才报错，
# 先检查一次给出明确指引，比看 16 份超时日志快得多。
if ! curl -fsS --max-time 3 "http://127.0.0.1:5000/" >/dev/null 2>&1; then
  fail "AgileConfig（127.0.0.1:5000）不可达——服务启动会 fail-fast。"
  fail "  先执行：./start-infra.sh minimal"
  exit 1
fi

AVAIL="$(mem_available_mb)"
gray "当前可用内存：${AVAIL} MB"
if [ "$AVAIL" -lt 2500 ]; then
  warn "可用内存低于 2.5G，串行启动也可能紧张：先关掉浏览器 / IDE 等大内存程序。"
fi

# 低内存运行参数（只影响本次进程，不改系统）。
export DOTNET_gcServer=0          # 工作站 GC（默认 Server GC 按核数开堆）
export DOTNET_TieredPGO=1
export DOTNET_TieredCompilation=1
export ASPNETCORE_ENVIRONMENT=Development

started=0; skipped=0; failed=0
declare -a FAILED_NAMES=()

start_one() {
  local name="$1" port="$2" project="$3"

  if [ "$name" = "LogService" ] && [ "$WITH_LOG" -eq 0 ]; then
    gray "跳过 $name（依赖 Elasticsearch；需要时加 --with-log，并把中间件升到 full 档）"
    skipped=$((skipped+1))
    return 0
  fi

  # 已在运行的服务不重复拉起（端口探活优先，pid 文件只兜底无端口进程）。
  if [ "$port" -gt 0 ]; then
    if health_ok "$port"; then
      gray "$name 已在运行（端口 $port），跳过"
      skipped=$((skipped+1))
      return 0
    fi
  elif pid_alive "$LOG_DIR/$name.pid"; then
    gray "$name 已在运行（pid 文件），跳过"
    skipped=$((skipped+1))
    return 0
  fi

  local proj_dir assembly dll
  proj_dir="$(dirname "$ROOT_DIR/$project")"
  assembly="$(basename "$project" .csproj)"
  dll="$proj_dir/bin/Debug/net10.0/$assembly.dll"
  if [ ! -f "$dll" ]; then
    fail "$name 缺少编译产物：$dll（先跑 ./build.sh --backend）"
    failed=$((failed+1)); FAILED_NAMES+=("$name")
    return 0
  fi

  local log="$LOG_DIR/$name.log" err="$LOG_DIR/$name.err.log" pidf="$LOG_DIR/$name.pid"
  info "启动 $name（端口 ${port:-无}）"
  (
    cd "$proj_dir"
    if [ "$port" -gt 0 ]; then
      nohup dotnet "$dll" --urls "http://127.0.0.1:$port" >"$log" 2>"$err" &
    else
      nohup dotnet "$dll" >"$log" 2>"$err" &
    fi
    echo $! >"$pidf"
  )
  local pid; pid="$(cat "$pidf" 2>/dev/null || echo '?')"

  # 就绪判定：有端口的探 /health（最多 30s），无端口的看进程是否还活着。
  local ready=0
  for _ in $(seq 1 30); do
    sleep 1
    if [ "$port" -gt 0 ]; then
      if health_ok "$port"; then ready=1; break; fi
    elif pid_alive "$pidf"; then ready=1; sleep 1; break; fi
    # 进程已退出（不管有没有端口），不用再等满 30 秒
    if ! pid_alive "$pidf"; then break; fi
  done

  if [ "$ready" -eq 1 ]; then
    ok "$name 就绪（pid $pid）"
    started=$((started+1))
  else
    fail "$name 启动失败（pid $pid）——日志尾部："
    tail -n 5 "$log" 2>/dev/null | sed 's/^/      /' || true
    tail -n 5 "$err" 2>/dev/null | sed 's/^/      /' || true
    failed=$((failed+1)); FAILED_NAMES+=("$name")
  fi
}

while IFS=$'\t' read -r name port project; do
  [ -n "$name" ] || continue
  if [ -n "$ONLY" ] && [[ "$name" != *"$ONLY"* ]]; then
    continue
  fi
  start_one "$name" "$port" "$project"
done < <(load_services)

echo
if [ "$failed" -eq 0 ]; then
  ok "启动完成：成功 $started / 跳过 $skipped / 失败 0"
else
  fail "启动完成：成功 $started / 跳过 $skipped / 失败 $failed（${FAILED_NAMES[*]}）"
  exit 1
fi
