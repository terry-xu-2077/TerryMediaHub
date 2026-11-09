#!/usr/bin/env python3
"""修正 CFR 对「扁平内部类」桩的反编译结果。

早期构建的 Android 桩把内部类存成了顶层类（文件名含 `$`，如 DialogInterface$OnClickListener），
CFR 输出的引用却写成 `DialogInterface.OnClickListener`，javac 找不到。
这里把 `Outer.Inner` 引用改回 `Outer$Inner`（仅当存在同名顶层类文件时）。

用法: python scripts/fix_decompiled.py <源码根目录>
"""
import os
import re
import sys

root = sys.argv[1]
files = []
for d, _, fs in os.walk(root):
    for f in fs:
        if f.endswith(".java"):
            files.append(os.path.join(d, f))

pairs = set()
for f in files:
    base = os.path.basename(f)[:-5]
    if "$" in base:
        parts = base.split("$")
        pairs.add(("$".join(parts[:-1]), parts[-1]))

changed = 0
for f in files:
    s = open(f, encoding="utf-8").read()
    o = s
    for outer, inner in pairs:
        s = re.sub(r"(?<![\w$.])" + re.escape(outer) + r"\." + re.escape(inner) + r"(?![\w$])", outer + "$" + inner, s)
    if s != o:
        open(f, "w", encoding="utf-8").write(s)
        changed += 1
print("rewrote", changed, "files")

# ---- 第二步：补 import ----
# 扁平内部类被写成 Outer$Inner 后，若与引用方不在同一个包，需要显式 import。
flat = {}  # 简单名 → 包
for f in files:
    base = os.path.basename(f)[:-5]
    if "$" in base:
        flat[base] = os.path.dirname(os.path.relpath(f, root)).replace(os.sep, ".")

added = 0
for f in files:
    s = open(f, encoding="utf-8").read()
    pkg = os.path.dirname(os.path.relpath(f, root)).replace(os.sep, ".")
    need = []
    for name, p in flat.items():
        if p == pkg:
            continue
        if re.search(r"(?<![\w$.])" + re.escape(name) + r"(?![\w$])", s) and ("import " + p + "." + name + ";") not in s:
            need.append("import " + p + "." + name + ";")
    if need:
        s = re.sub(r"(package [^;]+;\n)", lambda m: m.group(1) + "\n" + "\n".join(need) + "\n", s, count=1)
        open(f, "w", encoding="utf-8").write(s)
        added += 1
print("added imports in", added, "files")
