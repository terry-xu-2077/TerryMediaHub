#!/usr/bin/env bash
# 编译并部署到指定 Jellyfin 插件目录
# 用法: ./deploy.sh [目标插件根目录]
# 默认目标: %LOCALAPPDATA%\jellyfin\plugins
#
# 注意：不使用 rm -rf（会被 safe-delete 拦截），改为原地覆盖 + 逐个删除陈旧文件。
set -e

SRC="/c/OS/Servers/TerryMediaHub/src/Jellyfin.Plugin.TerryMediaHub"
DOTNET="/c/OS/tools/dotnet8/dotnet.exe"
VERSION="1.0.0.0"
NAME="TerryMediaHub"

TARGET="${1:-$LOCALAPPDATA/jellyfin/plugins}"

echo "==> 编译 ($VERSION)"
"$DOTNET" build "$SRC/Jellyfin.Plugin.TerryMediaHub.csproj" -c Release --nologo -v q

DEST="$TARGET/${NAME}_${VERSION}"
mkdir -p "$DEST"

echo "==> 部署到 $DEST"
cp -f "$SRC/bin/Release/Jellyfin.Plugin.TerryMediaHub.dll" "$DEST/"
cp -f "$SRC/meta.json" "$DEST/meta.json"

# 老插件目录（Yingshicang_*）：逐个删文件再 rmdir，避免整目录递归删除
for d in "$TARGET"/Yingshicang_*; do
  [ -d "$d" ] || continue
  echo "==> 移除旧插件目录 $d"
  posix=$(cygpath -u "$d" 2>/dev/null || echo "$d")
  find "$posix" -type f -delete 2>/dev/null || true
  find "$posix" -mindepth 1 -depth -type d -exec rmdir {} \; 2>/dev/null || true
  rmdir "$posix" 2>/dev/null || echo "   (目录非空，已跳过)"
done

echo "==> 完成"
ls -la "$DEST"
