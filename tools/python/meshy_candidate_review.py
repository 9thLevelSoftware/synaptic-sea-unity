#!/usr/bin/env python3
"""Gate staged Meshy candidates through a monotonic review state machine."""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Tuple, Union

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools import meshy_governance as governance  # noqa: E402
from tools.meshy_asset_contract import canonical_json_bytes  # noqa: E402


SCHEMA_VERSION = "1.0.0"
DOCUMENT_KIND = "meshy_candidate_review"
CHECK_FIELDS = (
    "silhouette_readable",
    "proportions_match_contract",
    "functional_volume_present",
    "movable_parts_separable",
    "cleanup_bounded",
    "camera_readability",
)
REVIEW_STATES = (
    "pending",
    "rejected",
    "selected",
    "blender_cleanup_pass",
    "runtime_review_pass",
    "promotion_ready",
)
TRANSITIONS = {
    "pending": frozenset(("rejected", "selected")),
    "selected": frozenset(("blender_cleanup_pass",)),
    "blender_cleanup_pass": frozenset(("runtime_review_pass",)),
    "runtime_review_pass": frozenset(("promotion_ready",)),
    "rejected": frozenset(),
    "promotion_ready": frozenset(),
}
_STATE_DECISIONS = {
    "pending": "pending",
    "rejected": "reject",
    "selected": "accept_for_cleanup",
    "blender_cleanup_pass": "cleanup_validated",
    "runtime_review_pass": "runtime_validated",
    "promotion_ready": "promotion_ready",
}
_POST_SELECTED_STATES = frozenset(
    ("blender_cleanup_pass", "runtime_review_pass", "promotion_ready")
)
_IDENTIFIER_RE = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
_TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_MAX_JSON_DEPTH = 64
_REVIEW_MAX_BYTES = 4 * 1024 * 1024
_TERMINAL_GENERATION_STATES = frozenset(("SUCCEEDED", "FAILED"))


class ReviewError(ValueError):
    """Raised when a review record, evidence binding, or transition is invalid."""


def _is_nonempty_text(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _check_json_safety(value: object) -> List[str]:
    """Reject non-finite, cyclic, too-deep, or non-JSON review values."""

    errors: List[str] = []
    stack: List[Tuple[object, int, str]] = [(value, 0, "review")]
    seen: set[int] = set()
    while stack:
        current, depth, label = stack.pop()
        if depth > _MAX_JSON_DEPTH:
            errors.append("review JSON maximum nesting depth exceeded")
            continue
        if isinstance(current, float):
            if not math.isfinite(current):
                errors.append("{0} contains a non-finite number".format(label))
            continue
        if isinstance(current, (dict, list, tuple)):
            identity = id(current)
            if identity in seen:
                errors.append("{0} contains a cyclic value".format(label))
                continue
            seen.add(identity)
            if isinstance(current, dict):
                for key, child in current.items():
                    if not isinstance(key, str):
                        errors.append("{0} contains a non-string key".format(label))
                        continue
                    stack.append((child, depth + 1, "{0}.{1}".format(label, key)))
            else:
                for index, child in enumerate(current):
                    stack.append((child, depth + 1, "{0}[{1}]".format(label, index)))
            continue
        if current is None or isinstance(current, (str, bool, int)):
            continue
        errors.append("{0} contains a non-JSON value".format(label))
    return sorted(set(errors))


def validate_review(record: object) -> List[str]:
    """Return deterministic validation errors for one review record."""

    errors = _check_json_safety(record)
    required = {
        "schema_version",
        "document_kind",
        "asset_id",
        "task_id",
        "state",
        "decision",
        "checks",
        "rejection_reasons",
        "reviewer",
    }
    if not isinstance(record, dict):
        return errors + ["review record must be an object"]

    unknown = sorted((key for key in record if key not in required), key=str)
    errors.extend("unknown review field: {0}".format(key) for key in unknown)
    missing = sorted(required - set(record))
    errors.extend("review missing field: {0}".format(key) for key in missing)

    if record.get("schema_version") != SCHEMA_VERSION:
        errors.append("schema_version must be {0}".format(SCHEMA_VERSION))
    if record.get("document_kind") != DOCUMENT_KIND:
        errors.append("document_kind must be {0}".format(DOCUMENT_KIND))

    asset_id = record.get("asset_id")
    if not isinstance(asset_id, str) or _IDENTIFIER_RE.fullmatch(asset_id) is None:
        errors.append("asset_id must be a lowercase identifier")
    task_id = record.get("task_id")
    if not isinstance(task_id, str) or _TASK_ID_RE.fullmatch(task_id) is None:
        errors.append("task_id must be a safe identifier")

    state = record.get("state")
    if state not in REVIEW_STATES:
        errors.append("state must be one of: {0}".format(", ".join(REVIEW_STATES)))
    decision = record.get("decision")
    if not _is_nonempty_text(decision):
        errors.append("decision must be non-empty text")

    checks = record.get("checks")
    if not isinstance(checks, dict):
        errors.append("checks must be an object")
    else:
        unknown_checks = sorted((key for key in checks if key not in CHECK_FIELDS), key=str)
        errors.extend("unknown checks field: {0}".format(key) for key in unknown_checks)
        errors.extend(
            "checks missing field: {0}".format(field)
            for field in CHECK_FIELDS
            if field not in checks
        )
        for field in CHECK_FIELDS:
            if field in checks and type(checks[field]) is not bool:
                errors.append("checks.{0} must be boolean".format(field))

    rejection_reasons = record.get("rejection_reasons")
    if not isinstance(rejection_reasons, list):
        errors.append("rejection_reasons must be a list")
    else:
        for index, reason in enumerate(rejection_reasons):
            if not _is_nonempty_text(reason):
                errors.append("rejection_reasons[{0}] must be non-empty text".format(index))
        if all(isinstance(reason, str) for reason in rejection_reasons):
            if len(rejection_reasons) != len(set(rejection_reasons)):
                errors.append("rejection_reasons must contain unique values")

    reviewer = record.get("reviewer")
    if not _is_nonempty_text(reviewer):
        errors.append("reviewer must be non-empty text")

    if isinstance(state, str) and state in _STATE_DECISIONS:
        if decision != _STATE_DECISIONS[state]:
            errors.append(
                "{0} review decision must be {1}".format(state, _STATE_DECISIONS[state])
            )
    if isinstance(state, str) and state == "rejected":
        if isinstance(rejection_reasons, list) and not rejection_reasons:
            errors.append("rejected review requires at least one rejection reason")
    elif state == "pending":
        if rejection_reasons != []:
            errors.append("pending review cannot have rejection reasons")
    elif state in REVIEW_STATES:
        if rejection_reasons != []:
            errors.append("accepted review cannot have rejection reasons")

    if isinstance(state, str) and (state == "selected" or state in _POST_SELECTED_STATES):
        if isinstance(checks, dict) and any(checks.get(field) is not True for field in CHECK_FIELDS):
            errors.append("{0} review requires every check to be true".format(state))
    return sorted(set(errors))


def _copy_record(record: Dict[str, Any]) -> Dict[str, Any]:
    try:
        copied = json.loads(canonical_json_bytes(record).decode("utf-8"))
    except (TypeError, ValueError, RecursionError) as exc:
        raise ReviewError("review record cannot be copied safely") from exc
    if not isinstance(copied, dict):  # pragma: no cover - canonical input is a dict
        raise ReviewError("review record copy must be an object")
    return copied


def _validate_check_mapping(checks: object, *, require_all_true: bool) -> Dict[str, bool]:
    if not isinstance(checks, Mapping):
        raise ReviewError("checks must be an explicit mapping")
    keys = list(checks.keys())
    if any(not isinstance(key, str) for key in keys):
        raise ReviewError("checks fields must be strings")
    unknown = [key for key in keys if key not in CHECK_FIELDS]
    missing = [field for field in CHECK_FIELDS if field not in checks]
    if unknown:
        raise ReviewError("unknown checks field: {0}".format(unknown[0]))
    if missing:
        raise ReviewError("checks missing field: {0}".format(missing[0]))
    result: Dict[str, bool] = {}
    for field in CHECK_FIELDS:
        value = checks[field]
        if type(value) is not bool:
            raise ReviewError("checks.{0} must be boolean".format(field))
        result[field] = value
    if require_all_true and any(value is not True for value in result.values()):
        raise ReviewError("select requires every checklist field to be true")
    return result


def transition_review(
    record: Dict[str, Any], target_state: str, checks: Optional[Mapping[str, bool]] = None
) -> Dict[str, Any]:
    """Apply one ordinary transition; post-selected transitions require the binder."""

    if isinstance(target_state, str) and target_state in _POST_SELECTED_STATES:
        raise ReviewError(
            "evidence-bound transition unavailable until an evidence binder is implemented: "
            "{0} -> {1}".format(record.get("state") if isinstance(record, dict) else None, target_state)
        )
    current_state = record.get("state") if isinstance(record, dict) else None
    validation_record = record
    # Rejection reasons are the action payload, so validate the pending source
    # before applying them.
    if current_state == "pending" and target_state == "rejected" and isinstance(record, dict):
        validation_record = _copy_record(record)
        validation_record["rejection_reasons"] = []
    errors = validate_review(validation_record)
    if errors:
        raise ReviewError("invalid review record: {0}".format("; ".join(errors)))
    if not isinstance(target_state, str) or target_state not in REVIEW_STATES:
        raise ReviewError("invalid target state: {0}".format(target_state))
    if current_state not in TRANSITIONS:
        raise ReviewError("invalid current state: {0}".format(current_state))
    if target_state not in TRANSITIONS[current_state]:
        raise ReviewError("invalid transition: {0} -> {1}".format(current_state, target_state))

    updated = _copy_record(record)
    if checks is not None:
        updated["checks"] = _validate_check_mapping(checks, require_all_true=False)
    updated["state"] = target_state
    updated["decision"] = _STATE_DECISIONS[target_state]
    if target_state == "selected":
        # Do not infer or manufacture checklist evidence.  The caller must have
        # supplied all true fields before this transition can validate.
        updated["rejection_reasons"] = []
    elif target_state == "rejected":
        if not updated["rejection_reasons"]:
            raise ReviewError("rejected transition requires a rejection reason")
    validation_errors = validate_review(updated)
    if validation_errors:
        raise ReviewError("invalid transitioned review: {0}".format("; ".join(validation_errors)))
    return updated


def _reject_lexical_traversal(path: Path, label: str) -> None:
    if ".." in path.parts:
        raise ReviewError("{0} must not contain lexical traversal".format(label))


def _task_layout(
    project_root: Union[os.PathLike, str], task_dir: Union[os.PathLike, str]
) -> Tuple[Path, Path, Path, str, str]:
    """Resolve and constrain a task to the direct physical staging layout."""

    try:
        root = governance.physical_project_root(project_root)
        raw_task = Path(task_dir).expanduser()
        _reject_lexical_traversal(raw_task, "task directory")
        candidate = governance.governed_task_path(
            project_root, raw_task, "Meshy candidate task directory", allow_missing=False
        )
        governance.reject_protected_output(
            project_root, candidate, "Meshy candidate task directory"
        )
    except (OSError, RuntimeError, TypeError, ValueError) as exc:
        if isinstance(exc, ReviewError):
            raise
        raise ReviewError(str(exc)) from exc

    stage_root = root / governance.STAGING_RELATIVE
    try:
        relative = candidate.relative_to(stage_root)
    except ValueError as exc:
        raise ReviewError("task directory must be physically under Meshy staging") from exc
    if len(relative.parts) != 2:
        raise ReviewError("task directory must be a direct asset/task child")
    asset_id, task_id = relative.parts
    if _IDENTIFIER_RE.fullmatch(asset_id) is None:
        raise ReviewError("task directory asset_id is invalid")
    if _TASK_ID_RE.fullmatch(task_id) is None:
        raise ReviewError("task directory task_id is invalid")
    try:
        info = os.lstat(candidate)
    except OSError as exc:
        raise ReviewError("task directory could not be inspected") from exc
    if not os.path.isdir(candidate) or os.path.islink(candidate):
        raise ReviewError("task directory must be a regular directory")
    if info.st_mode & 0o077:
        raise ReviewError("task directory must be private")
    return root, candidate, stage_root / asset_id, asset_id, task_id


def _governed_artifact(root: Path, task_dir: Path, name: str) -> Path:
    if not isinstance(name, str) or not name or Path(name).name != name or name in (".", ".."):
        raise ReviewError("review artifact name must be a basename")
    try:
        return governance.governed_task_path(
            root, task_dir / name, "Meshy review " + name, allow_missing=False
        )
    except (OSError, RuntimeError, TypeError, ValueError) as exc:
        raise ReviewError(str(exc)) from exc


def _load_task_record(
    project_root: Union[os.PathLike, str], task_dir: Union[os.PathLike, str]
) -> Tuple[Path, Dict[str, Any], Dict[str, Any], Path, Path]:
    """Load canonical review plus the stage module's fully bound generation evidence."""

    root, resolved_dir, asset_root, asset_id, task_id = _task_layout(project_root, task_dir)
    review_path = _governed_artifact(root, resolved_dir, "review.json")
    generation_path = _governed_artifact(root, resolved_dir, "generation.json")
    try:
        record, raw = governance.strict_load_json_bytes(
            review_path, "Meshy candidate review", _REVIEW_MAX_BYTES
        )
    except (OSError, TypeError, ValueError, RecursionError) as exc:
        raise ReviewError("invalid canonical review JSON: {0}".format(exc)) from exc
    try:
        canonical = canonical_json_bytes(record)
    except (TypeError, ValueError, RecursionError) as exc:
        raise ReviewError("review JSON cannot be canonicalized") from exc
    if raw != canonical:
        raise ReviewError("review.json is not canonical JSON")
    errors = validate_review(record)
    if errors:
        raise ReviewError("invalid review record: {0}".format("; ".join(errors)))
    if record.get("asset_id") != asset_id or record.get("task_id") != task_id:
        raise ReviewError("review identity does not match task directory")

    # meshy_stage imports validate_review, so this import must remain local to
    # avoid the candidate-review/stage import cycle.
    try:
        from tools import meshy_stage

        generation = meshy_stage.load_generation_record(generation_path)
    except Exception as exc:
        raise ReviewError("generation evidence is not fully bound: {0}".format(exc)) from exc
    if generation.get("asset_id") != asset_id or generation.get("task_id") != task_id:
        raise ReviewError("generation identity does not match task directory")
    if generation_path.parent.name != task_id:
        raise ReviewError("generation filename does not match task directory")
    return review_path, record, generation, root, asset_root


def _reload_published(
    project_root: Union[os.PathLike, str], task_dir: Union[os.PathLike, str], expected: Dict[str, Any]
) -> Dict[str, Any]:
    _review_path, record, _generation, _root, _asset_root = _load_task_record(project_root, task_dir)
    if record != expected:
        raise ReviewError("published review did not match the requested transition")
    return record


def select_candidate(
    project_root: Union[os.PathLike, str],
    task_dir: Union[os.PathLike, str],
    reviewer: str,
    checks: Mapping[str, bool],
) -> Dict[str, Any]:
    """Select a bound SUCCEEDED candidate after recording a complete checklist."""

    if not _is_nonempty_text(reviewer):
        raise ReviewError("reviewer must be non-empty text")
    explicit_checks = _validate_check_mapping(checks, require_all_true=True)
    review_path, record, generation, root, asset_root = _load_task_record(project_root, task_dir)
    if record["state"] != "pending":
        raise ReviewError(
            "candidate must be pending; invalid transition from {0}".format(record["state"])
        )
    if generation.get("status") != "SUCCEEDED":
        raise ReviewError("candidate selection requires SUCCEEDED generation evidence")
    candidate = _copy_record(record)
    candidate["reviewer"] = reviewer
    selected = transition_review(candidate, "selected", checks=explicit_checks)
    try:
        governance.atomic_write_json(
            review_path,
            selected,
            project_root=root,
            allowed_root=asset_root,
            mode=0o600,
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise ReviewError("review publication failed: {0}".format(exc)) from exc
    return _reload_published(project_root, task_dir, selected)


def reject_candidate(
    project_root: Union[os.PathLike, str],
    task_dir: Union[os.PathLike, str],
    reason: str,
    reviewer: str,
) -> Dict[str, Any]:
    """Reject a bound SUCCEEDED or FAILED candidate with an explicit reason."""

    if not _is_nonempty_text(reason):
        raise ReviewError("rejection reason must be non-empty text")
    if not _is_nonempty_text(reviewer):
        raise ReviewError("reviewer must be non-empty text")
    review_path, record, generation, root, asset_root = _load_task_record(project_root, task_dir)
    if record["state"] != "pending":
        raise ReviewError(
            "candidate must be pending; invalid transition from {0}".format(record["state"])
        )
    if generation.get("status") not in _TERMINAL_GENERATION_STATES:
        raise ReviewError("candidate rejection requires terminal generation evidence")
    candidate = _copy_record(record)
    candidate["reviewer"] = reviewer
    candidate["rejection_reasons"] = [reason]
    rejected = transition_review(candidate, "rejected")
    try:
        governance.atomic_write_json(
            review_path,
            rejected,
            project_root=root,
            allowed_root=asset_root,
            mode=0o600,
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise ReviewError("review publication failed: {0}".format(exc)) from exc
    return _reload_published(project_root, task_dir, rejected)


def verify_review(
    project_root: Union[os.PathLike, str], task_dir: Union[os.PathLike, str]
) -> Dict[str, Any]:
    """Verify full bound evidence and return the canonical review record."""

    _review_path, record, generation, _root, _asset_root = _load_task_record(project_root, task_dir)
    state = record["state"]
    if state == "pending":
        raise ReviewError("review is still pending")
    if state == "rejected":
        return record
    if state == "selected":
        if generation.get("status") != "SUCCEEDED":
            raise ReviewError("selected review requires SUCCEEDED generation evidence")
        return record
    if state in _POST_SELECTED_STATES:
        # Post-selection state is never trusted as evidence.  Re-derive the
        # complete chain from the fixed task-local contract, R4 report, six
        # fixed captures, and report hash map before accepting it.
        try:
            from tools import meshy_runtime_review as runtime_review

            runtime_review.verify_evidence_chain(project_root, task_dir)
        except ReviewError:
            raise
        except (OSError, TypeError, ValueError, RuntimeError) as exc:
            raise ReviewError("runtime evidence could not be derived: {0}".format(exc)) from exc
        if state != "promotion_ready":
            raise ReviewError("intermediate post-selected review state is not publishable")
        return record
    raise ReviewError("review state cannot be operationally verified")


def bind_promotion_evidence(
    project_root: Union[os.PathLike, str], task_dir: Union[os.PathLike, str]
) -> Dict[str, Any]:
    """Derive runtime evidence and publish the sole promotion-ready state.

    The caller supplies neither a target state nor evidence.  The binder reads
    the governed task and lets the runtime verifier derive every accepted fact
    from the task-local contract, generation record, R4 report, and fixed
    preview leaves.  Intermediate states exist only in memory and one final
    canonical review write is performed.
    """

    review_path, record, generation, root, asset_root = _load_task_record(project_root, task_dir)
    state = record["state"]
    if state == "promotion_ready":
        # Idempotency is read-only: verification must still be complete, but a
        # valid existing record is never rewritten or timestamped again.
        return verify_review(project_root, task_dir)
    if state != "selected":
        raise ReviewError("promotion evidence requires a selected review")
    if generation.get("status") != "SUCCEEDED":
        raise ReviewError("promotion evidence requires SUCCEEDED generation evidence")

    try:
        from tools import meshy_runtime_review as runtime_review

        runtime_review.verify_evidence_chain(project_root, task_dir)
    except ReviewError:
        raise
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise ReviewError("runtime evidence could not be derived: {0}".format(exc)) from exc

    bound = _copy_record(record)
    # Keep these transitions private.  No caller can request an intermediate
    # state and no intermediate record is ever persisted.
    for target_state in ("blender_cleanup_pass", "runtime_review_pass", "promotion_ready"):
        if target_state not in TRANSITIONS[bound["state"]]:
            raise ReviewError("invalid evidence-bound transition: {0} -> {1}".format(bound["state"], target_state))
        bound["state"] = target_state
        bound["decision"] = _STATE_DECISIONS[target_state]
        bound["rejection_reasons"] = []
        errors = validate_review(bound)
        if errors:
            raise ReviewError("invalid evidence-bound review: {0}".format("; ".join(errors)))
    try:
        governance.atomic_write_json(
            review_path,
            bound,
            project_root=root,
            allowed_root=asset_root,
            mode=0o600,
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise ReviewError("promotion review publication failed: {0}".format(exc)) from exc
    return _reload_published(project_root, task_dir, bound)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    select = subparsers.add_parser("select", help="select a pending candidate for Blender cleanup")
    select.add_argument("--project-root", type=Path, required=True)
    select.add_argument("--task-dir", type=Path, required=True)
    select.add_argument("--reviewer", required=True)
    select.add_argument("--check", action="append", default=[])

    reject = subparsers.add_parser("reject", help="reject a pending candidate")
    reject.add_argument("--project-root", type=Path, required=True)
    reject.add_argument("--task-dir", type=Path, required=True)
    reject.add_argument("--reason", required=True)
    reject.add_argument("--reviewer", required=True)

    verify = subparsers.add_parser("verify", help="verify a candidate review record")
    verify.add_argument("--project-root", type=Path, required=True)
    verify.add_argument("--task-dir", type=Path, required=True)

    bind = subparsers.add_parser("bind", help="bind derived runtime evidence to promotion_ready")
    bind.add_argument("--project-root", type=Path, required=True)
    bind.add_argument("--task-dir", type=Path, required=True)
    return parser


def _cli_checks(values: object) -> Dict[str, bool]:
    if not isinstance(values, list):
        raise ReviewError("select requires repeated --check options")
    duplicates = [field for field in CHECK_FIELDS if values.count(field) > 1]
    if duplicates:
        raise ReviewError("duplicate --check field: {0}".format(duplicates[0]))
    if len(values) != len(CHECK_FIELDS):
        raise ReviewError(
            "select requires exactly one --check for every checklist field"
        )
    unknown = [value for value in values if value not in CHECK_FIELDS]
    if unknown:
        raise ReviewError("unknown --check field: {0}".format(unknown[0]))
    missing = [field for field in CHECK_FIELDS if field not in values]
    if missing:
        raise ReviewError("missing --check field: {0}".format(missing[0]))
    return {field: True for field in CHECK_FIELDS}


def main(argv: Optional[List[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        if args.command == "select":
            record = select_candidate(
                args.project_root, args.task_dir, args.reviewer, _cli_checks(args.check)
            )
            print(json.dumps(record, ensure_ascii=False, sort_keys=True))
        elif args.command == "reject":
            record = reject_candidate(
                args.project_root, args.task_dir, args.reason, args.reviewer
            )
            print(json.dumps(record, ensure_ascii=False, sort_keys=True))
        elif args.command == "verify":
            record = verify_review(args.project_root, args.task_dir)
            if record["state"] == "rejected":
                print(
                    "MESHY CANDIDATE REVIEW REJECTED task_id={0} state={1}".format(
                        record["task_id"], record["state"]
                    )
                )
                return 1
            print(
                "MESHY CANDIDATE REVIEW PASS task_id={0} state={1}".format(
                    record["task_id"], record["state"]
                )
            )
        elif args.command == "bind":
            record = bind_promotion_evidence(args.project_root, args.task_dir)
            print(
                "MESHY CANDIDATE REVIEW PASS task_id={0} state={1}".format(
                    record["task_id"], record["state"]
                )
            )
        else:  # pragma: no cover - argparse owns the command choices
            return 2
    except (OSError, TypeError, ValueError) as exc:
        print("error: {0}".format(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "CHECK_FIELDS",
    "DOCUMENT_KIND",
    "REVIEW_STATES",
    "ReviewError",
    "TRANSITIONS",
    "bind_promotion_evidence",
    "main",
    "reject_candidate",
    "select_candidate",
    "transition_review",
    "validate_review",
    "verify_review",
]
