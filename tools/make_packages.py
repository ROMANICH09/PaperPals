"""Builds every PaperPals upload into dist/:
  PaperPals-<v>.zip            library: manifest/README/CHANGELOG/icon at the root, plugins/PaperPals.dll,
                               plugins/Template/... (kept as a sub-folder next to the DLL by mod managers)
  <Addon>-<v>.zip              one per folder in addons/: root files + npc.json + frames/
  PaperPals_AddonTemplate.zip  the template on its own (for authors)
Validates Thunderstore manifest rules and the npc.json/frames rules. Run: python -I tools/make_packages.py"""
import json, os, re, sys, zipfile
from PIL import Image

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
dist = os.path.join(root, "dist"); os.makedirs(dist, exist_ok=True)
VIEWS, MOUTHS = ["front", "back", "left", "right"], ["quiet", "talking"]
REQUIRED = [f"{v}_{p}_{m}.png" for v in VIEWS for p in ("idle", "step-a", "step-b") for m in MOUTHS]
errors = []

def check_manifest(path, expect_dep=None):
    m = json.load(open(path, encoding="utf-8"))
    where = os.path.relpath(path, root)
    if not re.fullmatch(r"[A-Za-z0-9_]{1,128}", m.get("name", "")): errors.append(f"{where}: bad name")
    if not re.fullmatch(r"\d+\.\d+\.\d+", m.get("version_number", "")): errors.append(f"{where}: bad version")
    if len(m.get("description", "")) > 250: errors.append(f"{where}: description {len(m['description'])} > 250")
    if "website_url" not in m: errors.append(f"{where}: website_url missing")
    for d in m.get("dependencies", []):
        if not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", d): errors.append(f"{where}: bad dependency {d}")
    if expect_dep and expect_dep not in m.get("dependencies", []): errors.append(f"{where}: must depend on {expect_dep}")
    return m

def check_icon(path):
    im = Image.open(path)
    if im.size != (256, 256) or im.format != "PNG": errors.append(f"{os.path.relpath(path, root)}: icon must be 256x256 PNG")

def zip_dir_files(z, src_dir, arc_prefix, skip=()):
    for dp, dns, fns in os.walk(src_dir):
        for f in sorted(fns):
            if f in skip: continue
            p = os.path.join(dp, f)
            z.write(p, (arc_prefix + os.path.relpath(p, src_dir)).replace("\\", "/"))

def finish(out):
    with zipfile.ZipFile(out) as z:
        names = z.namelist()
        assert all("\\" not in n for n in names)
        bad = [n for n in names if n.lower().endswith((".wav", ".opus", ".ogg", ".mp3"))]
        if bad: errors.append(f"{os.path.basename(out)}: contains audio {bad[:3]}")
    print(f"  {os.path.basename(out):34s} {len(names):3d} files  {os.path.getsize(out)/1024/1024:5.1f} MB")

# ---------------------------------------------------------------- library
pkg = os.path.join(root, "package")
lib = check_manifest(os.path.join(pkg, "manifest.json"))
check_icon(os.path.join(pkg, "icon.png"))
dll = os.path.join(root, "src", "bin", "Release", "PaperPals.dll")
if not os.path.isfile(dll): errors.append("missing " + dll)
tpl = os.path.join(root, "Template")
if os.path.exists(os.path.join(tpl, "npc.json")): errors.append("Template must not contain npc.json (it would spawn)")
lib_dep = f"Luver_Inn-{lib['name']}-{lib['version_number']}"

# ---------------------------------------------------------------- addons (validate first)
addons = []
addons_dir = os.path.join(root, "addons")  # optional: the public repo ships only the library and template
for a in (sorted(os.listdir(addons_dir)) if os.path.isdir(addons_dir) else []):
    d = os.path.join(root, "addons", a)
    m = check_manifest(os.path.join(d, "manifest.json"), expect_dep=lib_dep)
    check_icon(os.path.join(d, "icon.png"))
    for f in ("README.md", "npc.json"):
        if not os.path.isfile(os.path.join(d, f)): errors.append(f"{a}: missing {f}")
    npc = json.load(open(os.path.join(d, "npc.json"), encoding="utf-8"))
    if npc.get("format") != 1: errors.append(f"{a}: npc.json format must be 1")
    if not re.fullmatch(r"[a-z0-9_]+", npc.get("id", "")): errors.append(f"{a}: bad id {npc.get('id')}")
    frames = {f for dp, _, fns in os.walk(d) for f in fns if f.endswith(".png")}
    missing = [f for f in REQUIRED if f not in frames]
    if missing: errors.append(f"{a}: missing frames {missing[:4]}")
    addons.append((a, d, m, npc))
ids = [n["id"] for *_, n in addons]
if len(ids) != len(set(ids)): errors.append(f"duplicate ids: {ids}")

if errors:
    print("\n".join("ERROR: " + e for e in errors)); sys.exit(1)

print("Packages:")
out = os.path.join(dist, f"{lib['name']}-{lib['version_number']}.zip")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in ("manifest.json", "README.md", "CHANGELOG.md", "icon.png"):
        z.write(os.path.join(pkg, f), f)
    z.write(dll, "plugins/PaperPals.dll")
    zip_dir_files(z, tpl, "plugins/Template/")
finish(out)

for a, d, m, npc in addons:
    out = os.path.join(dist, f"{m['name']}-{m['version_number']}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        zip_dir_files(z, d, "")
    finish(out)
    print(f"      id={npc['id']} voice='{npc['voice'].get('targetPlayer', '')}' heightCm={npc['sprite'].get('heightCm')} eyeLevel={npc['sprite'].get('eyeLevel')}")

out = os.path.join(dist, "PaperPals_AddonTemplate.zip")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    zip_dir_files(z, tpl, "Template/")
finish(out)

if errors:
    print("\n".join("ERROR: " + e for e in errors)); sys.exit(1)
print("All packages valid.")
