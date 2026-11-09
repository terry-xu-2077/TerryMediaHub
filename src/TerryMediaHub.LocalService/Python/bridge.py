"""TV6.2.9-compatible Python spider RPC over one JSON line per request."""
import contextlib
import base64
import hashlib
import importlib.util
import json
import os
import pathlib
import sys
import traceback
import urllib.parse
import requests

cache = pathlib.Path(os.environ.get("TERRY_PY_CACHE", pathlib.Path(__file__).parent / "cache"))
cache.mkdir(parents=True, exist_ok=True)
os.environ["TERRY_PY_CACHE"] = str(cache)
sys.path.insert(0, str(pathlib.Path(__file__).parent))
sys.path.insert(0, str(cache))
spiders = {}


def download(api, name):
    if api.startswith(("http://", "https://")):
        response = requests.get(api, timeout=15)
        response.raise_for_status()
        body = response.content
        if body.lstrip().startswith((b"<!DOCTYPE", b"<html")):
            raise ValueError("Python 站点返回 HTML 而非脚本")
    else:
        body = api.encode("utf-8")
    path = cache / name
    path.write_bytes(body)
    return path


def get_spider(request):
    key = request["site"] + "|" + request["api"] + "|" + request.get("ext", "")
    if key in spiders:
        return spiders[key]
    api = request["api"]
    name = hashlib.sha256(api.encode()).hexdigest()[:16] + ".py"
    path = download(api, name)
    module_name = "terry_" + name[:-3]
    spec = importlib.util.spec_from_file_location(module_name, path)
    module = importlib.util.module_from_spec(spec)
    with contextlib.redirect_stdout(sys.stderr):
        spec.loader.exec_module(module)
        spider = module.Spider()
        spider.siteKey = request["site"]
        spider.siteApi = api
        spider.siteExt = request.get("ext", "")
        for dep in spider.getDependence() or []:
            dep_name = str(dep) + ".py"
            dep_url = urllib.parse.urljoin(api, dep_name) if api.startswith("http") else dep_name
            download(dep_url, dep_name)
        spider.init(request.get("ext", ""))
    spiders[key] = spider
    return spider


def call(request):
    spider = get_spider(request)
    args = request.get("args") or {}
    method = request["method"]
    with contextlib.redirect_stdout(sys.stderr):
        if method == "home":
            result = spider.homeContent(True)
        elif method == "category":
            result = spider.categoryContent(args.get("tid", ""), str(args.get("pg", "1")),
                                            True, args.get("extend", {}))
        elif method == "detail":
            result = spider.detailContent(args.get("ids", []))
        elif method == "play":
            result = spider.playerContent(args.get("flag", ""), args.get("id", ""),
                                          args.get("vipFlags", []))
        elif method == "search":
            result = spider.searchContent(args.get("key", ""), bool(args.get("quick", False)))
        elif method == "proxy":
            result = spider.localProxy(args.get("params", {}))
            if isinstance(result, (list, tuple)) and len(result) >= 3 and isinstance(result[2], bytes):
                result = list(result)
                result[2] = base64.b64encode(result[2]).decode("ascii")
                result.append({"terryBase64": True})
        else:
            raise ValueError("unknown method: " + method)
    return result if isinstance(result, str) else json.dumps(result, ensure_ascii=False)


for line in sys.stdin:
    try:
        req = json.loads(line.lstrip("\ufeff"))
        data = call(req)
        print(json.dumps({"ok": True, "data": data}, ensure_ascii=False), flush=True)
    except BaseException as exc:
        print(json.dumps({"ok": False, "error": str(exc),
                          "trace": traceback.format_exc(limit=5)}, ensure_ascii=False), flush=True)
