# -*- coding: utf-8 -*-
"""TVBox Python 插件基类（桌面实现）。

影视仓 APP 里的 base.spider 依赖 Android(Chaquopy) 环境，这里按插件实际用到的接口重写：
Spider 抽象方法 + fetch/post/regStr/cleanText/html 等常用辅助，以及代理地址占位。
"""
import json
import re
from abc import ABCMeta, abstractmethod

import requests

_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"


class Spider(metaclass=ABCMeta):
    _instance = None

    def __init__(self):
        self.extend = ""

    def __new__(cls, *args, **kwargs):
        if cls._instance:
            return cls._instance
        cls._instance = super().__new__(cls)
        return cls._instance

    @abstractmethod
    def init(self, extend=""):
        pass

    # ---- TVBox 约定的入口，插件按需覆盖 ----
    def getName(self):
        return ""

    def getDependence(self):
        return []

    def homeContent(self, filter):
        pass

    def homeVideoContent(self):
        pass

    def categoryContent(self, tid, pg, filter, extend):
        pass

    def detailContent(self, ids):
        pass

    def searchContent(self, key, quick, pg="1"):
        pass

    def playerContent(self, flag, id, vipFlags):
        pass

    def liveContent(self, url):
        pass

    def localProxy(self, param):
        pass

    def isVideoFormat(self, url):
        pass

    def manualVideoCheck(self):
        pass

    def action(self, action):
        pass

    def destroy(self):
        pass

    # ---- 常用辅助 ----
    def fetch(self, url, params=None, cookies=None, headers=None, timeout=5, verify=True, stream=False, allow_redirects=True):
        h = {"User-Agent": _UA}
        h.update(headers or {})
        return requests.get(url, params=params, cookies=cookies, headers=h, timeout=timeout, verify=verify,
                            stream=stream, allow_redirects=allow_redirects)

    def post(self, url, params=None, data=None, json=None, cookies=None, headers=None, timeout=5, verify=True, stream=False, allow_redirects=True):
        h = {"User-Agent": _UA}
        h.update(headers or {})
        return requests.post(url, params=params, data=data, json=json, cookies=cookies, headers=h, timeout=timeout,
                             verify=verify, stream=stream, allow_redirects=allow_redirects)

    def html(self, content):
        from lxml import etree
        return etree.HTML(content)

    def regStr(self, reg, src, group=1):
        m = re.search(reg, src)
        return m.group(group) if m else ""

    def cleanText(self, src):
        return re.sub(r"[\U0001F600-\U0001F64F\U0001F300-\U0001F5FF\U0001F680-\U0001F6FF\U0001F1E0-\U0001F1FF]", "", src)

    def str2json(self, s):
        return json.loads(s)

    def log(self, msg):
        print(msg)

    # ---- 代理：桌面端没有 APP 的本地代理服务，原样返回地址，由主服务的 /api/proxy 负责转发 ----
    def getProxyUrl(self, local=True):
        return "http://127.0.0.1:0/proxy?do=py"

    def proxyM3u8(self, url, *a, **k):
        return url

    def proxyMedia(self, url, *a, **k):
        return url

    def proxyTs(self, url, *a, **k):
        return url
