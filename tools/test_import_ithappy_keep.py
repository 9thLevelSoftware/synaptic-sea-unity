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


def test_copy_tree_allowlists_inert_assets() -> None:
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        src = tmp / "godot"
        repo = tmp / "repo"
        dst = repo / "SynapticSea/Assets/Content/Structural/ithappy"
        (src / "floor").mkdir(parents=True)
        (src / "floor" / "floor.glb").write_text("glb", encoding="utf-8")
        (src / "floor" / "floor_Color.png").write_bytes(b"png")
        (src / "floor" / "note.json").write_text("{}", encoding="utf-8")
        (src / "floor" / "floor.glb.meta").write_text("supplier-meta", encoding="utf-8")
        (src / "Editor").mkdir()
        (src / "Editor" / "Payload.cs").write_text("class Payload {}", encoding="utf-8")
        (src / "Plugins").mkdir()
        (src / "Plugins" / "evil.dll").write_bytes(b"MZ")
        (src / "sneaky.cs").write_text("class Sneaky {}", encoding="utf-8")
        link = src / "linked.glb"
        try:
            link.symlink_to(src / "floor" / "floor.glb")
        except OSError:
            link = None
        copied = imp.copy_tree_assets(src, dst, "structural", repo)
        names = {p.relative_to(dst).as_posix() for p in copied}
        t("copies glb", "floor/floor.glb" in names)
        t("copies png", "floor/floor_Color.png" in names)
        t("copies json", "floor/note.json" in names)
        t("skips supplier meta", "floor/floor.glb.meta" not in names)
        t("skips scripts", "sneaky.cs" not in names)
        t("skips Editor payload", "Editor/Payload.cs" not in names)
        t("does not create Editor folder", not (dst / "Editor").exists())
        t("skips Plugins dll", "Plugins/evil.dll" not in names)
        if link is not None:
            t("skips symlinks", "linked.glb" not in names)


def test_wall_x_asset_json_matches_kit_authority() -> None:
    companion = imp.wall_x_asset_json()
    module = imp.wall_x_module()
    t("document_kind is structural_art_package", companion["document_kind"] == "structural_art_package")
    t("module_id", companion["module_id"] == "wall_x_junction")
    t("module_family", companion["module_family"] == module["module_family"])
    t("footprint_cells", companion["footprint_cells"] == module["footprint_cells"])
    t(
        "socket_names are kit SOCK_wall_face_*",
        companion["socket_names"]
        == [
            "SOCK_wall_face_north_01",
            "SOCK_wall_face_east_01",
            "SOCK_wall_face_south_01",
            "SOCK_wall_face_west_01",
        ],
    )
    t("pivot_policy", companion["pivot_policy"] == module["pivot_policy"])
    t("nav_blocker", companion["nav_blocker"] is True)
    t("collision_note present", bool(companion.get("collision_note")))


def test_overlay_without_root_returns_keep_text() -> None:
    keep = '[gd_scene load_steps=1 format=3]\n\n[node name="Visual" type="MeshInstance3D"]\n'
    v0 = '[sub_resource type="BoxShape3D" id="1"]\nsize = Vector3(1, 1, 1)\n[node name="CollisionRoot"]\n[node name="Visual"]\n'
    t("missing Node3D root keeps text", imp.overlay_v0_collision(keep, v0) == keep)


test_guid_is_repo_relative()
test_guid_relative_vs_absolute_repo()
test_dot_repo_matches_absolute()
test_write_meta_stable_across_clones()
test_write_meta_keeps_existing_guid()
test_write_catalog_creates_then_preserves()
test_copy_tree_allowlists_inert_assets()
test_wall_x_asset_json_matches_kit_authority()
test_overlay_without_root_returns_keep_text()
print("OK")
