#!/usr/bin/env python3
"""Synchronize external structural Blender sources to a local or cloud backup.

The source root is treated as the root of the backup tree: every relative file
path below it is copied to the corresponding path below the target.  Local
backups use atomic ``shutil.copy2`` operations; cloud backups delegate the
transfer to the provider's standard sync CLI.

Examples::

    python tools/backup_structural_sources.py \\
        --source-root /Volumes/Untitled/SynapticSeaAssets/meshes/source \\
        --backup-target /Volumes/Backups/SynapticSeaAssets

    python tools/backup_structural_sources.py \\
        --source-root /Volumes/Untitled/SynapticSeaAssets/meshes/source \\
        --backup-target s3://bucket/synaptic-sea/meshes/source \\
        --dry-run
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import filecmp
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Sequence
from urllib.parse import unquote, urlsplit


@dataclass(frozen=True)
class BackupResult:
    """Counts and destination metadata from one backup synchronization."""

    would_sync: int
    uploaded: int
    target: str = ""
    backend: str = "local"


class BackupError(RuntimeError):
    """Raised when a backup cannot be planned or completed."""


@dataclass(frozen=True)
class _BackupTarget:
    backend: str
    value: str
    local_path: Path | None = None


def _parse_target(target: str | os.PathLike[str]) -> _BackupTarget:
    """Classify a local path, ``local://`` URL, S3 URL, or GCS URL."""

    raw = os.fspath(target)
    if not raw.strip():
        raise BackupError("backup target must not be empty")

    parsed = urlsplit(raw)
    if parsed.scheme in {"s3", "gs"}:
        if not parsed.netloc:
            raise BackupError(f"backup target is missing a bucket: {raw}")
        return _BackupTarget(parsed.scheme, raw)

    if parsed.scheme in {"local", "file"}:
        if parsed.netloc and parsed.netloc not in {"", "localhost"}:
            # A non-empty local URL host is a UNC-style path.  Preserve the
            # host rather than silently backing up to a different local path.
            local_path = Path(f"//{parsed.netloc}{unquote(parsed.path)}")
        else:
            local_path = Path(unquote(parsed.path))
        if not str(local_path):
            raise BackupError(f"local backup target is missing a path: {raw}")
        return _BackupTarget("local", raw, local_path)

    if parsed.scheme:
        raise BackupError(
            f"unsupported backup target scheme {parsed.scheme!r}; "
            "use a local path, s3://, or gs://"
        )

    return _BackupTarget("local", raw, Path(raw).expanduser())


def _source_files(source_root: Path) -> list[Path]:
    """Return regular files below ``source_root`` in deterministic order."""

    return sorted(
        (path for path in source_root.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(source_root).as_posix(),
    )


def _validate_source_root(source_root: str | os.PathLike[str]) -> Path:
    root = Path(source_root).expanduser()
    if not root.exists():
        raise BackupError(f"source root does not exist: {root}")
    if not root.is_dir():
        raise BackupError(f"source root is not a directory: {root}")
    return root.resolve()


def _validate_local_target(source_root: Path, target: Path) -> Path:
    resolved_target = target.expanduser().resolve(strict=False)
    if resolved_target == source_root:
        raise BackupError("backup target must differ from source root")
    if source_root in resolved_target.parents:
        raise BackupError("backup target cannot be inside source root")
    return resolved_target


def _needs_local_sync(source: Path, destination: Path) -> bool:
    if not destination.is_file():
        return True
    try:
        return not filecmp.cmp(source, destination, shallow=False)
    except OSError:
        return True


def _copy_atomic(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary_name: str | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb", dir=destination.parent, prefix=f".{destination.name}.backup-", delete=False
        ) as temporary:
            temporary_name = temporary.name
            with source.open("rb") as source_handle:
                shutil.copyfileobj(source_handle, temporary)
            temporary.flush()
            os.fsync(temporary.fileno())
        shutil.copystat(source, temporary_name, follow_symlinks=True)
        os.replace(temporary_name, destination)
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)


def _backup_local(
    source_root: Path,
    target: _BackupTarget,
    *,
    dry_run: bool,
) -> BackupResult:
    assert target.local_path is not None
    destination_root = _validate_local_target(source_root, target.local_path)
    files_to_sync: list[tuple[Path, Path]] = []
    for source in _source_files(source_root):
        relative = source.relative_to(source_root)
        destination = destination_root / relative
        if _needs_local_sync(source, destination):
            files_to_sync.append((source, destination))

    for source, destination in files_to_sync:
        relative = source.relative_to(source_root).as_posix()
        if dry_run:
            print(f"BACKUP_PLAN file={relative} target={destination}")
        else:
            _copy_atomic(source, destination)
            print(f"BACKUP_SYNC file={relative} target={destination}")

    return BackupResult(
        would_sync=len(files_to_sync),
        uploaded=0 if dry_run else len(files_to_sync),
        target=target.value,
        backend=target.backend,
    )


def _cloud_command(target: _BackupTarget, source_root: Path, *, dry_run: bool) -> list[str]:
    if target.backend == "s3":
        command = ["aws", "s3", "sync", str(source_root), target.value]
        if dry_run:
            command.append("--dryrun")
        return command
    if target.backend == "gs":
        command = ["gsutil", "-m", "rsync", "-r"]
        if dry_run:
            command.append("-n")
        command.extend((str(source_root), target.value))
        return command
    raise BackupError(f"unsupported cloud backend: {target.backend}")


def _backup_cloud(
    source_root: Path,
    target: _BackupTarget,
    *,
    dry_run: bool,
) -> BackupResult:
    files = _source_files(source_root)
    command = _cloud_command(target, source_root, dry_run=dry_run)
    try:
        completed = subprocess.run(
            command,
            cwd=str(source_root),
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError as exc:
        raise BackupError(f"cannot invoke backup provider CLI: {exc}") from exc
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip()
        suffix = f": {detail}" if detail else f": exit {completed.returncode}"
        raise BackupError(f"backup sync failed for {target.value}{suffix}")

    output = completed.stdout.strip()
    if output:
        print(output)
    if dry_run:
        print(f"BACKUP_PLAN backend={target.backend} files={len(files)} target={target.value}")
    else:
        print(f"BACKUP_SYNC backend={target.backend} files={len(files)} target={target.value}")
    return BackupResult(
        would_sync=len(files),
        uploaded=0 if dry_run else len(files),
        target=target.value,
        backend=target.backend,
    )


def backup_sources(
    source_root: str | os.PathLike[str],
    target: str | os.PathLike[str],
    dry_run: bool = False,
) -> BackupResult:
    """Sync ``source_root`` to ``target`` and return transfer counts.

    ``target`` may be a local filesystem path (or ``local://``/``file://``
    URL), an ``s3://`` URL, or a ``gs://`` URL.  A dry run performs planning
    only; local targets are not created and cloud targets use their provider
    dry-run mode.  Missing or invalid source roots raise ``BackupError``.
    """

    source = _validate_source_root(source_root)
    parsed_target = _parse_target(target)
    if parsed_target.backend == "local":
        return _backup_local(source, parsed_target, dry_run=dry_run)
    return _backup_cloud(source, parsed_target, dry_run=dry_run)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source-root",
        required=True,
        type=Path,
        help="directory containing external structural source files",
    )
    parser.add_argument(
        "--backup-target",
        required=True,
        help="local path, local:// URL, s3:// URL, or gs:// URL",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="show files that would sync without copying/uploading",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        result = backup_sources(
            args.source_root,
            args.backup_target,
            dry_run=args.dry_run,
        )
    except (BackupError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    print(
        f"BACKUP_SUMMARY backend={result.backend} target={result.target} "
        f"would_sync={result.would_sync} uploaded={result.uploaded}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
