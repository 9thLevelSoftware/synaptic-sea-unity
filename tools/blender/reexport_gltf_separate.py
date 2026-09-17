"""Re-export structural ``*_textured.glb`` files as glTF Separate (.gltf + .bin + texture files).

Unity port, plan Phase 7. glTFast imports textures embedded in a GLB as sub-assets, so the Unity texture import
presets (BC7, max size, sRGB/linear by suffix) cannot apply to them. Re-exporting as glTF Separate writes each
texture as its own image file, which Unity imports with the project presets.

Runs inside Blender's bundled Python (no pip packages):

    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" --background --factory-startup ^
        --python tools/blender/reexport_gltf_separate.py -- ^
        --input SynapticSea/Assets/Content/Structural/ship_structural_v0 --output <dir> [--pattern *_textured.glb] [--dry-run]

``--input`` is one ``.glb`` or a directory searched recursively for ``--pattern``. Each file is written to
``<output>/<parent folder>/<stem>.gltf`` with its ``.bin`` beside it and images under ``textures/``. The scene is
reset to factory settings before every file, so exports do not leak objects into each other.
Prints ``GLTF_SEPARATE_EXPORTED name=... gltf=... bin=... textures=N`` per file and
``GLTF_SEPARATE_SUMMARY files=N failed=N``; exits 1 when any file fails.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:
    import bpy
except ModuleNotFoundError:  # imported outside Blender (argument parsing and planning stay testable)
    bpy = None

TEXTURE_DIR = "textures"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Re-export GLBs as glTF Separate.")
    parser.add_argument("--input", required=True, type=Path, help="a .glb file or a directory to search")
    parser.add_argument("--output", required=True, type=Path, help="output root directory")
    parser.add_argument("--pattern", default="*_textured.glb", help="glob used when --input is a directory")
    parser.add_argument("--dry-run", action="store_true", help="list the planned exports without running them")
    return parser.parse_args(argv)


def script_argv(argv: list[str]) -> list[str]:
    """Arguments after Blender's ``--`` separator (all of them when run outside Blender)."""

    return argv[argv.index("--") + 1:] if "--" in argv else argv[1:]


def plan_exports(input_path: Path, output_root: Path, pattern: str) -> list[tuple[Path, Path]]:
    """(source .glb, target .gltf) pairs in a stable order."""

    if input_path.is_file():
        sources = [input_path]
    elif input_path.is_dir():
        sources = sorted(input_path.rglob(pattern), key=lambda p: p.as_posix())
    else:
        raise FileNotFoundError(f"input not found: {input_path}")
    return [(src, output_root / src.parent.name / (src.stem + ".gltf")) for src in sources]


def export_one(source: Path, target: Path) -> dict[str, object]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = bpy.ops.import_scene.gltf(filepath=str(source))
    if "FINISHED" not in result:
        raise RuntimeError(f"import failed: {result}")
    target.parent.mkdir(parents=True, exist_ok=True)
    result = bpy.ops.export_scene.gltf(
        filepath=str(target),
        export_format="GLTF_SEPARATE",
        export_texture_dir=TEXTURE_DIR,
        export_image_format="AUTO",
        export_apply=True,
        export_yup=True,
        use_selection=False,
    )
    if "FINISHED" not in result:
        raise RuntimeError(f"export failed: {result}")
    bin_path = target.with_suffix(".bin")
    if not target.is_file() or not bin_path.is_file():
        raise RuntimeError(f"export wrote no .gltf/.bin pair for {target}")
    textures = sorted((target.parent / TEXTURE_DIR).glob("*")) if (target.parent / TEXTURE_DIR).is_dir() else []
    return {"gltf": target, "bin": bin_path, "textures": len(textures)}


def main(argv: list[str]) -> int:
    args = parse_args(script_argv(argv))
    plan = plan_exports(args.input, args.output, args.pattern)
    if not plan:
        print(f"GLTF_SEPARATE_SUMMARY files=0 failed=0 (no match for {args.pattern} under {args.input})")
        return 1
    if args.dry_run:
        for source, target in plan:
            print(f"GLTF_SEPARATE_PLAN source={source} gltf={target}")
        print(f"GLTF_SEPARATE_SUMMARY files={len(plan)} failed=0 dry_run=true")
        return 0
    if bpy is None:
        print("ERROR: run inside Blender: blender --background --python reexport_gltf_separate.py -- ...")
        return 2
    failed = 0
    for source, target in plan:
        try:
            info = export_one(source, target)
            print(f"GLTF_SEPARATE_EXPORTED name={source.stem} gltf={info['gltf']} bin={info['bin']} textures={info['textures']}")
        except Exception as exc:  # report every file, fail at the end
            failed += 1
            print(f"GLTF_SEPARATE_FAILED name={source.stem} error={exc}")
    print(f"GLTF_SEPARATE_SUMMARY files={len(plan)} failed={failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    code = main(sys.argv)
    if bpy is not None:
        # Blender ignores the script's return value; exit explicitly so callers see failures.
        sys.stdout.flush()
        import os

        os._exit(code)
    sys.exit(code)
