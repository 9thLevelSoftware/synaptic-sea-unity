"""Self-test for import_ithappy_keep.py. Run from anywhere:

    python3 tools/test_import_ithappy_keep.py
"""
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import import_ithappy_keep as imp  # noqa: E402

REL = "SynapticSea/Assets/Content/Structural/ithappy/floor_1x1/floor_1x1.glb"


def t(name: str, cond: bool) -> None:
    assert cond, f"FAIL: {name}"


def meta_guid(meta: Path) -> str:
    for line in meta.read_text(encoding="utf-8").splitlines():
        if line.startswith("guid: "):
            return line.split(":", 1)[1].strip()
    raise AssertionError(f"no guid in {meta}")


def test_guid_is_repo_relative() -> None:
    a = Path("/tmp/clone-a")
    b = Path("/home/other/clone-b")
    ga = imp.guid_for_asset("structural", a, a / REL)
    gb = imp.guid_for_asset("structural", b, b / REL)
    t("guid ignores checkout path", ga == gb)
    t("guid uses posix relative path", ga == imp.guid_for("structural", REL))
    t("different assets differ", ga != imp.guid_for_asset("structural", a, a / "other.glb"))
    t("kind is part of the hash", ga != imp.guid_for_asset("folder", a, a / REL))


def test_guid_relative_vs_absolute_repo() -> None:
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        repo = tmp / "clone"
        asset = repo / REL
        abs_guid = imp.guid_for_asset("structural", repo, asset)
        same_root = imp.guid_for_asset("structural", repo.resolve(), asset)
        t("resolve() does not change the guid", abs_guid == same_root)
        t("relative file under absolute repo is REL", imp.repo_relative(repo, asset) == REL)
        t("relative Path under repo is REL", imp.repo_relative(repo, Path(REL)) == REL)


def test_dot_repo_matches_absolute() -> None:
    with tempfile.TemporaryDirectory() as raw:
        repo = Path(raw) / "clone"
        asset = repo / REL
        asset.parent.mkdir(parents=True)
        asset.write_text("glb", encoding="utf-8")
        g_abs = imp.guid_for_asset("structural", repo.resolve(), asset)
        cwd = os.getcwd()
        try:
            os.chdir(repo)
            g_rel = imp.guid_for_asset("structural", Path("."), Path(REL))
        finally:
            os.chdir(cwd)
        t("--repo . matches absolute --repo", g_abs == g_rel)


def test_write_meta_stable_across_clones() -> None:
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        guids = []
        for name in ("clone-a", "clone-b"):
            repo = tmp / name
            asset = repo / REL
            asset.parent.mkdir(parents=True)
            asset.write_text("glb", encoding="utf-8")
            imp.write_meta_for(asset, "structural", repo)
            guids.append(meta_guid(asset.with_name(asset.name + ".meta")))
        t("two clones write the same guid", guids[0] == guids[1])


def test_write_meta_keeps_existing_guid() -> None:
    with tempfile.TemporaryDirectory() as raw:
        repo = Path(raw)
        asset = repo / REL
        asset.parent.mkdir(parents=True)
        asset.write_text("glb", encoding="utf-8")
        meta = asset.with_name(asset.name + ".meta")
        meta.write_text("fileFormatVersion: 2\nguid: deadbeefcafebabe0123456789abcdef\n", encoding="utf-8")
        imp.write_meta_for(asset, "structural", repo)
        t("existing meta guid is kept", meta_guid(meta) == "deadbeefcafebabe0123456789abcdef")


def test_write_catalog_creates_then_preserves() -> None:
    with tempfile.TemporaryDirectory() as raw:
        repo = Path(raw)
        path = repo / "SynapticSea/Assets/Resources/Catalogs/KitCatalog_ithappy_scifi_v0.asset"
        t("missing catalog is created", imp.write_catalog(path, repo) is None)
        t("empty modules on first write", "modules: []" in path.read_text(encoding="utf-8"))
        t("catalog meta uses NativeFormatImporter", "NativeFormatImporter:" in path.with_name(path.name + ".meta").read_text(encoding="utf-8"))
        populated = path.read_text(encoding="utf-8").replace("modules: []", "modules:\n  - moduleId: floor_1x1")
        path.write_text(populated, encoding="utf-8")
        imp.write_catalog(path, repo)
        t("populated catalog is not wiped", "moduleId: floor_1x1" in path.read_text(encoding="utf-8"))


test_guid_is_repo_relative()
test_guid_relative_vs_absolute_repo()
test_dot_repo_matches_absolute()
test_write_meta_stable_across_clones()
test_write_meta_keeps_existing_guid()
test_write_catalog_creates_then_preserves()
print("OK")
