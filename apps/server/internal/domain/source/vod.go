// Package source 定义 spider 返回的影视数据结构（TVBox/CatVod 模型）。
package source

import (
	"encoding/json"
	"fmt"
)

// FlexString 兼容 string 与 number 的 JSON 字段（TVBox 的 vod_id 等字段类型松散）。
type FlexString string

func (f *FlexString) UnmarshalJSON(b []byte) error {
	if len(b) == 0 || string(b) == "null" {
		*f = ""
		return nil
	}
	if b[0] == '"' {
		var s string
		if err := json.Unmarshal(b, &s); err != nil {
			return err
		}
		*f = FlexString(s)
		return nil
	}
	// number 或 bool
	var n any
	if err := json.Unmarshal(b, &n); err != nil {
		return err
	}
	*f = FlexString(fmt.Sprintf("%v", n))
	return nil
}

func (f FlexString) String() string { return string(f) }

// Vod 是影视条目（列表或详情通用）。
type Vod struct {
	VodID       FlexString `json:"vod_id"`
	VodName     string     `json:"vod_name"`
	VodPic      string     `json:"vod_pic"`
	VodRemarks  string     `json:"vod_remarks"` // 备注（如"更新至20集"）
	VodYear     string     `json:"vod_year"`
	VodArea     string     `json:"vod_area"`
	VodActor    string     `json:"vod_actor"`
	VodDirector string     `json:"vod_director"`
	VodContent  string     `json:"vod_content"`    // 简介
	TypeName    string     `json:"type_name"`      // 分类名
	VodPlayFrom string     `json:"vod_play_from"`  // 播放来源（线路）
	VodPlayURL  string     `json:"vod_play_url"`   // 播放地址
	VodTag      string     `json:"vod_tag"`        // 标签
	VodNameNoEp string     `json:"vod_name_no_ep"` // 无集数名
}

// Class 是分类。
type Class struct {
	TypeID   string `json:"type_id"`
	TypeName string `json:"type_name"`
}

// ClassFilter 是分类筛选条件。
type ClassFilter struct {
	Key   string `json:"key"`
	Name  string `json:"name"`
	Value []struct {
		N string `json:"n"`
		V string `json:"v"`
	} `json:"value"`
}

// HomeResult 是 homeContent 的返回。
type HomeResult struct {
	Class   []Class                  `json:"class"`
	Filters map[string][]ClassFilter `json:"filters"`
	VodList []Vod                    `json:"list"`
}

// CategoryResult 是 categoryContent 的返回。
type CategoryResult struct {
	Page      int   `json:"page"`
	PageCount int   `json:"pagecount"`
	Limit     int   `json:"limit"`
	Total     int   `json:"total"`
	List      []Vod `json:"list"`
}

// DetailResult 是 detailContent 的返回。
type DetailResult struct {
	List []Vod `json:"list"`
}

// Episode 是解析出的单集。
type Episode struct {
	Name string // 集名，如 "第1集"
	URL  string // 播放地址（或待解析的加密地址）
}

// PlayResult 是 playerContent 的返回（线路解析结果）。
type PlayResult struct {
	Parse      int    `json:"parse"`
	URL        string `json:"url"`
	Header     string `json:"header"` // JSON 字符串
	PlayerType int    `json:"playerType"`
	Extra      string `json:"extra"`
}

// Searchable 判断站点返回的 vod 是否可直接播放。
type Searchable struct {
	VodID  string
	Name   string
	Pic    string
	Remark string
	Year   string
}
