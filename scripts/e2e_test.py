import json, urllib.request, urllib.error, urllib.parse, time, sys, os

BASE = "http://127.0.0.1:8097"
PLUGIN_ID = "3f5c8a41-9d2e-4b76-8c15-6a0e7d94b2f1"
H = {
    "X-Emby-Authorization": 'MediaBrowser Client="test", Device="cli", DeviceId="dev1", Version="1.0.0"',
    "Content-Type": "application/json",
    "Accept": "application/json",
}


def req(path, data=None, method=None, token=None):
    body = None if data is None else json.dumps(data).encode()
    r = urllib.request.Request(BASE + path, data=body,
                               method=method or ("POST" if data is not None else "GET"),
                               headers=dict(H))
    if token:
        r.add_header("X-Emby-Token", token)
    try:
        with urllib.request.urlopen(r, timeout=180) as resp:
            b = resp.read()
            return resp.status, (json.loads(b) if b else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read()[:400].decode("utf8", "ignore")


# 1. 等待就绪
for i in range(60):
    try:
        st, info = req("/System/Info/Public")
        if st == 200:
            break
    except Exception:
        pass
    time.sleep(2)
else:
    print("server not ready")
    sys.exit(1)

print("ready:", info.get("Version"), "wizard:", info.get("StartupWizardCompleted"))

if not info.get("StartupWizardCompleted"):
    print("create user:", req("/Startup/User?name=admin&password=admin123"))
    print("complete:", req("/Startup/Complete", {})[0])

st, res = 0, None
for _user, _pw in [("Administrator", ""), ("admin", ""), ("admin", "admin123")]:
    st, res = req("/Users/AuthenticateByName", {"Username": _user, "Pw": _pw})
    if st == 200:
        print("login as", _user)
        break
if st != 200:
    print("auth failed", st, res)
    sys.exit(1)

tok = res["AccessToken"]
uid = res["User"]["Id"]
print("token ok")

# 2. 缩小同步规模，方便快速验证
st, cfg = req("/Plugins/%s/Configuration" % PLUGIN_ID, token=tok)
print("get cfg:", st)
cfg["MaxPagesPerCategory"] = 1
cfg["MaxCategoriesPerSite"] = 3
cfg["MaxItemsPerSite"] = 24
cfg["VerifyPlayUrls"] = True
cfg["LibraryName"] = "在线媒体库"
cfg["EnableJarSpider"] = True
cfg["HealthTimeoutMs"] = 12000
st, body = req("/Plugins/%s/Configuration" % PLUGIN_ID, cfg, method="POST", token=tok)
print("save cfg:", st)

# 3. 测试源
st, body = req("/TerryMediaHub/TestSource?url=" + urllib.parse.quote(cfg["Sources"][0]["Url"], safe=""), token=tok)
print("TestSource:", st, body)

# 4. 触发同步
st, body = req("/TerryMediaHub/Sync", {}, token=tok)
print("sync:", st, body)

# 5. 轮询
for i in range(180):
    st, s = req("/TerryMediaHub/Status", token=tok)
    if st != 200:
        print("status err", st, s)
        break
    print("  [%s] running=%s progress=%s phase=%s" % (i, s.get("running"), s.get("progress"), s.get("phase")))
    if not s.get("running"):
        print("SUMMARY:", s.get("lastSummary"))
        print("baseUrl:", s.get("baseUrl"), "root:", s.get("libraryRoot"))
        print("sites total/supported:", s.get("sitesTotal"), s.get("sitesSupported"))
        for n in (s.get("notes") or [])[:10]:
            print("  note:", n)
        for site in s.get("sites", []):
            print("   - %-14s %-10s supported=%s enabled=%s %s" % (
                site["key"][:14], site["kind"], site["supported"], site["enabled"],
                (site.get("reason") or "")[:50]))
        break
    time.sleep(3)

# 6. 媒体库是否自动创建
st, folders = req("/Library/VirtualFolders", token=tok)
print("libraries:", st)
if isinstance(folders, list):
    for f in folders:
        print("  -", f.get("Name"), f.get("CollectionType"), f.get("Locations"))
