#!/usr/bin/env bash
# 首次初始化数据库：建 14 个库 → 建表 + SQL 种子（幂等，可重复执行）。
#
#   ./init-db.sh
#
# 只做「纯 SQL」那部分（deploy/sql/**）。
# 权限树 / 内置角色 / AgileConfig 配置的种子在 3 个 PowerShell 脚本里（数据量大，
# 不做第二份实现以免漂移）——见 README.md「首次初始化」一节，用 pwsh 跑一次即可。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_docker

CONTAINER="simpleshop-postgres"
DB_USER="postgres"
SQL_ROOT="$DEPLOY_DIR/sql"

# 先确认 postgres 已就绪（最多 60s）。
info "确认 $CONTAINER 就绪"
ready=0
for _ in $(seq 1 30); do
  if docker exec "$CONTAINER" pg_isready -U "$DB_USER" >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
if [ "$ready" -ne 1 ]; then
  fail "$CONTAINER 未就绪——先执行：./start-infra.sh minimal"
  exit 1
fi
ok "postgres 就绪"

info "执行 00-create-databases.sql（建库）"
docker exec -i "$CONTAINER" psql -U "$DB_USER" -d postgres -v ON_ERROR_STOP=1 \
  <"$SQL_ROOT/00-create-databases.sql"
ok "建库完成"

# 建表 + 种子：每个子目录对应一个库（simpleshop<目录名>），按文件名顺序执行。
for dir in "$SQL_ROOT"/*/; do
  svc="$(basename "$dir")"
  db="simpleshop$svc"
  shopt -s nullglob
  files=("$dir"*.sql)
  shopt -u nullglob
  [ "${#files[@]}" -gt 0 ] || { gray "跳过 $svc（无 SQL 文件）"; continue; }

  for f in "${files[@]}"; do
    info "[$db] 执行 $(basename "$f")"
    if ! docker exec -i "$CONTAINER" psql -U "$DB_USER" -d "$db" -v ON_ERROR_STOP=1 <"$f"; then
      fail "[$db] $(basename "$f") 执行失败"
      exit 1
    fi
  done
  ok "[$db] 完成"
done

ok "数据库初始化完成（可重复执行，幂等）"
