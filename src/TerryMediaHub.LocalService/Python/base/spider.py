"""Desktop subset of the TV6.2.9 Chaquopy base.spider interface.

The method signatures come from the APK's app.pyc and base/spider.pyc.
Includes signed local proxy URLs and HLS/media fetch helpers used by site scripts.
"""
import json
import base64
import hashlib
import hmac
import os
import re
import time
import importlib.util
import requests
from urllib.parse import quote, unquote, urljoin, urlparse
from lxml import etree

_cache = {}


class Spider:
    def __init__(self):
        self.extend = ""
        self.siteKey = ""

    def init(self, extend=""):
        self.extend = extend

    def getDependence(self):
        return []

    def loadSpider(self, name):
        return self.loadModule(name).Spider()

    def loadModule(self, name):
        path = os.path.join(os.environ["TERRY_PY_CACHE"], name + ".py")
        spec = importlib.util.spec_from_file_location(name, path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def regStr(self, reg, src, group=1):
        match = re.search(reg, src)
        return match.group(group) if match else ""

    def removeHtmlTags(self, src):
        return re.sub("<.*?>", "", src)

    def cleanText(self, src):
        return re.sub("[😀-🙏🌀-🗿🚀-\U0001f6ff\U0001f1e0-🇿]", "", src)

    def fetch(self, url, params=None, cookies=None, headers=None, timeout=5,
              verify=True, stream=False, allow_redirects=True):
        response = requests.get(url, params=params, cookies=cookies, headers=headers,
                                timeout=timeout, verify=verify, stream=stream,
                                allow_redirects=allow_redirects)
        response.encoding = "utf-8"
        return response

    def post(self, url, params=None, data=None, json=None, cookies=None, headers=None,
             timeout=5, verify=True, stream=False, allow_redirects=True):
        response = requests.post(url, params=params, data=data, json=json, cookies=cookies,
                                 headers=headers, timeout=timeout, verify=verify,
                                 stream=stream, allow_redirects=allow_redirects)
        response.encoding = "utf-8"
        return response

    def html(self, content):
        return etree.HTML(content)

    def str2json(self, content):
        return json.loads(content)

    def json2str(self, content):
        return json.dumps(content, ensure_ascii=False)

    def getProxyUrl(self, local=True):
        secret = os.environ.get("TERRY_SERVICE_KEY", "")
        if not secret or not self.siteKey:
            return ""
        descriptor = json.dumps({"lang": "py", "site": self.siteKey,
                                 "api": getattr(self, "siteApi", ""),
                                 "ext": getattr(self, "siteExt", ""), "type": 3},
                                ensure_ascii=False, separators=(",", ":"))
        encoded = base64.urlsafe_b64encode((descriptor + "\n").encode()).decode().rstrip("=")
        signature = hmac.new(secret.encode(), encoded.encode(), hashlib.sha256).digest()[:4].hex()
        return "http://127.0.0.1:18766/site-proxy/" + encoded + "-" + signature + "?do=py"

    def _proxy_request(self, params):
        url = unquote(str(params.get("url", "")))
        if urlparse(url).scheme not in ("http", "https"):
            raise ValueError("代理缺少有效 HTTP 地址")
        headers = params.get("header") or params.get("headers") or {}
        if isinstance(headers, str):
            try:
                headers = json.loads(headers)
            except ValueError:
                headers = {}
        return url, requests.get(url, headers=headers if isinstance(headers, dict) else {}, timeout=20)

    def proxyM3u8(self, params):
        url, response = self._proxy_request(params)
        response.raise_for_status()
        playlist = response.content.decode("utf-8-sig", errors="replace")
        if not playlist.lstrip().startswith("#EXTM3U"):
            raise ValueError("代理上游未返回 HLS 播放列表")
        base = self.getProxyUrl()
        headers = params.get("header") or params.get("headers")

        def proxied(value, kind=None):
            absolute = urljoin(url, value)
            media_kind = kind or ("m3u8" if urlparse(absolute).path.lower().endswith(".m3u8") else "ts")
            target = base + "&type=" + media_kind + "&url=" + quote(absolute, safe="")
            if headers:
                target += "&header=" + quote(headers if isinstance(headers, str) else json.dumps(headers), safe="")
            return target

        lines = []
        for line in playlist.splitlines():
            if line.startswith("#"):
                line = re.sub(r'URI="([^"]+)"',
                              lambda match: 'URI="' + proxied(match.group(1), "media") + '"', line)
            elif line.strip():
                line = proxied(line.strip())
            lines.append(line)
        return [200, "application/vnd.apple.mpegurl", ("\n".join(lines) + "\n").encode("utf-8")]

    def proxyMedia(self, params):
        _, response = self._proxy_request(params)
        return [response.status_code,
                response.headers.get("Content-Type", "application/octet-stream"), response.content]

    def proxyTs(self, params):
        return self.proxyMedia(params)

    def log(self, msg):
        print(json.dumps(msg, ensure_ascii=False) if isinstance(msg, (dict, list)) else msg)

    def getCache(self, key):
        value = _cache.get(key)
        if isinstance(value, dict) and value.get("expiresAt", time.time() + 1) < time.time():
            _cache.pop(key, None)
            return None
        return value

    def setCache(self, key, value):
        _cache[key] = value
        return "succeed"

    def delCache(self, key):
        _cache.pop(key, None)
        return "succeed"
