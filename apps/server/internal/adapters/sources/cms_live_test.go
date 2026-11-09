package sources

import (
	"context"
	"encoding/json"
	"os"
	"testing"
	"time"

	"terryhub/internal/domain/tvbox"
)

// 联网冒烟测试：TMH_LIVE_SUB 指向订阅缓存 JSON（如 data/subscription.json）时才运行。
func TestCMSLive(t *testing.T) {
	path := os.Getenv("TMH_LIVE_SUB")
	if path == "" {
		t.Skip("未设置 TMH_LIVE_SUB")
	}
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var cfg tvbox.Config
	if err := json.Unmarshal(b, &cfg); err != nil {
		t.Fatal(err)
	}
	r := NewCMSResolver(nil)
	for _, s := range cfg.Sites {
		if s.Kind() != tvbox.KindCMS {
			continue
		}
		t.Run(s.Key, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			home, err := r.Home(ctx, s)
			if err != nil {
				t.Fatalf("home: %v", err)
			}
			t.Logf("分类 %d 个，首个 %v", len(home.Class), home.Class[0])
			cat, err := r.Category(ctx, s, home.Class[0].TypeID, "1", nil)
			if err != nil || len(cat.List) == 0 {
				t.Fatalf("category: %v n=%v", err, cat)
			}
			v := cat.List[0]
			t.Logf("列表 %d 条，首条 %s id=%s play=%.60s", len(cat.List), v.VodName, v.VodID, v.VodPlayURL)
			d, err := r.Detail(ctx, s, []string{v.VodID.String()})
			if err != nil || len(d.List) == 0 || d.List[0].VodPlayURL == "" {
				t.Fatalf("detail: %v %+v", err, d)
			}
			sr, err := r.Search(ctx, s, v.VodName, false)
			if err != nil || len(sr.List) == 0 {
				// 个别采集站不支持搜索（返回「暂不支持搜索」纯文本），属上游能力缺失，不算失败。
				t.Logf("搜索 %q 不可用: %v", v.VodName, err)
				return
			}
			t.Logf("搜索 %q → %d 条", v.VodName, len(sr.List))
		})
	}
}
