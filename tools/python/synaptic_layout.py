"""Godot project paths mapped onto the Unity repository layout.

The tools copied from the Godot repository (``D:\\the-synaptic-sea\\tools``) address files the way the Godot
project laid them out: ``data/...``, ``assets/imported/...``, ``scenes/wrappers/...`` and ``res://`` strings.
The Unity port keeps the data files verbatim (``res://`` strings inside JSON are unchanged) but stores them in
different folders. Every filesystem access in the ported tools goes through this module, so the mapping lives
in one place:

=====================================  ==============================================
Godot project path                     Unity repository path
=====================================  ==============================================
``data/asset_generation/``             ``tools/asset_generation/`` (not runtime data)
``data/``                              ``SynapticSea/Assets/StreamingAssets/data/``
``assets/imported/structural/``        ``SynapticSea/Assets/Content/Structural/``
``assets/imported/props/``             ``SynapticSea/Assets/Content/Props/``
``assets/imported/``                   ``SynapticSea/Assets/Content/``
``assets/_staging/``                   ``artifacts/_staging/`` (git-ignored)
``assets/_review/``                    ``artifacts/_review/`` (git-ignored)
``scenes/wrappers/structural/``        ``fixtures/godot_wrappers/``
=====================================  ==============================================

Stdlib only.
"""

from __future__ import annotations

from pathlib import Path, PurePosixPath

REPO_ROOT = Path(__file__).resolve().parents[2]
RES_SCHEME = "res://"

# Longest prefix first: the first matching entry wins.
GODOT_TO_UNITY: tuple[tuple[str, str], ...] = (
    ("data/asset_generation", "tools/asset_generation"),
    ("data", "SynapticSea/Assets/StreamingAssets/data"),
    ("assets/imported/structural", "SynapticSea/Assets/Content/Structural"),
    ("assets/imported/props", "SynapticSea/Assets/Content/Props"),
    ("assets/imported", "SynapticSea/Assets/Content"),
    ("assets/_staging", "artifacts/_staging"),
    ("assets/_review", "artifacts/_review"),
    ("scenes/wrappers/structural", "fixtures/godot_wrappers"),
)


def _split(path: str) -> tuple[str, ...]:
    return tuple(part for part in PurePosixPath(path.replace("\\", "/")).parts if part not in ("", "."))


def _swap(parts: tuple[str, ...], table: tuple[tuple[str, str], ...]) -> tuple[str, ...] | None:
    for source, target in table:
        prefix = _split(source)
        if parts[: len(prefix)] == prefix:
            return _split(target) + parts[len(prefix):]
    return None


def unity_relative(godot_relative: str | PurePosixPath) -> Path:
    """Map a Godot project-relative path to the Unity repository-relative path (unmapped paths pass through)."""

    parts = _split(str(godot_relative))
    mapped = _swap(parts, GODOT_TO_UNITY)
    return Path(*(mapped if mapped is not None else parts)) if parts else Path()


def godot_relative(unity_relative_path: str | PurePosixPath) -> str | None:
    """Inverse of :func:`unity_relative`; ``None`` when the path is outside every mapped folder."""

    inverse = tuple((target, source) for source, target in GODOT_TO_UNITY)
    # Longest Unity prefix first so Content/Structural beats Content.
    inverse = tuple(sorted(inverse, key=lambda item: len(_split(item[0])), reverse=True))
    mapped = _swap(_split(str(unity_relative_path)), inverse)
    return "/".join(mapped) if mapped is not None else None


def project_path(project_root: Path, godot_relative_path: str) -> Path:
    """``project_root / <Godot-relative path>`` in the Unity layout."""

    return Path(project_root) / unity_relative(godot_relative_path)


def res_path_for(project_root: Path, path: Path) -> str:
    """The ``res://`` string Godot data uses for a file stored in the Unity layout.

    Raises ``ValueError`` when the file is not below ``project_root`` or not inside a mapped folder.
    """

    root = Path(project_root).resolve()
    resolved = Path(path).resolve()
    relative = resolved.relative_to(root).as_posix()
    godot = godot_relative(relative)
    if godot is None:
        raise ValueError(f"path is outside the Godot-mapped folders: {relative}")
    return RES_SCHEME + godot


def resolve_res(project_root: Path, res_path: str) -> Path:
    """The Unity-layout file for a ``res://`` string (not resolved against symlinks)."""

    if not res_path.startswith(RES_SCHEME):
        raise ValueError(f"not a res:// path: {res_path}")
    return project_path(project_root, res_path[len(RES_SCHEME):])
