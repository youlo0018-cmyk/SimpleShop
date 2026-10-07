#!/usr/bin/env bash
# 构建后端（.NET 10）与两个前端（后台 + 商城 H5）。
#
#   ./build.sh               # 全量构建
#   ./build.sh --backend     # 只构建后端
#   ./build.sh --web         # 只构建前端
#   ./build.sh --fresh       # 强制重装前端依赖（从 Windows 拷过来的 node_modules 不可用：
#                            #   esbuild 等原生二进制是平台相关的，必须删掉重装）
#
# 构建需要 .NET 10 SDK 与 Node.js 20+；启动（start-*.sh）只需要 .NET 运行时与 Node。
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

MODE="all"
FRESH=0
case "${1:-}" in
  --backend) MODE="backend" ;;
  --web)     MODE="web" ;;
  --fresh)   MODE="all"; FRESH=1 ;;
  "")        MODE="all" ;;
  *) fail "未知参数：$1（可选 --backend / --web / --fresh）"; exit 1 ;;
esac

if [ "$MODE" = "all" ] || [ "$MODE" = "backend" ]; then
  require_cmd dotnet "安装 .NET 10 SDK：https://dotnet.microsoft.com/download"
  info "构建后端（dotnet build SimpleShop.slnx）"
  dotnet build "$ROOT_DIR/SimpleShop.slnx" -c Debug --nologo -v q
  ok "后端构建完成（0 警告 0 错误才算过——CS1591 是 WarningsAsErrors）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "web" ]; then
  require_node
  build_front() {
    local dir="$1" script="$2" label="$3"
    info "构建 $label（npm run $script）"
    cd "$dir"
    if [ "$FRESH" -eq 1 ]; then
      gray "  --fresh：清理 node_modules 后重装"
      rm -rf node_modules
    fi
    # 首次或依赖变更时用 ci 安装；已装过就直接构建（省时间也省网络）。
    if [ ! -d node_modules ]; then
      npm ci
    fi
    npm run "$script"
    ok "$label 构建完成"
  }
  build_front "$ROOT_DIR/apps/admin-vue" "build" "后台管理端"
  build_front "$ROOT_DIR/apps/user-uniapp" "build:h5" "商城 H5"
fi

ok "全部构建完成"
