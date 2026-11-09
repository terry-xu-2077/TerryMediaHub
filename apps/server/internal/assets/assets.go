// Package assets 内嵌 JVM 解析宿主 host-runtime.jar。
package assets

import (
	"embed"
)

//go:embed host-runtime.jar
var HostRuntimeJar []byte

// Runtime 是 python/node 桥接 worker 的脚本与 JS 运行库（运行时释放到数据目录）。
//
//go:embed all:runtime
var Runtime embed.FS
