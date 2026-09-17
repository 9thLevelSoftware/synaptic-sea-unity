#!/usr/bin/env python3
"""Create the external Blender master for a selected Meshy candidate.

Host Python validates the governed candidate and launches Blender with a bounded
process-group lifecycle.  ``bpy`` is imported only by the Blender runtime.
"""

from __future__ import annotations

import argparse
import os
import selectors
import signal
import stat
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, List, Optional, Sequence, Tuple, Union

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools.meshy_asset_contract import AssetContract, load_contract  # noqa: E402
from tools import meshy_governance as governance  # noqa: E402


BLENDER_PATH = "/opt/homebrew/bin/blender"
TRUSTED_MASTER_ROOT = Path("/Volumes/Untitled/SynapticSeaAssets/meshy/source")
MASTER_SOURCE_ROOT = TRUSTED_MASTER_ROOT
MASTER_ROOT = TRUSTED_MASTER_ROOT
PROTECTED_RELATIVE = (
    Path("assets/imported"),
    Path("data/combat"),
    Path("data/props"),
    Path("scenes/wrappers"),
)
PROTECTED_RUNTIME_PATHS = PROTECTED_RELATIVE
_MAX_PROCESS_OUTPUT = 1024 * 1024
_MAX_PROCESS_TIMEOUT = 120.0
_PROCESS_GRACE = 2.0

PathLike = Union[str, os.PathLike]


class BlenderMasterError(ValueError):
    """Raised when a Blender master request is unsafe or invalid."""


def _as_path(value: PathLike) -> Path:
    return Path(value).expanduser()


def _project_path(project_root: Path, value: PathLike) -> Path:
    path = _as_path(value)
    if not path.is_absolute():
        path = _as_path(project_root) / path
    return Path(os.path.abspath(os.fspath(path)))


def _safe_asset_id(asset_id: object) -> str:
    if not isinstance(asset_id, str) or not asset_id or any(
        character not in "abcdefghijklmnopqrstuvwxyz0123456789_-"
        for character in asset_id
    ) or asset_id[0] not in "abcdefghijklmnopqrstuvwxyz0123456789":
        raise BlenderMasterError("asset_id must be a lowercase identifier")
    return asset_id


def derive_master_path(asset_id: Union[str, AssetContract], optional_asset_id: Optional[str] = None) -> Path:
    """Return the fixed external editable-source path for ``asset_id``."""

    candidate = optional_asset_id
    if candidate is None:
        candidate = asset_id.asset_id if isinstance(asset_id, AssetContract) else asset_id
    safe_asset_id = _safe_asset_id(candidate)
    return TRUSTED_MASTER_ROOT / safe_asset_id / (safe_asset_id + "_master.blend")


def _contained(root: Path, candidate: Path) -> bool:
    return candidate == root or root in candidate.parents


def _reject_symlink_components(path: Path, label: str, allow_missing_leaf: bool = True) -> None:
    absolute = Path(os.path.abspath(os.fspath(path)))
    current = Path(absolute.anchor)
    for index, part in enumerate(absolute.parts[1:]):
        current /= part
        try:
            mode = current.lstat().st_mode
        except FileNotFoundError:
            if not allow_missing_leaf:
                raise BlenderMasterError(label + " contains a missing component")
            break
        except OSError as exc:
            raise BlenderMasterError("cannot inspect " + label) from exc
        if stat.S_ISLNK(mode):
            raise BlenderMasterError(label + " contains symlink component")
        if index != len(absolute.parts[1:]) - 1 and not stat.S_ISDIR(mode):
            raise BlenderMasterError(label + " contains a non-directory ancestor")


def reject_protected_output_path(master_path: PathLike, project_root: Optional[PathLike] = None) -> Path:
    """Reject a path targeting one of the live runtime surfaces.

    This compatibility policy helper does not make an arbitrary path a valid
    master path; ``validate_master_output_path`` is the strict writer boundary.
    """

    target = _as_path(master_path).resolve(strict=False)
    parts = target.parts
    for index in range(len(parts) - 1):
        if parts[index:index + 2] in (("assets", "imported"), ("data", "combat"), ("data", "props"), ("scenes", "wrappers")):
            raise BlenderMasterError("master output path is protected")
    if project_root is not None:
        root = _as_path(project_root).resolve(strict=False)
        for relative in PROTECTED_RELATIVE:
            protected = root / relative
            if _contained(protected, target):
                raise BlenderMasterError("master output path is protected")
    return target


def validate_master_output_path(path: PathLike) -> Path:
    """Validate the exact trusted-root ``<asset>/<asset>_master.blend`` leaf.

    The trusted root is deliberately an existing, inspectable, same-UID
    workspace boundary.  This is not protection from a malicious same-UID
    process rebinding the path after these checks; governance's pinned atomic
    writer is the defense at publication time.
    """

    target = _as_path(path)
    if not target.is_absolute():
        raise BlenderMasterError("master output path must be absolute")
    root = Path(TRUSTED_MASTER_ROOT)
    if not root.exists() or not root.is_dir() or root.is_symlink():
        raise BlenderMasterError("trusted master root must be an existing regular directory")
    _reject_symlink_components(root, "trusted master root", allow_missing_leaf=False)
    lexical = Path(os.path.abspath(os.fspath(target)))
    if not _contained(root, lexical) or lexical == root:
        raise BlenderMasterError("master output path is outside trusted master root")
    relative = lexical.relative_to(root)
    if len(relative.parts) != 2:
        raise BlenderMasterError("master output path must be the exact asset master leaf")
    asset_id, filename = relative.parts
    if filename != asset_id + "_master.blend":
        raise BlenderMasterError("master output filename is not canonical")
    _safe_asset_id(asset_id)
    if lexical.name.endswith((".imported", ".runtime", ".tmp", ".bak")):
        raise BlenderMasterError("master output path uses a protected suffix")
    _reject_symlink_components(lexical, "master output path")
    if lexical.exists() and (lexical.is_symlink() or not lexical.is_file()):
        raise BlenderMasterError("master output path must be a regular file")
    if lexical.parent.exists() and (not lexical.parent.is_dir() or lexical.parent.is_symlink()):
        raise BlenderMasterError("master output parent must be inspectable")
    return lexical


def build_blender_command(project_root: PathLike, contract: PathLike, task_dir: PathLike, reviewer: str, blender: str = BLENDER_PATH) -> List[str]:
    """Build the canonical host-to-Blender invocation without running it."""

    if not isinstance(reviewer, str) or not reviewer.strip():
        raise BlenderMasterError("reviewer must be non-empty text")
    return [
        blender,
        "--background",
        "--factory-startup",
        "--python",
        str(Path(__file__).resolve()),
        "--",
        "--project-root",
        str(_as_path(project_root)),
        "--contract",
        str(_as_path(contract)),
        "--task-dir",
        str(_as_path(task_dir)),
        "--reviewer",
        reviewer,
    ]


def _regular_file(path: Path, label: str, max_bytes: int = 512 * 1024 * 1024) -> Path:
    try:
        info = os.lstat(path)
    except OSError as exc:
        raise BlenderMasterError(label + " could not be inspected") from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise BlenderMasterError(label + " must be a regular file")
    if info.st_size <= 0 or info.st_size > max_bytes:
        raise BlenderMasterError(label + " has an invalid size")
    return path


def _request_inputs(project_root: PathLike, contract_path: PathLike, task_dir: PathLike, reviewer: str) -> Tuple[AssetContract, Path, Path]:
    """Load only a selected, fully bound direct Meshy candidate task."""

    if not isinstance(reviewer, str) or not reviewer.strip():
        raise BlenderMasterError("reviewer must be non-empty text")
    try:
        from tools import meshy_candidate_review as candidate_review
        review_path, review, generation, root, asset_root = candidate_review._load_task_record(project_root, task_dir)
    except Exception as exc:
        if isinstance(exc, BlenderMasterError):
            raise
        raise BlenderMasterError("candidate task is not fully governed: " + str(exc)) from exc
    if review.get("state") != "selected":
        raise BlenderMasterError("Blender master requires a selected review")
    if generation.get("status") != "SUCCEEDED":
        raise BlenderMasterError("Blender master requires SUCCEEDED generation evidence")
    resolved_task = Path(review_path).parent
    task_asset_id = resolved_task.parent.name
    task_id = resolved_task.name
    try:
        contract_file = _project_path(root, contract_path)
        _regular_file(contract_file, "contract")
        contract = load_contract(contract_file)
    except (OSError, TypeError, ValueError) as exc:
        raise BlenderMasterError("contract is invalid: " + str(exc)) from exc
    if contract.asset_id != task_asset_id or review.get("asset_id") != task_asset_id or generation.get("asset_id") != task_asset_id:
        raise BlenderMasterError("asset identity does not match the governed task")
    if review.get("task_id") != task_id or generation.get("task_id") != task_id:
        raise BlenderMasterError("task identity does not match the governed task")
    if generation.get("contract_sha256") != contract.sha256:
        raise BlenderMasterError("contract hash does not match generation evidence")
    raw_path = _regular_file(resolved_task / "raw.glb", "raw.glb")
    outputs = generation.get("outputs")
    raw_evidence = outputs.get("raw.glb") if isinstance(outputs, dict) else None
    if not isinstance(raw_evidence, dict) or raw_evidence.get("byte_size") != raw_path.stat().st_size:
        raise BlenderMasterError("raw.glb size does not match generation evidence")
    try:
        raw_hash = governance.file_sha256(raw_path, max_bytes=512 * 1024 * 1024)
    except (OSError, ValueError) as exc:
        raise BlenderMasterError("raw.glb could not be hashed") from exc
    if raw_evidence.get("sha256") != raw_hash:
        raise BlenderMasterError("raw.glb hash does not match generation evidence")
    master_path = validate_master_output_path(derive_master_path(contract.asset_id))
    return contract, raw_path, master_path


def _bounded_timeout(value: object) -> float:
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math_is_finite(float(value)) or float(value) <= 0.0 or float(value) > _MAX_PROCESS_TIMEOUT:
        raise BlenderMasterError("timeout must be finite, positive, and at most 120 seconds")
    return float(value)


def math_is_finite(value: float) -> bool:
    # Kept tiny and import-free for Blender's host bootstrap.
    return value == value and value not in (float("inf"), float("-inf"))


def _kill_process_group(process: Any, sig: int) -> None:
    try:
        os.killpg(process.pid, sig)
    except ProcessLookupError:
        return
    except PermissionError:
        poll = getattr(process, "poll", None)
        if getattr(process, "returncode", None) is not None or callable(poll) and poll() is not None:
            return
        raise BlenderMasterError("Blender process group cleanup failed")
    except OSError as exc:
        raise BlenderMasterError("Blender process group cleanup failed") from exc


def _text_output(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")
    return str(value)


def _isolated_process_group_id(process: Any) -> int:
    pid = getattr(process, "pid", None)
    if os.name != "posix" or not isinstance(pid, int) or isinstance(pid, bool) or pid <= 1:
        raise BlenderMasterError("Blender process did not establish a safe process group")
    try:
        group_id = os.getpgid(pid)
    except ProcessLookupError:
        group_id = pid
    except OSError as exc:
        raise BlenderMasterError("Blender process group could not be inspected") from exc
    if group_id != pid:
        raise BlenderMasterError("Blender process is not the isolated session leader")
    return pid


def _terminate_and_drain(process: Any, selector: Any) -> None:
    pid = getattr(process, "pid", None)
    try:
        group_id = _isolated_process_group_id(process)
    except BlenderMasterError:
        if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 1:
            try:
                process.wait(timeout=_PROCESS_GRACE)
            except subprocess.TimeoutExpired:
                pass
            _drain_selector(selector)
            raise
        group_id = pid
    _signal_process_group(group_id, signal.SIGTERM, process)
    try:
        process.wait(timeout=_PROCESS_GRACE)
    except subprocess.TimeoutExpired:
        pass
    # Always issue the escalation signal: descendants can retain inherited
    # pipes after the session leader has exited.
    _signal_process_group(group_id, signal.SIGKILL, process)
    try:
        process.wait(timeout=_PROCESS_GRACE)
    except subprocess.TimeoutExpired as exc:
        raise BlenderMasterError("Blender process could not be reaped") from exc
    _drain_selector(selector)


def _signal_process_group(group_id: int, sig: int, process: Any) -> None:
    try:
        os.killpg(group_id, sig)
    except ProcessLookupError:
        return
    except PermissionError:
        poll = getattr(process, "poll", None)
        if getattr(process, "returncode", None) is not None or callable(poll) and poll() is not None:
            return
        raise BlenderMasterError("Blender process group cleanup failed")
    except OSError as exc:
        raise BlenderMasterError("Blender process group cleanup failed") from exc


def _drain_selector(selector: Any) -> None:
    if selector is None:
        return
    deadline = time.monotonic() + _PROCESS_GRACE
    while selector.get_map() and time.monotonic() < deadline:
        events = selector.select(max(0.0, deadline - time.monotonic()))
        if not events:
            break
        for key, _mask in events:
            try:
                chunk = os.read(key.fd, 65536)
            except OSError:
                chunk = b""
            if not chunk:
                try:
                    selector.unregister(key.fileobj)
                except (KeyError, ValueError):
                    pass
    if selector.get_map():
        raise BlenderMasterError("Blender process output did not drain")


def _run_with_bounded_streams(process: Any, command: Sequence[str], timeout: float) -> subprocess.CompletedProcess:
    selector = selectors.DefaultSelector()
    captured = {"stdout": bytearray(), "stderr": bytearray()}
    total = 0
    reason: Optional[str] = None
    streams = (("stdout", process.stdout), ("stderr", process.stderr))
    try:
        for label, stream in streams:
            if stream is None:
                continue
            try:
                os.set_blocking(stream.fileno(), False)
            except (AttributeError, OSError) as exc:
                raise BlenderMasterError("Blender output stream could not be made non-blocking") from exc
            selector.register(stream, selectors.EVENT_READ, label)
        deadline = time.monotonic() + timeout
        while selector.get_map() or process.poll() is None:
            remaining = deadline - time.monotonic()
            if remaining <= 0.0:
                reason = "timeout"
                break
            if selector.get_map():
                events = selector.select(remaining)
                if not events:
                    reason = "timeout"
                    break
                for key, _mask in events:
                    try:
                        chunk = os.read(key.fd, 65536)
                    except OSError as exc:
                        raise BlenderMasterError("Blender output could not be read") from exc
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    total += len(chunk)
                    if total > _MAX_PROCESS_OUTPUT:
                        reason = "output cap"
                        break
                    captured[key.data].extend(chunk)
                if reason is not None:
                    break
            else:
                time.sleep(min(0.01, remaining))
        if reason is None:
            try:
                returncode = process.wait(timeout=max(0.0, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                reason = "timeout"
                _terminate_and_drain(process, selector)
                returncode = -signal.SIGKILL
        else:
            _terminate_and_drain(process, selector)
            returncode = -signal.SIGKILL
        return subprocess.CompletedProcess(list(command), returncode, bytes(captured["stdout"]), bytes(captured["stderr"]))
    except Exception:
        try:
            _terminate_and_drain(process, selector)
        except Exception:
            pass
        raise
    finally:
        selector.close()
        for _label, stream in streams:
            if stream is not None:
                stream.close()


def _run_with_bounded_communicate(process: Any, command: Sequence[str], timeout: float) -> subprocess.CompletedProcess:
    """Compatibility path for test doubles without real file descriptors."""

    timed_out = False
    stdout: Any = ""
    stderr: Any = ""
    try:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            timed_out = True
            stdout = _text_output(exc.stdout)
            stderr = _text_output(exc.stderr)
            _terminate_and_drain(process, None)
            try:
                tail_out, tail_err = process.communicate(timeout=_PROCESS_GRACE)
                stdout += _text_output(tail_out)
                stderr += _text_output(tail_err)
            except subprocess.TimeoutExpired as second:
                stdout += _text_output(second.stdout)
                stderr += _text_output(second.stderr)
        returncode = process.wait(timeout=_PROCESS_GRACE)
    except Exception as exc:
        try:
            _terminate_and_drain(process, None)
        except Exception:
            pass
        raise BlenderMasterError("Blender process cleanup failed: " + str(exc)) from exc
    if timed_out:
        returncode = -signal.SIGKILL
    return subprocess.CompletedProcess(list(command), returncode, _text_output(stdout)[-_MAX_PROCESS_OUTPUT:], _text_output(stderr)[-_MAX_PROCESS_OUTPUT:])


def _run_bounded_process(command: Sequence[str], *, cwd: PathLike, timeout: float = _MAX_PROCESS_TIMEOUT) -> subprocess.CompletedProcess:
    """Run Blender with bounded capture and whole-process-group cleanup."""

    limit = _bounded_timeout(timeout)
    try:
        process = subprocess.Popen(
            list(command),
            cwd=str(_as_path(cwd)),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=False,
            start_new_session=True,
        )
    except (OSError, TypeError) as exc:
        raise BlenderMasterError("Blender could not be launched") from exc
    try:
        if not isinstance(process.pid, int) or isinstance(process.pid, bool) or process.pid <= 1:
            raise BlenderMasterError("Blender process did not establish a process group")
        _isolated_process_group_id(process)
        if getattr(process, "stdout", None) is None or not callable(getattr(process, "poll", None)):
            return _run_with_bounded_communicate(process, command, limit)
        return _run_with_bounded_streams(process, command, limit)
    except Exception:
        try:
            _terminate_and_drain(process, None)
        except Exception:
            pass
        for stream in (getattr(process, "stdout", None), getattr(process, "stderr", None)):
            if stream is not None:
                try:
                    stream.close()
                except OSError:
                    pass
        raise


def _ensure_collection(bpy: object, scene: object, name: str) -> object:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if scene.collection.children.get(name) is None:
        scene.collection.children.link(collection)
    return collection


def _link_only(obj: object, collection: object) -> None:
    for existing in list(obj.users_collection):
        existing.objects.unlink(obj)
    collection.objects.link(obj)


def _save_blend_atomically(bpy: object, master_path: PathLike) -> None:
    """Save Blender to a private temp file, then publish exact bytes atomically."""

    target = validate_master_output_path(master_path)
    root = Path(TRUSTED_MASTER_ROOT)
    target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    if target.parent.is_symlink():
        raise BlenderMasterError("master output parent must not be a symlink")
    with tempfile.TemporaryDirectory(prefix=".meshy-master-", dir=str(root)) as temporary_dir:
        temporary = Path(temporary_dir) / target.name
        try:
            result = bpy.ops.wm.save_as_mainfile(filepath=str(temporary))
        except Exception as exc:
            raise BlenderMasterError("Blender could not save the master") from exc
        try:
            result_values = set(result)
        except TypeError:
            result_values = {str(result)}
        if "FINISHED" not in result_values or "CANCELLED" in result_values or "ERROR" in result_values:
            raise BlenderMasterError("Blender master save did not finish")
        _regular_file(temporary, "temporary Blender master")
        try:
            payload = temporary.read_bytes()
        except OSError as exc:
            raise BlenderMasterError("temporary Blender master could not be read") from exc
        if not payload:
            raise BlenderMasterError("temporary Blender master is empty")
        try:
            governance.atomic_write_bytes(
                target,
                payload,
                project_root=root,
                allowed_root=root,
                mode=0o600,
            )
        except (OSError, RuntimeError, TypeError, ValueError) as exc:
            raise BlenderMasterError("master publication failed") from exc
    _regular_file(target, "published Blender master")
    if stat.S_IMODE(target.stat().st_mode) != 0o600 or target.stat().st_size <= 0:
        raise BlenderMasterError("published Blender master permissions or size are invalid")


def run_blender_master(raw_path: Path, master_path: Path, contract: AssetContract, reviewer: str) -> None:
    """Run Blender-only import, organization, marker, and save steps."""

    import bpy  # type: ignore

    scene = bpy.context.scene
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection, do_unlink=True)

    source_raw = _ensure_collection(bpy, scene, "SOURCE_RAW")
    working = _ensure_collection(bpy, scene, "WORKING")
    sockets_markers = _ensure_collection(bpy, scene, "SOCKETS_MARKERS")
    _ensure_collection(bpy, scene, "EXPORT")
    source_raw.hide_viewport = True
    source_raw.hide_render = True

    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.gltf(filepath=str(raw_path))
    if "FINISHED" not in result:
        raise BlenderMasterError("Blender could not import raw.glb")
    imported = [obj for obj in bpy.data.objects if obj not in before]
    source_meshes = [obj for obj in imported if obj.type == "MESH"]
    if not source_meshes:
        raise BlenderMasterError("raw.glb contains no mesh geometry")

    for obj in imported:
        _link_only(obj, source_raw)
        obj.hide_viewport = True
        obj.hide_render = True
        obj.hide_select = True
        obj["meshy_source_raw"] = True

    for source_obj in source_meshes:
        duplicate = source_obj.copy()
        duplicate.data = source_obj.data.copy()
        duplicate.name = source_obj.name + "_WORKING"
        working.objects.link(duplicate)
        duplicate.hide_viewport = False
        duplicate.hide_render = False
        duplicate.hide_select = False
        duplicate["meshy_working_copy"] = True

    dimensions = contract.document["dimensions_m"]
    marker_height = max(float(dimensions[2]), 0.001)
    origin = bpy.data.objects.new("ORIGIN_MARKER", None)
    origin.empty_display_type = "PLAIN_AXES"
    origin.empty_display_size = max(max(float(value) for value in dimensions) * 0.1, 0.05)
    origin["pivot_policy"] = contract.document["pivot"]
    origin["asset_id"] = contract.asset_id
    sockets_markers.objects.link(origin)

    forward = bpy.data.objects.new("FORWARD_Z_MARKER", None)
    forward.empty_display_type = "ARROWS"
    forward.empty_display_size = max(marker_height * 0.5, 0.05)
    forward.location = (0.0, 0.0, marker_height)
    forward["forward_axis"] = "+Z"
    forward["asset_id"] = contract.asset_id
    sockets_markers.objects.link(forward)

    scene["meshy_asset_id"] = contract.asset_id
    scene["meshy_contract_sha256"] = contract.sha256
    scene["meshy_reviewer"] = reviewer
    _save_blend_atomically(bpy, master_path)
    print("MESHY BLENDER MASTER READY asset={0}".format(contract.asset_id))


def _validate_timeout(value: str) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError) as exc:
        raise argparse.ArgumentTypeError("timeout must be finite") from exc
    try:
        return _bounded_timeout(parsed)
    except BlenderMasterError as exc:
        raise argparse.ArgumentTypeError(str(exc)) from exc


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--contract", type=Path, required=True)
    parser.add_argument("--task-dir", type=Path, required=True)
    parser.add_argument("--reviewer", required=True)
    parser.add_argument("--timeout", type=_validate_timeout, default=_MAX_PROCESS_TIMEOUT)
    return parser


_build_parser = build_parser


def _is_blender_runtime() -> bool:
    executable = Path(sys.executable).name.lower()
    return executable.startswith("blender") or "--background" in sys.argv


def _runtime_argv(argv: Optional[Sequence[str]]) -> Optional[List[str]]:
    if argv is not None:
        return list(argv)
    if "--" not in sys.argv:
        return None
    return list(sys.argv[sys.argv.index("--") + 1:])


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    return build_parser().parse_args(_runtime_argv(argv) if argv is None and _is_blender_runtime() else argv)


master_path_for_asset = derive_master_path
reject_protected_path = reject_protected_output_path
build_master_command = build_blender_command


def main(argv: Optional[Sequence[str]] = None) -> int:
    runtime = _is_blender_runtime() if argv is None else False
    parse_argv = _runtime_argv(argv) if runtime else argv
    try:
        args = build_parser().parse_args(parse_argv)
        contract, raw_path, master_path = _request_inputs(args.project_root, args.contract, args.task_dir, args.reviewer)
        if runtime:
            run_blender_master(raw_path, master_path, contract, args.reviewer)
            return 0
        command = build_blender_command(args.project_root, args.contract, args.task_dir, args.reviewer)
        completed = _run_bounded_process(command, cwd=args.project_root, timeout=args.timeout)
        if completed.returncode != 0:
            print("meshy_blender_master: Blender command failed", file=sys.stderr)
            return int(completed.returncode or 1)
        print("MESHY BLENDER MASTER COMMAND PASS asset={0}".format(contract.asset_id))
        return 0
    except (BlenderMasterError, OSError, ValueError) as exc:
        print("meshy_blender_master: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
