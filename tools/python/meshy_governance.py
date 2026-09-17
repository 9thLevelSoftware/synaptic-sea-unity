"""Shared secure policy and publication primitives for Meshy task artifacts.

The helper intentionally has a narrow boundary: task files are confined to the
Meshy staging tree, runtime surfaces are deny-listed, and JSON publication uses
pinned directory descriptors rather than reopening a validated pathname.

The descriptor controls prevent ordinary symlink and validation-to-use
accidents.  This module operates within a trusted-workspace boundary: user-space
code on macOS cannot prove ownership against a malicious same-UID actor that
rebinds a just-created directory between mkdirat/openat.  It therefore makes no
absolute race-proof claim against that actor; the existing mkdir, fsync, open,
and identity checks are fail-closed controls for the supported threat model.
"""

from __future__ import annotations

import ctypes
import errno
import fcntl
import hashlib
import inspect
import json
import math
import os
import re
import secrets
import stat
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Callable, Dict, Iterator, List, NamedTuple, Optional, Tuple, Union


STAGING_RELATIVE = Path("assets/_staging/meshy")
CREDIT_LOCK_RELATIVE = STAGING_RELATIVE / "_credit.lock"
_ACCOUNT_LOCK_ID_RE = re.compile(r"^[0-9a-f]{64}$")
_CREDIT_LOCK_DIRECTORY = Path(tempfile.gettempdir()).resolve(strict=False) / "synaptic-sea-meshy-credit-locks"
PROTECTED_RUNTIME_RELATIVE_PATHS = (
    Path("assets/imported"),
    Path("data/combat"),
    Path("data/props"),
    Path("scenes/wrappers"),
)
DEFAULT_FILE_MAX_BYTES = 64 * 1024 * 1024
DEFAULT_TOTAL_MAX_BYTES = 256 * 1024 * 1024
DEFAULT_SNAPSHOT_MAX_ENTRIES = 10_000
DEFAULT_SNAPSHOT_MAX_DEPTH = 64


class ProtectedSurfaceRecord(NamedTuple):
    """Immutable manifest record for one protected runtime surface."""

    type: str
    path: str
    sha256: Optional[str]
    size: int

    @property
    def hash(self) -> Optional[str]:
        return self.sha256


class PublicationUncertainError(OSError):
    """The task directory was renamed, but its parent fsync was not confirmed."""

    published = True

    def __init__(self, message: str = "Meshy directory publication durability is uncertain") -> None:
        super().__init__(message)


# Tests and callers may use this seam to deterministically exercise the
# validation-to-open boundary.  It is deliberately a no-op in normal use.
_ATOMIC_VALIDATION_HOOK: Optional[Callable[[Path], None]] = None
_FILE_VALIDATION_HOOK: Optional[Callable[[Path], None]] = None
_SNAPSHOT_VALIDATION_HOOK: Optional[Callable[[Path], None]] = None

# Capture capability markers before tests or callers inject wrappers around the
# low-level functions.  The actual operation still uses the current os.* call.
_ORIGINAL_OPEN = os.open
_ORIGINAL_MKDIR = os.mkdir
_ORIGINAL_UNLINK = os.unlink
_ORIGINAL_REPLACE = os.replace


def physical_project_root(path: Union[str, os.PathLike]) -> Path:
    """Resolve an explicit project-root alias once and require a directory."""

    candidate = Path(path).expanduser()
    try:
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise ValueError(f"project root could not be resolved: {exc}") from exc
    if not resolved.is_dir():
        raise ValueError(f"project root must be a directory: {candidate}")
    return resolved


def protected_runtime_paths(root: Union[str, os.PathLike]) -> Tuple[Path, ...]:
    """Return the exact physical runtime surfaces protected from Meshy writes."""

    physical = physical_project_root(root)
    return tuple(physical / relative for relative in PROTECTED_RUNTIME_RELATIVE_PATHS)


def _absolute_lexical(path: Union[str, os.PathLike], base: Optional[Path] = None) -> Path:
    candidate = Path(path).expanduser()
    if not candidate.is_absolute():
        candidate = (base if base is not None else Path.cwd()) / candidate
    return Path(os.path.abspath(os.fspath(candidate)))


def _path_from_explicit_root(
    project_root: Union[str, os.PathLike], physical_root: Path, path: Union[str, os.PathLike]
) -> Path:
    """Map a path written through the explicit root alias to physical spelling."""

    explicit = _absolute_lexical(project_root)
    candidate = _absolute_lexical(path, physical_root)
    try:
        relative = candidate.relative_to(explicit)
    except ValueError:
        return candidate
    return physical_root / relative


def _contained(root: Path, candidate: Path) -> bool:
    return candidate == root or root in candidate.parents


def _reject_symlink_components_below(root: Path, candidate: Path, label: str) -> None:
    """Reject symlink components below an already-resolved project root."""

    if not _contained(root, candidate):
        raise ValueError(f"{label} escapes project root")
    relative = candidate.relative_to(root)
    current = root
    for component in relative.parts:
        current = current / component
        try:
            info = os.lstat(current)
        except FileNotFoundError:
            break
        except OSError as exc:
            raise ValueError(f"{label} could not be inspected: {exc}") from exc
        if stat.S_ISLNK(info.st_mode):
            raise ValueError(f"{label} contains a symlink component")
        if current != candidate and not stat.S_ISDIR(info.st_mode):
            raise ValueError(f"{label} contains a non-directory ancestor")


def _resolved_below(root: Path, candidate: Path, label: str) -> Path:
    try:
        resolved = candidate.resolve(strict=False)
    except (OSError, RuntimeError) as exc:
        raise ValueError(f"{label} could not be resolved: {exc}") from exc
    if not _contained(root, resolved):
        raise ValueError(f"{label} escapes project root")
    return resolved


def governed_task_path(
    root: Union[str, os.PathLike],
    path: Union[str, os.PathLike],
    label: str,
    allow_missing: bool = True,
) -> Path:
    """Validate one task path as physically contained by Meshy staging."""

    physical = physical_project_root(root)
    candidate = _path_from_explicit_root(root, physical, path)
    stage = physical / STAGING_RELATIVE
    _reject_symlink_components_below(physical, stage, label)
    _reject_symlink_components_below(physical, candidate, label)
    resolved_stage = _resolved_below(physical, stage, label)
    resolved_candidate = _resolved_below(physical, candidate, label)
    if not _contained(resolved_stage, resolved_candidate):
        raise ValueError(f"{label} must be physically under Meshy staging")
    if not allow_missing and not candidate.exists():
        raise ValueError(f"{label} is missing")
    return candidate


def reject_protected_output(
    root: Union[str, os.PathLike], path: Union[str, os.PathLike], label: str
) -> Path:
    """Return a resolved output path unless it targets a protected surface."""

    physical = physical_project_root(root)
    candidate = _path_from_explicit_root(root, physical, path)
    try:
        resolved_candidate = _resolved_below(physical, candidate, label)
    except ValueError:
        try:
            _reject_symlink_components_below(physical, candidate, label)
        except ValueError as symlink_error:
            if "symlink" in str(symlink_error):
                raise
        raise
    for relative in PROTECTED_RUNTIME_RELATIVE_PATHS:
        protected_candidate = physical / relative
        _reject_symlink_components_below(physical, protected_candidate, "protected surface")
        protected = _resolved_below(physical, protected_candidate, "protected surface")
        if _contained(protected, resolved_candidate):
            raise ValueError(f"{label} targets protected runtime surface: {relative}")
    _reject_symlink_components_below(physical, candidate, label)
    return resolved_candidate


def canonical_json_bytes(value: object) -> bytes:
    """Encode JSON deterministically, rejecting non-finite numbers."""

    encoded = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )
    return (encoded + "\n").encode("utf-8")


def _reject_duplicate_keys(pairs: List[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> None:
    raise ValueError(f"non-standard JSON constant: {value}")


def _parse_finite_float(value: str) -> float:
    parsed = float(value)
    if not math.isfinite(parsed):
        raise ValueError(f"non-finite JSON number: {value}")
    return parsed


def _resolve_accepted_macos_alias(path: Union[str, os.PathLike], label: str) -> Path:
    """Resolve only the Darwin /var and /tmp aliases, once, before traversal."""

    lexical = _absolute_lexical(path)
    if len(lexical.parts) < 2:
        return lexical
    alias_name = lexical.parts[1]
    alias_target = {
        "var": Path("/private/var"),
        "tmp": Path("/private/tmp"),
    }.get(alias_name)
    if alias_target is None:
        return lexical
    alias = Path(os.sep) / alias_name
    try:
        alias_info = os.lstat(alias)
    except FileNotFoundError:
        return lexical
    except OSError as exc:
        raise ValueError(f"{label} could not be inspected: {exc}") from exc
    if not stat.S_ISLNK(alias_info.st_mode):
        return lexical
    try:
        resolved_alias = alias.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise ValueError(f"{label} could not resolve macOS alias: {exc}") from exc
    if resolved_alias != alias_target:
        raise ValueError(f"{label} contains an unsupported symlink component")
    return alias_target.joinpath(*lexical.parts[2:])


def _file_stat_signature(info: os.stat_result) -> Tuple[int, int, int, int, int]:
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _read_descriptor(
    descriptor: int,
    label: str,
    max_bytes: int,
    opened: os.stat_result,
    hasher: Optional[Any] = None,
) -> bytes:
    chunks: List[bytes] = []
    total = 0
    while True:
        chunk = os.read(descriptor, min(1024 * 1024, max_bytes - total + 1))
        if not chunk:
            break
        total += len(chunk)
        if total > max_bytes:
            raise ValueError(f"{label} exceeds maximum size")
        if hasher is None:
            chunks.append(chunk)
        else:
            hasher.update(chunk)
    finished = os.fstat(descriptor)
    if _file_stat_signature(finished) != _file_stat_signature(opened):
        raise ValueError(f"{label} changed while reading")
    if hasher is not None:
        return b""
    return b"".join(chunks)


def _read_bounded_regular_file(path: Union[str, os.PathLike], label: str, max_bytes: int) -> bytes:
    if not isinstance(max_bytes, int) or isinstance(max_bytes, bool) or max_bytes < 0:
        raise ValueError(f"{label} max_bytes must be a non-negative integer")
    descriptor: Optional[int] = None
    try:
        descriptor, opened = _open_pinned_regular_file(path, label, max_bytes)
        return _read_descriptor(descriptor, label, max_bytes, opened)
    finally:
        if descriptor is not None:
            os.close(descriptor)


def _strict_json_source(
    path: Union[str, os.PathLike], label: str, max_bytes: int
) -> Tuple[Dict[str, Any], bytes]:
    raw = _read_bounded_regular_file(path, label, max_bytes)
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise ValueError(f"{label} is not valid UTF-8: {exc}") from exc
    try:
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_json_constant,
            parse_float=_parse_finite_float,
        )
    except RecursionError as exc:
        raise ValueError(f"{label} maximum nesting depth exceeded") from exc
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} contains invalid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be an object")
    return value, raw


def strict_load_json(
    path: Union[str, os.PathLike], label: str, max_bytes: int
) -> Dict[str, Any]:
    """Load a bounded regular JSON object with strict parser settings."""

    value, _raw = _strict_json_source(path, label, max_bytes)
    return value


def strict_load_json_bytes(
    path: Union[str, os.PathLike], label: str, max_bytes: int
) -> Tuple[Dict[str, Any], bytes]:
    """Load strict JSON and return the exact source bytes for evidence hashing."""

    return _strict_json_source(path, label, max_bytes)


def file_sha256(
    path: Union[str, os.PathLike], max_bytes: Optional[int] = DEFAULT_FILE_MAX_BYTES
) -> str:
    """Hash a regular non-symlink file through a bounded descriptor stream."""

    if max_bytes is None:
        max_bytes = DEFAULT_FILE_MAX_BYTES
    if not isinstance(max_bytes, int) or isinstance(max_bytes, bool) or max_bytes < 0:
        raise ValueError("file max_bytes must be a non-negative integer")
    descriptor: Optional[int] = None
    try:
        descriptor, opened = _open_pinned_regular_file(path, "file", max_bytes)
        digest = hashlib.sha256()
        _read_descriptor(descriptor, "file", max_bytes, opened, hasher=digest)
        return digest.hexdigest()
    finally:
        if descriptor is not None:
            os.close(descriptor)


class _SnapshotBudget:
    def __init__(
        self, max_file_bytes: int, max_total_bytes: int, max_entries: int, max_depth: int
    ) -> None:
        self.max_file_bytes = max_file_bytes
        self.max_total_bytes = max_total_bytes
        self.max_entries = max_entries
        self.max_depth = max_depth
        self.total_bytes = 0
        self.entries = 0

    def claim_entry(self, relative: str) -> None:
        if self.entries >= self.max_entries:
            raise ValueError(f"protected snapshot exceeds maximum entries at {relative}")
        self.entries += 1

    def check_depth(self, depth: int, relative: str) -> None:
        if depth > self.max_depth:
            raise ValueError(f"protected snapshot exceeds maximum depth at {relative}")

    def claim_file(self, size: int, relative: str) -> None:
        if size > self.max_file_bytes:
            raise ValueError(f"protected snapshot file exceeds maximum size: {relative}")
        if self.total_bytes + size > self.max_total_bytes:
            raise ValueError(f"protected snapshot exceeds maximum total bytes at {relative}")
        self.total_bytes += size


def _validate_snapshot_limit(value: object, name: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"{name} must be a positive integer")
    return value


def _snapshot_path_metadata(
    path: Path, label: str
) -> Tuple[Dict[Tuple[str, ...], Tuple[int, int]], Dict[Tuple[str, ...], Tuple[int, int, int, int, int]]]:
    """Capture path identities and metadata before descriptor traversal begins."""

    if not path.is_absolute() or path.anchor != os.sep:
        raise OSError(f"{label} requires an absolute POSIX path")
    identities: Dict[Tuple[str, ...], Tuple[int, int]] = {}
    signatures: Dict[Tuple[str, ...], Tuple[int, int, int, int, int]] = {}
    for end in range(1, len(path.parts) + 1):
        parts = path.parts[:end]
        component = Path(parts[0])
        for part in parts[1:]:
            component /= part
        try:
            info = os.lstat(component)
        except FileNotFoundError:
            break
        except OSError as exc:
            raise OSError(f"{label} identity snapshot failed: {exc}") from exc
        if stat.S_ISLNK(info.st_mode):
            raise OSError(f"{label} contains a symlink component")
        if end < len(path.parts) and not stat.S_ISDIR(info.st_mode):
            raise OSError(f"{label} contains a non-directory ancestor")
        identities[parts] = (info.st_dev, info.st_ino)
        signatures[parts] = _file_stat_signature(info)
    return identities, signatures


def _snapshot_directory_flags() -> int:
    directory_flag = getattr(os, "O_DIRECTORY", None)
    nofollow_flag = getattr(os, "O_NOFOLLOW", None)
    supports_dir_fd = getattr(os, "supports_dir_fd", ())
    if directory_flag is None or nofollow_flag is None:
        raise OSError("protected snapshots require O_DIRECTORY and O_NOFOLLOW")
    if _ORIGINAL_OPEN not in supports_dir_fd or not callable(os.open):
        raise OSError("protected snapshots require directory-fd open operations")
    flags = os.O_RDONLY | directory_flag | nofollow_flag
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    return flags


def _snapshot_open_error(exc: OSError, label: str) -> None:
    if exc.errno == errno.ELOOP:
        raise ValueError(f"{label} contains a symlink component") from exc
    if exc.errno == errno.ENOTDIR:
        raise ValueError(f"{label} ancestor is not a directory") from exc
    raise ValueError(f"{label} could not be opened safely: {exc}") from exc


def _open_pinned_surface(
    path: Path,
    identities: Dict[Tuple[str, ...], Tuple[int, int]],
    signatures: Dict[Tuple[str, ...], Tuple[int, int, int, int, int]],
    label: str,
) -> Optional[Tuple[str, int, os.stat_result]]:
    """Open a protected surface through component-pinned descriptors only."""

    directory_flags = _snapshot_directory_flags()
    current_fd: Optional[int] = None
    opened_fd: Optional[int] = None
    try:
        try:
            current_fd = os.open(os.sep, directory_flags)
            _check_fd_identity(current_fd, identities.get(path.parts[:1]), "Meshy filesystem root")
            for index, component in enumerate(path.parts[1:-1], start=2):
                expected = identities.get(path.parts[:index])
                child_fd: Optional[int] = None
                try:
                    try:
                        child_fd = os.open(component, directory_flags, dir_fd=current_fd)
                    except FileNotFoundError as exc:
                        if expected is None:
                            return None
                        raise OSError(
                            f"{label} component {component} disappeared after validation"
                        ) from exc
                    if expected is None:
                        raise OSError(f"{label} component {component} appeared during validation")
                    _check_fd_identity(child_fd, expected, f"{label} ancestor {component}")
                    previous_fd = current_fd
                    os.close(previous_fd)
                    current_fd = child_fd
                    child_fd = None
                finally:
                    if child_fd is not None:
                        os.close(child_fd)

            expected = identities.get(path.parts)
            expected_signature = signatures.get(path.parts)
            try:
                opened_fd = os.open(path.name, directory_flags, dir_fd=current_fd)
            except FileNotFoundError as exc:
                if expected is None:
                    return None
                raise OSError(f"{label} disappeared after validation") from exc
            except OSError as exc:
                if exc.errno != errno.ENOTDIR:
                    raise
                try:
                    leaf_flags = _read_leaf_flags()
                    if hasattr(os, "O_NONBLOCK"):
                        leaf_flags |= os.O_NONBLOCK
                    opened_fd = os.open(path.name, leaf_flags, dir_fd=current_fd)
                except FileNotFoundError as leaf_exc:
                    if expected is None:
                        return None
                    raise OSError(f"{label} disappeared after validation") from leaf_exc
                info = os.fstat(opened_fd)
                if expected is None:
                    raise OSError(f"{label} appeared during validation")
                if not stat.S_ISREG(info.st_mode):
                    raise ValueError(f"{label} must be a regular file")
                _check_fd_identity(opened_fd, expected, label)
                if expected_signature is None or _file_stat_signature(info) != expected_signature:
                    raise ValueError(f"{label} changed during validation")
                result = ("file", opened_fd, info)
                opened_fd = None
                return result

            info = os.fstat(opened_fd)
            if expected is None:
                raise OSError(f"{label} appeared during validation")
            if not stat.S_ISDIR(info.st_mode):
                raise ValueError(f"{label} must be a directory")
            _check_fd_identity(opened_fd, expected, label)
            if expected_signature is None or _file_stat_signature(info) != expected_signature:
                raise ValueError(f"{label} changed during validation")
            result = ("directory", opened_fd, info)
            opened_fd = None
            return result
        except ValueError:
            raise
        except OSError as exc:
            _snapshot_open_error(exc, label)
    finally:
        if opened_fd is not None:
            os.close(opened_fd)
        if current_fd is not None:
            os.close(current_fd)


def _snapshot_directory_fd(
    directory_fd: int, relative_directory: str, budget: _SnapshotBudget, depth: int
) -> Tuple[str, int, List[Tuple[str, str, Optional[str], int]]]:
    """Snapshot one already-pinned directory without reopening its pathname."""

    budget.check_depth(depth, relative_directory)
    try:
        before = os.fstat(directory_fd)
    except OSError as exc:
        raise ValueError(f"protected directory could not be inspected: {exc}") from exc
    children: List[os.DirEntry] = []
    try:
        with os.scandir(directory_fd) as iterator:
            for entry in iterator:
                if budget.entries + len(children) >= budget.max_entries:
                    raise ValueError(
                        f"protected snapshot exceeds maximum entries at {relative_directory}"
                    )
                children.append(entry)
        children.sort(key=lambda entry: entry.name)
        after_enumeration = os.fstat(directory_fd)
    except ValueError:
        raise
    except OSError as exc:
        raise ValueError(f"protected directory could not be read: {exc}") from exc
    if _file_stat_signature(after_enumeration) != _file_stat_signature(before):
        raise ValueError(f"protected directory changed while enumerating: {relative_directory}")

    entries: List[Tuple[str, str, Optional[str], int]] = []
    total_size = 0
    directory_flags = _snapshot_directory_flags()
    for entry in children:
        relative = f"{relative_directory}/{entry.name}"
        child_depth = depth + 1
        budget.check_depth(child_depth, relative)
        budget.claim_entry(relative)
        try:
            info = entry.stat(follow_symlinks=False)
        except OSError as exc:
            raise ValueError(f"protected directory could not be inspected: {exc}") from exc
        if stat.S_ISLNK(info.st_mode):
            raise ValueError(f"protected surface contains a symlink: {relative}")
        if stat.S_ISDIR(info.st_mode):
            child_fd: Optional[int] = None
            try:
                try:
                    child_fd = os.open(entry.name, directory_flags, dir_fd=directory_fd)
                except OSError as exc:
                    _snapshot_open_error(exc, relative)
                assert child_fd is not None
                opened = os.fstat(child_fd)
                if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                    raise ValueError(f"protected directory changed while opening: {relative}")
                if _file_stat_signature(opened) != _file_stat_signature(info):
                    raise ValueError(f"protected directory changed while opening: {relative}")
                child_hash, child_size, _child_entries = _snapshot_directory_fd(
                    child_fd, relative, budget, child_depth
                )
                entries.append((relative, "directory", child_hash, child_size))
                total_size += child_size
            finally:
                if child_fd is not None:
                    os.close(child_fd)
        elif stat.S_ISREG(info.st_mode):
            budget.claim_file(info.st_size, relative)
            descriptor: Optional[int] = None
            try:
                try:
                    descriptor = os.open(entry.name, _read_leaf_flags(), dir_fd=directory_fd)
                except OSError as exc:
                    _snapshot_open_error(exc, relative)
                assert descriptor is not None
                opened = os.fstat(descriptor)
                if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                    raise ValueError(f"protected file changed while opening: {relative}")
                if _file_stat_signature(opened) != _file_stat_signature(info):
                    raise ValueError(f"protected file changed while opening: {relative}")
                digest = hashlib.sha256()
                _read_descriptor(descriptor, relative, budget.max_file_bytes, opened, hasher=digest)
                entries.append((relative, "file", digest.hexdigest(), opened.st_size))
                total_size += opened.st_size
            finally:
                if descriptor is not None:
                    os.close(descriptor)
        else:
            raise ValueError(f"protected surface contains a non-regular entry: {relative}")

    try:
        after_processing = os.fstat(directory_fd)
    except OSError as exc:
        raise ValueError(f"protected directory could not be inspected: {exc}") from exc
    if _file_stat_signature(after_processing) != _file_stat_signature(before):
        raise ValueError(f"protected directory changed while snapshotting: {relative_directory}")
    digest = hashlib.sha256(canonical_json_bytes(entries)).hexdigest()
    return digest, total_size, entries


def snapshot_protected_surfaces(
    root: Union[str, os.PathLike],
    max_file_bytes: int = DEFAULT_FILE_MAX_BYTES,
    max_total_bytes: int = DEFAULT_TOTAL_MAX_BYTES,
    max_entries: int = DEFAULT_SNAPSHOT_MAX_ENTRIES,
    max_depth: int = DEFAULT_SNAPSHOT_MAX_DEPTH,
) -> Tuple[ProtectedSurfaceRecord, ...]:
    """Return deterministic immutable records under bounded traversal budgets."""

    validated_limits = tuple(
        _validate_snapshot_limit(value, name)
        for name, value in (
            ("max_file_bytes", max_file_bytes),
            ("max_total_bytes", max_total_bytes),
            ("max_entries", max_entries),
            ("max_depth", max_depth),
        )
    )
    budget = _SnapshotBudget(*validated_limits)
    physical = physical_project_root(root)
    records: List[ProtectedSurfaceRecord] = []
    for relative in PROTECTED_RUNTIME_RELATIVE_PATHS:
        surface = physical / relative
        budget.claim_entry(relative.as_posix())
        try:
            identities, signatures = _snapshot_path_metadata(surface, "protected surface")
        except OSError as exc:
            raise ValueError(str(exc)) from exc
        hook = _SNAPSHOT_VALIDATION_HOOK
        if hook is not None:
            hook(surface)
        opened = _open_pinned_surface(surface, identities, signatures, "protected surface")
        if opened is None:
            records.append(ProtectedSurfaceRecord("missing", relative.as_posix(), None, 0))
            continue
        surface_type, descriptor, info = opened
        try:
            if surface_type == "file":
                budget.claim_file(info.st_size, relative.as_posix())
                digest = hashlib.sha256()
                _read_descriptor(
                    descriptor,
                    relative.as_posix(),
                    budget.max_file_bytes,
                    info,
                    hasher=digest,
                )
                records.append(
                    ProtectedSurfaceRecord("file", relative.as_posix(), digest.hexdigest(), info.st_size)
                )
            else:
                digest, size, _entries = _snapshot_directory_fd(
                    descriptor, relative.as_posix(), budget, 0
                )
                records.append(ProtectedSurfaceRecord("directory", relative.as_posix(), digest, size))
        finally:
            os.close(descriptor)
    return tuple(records)


def _pinned_directory_flags() -> int:
    directory_flag = getattr(os, "O_DIRECTORY", None)
    nofollow_flag = getattr(os, "O_NOFOLLOW", None)
    supports_dir_fd = getattr(os, "supports_dir_fd", ())
    if directory_flag is None or nofollow_flag is None:
        raise OSError("Meshy publication requires O_DIRECTORY and O_NOFOLLOW")
    if not all(
        original in supports_dir_fd and callable(current)
        for original, current in (
            (_ORIGINAL_OPEN, os.open),
            (_ORIGINAL_MKDIR, os.mkdir),
            (_ORIGINAL_UNLINK, os.unlink),
        )
    ):
        raise OSError("Meshy publication requires directory-fd operations")
    try:
        replace_parameters = inspect.signature(_ORIGINAL_REPLACE).parameters
    except (TypeError, ValueError) as exc:
        raise OSError("Meshy publication requires directory-fd replace operations") from exc
    replace_supported = _ORIGINAL_REPLACE in supports_dir_fd or (
        os.rename in supports_dir_fd
        and all(name in replace_parameters for name in ("src_dir_fd", "dst_dir_fd"))
        and callable(os.replace)
    )
    if not replace_supported:
        raise OSError("Meshy publication requires directory-fd replace operations")
    flags = os.O_RDONLY | directory_flag | nofollow_flag
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    return flags


def _snapshot_identities(path: Path, label: str) -> Dict[Tuple[str, ...], Tuple[int, int]]:
    if not path.is_absolute() or path.anchor != os.sep:
        raise OSError(f"{label} requires an absolute POSIX path")
    identities: Dict[Tuple[str, ...], Tuple[int, int]] = {}
    for end in range(1, len(path.parts) + 1):
        parts = path.parts[:end]
        component = Path(parts[0])
        for part in parts[1:]:
            component /= part
        try:
            info = os.lstat(component)
        except FileNotFoundError:
            break
        except OSError as exc:
            raise OSError(f"{label} identity snapshot failed: {exc}") from exc
        if stat.S_ISLNK(info.st_mode):
            raise OSError(f"{label} contains a symlink component")
        identities[parts] = (info.st_dev, info.st_ino)
    return identities


def _check_fd_identity(
    descriptor: int,
    expected: Optional[Tuple[int, int]],
    label: str,
    required: bool = True,
) -> None:
    if expected is None:
        if required:
            raise OSError(f"{label} appeared during validation")
        return
    info = os.fstat(descriptor)
    actual = (info.st_dev, info.st_ino)
    if actual != expected:
        raise OSError(f"{label} identity changed after validation")


def _read_directory_flags() -> int:
    directory_flag = getattr(os, "O_DIRECTORY", None)
    nofollow_flag = getattr(os, "O_NOFOLLOW", None)
    if directory_flag is None or nofollow_flag is None:
        raise ValueError("file reads require O_DIRECTORY and O_NOFOLLOW")
    flags = os.O_RDONLY | directory_flag | nofollow_flag
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    return flags


def _read_leaf_flags() -> int:
    nofollow_flag = getattr(os, "O_NOFOLLOW", None)
    if nofollow_flag is None:
        raise ValueError("file reads require O_NOFOLLOW")
    flags = os.O_RDONLY | nofollow_flag
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    return flags


def _open_pinned_regular_file(
    path: Union[str, os.PathLike], label: str, max_bytes: int
) -> Tuple[int, os.stat_result]:
    """Open a validated regular file through one descriptor-relative walk."""

    normalized = _resolve_accepted_macos_alias(path, label)
    try:
        identities = _snapshot_identities(normalized, label)
    except FileNotFoundError as exc:
        raise ValueError(f"{label} is missing") from exc
    except OSError as exc:
        raise ValueError(str(exc)) from exc
    expected = identities.get(normalized.parts)
    if expected is None:
        raise ValueError(f"{label} is missing")
    try:
        pre_open = os.lstat(normalized)
    except FileNotFoundError as exc:
        raise ValueError(f"{label} is missing") from exc
    except OSError as exc:
        raise ValueError(f"{label} could not be inspected: {exc}") from exc
    if stat.S_ISLNK(pre_open.st_mode):
        raise ValueError(f"{label} contains a symlink component")
    if not stat.S_ISREG(pre_open.st_mode):
        raise ValueError(f"{label} must be a regular file")
    if pre_open.st_size > max_bytes:
        raise ValueError(f"{label} exceeds maximum size")
    if (pre_open.st_dev, pre_open.st_ino) != expected:
        raise ValueError(f"{label} identity changed during validation")

    hook = _FILE_VALIDATION_HOOK
    if hook is not None:
        hook(normalized)

    directory_fd: Optional[int] = None
    descriptor: Optional[int] = None
    try:
        try:
            directory_fd = os.open(os.sep, _read_directory_flags())
            _check_fd_identity(
                directory_fd, identities.get(normalized.parts[:1]), "Meshy filesystem root"
            )
            for index, component in enumerate(normalized.parts[1:-1], start=2):
                child_fd: Optional[int] = None
                try:
                    child_fd = os.open(
                        component,
                        _read_directory_flags(),
                        dir_fd=directory_fd,
                    )
                    _check_fd_identity(
                        child_fd,
                        identities.get(normalized.parts[:index]),
                        f"{label} ancestor {component}",
                    )
                    previous_fd = directory_fd
                    os.close(previous_fd)
                    directory_fd = child_fd
                    child_fd = None
                finally:
                    if child_fd is not None:
                        os.close(child_fd)

            descriptor = os.open(
                normalized.name,
                _read_leaf_flags(),
                dir_fd=directory_fd,
            )
            opened = os.fstat(descriptor)
            if stat.S_ISLNK(opened.st_mode):
                raise ValueError(f"{label} must not be a symlink")
            if not stat.S_ISREG(opened.st_mode):
                raise ValueError(f"{label} must be a regular file")
            if (opened.st_dev, opened.st_ino) != expected:
                raise ValueError(f"{label} identity changed while opening")
            if _file_stat_signature(opened) != _file_stat_signature(pre_open):
                raise ValueError(f"{label} changed while opening")
            if opened.st_size > max_bytes:
                raise ValueError(f"{label} exceeds maximum size")

            previous_fd = directory_fd
            os.close(previous_fd)
            directory_fd = None
            result = descriptor
            descriptor = None
            return result, opened
        except ValueError:
            raise
        except FileNotFoundError as exc:
            raise ValueError(f"{label} is missing") from exc
        except OSError as exc:
            if exc.errno == errno.ELOOP:
                raise ValueError(f"{label} contains a symlink component") from exc
            if exc.errno == errno.ENOTDIR:
                raise ValueError(f"{label} ancestor identity changed after validation") from exc
            raise ValueError(f"{label} could not be opened safely: {exc}") from exc
    finally:
        if descriptor is not None:
            os.close(descriptor)
        if directory_fd is not None:
            os.close(directory_fd)


def _open_pinned_parent(path: Path, identities: Dict[Tuple[str, ...], Tuple[int, int]], flags: int) -> int:
    current_fd: Optional[int] = os.open(os.sep, flags)
    try:
        _check_fd_identity(current_fd, identities.get(path.parts[:1]), "Meshy root")
        for index, component in enumerate(path.parent.parts[1:], start=2):
            child_fd: Optional[int] = None
            created_here = False
            component_parts = path.parts[:index]
            try:
                try:
                    child_fd = os.open(component, flags, dir_fd=current_fd)
                except FileNotFoundError:
                    if identities.get(component_parts) is not None:
                        raise OSError(f"Meshy component {component} disappeared after validation")
                    try:
                        os.mkdir(component, mode=0o700, dir_fd=current_fd)
                    except FileExistsError as exc:
                        raise OSError(f"Meshy component {component} appeared during validation") from exc
                    created_here = True
                    os.fsync(current_fd)
                    child_fd = os.open(component, flags, dir_fd=current_fd)
                if identities.get(component_parts) is None and not created_here:
                    raise OSError(f"Meshy component {component} appeared during validation")
                if identities.get(component_parts) is not None:
                    _check_fd_identity(child_fd, identities.get(component_parts), f"Meshy component {component}")
                previous_fd = current_fd
                os.close(previous_fd)
                current_fd = child_fd
                child_fd = None
            finally:
                if child_fd is not None:
                    os.close(child_fd)
        if current_fd is None:
            raise OSError("Meshy publication lost its parent directory fd")
        result = current_fd
        current_fd = None
        return result
    finally:
        if current_fd is not None:
            os.close(current_fd)


def _validate_existing_leaf(
    parent_fd: int, target: Path, identities: Dict[Tuple[str, ...], Tuple[int, int]]
) -> None:
    nofollow = getattr(os, "O_NOFOLLOW", None)
    if nofollow is None:
        raise OSError("Meshy publication requires O_NOFOLLOW")
    flags = os.O_RDONLY | nofollow
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    expected = identities.get(target.parts)
    descriptor: Optional[int] = None
    try:
        try:
            descriptor = os.open(target.name, flags, dir_fd=parent_fd)
        except FileNotFoundError:
            if expected is not None:
                raise OSError("Meshy output target disappeared after validation")
            return
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode):
            raise OSError("Meshy output target must be a regular file")
        if expected is None:
            raise OSError("Meshy output target appeared during validation")
        _check_fd_identity(descriptor, expected, "Meshy output target")
    finally:
        if descriptor is not None:
            os.close(descriptor)


def _create_sibling_temp(parent_fd: int, target_name: str, mode: int) -> Tuple[int, str]:
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    for _attempt in range(32):
        name = f".{target_name}.{secrets.token_hex(12)}.tmp"
        try:
            return os.open(name, flags, mode, dir_fd=parent_fd), name
        except FileExistsError:
            continue
    raise OSError("could not allocate a unique Meshy temporary file")


def _write_all(descriptor: int, payload: bytes) -> None:
    offset = 0
    while offset < len(payload):
        written = os.write(descriptor, payload[offset:])
        if written <= 0:
            raise OSError("Meshy temporary write made no progress")
        offset += written


def _atomic_write_payload(
    path: Union[str, os.PathLike],
    payload: bytes,
    project_root: Union[str, os.PathLike],
    allowed_root: Union[str, os.PathLike],
    mode: int = 0o600,
    replace: bool = True,
) -> None:
    """Publish bytes only through a pinned allowed directory FD."""

    if not isinstance(payload, bytes):
        raise TypeError("Meshy payload must be bytes")
    if not isinstance(mode, int) or isinstance(mode, bool) or mode <= 0 or mode & ~0o777:
        raise ValueError("Meshy payload mode must be a positive permission mask")
    physical = physical_project_root(project_root)
    target = _path_from_explicit_root(project_root, physical, path)
    allowed = _path_from_explicit_root(project_root, physical, allowed_root)

    # Deny protected targets before the allowed-root diagnostic, including
    # aliases and missing descendants resolved through a protected surface.
    reject_protected_output(physical, target, "Meshy output")
    _reject_symlink_components_below(physical, target, "Meshy output")
    _reject_symlink_components_below(physical, allowed, "Meshy allowed root")
    resolved_allowed = _resolved_below(physical, allowed, "Meshy allowed root")
    resolved_target = _resolved_below(physical, target, "Meshy output")
    if not _contained(allowed, target) or not _contained(resolved_allowed, resolved_target):
        raise ValueError("Meshy output must be within the allowed staging root")
    if target.name in {"", ".", ".."}:
        raise ValueError("Meshy output must have a file name")

    identities = _snapshot_identities(target, "Meshy output")
    if not replace and identities.get(target.parts) is not None:
        raise ValueError("Meshy output target already exists")
    hook = _ATOMIC_VALIDATION_HOOK
    if hook is not None:
        hook(target)

    directory_fd: Optional[int] = None
    temporary_fd: Optional[int] = None
    temporary_name: Optional[str] = None
    primary_error: Optional[BaseException] = None
    primary_traceback = None
    try:
        flags = _pinned_directory_flags()
        directory_fd = _open_pinned_parent(target, identities, flags)
        os.fsync(directory_fd)
        _validate_existing_leaf(directory_fd, target, identities)
        temporary_fd, temporary_name = _create_sibling_temp(directory_fd, target.name, mode)
        try:
            _write_all(temporary_fd, payload)
            os.fsync(temporary_fd)
        finally:
            os.close(temporary_fd)
            temporary_fd = None
        if replace:
            os.replace(
                temporary_name,
                target.name,
                src_dir_fd=directory_fd,
                dst_dir_fd=directory_fd,
            )
        else:
            _rename_noreplace(
                directory_fd,
                temporary_name,
                target.name,
                "Meshy output target",
            )
        temporary_name = None
        os.fsync(directory_fd)
    except BaseException as exc:
        primary_error = exc
        primary_traceback = exc.__traceback__

    cleanup_error: Optional[BaseException] = None
    cleanup_traceback = None
    if temporary_fd is not None:
        try:
            os.close(temporary_fd)
        except BaseException as exc:
            cleanup_error = exc
            cleanup_traceback = exc.__traceback__
    if temporary_name is not None and directory_fd is not None:
        try:
            os.unlink(temporary_name, dir_fd=directory_fd)
        except FileNotFoundError:
            pass
        except BaseException as exc:
            if cleanup_error is None:
                cleanup_error = exc
                cleanup_traceback = exc.__traceback__
    if directory_fd is not None:
        try:
            os.close(directory_fd)
        except BaseException as exc:
            if cleanup_error is None:
                cleanup_error = exc
                cleanup_traceback = exc.__traceback__

    if primary_error is not None:
        raise primary_error.with_traceback(primary_traceback)
    if cleanup_error is not None:
        raise cleanup_error.with_traceback(cleanup_traceback)


def _fsync_directory_tree(directory_fd: int) -> None:
    """Durably flush a private task tree before publishing its directory name."""

    os.fsync(directory_fd)
    directory_flags = _pinned_directory_flags()
    with os.scandir(directory_fd) as iterator:
        children = sorted(iterator, key=lambda entry: entry.name)
    for entry in children:
        info = entry.stat(follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode):
            raise OSError("Meshy task source contains a symlink")
        if info.st_mode & 0o077:
            raise OSError("Meshy task source contains a non-private entry")
        if stat.S_ISDIR(info.st_mode):
            child_fd: Optional[int] = None
            try:
                child_fd = os.open(entry.name, directory_flags, dir_fd=directory_fd)
                opened = os.fstat(child_fd)
                if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                    raise OSError("Meshy task source directory identity changed")
                _fsync_directory_tree(child_fd)
            finally:
                if child_fd is not None:
                    os.close(child_fd)
        elif stat.S_ISREG(info.st_mode):
            descriptor: Optional[int] = None
            try:
                descriptor = os.open(entry.name, _read_leaf_flags(), dir_fd=directory_fd)
                opened = os.fstat(descriptor)
                if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                    raise OSError("Meshy task source file identity changed")
                os.fsync(descriptor)
            finally:
                if descriptor is not None:
                    os.close(descriptor)
        else:
            raise OSError("Meshy task source contains a non-regular entry")


def _rename_noreplace(
    parent_fd: int,
    source_name: str,
    final_name: str,
    label: str,
    unavailable_message: str = "Meshy publication requires an exclusive rename primitive",
) -> None:
    """Use the platform's directory-relative exclusive rename primitive."""

    libc = ctypes.CDLL(None, use_errno=True)
    renameatx_np = getattr(libc, "renameatx_np", None)
    if renameatx_np is not None:
        renameatx_np.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        renameatx_np.restype = ctypes.c_int
        result = renameatx_np(
            parent_fd,
            os.fsencode(source_name),
            parent_fd,
            os.fsencode(final_name),
            ctypes.c_uint(0x00000004),  # RENAME_EXCL on Darwin.
        )
        if result == 0:
            return
        error_number = ctypes.get_errno()
        if error_number == errno.EEXIST:
            raise ValueError(f"{label} appeared during publication")
        raise OSError(error_number, os.strerror(error_number))
    renameat2 = getattr(libc, "renameat2", None)
    if renameat2 is not None:
        renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        renameat2.restype = ctypes.c_int
        result = renameat2(
            parent_fd,
            os.fsencode(source_name),
            parent_fd,
            os.fsencode(final_name),
            ctypes.c_uint(1),  # RENAME_NOREPLACE on Linux.
        )
        if result == 0:
            return
        error_number = ctypes.get_errno()
        if error_number == errno.EEXIST:
            raise ValueError(f"{label} appeared during publication")
        raise OSError(error_number, os.strerror(error_number))
    # No portable exclusive primitive is safe to emulate with os.rename:
    # that call replaces an existing destination.  Fail closed instead.
    raise OSError(unavailable_message)


def _rename_directory_noreplace(parent_fd: int, source_name: str, final_name: str) -> None:
    """Use the platform's directory-relative exclusive rename primitive."""

    _rename_noreplace(
        parent_fd,
        source_name,
        final_name,
        "Meshy final task directory",
        "Meshy publication requires an exclusive directory rename primitive",
    )


def atomic_publish_directory(
    source_dir: Union[str, os.PathLike],
    final_dir: Union[str, os.PathLike],
    project_root: Union[str, os.PathLike],
    allowed_root: Union[str, os.PathLike],
) -> None:
    """Atomically publish a complete private task directory without replacement.

    ``source_dir`` and ``final_dir`` must be direct siblings under an existing,
    pinned ``allowed_root``.  The source is recursively fsynced and renamed
    relative to the pinned parent descriptor; the parent is fsynced after the
    rename.  This is a trusted-workspace boundary, not a claim against a
    malicious same-UID actor racing every descriptor operation.
    """

    physical = physical_project_root(project_root)
    source = _path_from_explicit_root(project_root, physical, source_dir)
    final = _path_from_explicit_root(project_root, physical, final_dir)
    allowed = _path_from_explicit_root(project_root, physical, allowed_root)
    if source == final or source.name in ("", ".", "..") or final.name in ("", ".", ".."):
        raise ValueError("Meshy task directories must have distinct names")
    if source.parent != final.parent:
        raise ValueError("Meshy task directories must be direct siblings")
    _reject_symlink_components_below(physical, allowed, "Meshy allowed root")
    _reject_symlink_components_below(physical, source, "Meshy task source")
    _reject_symlink_components_below(physical, final, "Meshy task final")
    if not _contained(allowed, source.parent) or not _contained(allowed, source):
        raise ValueError("Meshy task directories must be under the allowed root")
    if not allowed.exists() or not allowed.is_dir():
        raise ValueError("Meshy allowed root is not a directory")
    try:
        source_info = os.lstat(source)
    except FileNotFoundError as exc:
        raise ValueError("Meshy task source directory is missing") from exc
    if stat.S_ISLNK(source_info.st_mode) or not stat.S_ISDIR(source_info.st_mode):
        raise ValueError("Meshy task source must be a private directory")
    if source_info.st_mode & 0o077:
        raise ValueError("Meshy task source directory must be private")
    try:
        os.lstat(final)
    except FileNotFoundError:
        pass
    else:
        raise ValueError("Meshy final task directory already exists")

    identities = _snapshot_identities(source, "Meshy task source")
    parent_identity = identities.get(source.parent.parts)
    if parent_identity is None:
        raise OSError("Meshy task parent disappeared during validation")
    hook = _ATOMIC_VALIDATION_HOOK
    if hook is not None:
        hook(final)

    parent_fd: Optional[int] = None
    source_fd: Optional[int] = None
    renamed = False
    try:
        try:
            parent_fd = _open_pinned_parent(source, identities, _pinned_directory_flags())
            _check_fd_identity(parent_fd, parent_identity, "Meshy task parent")
            try:
                source_fd = os.open(source.name, _pinned_directory_flags(), dir_fd=parent_fd)
            except FileNotFoundError as exc:
                raise OSError("Meshy task source disappeared after validation") from exc
            opened_source = os.fstat(source_fd)
            if (opened_source.st_dev, opened_source.st_ino) != (source_info.st_dev, source_info.st_ino):
                raise OSError("Meshy task source identity changed after validation")
            if not stat.S_ISDIR(opened_source.st_mode) or opened_source.st_mode & 0o077:
                raise OSError("Meshy task source is no longer a private directory")
            try:
                os.stat(final.name, dir_fd=parent_fd, follow_symlinks=False)
            except FileNotFoundError:
                pass
            else:
                raise ValueError("Meshy final task directory appeared during publication")
            _fsync_directory_tree(source_fd)
            try:
                os.stat(final.name, dir_fd=parent_fd, follow_symlinks=False)
            except FileNotFoundError:
                pass
            else:
                raise ValueError("Meshy final task directory appeared during publication")
            _rename_directory_noreplace(parent_fd, source.name, final.name)
            renamed = True
            assert source_fd is not None
            published_source_fd = source_fd
            os.close(published_source_fd)
            source_fd = None
            os.fsync(parent_fd)
        except BaseException as exc:
            if renamed:
                raise PublicationUncertainError() from exc
            raise
    finally:
        if source_fd is not None:
            try:
                os.close(source_fd)
            except OSError:
                pass
        if parent_fd is not None:
            try:
                os.close(parent_fd)
            except OSError:
                pass


def atomic_write_bytes(
    path: Union[str, os.PathLike],
    payload: bytes,
    project_root: Union[str, os.PathLike],
    allowed_root: Union[str, os.PathLike],
    mode: int = 0o600,
) -> None:
    """Publish exact bytes through the same pinned-FD path as JSON."""

    _atomic_write_payload(path, payload, project_root, allowed_root, mode=mode)


def atomic_create_bytes(
    path: Union[str, os.PathLike],
    payload: bytes,
    project_root: Union[str, os.PathLike],
    allowed_root: Union[str, os.PathLike],
    mode: int = 0o600,
) -> None:
    """Atomically create exact bytes without replacing an existing leaf."""

    _atomic_write_payload(
        path,
        payload,
        project_root,
        allowed_root,
        mode=mode,
        replace=False,
    )


def atomic_write_json(
    path: Union[str, os.PathLike],
    value: object,
    project_root: Union[str, os.PathLike],
    allowed_root: Union[str, os.PathLike],
    mode: int = 0o600,
) -> None:
    """Publish canonical JSON through the pinned-FD byte publisher."""

    _atomic_write_payload(path, canonical_json_bytes(value), project_root, allowed_root, mode=mode)


@contextmanager
def credit_lock(
    account_lock_id: str,
    deadline: float,
    clock: Optional[Callable[[], float]] = None,
) -> Iterator[None]:
    """Hold an account-scoped Meshy credit lock through a monotonic deadline.

    The account identifier is a lowercase SHA-256 digest, never the API key.
    The lock lives in a private directory in the canonical system temporary
    directory, and every pathname operation uses pinned, no-follow descriptors.
    Creation races are retried so a contender waits for the same lock rather
    than failing merely because another process created its leaf first.
    """

    if not isinstance(account_lock_id, str) or _ACCOUNT_LOCK_ID_RE.fullmatch(account_lock_id) is None:
        raise ValueError("Meshy account lock id must be a 64-character lowercase hex digest")
    if (
        not isinstance(deadline, (int, float))
        or isinstance(deadline, bool)
        or not math.isfinite(float(deadline))
    ):
        raise ValueError("credit lock deadline must be finite")
    clock_fn = clock or time.monotonic
    if not callable(clock_fn):
        raise ValueError("credit lock clock must be callable")
    lock_path = _CREDIT_LOCK_DIRECTORY / (str(os.getuid()) + "-" + account_lock_id + ".lock")
    if lock_path.parent != _CREDIT_LOCK_DIRECTORY or lock_path.name.endswith("/"):
        raise OSError("credit lock path is invalid")

    while True:
        parent_fd: Optional[int] = None
        lock_fd: Optional[int] = None
        acquired = False
        try:
            if clock_fn() >= float(deadline):
                raise TimeoutError("Meshy credit lock deadline exceeded")
            try:
                identities = _snapshot_identities(lock_path, "Meshy credit lock")
                parent_fd = _open_pinned_parent(lock_path, identities, _pinned_directory_flags())
            except OSError as exc:
                if (exc.errno != errno.EEXIST and "appeared during validation" not in str(exc)) or clock_fn() >= float(deadline):
                    raise OSError("Meshy credit lock could not prepare its private directory") from exc
                continue
            parent_info = os.fstat(parent_fd)
            if not stat.S_ISDIR(parent_info.st_mode) or parent_info.st_mode & 0o077:
                raise OSError("Meshy credit lock directory must be private")
            os.fchmod(parent_fd, 0o700)
            os.fsync(parent_fd)

            expected_lock = identities.get(lock_path.parts)
            lock_flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0)
            if expected_lock is None:
                lock_flags |= os.O_EXCL
            if hasattr(os, "O_CLOEXEC"):
                lock_flags |= os.O_CLOEXEC
            try:
                lock_fd = os.open(lock_path.name, lock_flags, 0o600, dir_fd=parent_fd)
            except OSError as exc:
                if exc.errno == errno.ELOOP:
                    raise OSError("Meshy credit lock must not be a symlink") from exc
                if exc.errno == errno.EEXIST and expected_lock is None:
                    if clock_fn() >= float(deadline):
                        raise TimeoutError("Meshy credit lock deadline exceeded")
                    continue
                raise OSError("Meshy credit lock could not be opened safely") from exc
            info = os.fstat(lock_fd)
            if not stat.S_ISREG(info.st_mode):
                raise OSError("Meshy credit lock must be a regular file")
            if expected_lock is not None:
                _check_fd_identity(lock_fd, expected_lock, "Meshy credit lock")
            os.fchmod(lock_fd, 0o600)
            os.fsync(lock_fd)
            while True:
                if clock_fn() >= float(deadline):
                    raise TimeoutError("Meshy credit lock deadline exceeded")
                try:
                    fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    acquired = True
                    break
                except OSError as exc:
                    if exc.errno not in (errno.EACCES, errno.EAGAIN):
                        raise OSError("Meshy credit lock acquisition failed") from exc
                    remaining = float(deadline) - clock_fn()
                    if remaining <= 0:
                        raise TimeoutError("Meshy credit lock deadline exceeded")
                    time.sleep(min(0.01, remaining))
            yield
            return
        finally:
            if lock_fd is not None:
                if acquired:
                    try:
                        fcntl.flock(lock_fd, fcntl.LOCK_UN)
                    except OSError:
                        pass
                try:
                    os.close(lock_fd)
                except OSError:
                    pass
            if parent_fd is not None:
                try:
                    os.close(parent_fd)
                except OSError:
                    pass


__all__ = [
    "STAGING_RELATIVE",
    "CREDIT_LOCK_RELATIVE",
    "PROTECTED_RUNTIME_RELATIVE_PATHS",
    "ProtectedSurfaceRecord",
    "PublicationUncertainError",
    "atomic_publish_directory",
    "atomic_create_bytes",
    "atomic_write_bytes",
    "atomic_write_json",
    "credit_lock",
    "canonical_json_bytes",
    "file_sha256",
    "governed_task_path",
    "physical_project_root",
    "protected_runtime_paths",
    "reject_protected_output",
    "snapshot_protected_surfaces",
    "strict_load_json",
    "strict_load_json_bytes",
]
