#!/usr/bin/env bash
# 从源码完整重建 host-runtime.jar：
#   workers/java/src          Host、Spider 基类、SpiderApi、SimplePrefs、org.json、Android/androidx 桩（源码）
#   workers/java/lib/third-party.jar   okhttp 3.14.9 / okio / gson / ASM / dex2jar（第三方二进制，见 workers/java/README.md）
# 产物写到 apps/server/internal/assets/host-runtime.jar（由 Go 服务内嵌）。
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$PWD
OUT=apps/server/internal/assets/host-runtime.jar
LIB=workers/java/lib/third-party.jar
BUILD=$(mktemp -d)

# javac 在 Windows 下需要 Windows 风格路径
win() { command -v cygpath >/dev/null 2>&1 && cygpath -w "$1" || echo "$1"; }

find workers/java/src -name '*.java' > "$BUILD/sources.txt"
javac -encoding UTF-8 -nowarn -proc:none -cp "$(win "$LIB")" -d "$(win "$BUILD/classes")" @"$BUILD/sources.txt" 2>&1 | grep -v '^注:' | grep -v '^Note:' || true
# 先放第三方，再用自编译类覆盖
mkdir -p "$BUILD/classes" "$BUILD/jar"
(cd "$BUILD/jar" && jar xf "$(win "$ROOT/$LIB")")
cp -r "$BUILD/classes/." "$BUILD/jar/"
jar cf "$(win "$BUILD/new.jar")" -C "$(win "$BUILD/jar")" .
mv "$BUILD/new.jar" "$OUT"
echo "已重建 $OUT（$(find "$BUILD/classes" -name '*.class' | wc -l) 个自编译类 + 第三方库）"
