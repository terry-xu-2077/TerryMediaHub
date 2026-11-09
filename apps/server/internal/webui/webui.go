// Package webui 内嵌前端构建产物（apps/web 构建输出到 dist/），使服务端成为单文件交付。
package webui

import (
	"embed"
	"io/fs"
)

//go:embed all:dist
var dist embed.FS

// FS 返回前端静态资源根目录。
func FS() fs.FS {
	sub, err := fs.Sub(dist, "dist")
	if err != nil {
		return nil
	}
	return sub
}
