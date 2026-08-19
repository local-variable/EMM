"""#19 -- what tax-related symbols exist in the Dalamud build actually installed here?

Source on GitHub is the primary reference, but the INSTALLED assemblies are what EMM
compiles against, so they are the binding version. Scan the managed DLLs' metadata
strings for anything containing "Tax".
"""
import os, re, glob
from collections import defaultdict

ADDON = os.path.expandvars(r"%APPDATA%\XIVLauncher\addon\Hooks\dev")
TARGETS = ["Dalamud.dll", "FFXIVClientStructs.dll", "Lumina.Excel.dll", "Lumina.dll"]

pat = re.compile(rb"[\x20-\x7e]{4,}")

def strings_with(path, needle):
    try:
        data = open(path, "rb").read()
    except OSError as e:
        return None, [f"<unreadable: {e}>"]
    hits = set()
    for m in pat.finditer(data):
        s = m.group().decode("ascii", "replace")
        if needle in s:
            hits.add(s)
    # UTF-16LE too (managed string literals)
    try:
        u = data.decode("utf-16-le", "ignore")
        for m in re.finditer(r"[\x20-\x7e]{4,}", u):
            s = m.group()
            if needle in s:
                hits.add(s)
    except Exception:
        pass
    return len(data), sorted(hits)

print("Dalamud addon dir:", ADDON, "\n")
if not os.path.isdir(ADDON):
    print("  NOT FOUND"); raise SystemExit

for name in TARGETS:
    p = os.path.join(ADDON, name)
    if not os.path.exists(p):
        print(f"== {name}: absent ==\n"); continue
    size, hits = strings_with(p, "Tax")
    print(f"== {name}  ({size:,} bytes) — {len(hits)} strings containing 'Tax' ==")
    for h in hits:
        if len(h) < 120:
            print("   ", h)
    print()
