#!/usr/bin/env bash
# 把 workers/js、workers/python 同步进 Go 内嵌目录（go:embed 不能引用模块外的文件）。
set -euo pipefail
cd "$(dirname "$0")/.."
DST=apps/server/internal/assets/runtime
rm -rf "$DST/js" "$DST/py"
mkdir -p "$DST/js" "$DST/py"
cp -r workers/js/bridge.mjs workers/js/lib "$DST/js/"
cp -r workers/python/bridge.py workers/python/base "$DST/py/"
find "$DST" -name __pycache__ -type d -prune -exec rm -rf {} +
echo "已同步 workers → $DST"
