#!/usr/bin/env bash
# SimpleShop Linux 启动脚本的共享函数库（被 source，不直接执行）。
#
# 面向设备：Linux Mint / Ubuntu，16G 内存（实际可用 ~13.5G）。
# 设计原则：先保证「起得来」，再谈「起得快」——内存峰值优先、并发放后。
set -euo pipefail

# ---------- 路径 ----------
LINUX_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$LINUX_DIR/.." && pwd)"
ROOT_DIR="$(cd "$DEPLOY_DIR/.." && pwd)"
COMPOSE_BASE="$DEPLOY_DIR/docker-compose.yml"
COMPOSE_LINUX="$DEPLOY_DIR/docker-compose.linux.yml"
REGISTRY="$ROOT_DIR/scripts/service-registry.json"
LOG_DIR="$ROOT_DIR/logs/runtime"

# ---------- 颜色（无 TTY 时自动降级为纯文本） ----------
if [ -t 1 ]; then
  C_RESET='\033[0m'; C_CYAN='\033[36m'; C_GREEN='\033[32m'
  C_YELLOW='\033[33m'; C_RED='\033[31m'; C_GRAY='\033[90m'
else
  C_RESET=''; C_CYAN=''; C_GREEN=''; C_YELLOW=''; C_RED=''; C_GRAY=''
fi

info()  { printf "${C_CYAN}==> %s${C_RESET}\n" "$*"; }
ok()    { printf "${C_GREEN}    %s${C_RESET}\n" "$*"; }
warn()  { printf "${C_YELLOW}    %s${C_RESET}\n" "$*"; }
fail()  { printf "${C_RED}    %s${C_RESET}\n" "$*" >&2; }
gray()  { printf "${C_GRAY}    %s${C_RESET}\n" "$*"; }

# ---------- 前置检查 ----------
require_cmd() {
  command -v "$1" >/dev/null 2>&1 || { fail "缺少命令：$1（$2）"; exit 1; }
}

require_docker() {
  require_cmd docker "安装 Docker：https://docs.docker.com/engine/install/ubuntu/"
  if ! docker info >/dev/null 2>&1; then
    fail "docker 不可用：守护进程没启动，或当前用户不在 docker 组。"
    fail "  sudo systemctl start docker && sudo usermod -aG docker \$USER  # 重登后生效"
    exit 1
  fi
  docker compose version >/dev/null 2>&1 || { fail "缺少 docker compose v2 插件"; exit 1; }
}

require_node() {
  require_cmd node "安装 Node.js 20+：https://nodejs.org/"
}

# ES 在 Linux 上要求 vm.max_map_count ≥ 262144，否则启动时直接崩（Docker Desktop 自带，原生 Docker 不会）。
ensure_es_sysctl() {
  local current
  current="$(sysctl -n vm.max_map_count 2>/dev/null || echo 0)"
  if [ "$current" -lt 262144 ]; then
    warn "vm.max_map_count=$current < 262144，Elasticsearch 会启动失败。"
    warn "  修复（需 sudo，一次即可，重启后需重做或写入 /etc/sysctl.d/）："
    warn "    sudo sysctl -w vm.max_map_count=262144"
    return 1
  fi
  return 0
}

# ---------- 内存 ----------
# 可用内存（MB），取 MemAvailable（比 free 更贴近真实可用）。
mem_available_mb() {
  awk '/MemAvailable/ {printf "%d", $2/1024}' /proc/meminfo
}

# ---------- compose ----------
compose() {
  docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_LINUX" "$@"
}

# 等待指定容器 healthy（最长 timeout 秒）。
wait_healthy() {
  local timeout="$1"; shift
  local deadline=$(( $(date +%s) + timeout ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    local pending=()
    for name in "$@"; do
      local state
      state="$(docker inspect --format '{{.State.Health.Status}}' "$name" 2>/dev/null || echo missing)"
      [ "$state" = "healthy" ] || pending+=("$name($state)")
    done
    if [ "${#pending[@]}" -eq 0 ]; then
      return 0
    fi
    gray "等待中: ${pending[*]}"
    sleep 5
  done
  fail "等待超时，仍未 healthy: $*"
  return 1
}

# ---------- 服务 ----------
# 输出：name<TAB>port<TAB>project（只含 implemented=true）。
# 主路径用 node 解析 JSON；没装 node 时用 awk 兜底——stop-all.sh 只停进程，
# 不应该因为「没装 Node」就停不下来（注册表格式固定：name/port/implemented/project 依次出现）。
load_services() {
  if command -v node >/dev/null 2>&1; then
    node -e '
      const r = require(process.argv[1]);
      for (const s of r.services) {
        if (s.implemented) console.log([s.name, s.port, s.project].join("\t"));
      }
    ' "$REGISTRY"
    return
  fi
  awk '
    # 去掉引号 / 逗号 / 回车：注册表在 Windows 上是 CRLF，带 \r 会让 impl 比较永远为假
    /"name"/        { name=$2; gsub(/[",\r]/,"",name) }
    /"port"/        { port=$2; gsub(/[",\r]/,"",port) }
    /"implemented"/ { impl=$2; gsub(/[",\r]/,"",impl) }
    /"project"/     { proj=$2; gsub(/[",\r]/,"",proj); if (impl=="true") print name "\t" port "\t" proj }
  ' "$REGISTRY"
}

health_ok() {
  curl -fsS --max-time 2 "http://127.0.0.1:$1/health" >/dev/null 2>&1
}

# 端口上的监听进程 PID（iproute2 的 ss，Linux 自带；无权限看 pid 时输出为空）。
port_pids() {
  ss -ltnpH "sport = :$1" 2>/dev/null \
    | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u || true
}

pid_alive() {
  local f="$1"
  [ -f "$f" ] || return 1
  local p
  p="$(cat "$f" 2>/dev/null | tr -d '[:space:]')"
  [ -n "$p" ] && kill -0 "$p" 2>/dev/null
}

mkdir -p "$LOG_DIR"
