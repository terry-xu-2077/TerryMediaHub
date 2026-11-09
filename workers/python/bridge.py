# -*- coding: utf-8 -*-
"""Python 爬虫宿主：与 Go 主服务通过 stdin/stdout 逐行 JSON RPC 通信（协议同 contracts/worker/protocol.md）。

加载 TVBox 风格的 Python 插件（`from base.spider import Spider`，类名 Spider）。
"""
import hashlib
import importlib.util
import json
import os
import sys
import time
import traceback
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.environ.get("TERRY_PY_CACHE") or os.path.join(HERE, "cache")
os.makedirs(CACHE, exist_ok=True)
# 第三方依赖（bs4 / lxml / pycryptodome）由 pip --target 装到 lib/
for p in (os.path.join(HERE, "lib"), HERE):
    if p not in sys.path:
        sys.path.insert(0, p)

# 协议通道独占真正的 stdout；插件里的 print 一律转到 stderr。
_proto = sys.stdout
sys.stdout = sys.stderr


def out(obj):
    _proto.write(json.dumps(obj, ensure_ascii=False) + "\n")
    _proto.flush()


_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
_spiders = {}


def load_plugin(api):
    path = os.path.join(CACHE, hashlib.sha1(api.encode()).hexdigest() + ".py")
    if not os.path.exists(path):
        req = urllib.request.Request(api, headers={"User-Agent": _UA})
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read()
        with open(path, "wb") as f:
            f.write(data)
    spec = importlib.util.spec_from_file_location("plugin_" + hashlib.md5(api.encode()).hexdigest(), path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    cls = getattr(mod, "Spider", None)
    if cls is None:
        raise RuntimeError("插件里没有 Spider 类")
    return cls()


def get_spider(req):
    key = "%s|%s|%s" % (req["site"], req["api"], req.get("ext") or "")
    ent = _spiders.get(key)
    if ent is None:
        ent = {"spider": load_plugin(req["api"]), "inited": False}
        _spiders[key] = ent
    if not ent["inited"]:
        ent["spider"].init(req.get("ext") or "")
        ent["inited"] = True
    return ent["spider"]


def handle(req):
    m = req["method"]
    if m == "ping":
        return "pong"
    sp = get_spider(req)
    a = req.get("args") or {}
    if m == "home":
        return sp.homeContent(True)
    if m == "homeVideo":
        return sp.homeVideoContent()
    if m == "category":
        return sp.categoryContent(a.get("tid"), a.get("pg", "1"), True, a.get("extend") or {})
    if m == "detail":
        return sp.detailContent(a.get("ids") or [])
    if m == "search":
        return sp.searchContent(a.get("key"), bool(a.get("quick")), a.get("pg", "1"))
    if m == "play":
        return sp.playerContent(a.get("flag"), a.get("id"), a.get("vipFlags") or [])
    raise ValueError("unknown method: " + m)


def main():
    out({"ready": True, "ok": True, "runtime": "python " + sys.version.split()[0]})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        t0 = time.time()
        try:
            data = handle(json.loads(line))
            if not isinstance(data, str):
                data = json.dumps(data if data is not None else {}, ensure_ascii=False)
            out({"ok": True, "data": data, "ms": int((time.time() - t0) * 1000)})
        except BaseException as e:  # 插件可能抛 SystemExit 等
            tb = traceback.format_exc().strip().splitlines()[-6:]
            out({"ok": False, "error": "%s: %s" % (type(e).__name__, e), "trace": "\n".join(tb),
                 "ms": int((time.time() - t0) * 1000)})
    os._exit(0)


if __name__ == "__main__":
    main()
