"""Import supported MegaKit floor visuals from a local licensed ZIP."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path, PurePosixPath

SOURCES = ("Platform_Simple.gltf", "Platform_Squares.gltf")

def import_floors(archive, repo):
    destination = repo / "SynapticSea/Assets/Content/Purchased/MegaKit"
    destination.mkdir(parents=True, exist_ok=True)
    manifest = []
    with zipfile.ZipFile(archive) as package:
        for model in SOURCES:
            document = json.loads(package.read("glTF (Godot)/Platforms/" + model))
            dependencies = [model]
            for group in ("buffers", "images"):
                for record in document.get(group, []):
                    uri = record.get("uri", "")
                    if not uri or uri.startswith("data:"):
                        continue
                    relative = PurePosixPath(uri)
                    if relative.is_absolute() or ".." in relative.parts or chr(92) in uri:
                        raise ValueError("Unsafe dependency: " + uri)
                    dependencies.append(uri)
            for relative in dependencies:
                source = "glTF (Godot)/Platforms/" + relative
                if source not in package.namelist():
                    source = "glTF (Godot)/" + relative
                data = package.read(source)
                target = destination / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                manifest.append({"archive_path": source, "local_path": str(target.relative_to(repo)),
                                 "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)})
    output = repo / "artifacts/purchased-megakit-source-manifest.json"
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps({"archive": str(archive), "sources": manifest}, indent=2))
    return manifest

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args()
    print("Imported supported floor sources locally:", len(import_floors(args.archive, args.repo.resolve())))
