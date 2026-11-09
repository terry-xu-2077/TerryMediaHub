package ranking

import (
	"testing"
	"time"
)

func TestOrdering(t *testing.T) {
	r := New()
	r.now = func() time.Time { return time.Date(2026, 11, 9, 0, 0, 0, 0, time.UTC) }
	r.SetHot(map[string]Hot{"狂飙": {Rate: 9.1, Rank: 1}, "老电影": {Rate: 9.4, Rank: 0}})

	hotNew := r.Score("狂飙", "2023", "全39集")
	oldClassic := r.Score("老电影", "1994", "HD")
	unknownNew := r.Score("某新剧", "2026", "更新至5集")
	unknownOld := r.Score("某旧剧", "2005", "更新至5集")

	if !(hotNew > unknownNew && unknownNew > oldClassic) {
		t.Fatalf("排序不符预期: 热门新=%.2f 无名新=%.2f 老经典=%.2f", hotNew, unknownNew, oldClassic)
	}
	if unknownNew <= unknownOld {
		t.Fatalf("同样无评分，新作应排在旧作前: %.2f vs %.2f", unknownNew, unknownOld)
	}
	// 年代越久越减权：同评分同热度，差 10 年应少 1.5 分。
	a, b := r.Score("狂飙", "2026", ""), r.Score("狂飙", "2016", "")
	if d := a - b; d < 1.49 || d > 1.51 {
		t.Fatalf("10 年应扣 1.5 分，实际 %.3f", d)
	}
	// 归一化：季/语言后缀与标点不影响匹配
	if Normalize("狂飙 第二季") != Normalize("狂飙") || Normalize("《狂飙》国语版") != Normalize("狂飙") {
		t.Fatal("归一化失败")
	}
}

func TestOrderTieBreakNewerFirst(t *testing.T) {
	r := New()
	items := []Item{{"甲", "2010", ""}, {"乙", "2010", ""}, {"丙", "2024", ""}}
	order := r.Order(items)
	if order[0] != 2 { // 丙分数更高
		t.Fatalf("order=%v", order)
	}
	if order[1] != 0 || order[2] != 1 { // 同分保持原顺序
		t.Fatalf("同分应稳定: %v", order)
	}
}

func TestDedupKey(t *testing.T) {
	if DedupKey("突如其来2026") != DedupKey("突如其来") {
		t.Fatal("年份后缀应合并")
	}
	if DedupKey("现在就出发第三季") == DedupKey("现在就出发第四季") {
		t.Fatal("不同季不应合并")
	}
}
