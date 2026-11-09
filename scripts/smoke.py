#!/usr/bin/env python3
"""冒烟测试：对一个已运行的 TerryMediaHub 服务做端到端检查（默认 http://localhost:8090）。

用法: python scripts/smoke.py [base_url]
退出码 0 = 全部通过；非 0 = 有失败项。上游站点天然不稳定，播放步骤会在多个候选里找第一个能播的。
"""
import json
import sys
import time
import urllib.parse
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8090").rstrip("/")
results = []


def call(path, data=None, method=None, timeout=60, raw=False):
    req = urllib.request.Request(BASE + path, method=method or ("POST" if data is not None else "GET"))
    body = None
    if data is not None:
        body = json.dumps(data).encode()
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, body, timeout=timeout) as r:
        b = r.read()
        return b if raw else json.loads(b)


def check(name, fn):
    t = time.time()
    try:
        detail = fn()
        results.append((name, True, detail or ""))
        print(f"  PASS  {name}  {detail or ''}  ({time.time() - t:.1f}s)")
    except Exception as e:  # noqa: BLE001
        results.append((name, False, str(e)))
        print(f"  FAIL  {name}  {e}")


state = {}


def t_status():
    s = call("/api/status")
    assert "running" in s
    return f"nextRun={s.get('nextRun')}"


def t_sites():
    n = len(call("/api/sites")["sites"])
    assert n >= 20, f"站点数过少: {n}"
    return f"{n} 站点"


def t_home():
    rows = call("/api/home")["rows"]
    assert len(rows) >= 3, f"首页行数过少: {len(rows)}"
    for r in rows:
        assert len(r["items"]) >= 10, f"{r['key']} 条目过少: {len(r['items'])}"
    state["items"] = [i for r in rows for i in r["items"][:6]]
    return ", ".join(f"{r['key']}:{len(r['items'])}" for r in rows)


def t_category_paging():
    p1 = call("/api/category?cat=series&page=1")["items"]
    p2 = call("/api/category?cat=series&page=2")["items"]
    assert p1 and p2, "分类分页为空"
    return f"p1={len(p1)} p2={len(p2)}"


def t_search():
    r = call("/api/search?q=" + urllib.parse.quote("狂飙"))["results"] or []
    assert r, "搜索无结果"
    return f"{len(r)} 条"


def t_detail_and_play():
    tried = 0
    for it in state["items"]:
        tried += 1
        if tried > 8:
            break
        try:
            d = call(f"/api/detail?site={urllib.parse.quote(it['site'])}&id={urllib.parse.quote(it['vodId'])}")
            if not d.get("episodes"):
                continue
            p = call("/api/play/auto", {"site": d["site"], "id": d["vodId"], "candidates": d["episodes"][0]["urls"]})
            if p.get("url"):
                state["play"] = p["url"]
                state["detail"] = d
                return f"《{d['name']}》{len(d['episodes'])} 集 → {p['url'][:50]}"
        except Exception:  # noqa: BLE001
            continue
    raise AssertionError(f"尝试 {tried} 部都没能解析出播放地址")


def t_proxy():
    b = call("/api/proxy?url=" + urllib.parse.quote(state["play"]), raw=True, timeout=30)
    assert len(b) > 0
    return f"{len(b)} bytes"


def t_img():
    pic = next((i["pic"] for i in state["items"] if i.get("pic")), None)
    assert pic, "没有海报"
    b = call("/api/img?url=" + urllib.parse.quote(pic), raw=True)
    assert len(b) > 500
    return f"{len(b)} bytes"


def t_favorites_history():
    d = state["detail"]
    e = {"site": d["site"], "vodId": d["vodId"], "name": "__smoke__" + d["name"], "pic": d["pic"]}
    call("/api/favorites", e)
    assert any(f["name"] == e["name"] for f in call("/api/favorites")["items"]), "收藏未写入"
    call("/api/history", {**e, "episode": "第1集", "position": 12.5, "duration": 100})
    h = [x for x in call("/api/history")["items"] if x["name"] == e["name"]]
    assert h and abs(h[0]["position"] - 12.5) < 0.01, "历史未写入"
    call("/api/favorites?name=" + urllib.parse.quote(e["name"]), method="DELETE")
    call("/api/history?name=" + urllib.parse.quote(e["name"]), method="DELETE")
    assert not any(f["name"] == e["name"] for f in call("/api/favorites")["items"]), "收藏未删除"
    return "增删查正常"


def t_health():
    s = call("/api/sites/health")["sites"]
    assert s and all("score" in x for x in s)
    return f"{len(s)} 站点有统计"


def t_frontend():
    html = call("/", raw=True).decode("utf-8", "ignore")
    assert "<div id=\"root\">" in html
    assert b"<div" in call("/library/history", raw=True), "SPA 回退失败"


print(f"冒烟测试 → {BASE}")
check("状态接口", t_status)
check("站点列表", t_sites)
check("首页推荐", t_home)
check("分类分页", t_category_paging)
check("聚合搜索", t_search)
if state.get("items"):
    check("详情+解析播放地址", t_detail_and_play)
if state.get("play"):
    check("流代理", t_proxy)
check("海报代理", t_img) if state.get("items") else None
if state.get("detail"):
    check("收藏/历史读写", t_favorites_history)
check("站点健康度", t_health)
check("前端静态与 SPA 回退", t_frontend)

failed = [r for r in results if not r[1]]
print(f"\n{len(results) - len(failed)}/{len(results)} 通过")
sys.exit(1 if failed else 0)
