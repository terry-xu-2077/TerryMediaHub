// Package tvbox 定义 TVBox / CatVod 订阅配置的数据模型。
package tvbox

import (
	"encoding/json"
	"strconv"
	"strings"
)

// Config 是订阅地址（如盒子迷）返回的完整配置。
type Config struct {
	Spider    string   `json:"spider"`    // 全局 spider jar，形如 url;md5;HASH
	Wallpaper string   `json:"wallpaper"` // 壁纸
	Logo      string   `json:"logo"`      // logo
	Sites     []Site   `json:"sites"`     // 站点列表
	Parses    []Parse  `json:"parses"`    // 解析接口列表
	Lives     []Live   `json:"lives"`     // 直播源（首版不处理）
	Flags     []string `json:"flags"`     // 线路 flags
	Icons     []Icon   `json:"icons"`     // 图标
}

// Site 是单个点播站点。
type Site struct {
	Key         string          `json:"key"`         // 唯一标识
	Name        string          `json:"name"`        // 显示名
	Type        FlexInt         `json:"type"`        // 0=普通 1=字典 2=web 3=xml 4=jar(csp)
	Api         string          `json:"api"`         // csp_XXX / js / python / cms url
	Ext         json.RawMessage `json:"ext"`         // 扩展参数：字符串或对象
	Jar         string          `json:"jar"`         // 站点专用 jar（可选）
	PlayerType  FlexInt         `json:"playerType"`  // 播放器类型
	Searchable  *FlexInt        `json:"searchable"`  // 是否可搜索（0/1）
	QuickSearch *FlexInt        `json:"quickSearch"` // 是否快速搜索
	Filterable  *FlexInt        `json:"filterable"`
	Categories  json.RawMessage `json:"categories"` // 指定分类：字符串或数组
	Style       json.RawMessage `json:"style"`      // 样式对象
	Timeout     FlexInt         `json:"timeout"`
	Changeable  FlexInt         `json:"changeable"`
	Playable    FlexInt         `json:"playable"`
}

// Parse 是解析接口（换真链用）。
type Parse struct {
	Name string          `json:"name"`
	Type FlexInt         `json:"type"`
	Url  string          `json:"url"`
	Ext  json.RawMessage `json:"ext"`
}

// Live 是直播源。
type Live struct {
	Name   string  `json:"name"`
	Type   FlexInt `json:"type"`
	Url    string  `json:"url"`
	Player int     `json:"playerType"`
}

// Icon 是站点图标。
type Icon struct {
	Name string `json:"name"`
	Url  string `json:"url"`
}

// IsCsp 判断站点是否为 csp_（JAR）爬虫。
func (s Site) IsCsp() bool {
	return len(s.Api) >= 4 && s.Api[:4] == "csp_"
}

// CspClass 返回 csp_ 站点的爬虫类名（去掉 csp_ 前缀）。
func (s Site) CspClass() string {
	if s.IsCsp() {
		return s.Api[4:]
	}
	return ""
}

// ExtString 返回 ext 作为字符串。
// TVBox 的 ext 可能是 JSON 对象，也可能本身就是字符串；对象则原样序列化，
// 因为 worker 端 spider.init(ctx, ext) 需要 JSON 字符串。
func (s Site) ExtString() string {
	if len(s.Ext) == 0 {
		return ""
	}
	// 如果是字符串，去掉引号返回；否则原样返回（对象 JSON）
	var str string
	if err := json.Unmarshal(s.Ext, &str); err == nil {
		return str
	}
	return string(s.Ext)
}

// Kind 是站点的运行方式。
type Kind string

const (
	KindCsp   Kind = "csp"   // JAR 爬虫（JVM）
	KindCMS   Kind = "cms"   // 苹果CMS v10 JSON/XML
	KindPy    Kind = "py"    // Python 爬虫
	KindJS    Kind = "js"    // JS 爬虫 / drpy2 规则
	KindGuard Kind = "guard" // *Guard 类：依赖 DexNative 原生加固库(.so)，桌面 JVM 无法运行
	KindOther Kind = "other" // 网盘/PHP/直播等，暂不支持
)

// Kind 判断站点类型。
func (s Site) Kind() Kind {
	if s.IsCsp() {
		if strings.HasSuffix(s.Api, "Guard") {
			return KindGuard
		}
		return KindCsp
	}
	path := s.Api
	if i := strings.IndexAny(path, "?#"); i >= 0 {
		path = path[:i]
	}
	lower := strings.ToLower(path)
	switch {
	case strings.Contains(lower, "drpy"): // drpy2 规则引擎：需要规则文件配合，暂不支持
		return KindOther
	case strings.HasSuffix(lower, ".py"):
		return KindPy
	case strings.HasSuffix(lower, ".js"):
		return KindJS
	case (s.Type == 0 || s.Type == 1) && strings.HasPrefix(lower, "http") &&
		!strings.HasSuffix(lower, ".json") && !strings.HasSuffix(lower, ".txt"):
		return KindCMS
	}
	return KindOther
}

// FlexInt 兼容 number / "123" / "" / true / null 的整型字段（TVBox 配置的字段类型很松散）。
type FlexInt int

func (f *FlexInt) UnmarshalJSON(b []byte) error {
	t := strings.Trim(strings.TrimSpace(string(b)), `"`)
	switch t {
	case "", "null", "false":
		*f = 0
		return nil
	case "true":
		*f = 1
		return nil
	}
	n, err := strconv.ParseFloat(t, 64)
	if err != nil {
		*f = 0 // 无法解析的值按 0 处理，不让整份订阅失败
		return nil
	}
	*f = FlexInt(n)
	return nil
}
