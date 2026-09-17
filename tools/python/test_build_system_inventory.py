import json
import build_system_inventory as b

def t(name, cond):
    assert cond, f"FAIL: {name}"

# closed-loop + sufficient content = 100
closed = {"kind":"simulation","model_exists":True,"reachable":True,"driven":True,
          "input":{"live":True},"output":{"live":True},"content":"sufficient","subsystems":[]}
t("closed=100", b.leaf_completion(closed) == 100)
t("closed coupling", b.derive_coupling(closed) == "closed")

# hollow (no input, no output) + partial -> raw 55, capped to 50
hollow = {"kind":"simulation","model_exists":True,"reachable":True,"driven":True,
          "input":{"live":False},"output":{"live":False},"content":"partial","subsystems":[]}
t("hollow capped 50", b.leaf_completion(hollow) == 50)
t("hollow coupling", b.derive_coupling(hollow) == "hollow")

# half, input live only + partial -> raw 72.5 -> output dead -> capped 50
half_in = {"kind":"simulation","model_exists":True,"reachable":True,"driven":True,
           "input":{"live":True},"output":{"live":False},"content":"partial","subsystems":[]}
t("half-in capped 50", b.leaf_completion(half_in) == 50)
t("half coupling", b.derive_coupling(half_in) == "half")

# half, output live only + none content -> 15+15+15+17.5 = 62.5 -> no cap -> 63
half_out = {"kind":"simulation","model_exists":True,"reachable":True,"driven":True,
            "input":{"live":False},"output":{"live":True},"content":"none","subsystems":[]}
t("half-out 63", b.leaf_completion(half_out) == 63)

# infra excluded from completion
infra = {"kind":"infra","input":{"live":False},"output":{"live":False},"subsystems":[]}
t("infra none", b.leaf_completion(infra) is None)
t("infra coupling na", b.derive_coupling(infra) == "na")

# parent rollup = mean of subsystem completions
parent = {"kind":"simulation","subsystems":[closed, half_out]}  # mean(100,63)=81.5 -> 82
t("parent rollup", b.system_completion(parent) == 82)

# validate: dangling integration target
bad_ref = {"systems":[{"id":"a","file":"tools/build_system_inventory.py","kind":"simulation",
  "confidence":"V","input":{"live":True},"output":{"live":True},
  "integrations":[{"to":"ghost"}],"subsystems":[]}], "loops":[]}
errs = b.validate(bad_ref, ".")
t("dangling ref caught", any("ghost" in e for e in errs))

# validate: simulation system with confidence '?'
unsure = {"systems":[{"id":"a","file":"tools/build_system_inventory.py","kind":"simulation",
  "confidence":"?","input":{"live":True},"output":{"live":True},"integrations":[],"subsystems":[]}], "loops":[]}
t("confidence ? caught", any("confidence" in e for e in b.validate(unsure, ".")))

# validate: missing file
missing = {"systems":[{"id":"a","file":"scripts/systems/DOES_NOT_EXIST.gd","kind":"simulation",
  "confidence":"V","input":{"live":True},"output":{"live":True},"integrations":[],"subsystems":[]}], "loops":[]}
t("missing file caught", any("DOES_NOT_EXIST" in e for e in b.validate(missing, ".")))

# validate: clean fixture passes
with open("tools/fixtures/inventory_min.json", encoding="utf-8") as f:
    clean = json.load(f)
t("clean fixture valid", b.validate(clean, ".") == [])

# null-safe: input/output explicitly null must not raise
nullio = {"kind":"simulation","model_exists":True,"reachable":True,"driven":True,
          "input":None,"output":None,"content":"none","subsystems":[]}
t("null io coupling hollow", b.derive_coupling(nullio) == "hollow")
t("null io completion capped", b.leaf_completion(nullio) == 45)  # 15+15+15, output dead -> <=50

# validate: system missing 'id' is reported, not crashed on
noid = {"systems":[{"file":"tools/build_system_inventory.py","kind":"simulation","confidence":"V",
  "input":{"live":True},"output":{"live":True},"integrations":[],"subsystems":[]}], "loops":[]}
t("missing id caught", any("missing 'id'" in e for e in b.validate(noid, ".")))

# subsystems appear as flattened rows in the map payload (not just top-level)
nested = {"systems":[{"id":"parent","file":"tools/build_system_inventory.py","name":"Parent","domain":"d",
  "kind":"simulation","confidence":"V","input":{"live":True},"output":{"live":True},"integrations":[],
  "subsystems":[{"id":"child","file":"tools/build_system_inventory.py","name":"Child","kind":"simulation",
   "confidence":"V","input":{"live":True},"output":{"live":True},"integrations":[],"subsystems":[]}]}], "loops":[]}
enr = b._enriched(nested)
t("subsystem flattened into rows", any(r["id"] == "child" for r in enr["systems"]))
t("subsystem carries parent", any(r.get("_parent") == "parent" for r in enr["systems"] if r["id"] == "child"))
t("subsystem inherits domain", any(r.get("domain") == "d" for r in enr["systems"] if r["id"] == "child"))

# apostrophe-safe HTML: no inline JSON-in-onclick (data with an apostrophe must not break the handler)
apos = {"systems":[{"id":"a","file":"tools/build_system_inventory.py","name":"end_run('death')","domain":"d",
  "kind":"simulation","confidence":"V","input":{"live":True},"output":{"live":True},"integrations":[],"subsystems":[]}], "loops":[]}
ahtml = b.render_html(apos)
t("no inline JSON onclick", "onclick='detail(" not in ahtml)
t("uses data-index handler", 'data-i="' in ahtml)

md = b.render_markdown(clean)
t("md has banner", "GENERATED" in md.upper())
t("md lists alpha", "Alpha" in md and "100%" in md)
t("md shows beta cap", "Beta" in md and "50%" in md)        # half-in capped
t("md matrix has edge", "alpha" in md and "beta" in md)

html = b.render_html(clean)
t("html self-contained", "<!DOCTYPE html" in html and "http://" not in html.replace("http://www.w3.org",""))
t("html embeds data", '"alpha"' in html or "alpha" in html)
t("html has card view", "card" in html.lower())
t("html has matrix tab", "matrix" in html.lower())

cov = b.coverage({"systems":[{"id":"x","file":"tools/build_system_inventory.py","subsystems":[]}],"loops":[]},
                 ".", ["tools"])
t("coverage finds gap", any("test_build_system_inventory.py" in c for c in cov))

print("BUILD INVENTORY SELFTEST PASS")
