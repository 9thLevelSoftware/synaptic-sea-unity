#!/usr/bin/env python3
"""Create immutable, review-only Meshy promotion proposal leaves.

The proposal boundary consumes only a governed task whose candidate review has
already been independently verified as ``promotion_ready``.  It writes no
runtime asset, catalog, wrapper, index, or imported sidecar.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import sys
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Tuple, Union
from urllib.parse import parse_qsl, urlsplit

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools import meshy_candidate_review as candidate_review  # noqa: E402
from tools import meshy_governance as governance  # noqa: E402
from tools.meshy_asset_contract import canonical_json_bytes, load_contract  # noqa: E402


PROP_OVERLAY_NAME = "sidecar-overlay.json"
THREAT_PATCH_NAME = "threat_visual_catalog.patch.json"
ASSET_PROVENANCE_NAME = "asset-provenance.json"
PROP_DOCUMENT_KIND = "meshy_sidecar_overlay"
THREAT_DOCUMENT_KIND = "meshy_threat_promotion_proposal"
THREAT_PATCH_DOCUMENT_KIND = "threat_visual_catalog_patch"
ASSET_PROVENANCE_DOCUMENT_KIND = "asset_provenance"
IDENTIFIER_RE = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
RIGHTS_STATES = frozenset(("paid-private", "free-cc-by-4.0"))
FORBIDDEN_SECRET_FIELD_RE = re.compile(
    r"(?:api[_-]?key|authorization|access[_-]?token|client[_-]?secret|password|private[_-]?key|secret)",
    re.IGNORECASE,
)
API_KEY_VALUE_RE = re.compile(
    r"(?:\b(?:sk|pk)[_-][A-Za-z0-9_-]{8,}\b|\bBearer\s+[A-Za-z0-9._~+/=-]{12,})",
    re.IGNORECASE,
)
SIGNED_QUERY_KEYS = {
    "sig",
    "signature",
    "x-amz-algorithm",
    "x-amz-credential",
    "x-amz-date",
    "x-amz-expires",
    "x-amz-signature",
    "x-amz-signedheaders",
    "se",
    "sp",
    "sr",
    "st",
    "sv",
    "token",
}

PathLike = Union[str, Path]


class PromotionPacketError(ValueError):
    """Raised when a promotion proposal cannot pass its safety gates."""


def _security_diagnostics(value: object, label: str = "") -> List[str]:
    diagnostics: List[str] = []
    stack: List[Tuple[object, str]] = [(value, label)]
    seen: set[int] = set()
    while stack:
        current, current_label = stack.pop()
        if isinstance(current, dict):
            identity = id(current)
            if identity in seen:
                continue
            seen.add(identity)
            for key, child in current.items():
                path = "{0}.{1}".format(current_label, key) if current_label else str(key)
                if isinstance(key, str) and FORBIDDEN_SECRET_FIELD_RE.search(key):
                    diagnostics.append("API key or secret field is not allowed: {0}".format(path))
                stack.append((child, path))
        elif isinstance(current, (list, tuple)):
            identity = id(current)
            if identity in seen:
                continue
            seen.add(identity)
            for index in range(len(current) - 1, -1, -1):
                stack.append((current[index], "{0}[{1}]".format(current_label, index)))
        elif isinstance(current, str):
            parsed = urlsplit(current)
            if parsed.scheme.lower() in ("http", "https"):
                query_keys = {
                    key.lower() for key, _value in parse_qsl(parsed.query, keep_blank_values=True)
                }
                if query_keys.intersection(SIGNED_QUERY_KEYS) or "signed" in parsed.path.lower():
                    diagnostics.append("signed URL is not allowed: {0}".format(current_label))
            if API_KEY_VALUE_RE.search(current):
                diagnostics.append("API key or bearer token is not allowed: {0}".format(current_label))
    return sorted(set(diagnostics))


def _hash_diagnostic(value: object, label: str, errors: List[str]) -> None:
    if not isinstance(value, str) or SHA256_RE.fullmatch(value) is None:
        errors.append("{0} must be 64 lowercase hexadecimal characters".format(label))


def validate_ai_provenance(value: object) -> List[str]:
    """Return deterministic diagnostics for the proposal provenance envelope."""
    errors = _security_diagnostics(value)
    if not isinstance(value, dict):
        return errors + ["provenance envelope must be an object"]

    if set(value) != {"provenance", "extensions"}:
        errors.extend(
            "unknown or missing provenance envelope field: {0}".format(key)
            for key in sorted(set(value) ^ {"provenance", "extensions"})
        )

    provenance = value.get("provenance")
    if not isinstance(provenance, dict):
        errors.append("provenance must be an object")
    else:
        if set(provenance) != {"provider", "license_state"}:
            errors.extend(
                "unknown or missing provenance field: {0}".format(key)
                for key in sorted(set(provenance) ^ {"provider", "license_state"})
            )
        if provenance.get("provider") != "meshy":
            errors.append("provenance.provider must be meshy")
        if provenance.get("license_state") not in RIGHTS_STATES:
            errors.append("provenance.license_state is not an approved rights state")

    extensions = value.get("extensions")
    if not isinstance(extensions, dict):
        errors.append("extensions must be an object")
    else:
        if set(extensions) != {"ai_generated", "ai_generation"}:
            errors.extend(
                "unknown or missing extensions field: {0}".format(key)
                for key in sorted(set(extensions) ^ {"ai_generated", "ai_generation"})
            )
        if extensions.get("ai_generated") is not True:
            errors.append("extensions.ai_generated must be true")
        ai_generation = extensions.get("ai_generation")
        required = {
            "provider",
            "task_id",
            "model",
            "input_sha256",
            "raw_output_sha256",
            "cleaned_output_sha256",
            "contract_sha256",
            "human_cleanup",
            "reviewer",
        }
        if not isinstance(ai_generation, dict):
            errors.append("extensions.ai_generation must be an object")
        else:
            if set(ai_generation) != required:
                errors.extend(
                    "unknown or missing ai_generation field: {0}".format(key)
                    for key in sorted(set(ai_generation) ^ required)
                )
            if ai_generation.get("provider") != "meshy":
                errors.append("extensions.ai_generation.provider must be meshy")
            task_id = ai_generation.get("task_id")
            if not isinstance(task_id, str) or TASK_ID_RE.fullmatch(task_id) is None:
                errors.append("extensions.ai_generation.task_id must be a safe identifier")
            if not isinstance(ai_generation.get("model"), str) or not ai_generation.get("model"):
                errors.append("extensions.ai_generation.model is required")
            inputs = ai_generation.get("input_sha256")
            if not isinstance(inputs, list) or not inputs:
                errors.append("extensions.ai_generation.input_sha256 must be a non-empty list")
            else:
                if any(not isinstance(item, str) for item in inputs):
                    errors.append("extensions.ai_generation.input_sha256 must contain hashes")
                string_inputs = [item for item in inputs if isinstance(item, str)]
                if len(string_inputs) != len(set(string_inputs)):
                    errors.append("extensions.ai_generation.input_sha256 must contain unique hashes")
                for index, item in enumerate(inputs):
                    _hash_diagnostic(item, "extensions.ai_generation.input_sha256[{0}]".format(index), errors)
            for field in ("raw_output_sha256", "cleaned_output_sha256", "contract_sha256"):
                _hash_diagnostic(
                    ai_generation.get(field), "extensions.ai_generation." + field, errors
                )
            if ai_generation.get("human_cleanup") is not True:
                errors.append("extensions.ai_generation.human_cleanup must be true")
            if not isinstance(ai_generation.get("reviewer"), str) or not ai_generation.get("reviewer", "").strip():
                errors.append("extensions.ai_generation.reviewer is required")
    return sorted(set(errors))


def _copy_json(value: object) -> object:
    try:
        return json.loads(canonical_json_bytes(value).decode("utf-8"))
    except (TypeError, ValueError, OverflowError, RecursionError) as exc:
        raise PromotionPacketError("proposal fields must be JSON serializable") from exc


def _validated_envelope(value: object) -> Dict[str, Any]:
    errors = validate_ai_provenance(value)
    if errors:
        raise PromotionPacketError("invalid AI provenance: " + "; ".join(errors))
    copied = _copy_json(value)
    assert isinstance(copied, dict)
    return copied


def _hash_file(path: Path, label: str) -> str:
    try:
        info = path.lstat()
    except OSError as exc:
        raise PromotionPacketError("missing {0}".format(label)) from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode) or info.st_size <= 0:
        raise PromotionPacketError("{0} must be a non-empty regular file".format(label))
    try:
        return governance.file_sha256(path)
    except (OSError, ValueError) as exc:
        raise PromotionPacketError("could not hash {0}".format(label)) from exc


def _verified_task(
    project_root: PathLike, task_dir: PathLike
) -> Tuple[Path, Path, str, str, Dict[str, Any], Dict[str, Any]]:
    """Return only task data authenticated by the candidate/runtime authority."""
    try:
        root, resolved_task, asset_root, asset_id, task_id = candidate_review._task_layout(
            project_root, task_dir
        )
        review = candidate_review.verify_review(root, resolved_task)
        if review.get("state") != "promotion_ready":
            raise PromotionPacketError("promotion proposal requires promotion_ready evidence")
        _review_path, canonical_review, generation, root, _asset_root = candidate_review._load_task_record(
            root, resolved_task
        )
        if canonical_review != review:
            raise PromotionPacketError("canonical promotion review changed during verification")
        if generation.get("status") != "SUCCEEDED":
            raise PromotionPacketError("promotion proposal requires SUCCEEDED generation evidence")
        if generation.get("asset_id") != asset_id or generation.get("task_id") != task_id:
            raise PromotionPacketError("generation identity does not match task directory")
        generation_provenance = generation.get("provenance")
        if not isinstance(generation_provenance, dict) or set(generation_provenance) != {
            "provider",
            "model",
            "license_state",
        }:
            raise PromotionPacketError("generation provenance is incomplete")
        output_license = generation.get("output_license")
        if output_license not in RIGHTS_STATES:
            raise PromotionPacketError("generation output license is not approved")
        if (
            generation_provenance.get("provider") != "meshy"
            or generation_provenance.get("license_state") != output_license
            or not isinstance(generation_provenance.get("model"), str)
            or not generation_provenance.get("model")
        ):
            raise PromotionPacketError("generation provenance and output license disagree")
        inputs = generation.get("input_image_hashes")
        if not isinstance(inputs, dict) or not inputs:
            raise PromotionPacketError("generation input_image_hashes are missing")
        input_hashes = [inputs[key] for key in sorted(inputs)]
        for index, value in enumerate(input_hashes):
            if not isinstance(value, str) or SHA256_RE.fullmatch(value) is None:
                raise PromotionPacketError("generation input hash is invalid at index {0}".format(index))
        outputs = generation.get("outputs")
        raw_output = outputs.get("raw.glb") if isinstance(outputs, dict) else None
        raw_hash = raw_output.get("sha256") if isinstance(raw_output, dict) else None
        if not isinstance(raw_hash, str) or SHA256_RE.fullmatch(raw_hash) is None:
            raise PromotionPacketError("generation raw.glb hash is missing")
        contract_hash = generation.get("contract_sha256")
        if not isinstance(contract_hash, str) or SHA256_RE.fullmatch(contract_hash) is None:
            raise PromotionPacketError("generation contract hash is invalid")
        reviewer = canonical_review.get("reviewer")
        if not isinstance(reviewer, str) or not reviewer.strip():
            raise PromotionPacketError("promotion review reviewer is missing")
        cleaned = candidate_review._governed_artifact(root, resolved_task, "cleaned.glb")
        cleaned_hash = _hash_file(cleaned, "cleaned.glb")
        envelope = {
            "provenance": {"provider": "meshy", "license_state": output_license},
            "extensions": {
                "ai_generated": True,
                "ai_generation": {
                    "provider": "meshy",
                    "task_id": task_id,
                    "model": generation_provenance["model"],
                    "input_sha256": input_hashes,
                    "raw_output_sha256": raw_hash,
                    "cleaned_output_sha256": cleaned_hash,
                    "contract_sha256": contract_hash,
                    "human_cleanup": True,
                    "reviewer": reviewer,
                },
            },
        }
        _validated_envelope(envelope)
        return root, resolved_task, asset_id, task_id, envelope, generation
    except PromotionPacketError:
        raise
    except (candidate_review.ReviewError, OSError, TypeError, ValueError, RuntimeError, RecursionError) as exc:
        raise PromotionPacketError("canonical promotion evidence is not valid: {0}".format(exc)) from exc


def _target_path(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise PromotionPacketError("{0} must be a non-empty path".format(label))
    if "\x00" in value or value.startswith("http://") or value.startswith("https://"):
        raise PromotionPacketError("{0} must not be a URL".format(label))
    if not value.startswith("res://"):
        raise PromotionPacketError("{0} must be a res:// path".format(label))
    relative = value[6:]
    parts = relative.split("/")
    if not parts or any(part in ("", ".", "..") for part in parts):
        raise PromotionPacketError("{0} must not contain traversal".format(label))
    return value


def _default_prop_target(asset_id: str, prop_kind: str) -> str:
    group = {
        "component": "components",
        "dressing": "dressing",
        "objective": "objectives",
    }[prop_kind]
    return "res://assets/imported/props/{0}/{1}.sidecar.json".format(group, asset_id)


def _validate_prop_target(value: object) -> str:
    target = _target_path(value, "target path")
    if not target.startswith("res://assets/imported/props/") or not target.endswith(".sidecar.json"):
        raise PromotionPacketError("target path must be an imported prop sidecar path")
    return target


def _archetype_for(asset_id: str, archetype: Optional[str]) -> str:
    value = archetype or re.sub(r"_v[0-9]+$", "", asset_id)
    if not value or IDENTIFIER_RE.fullmatch(value) is None:
        raise PromotionPacketError("threat archetype must be a safe identifier")
    return value


def _logical_cleaned_path(root: Path, task_dir: Path) -> str:
    cleaned = candidate_review._governed_artifact(root, task_dir, "cleaned.glb")
    return "res://" + cleaned.relative_to(root).as_posix()


def _validate_threat_mesh_path(
    root: Path, task_dir: Path, mesh_path: Optional[PathLike]
) -> str:
    expected = _logical_cleaned_path(root, task_dir)
    if mesh_path is None:
        return expected
    if isinstance(mesh_path, (Path, os.PathLike)) and not isinstance(mesh_path, str):
        try:
            candidate = governance.governed_task_path(
                root, mesh_path, "threat mesh path", allow_missing=False
            )
        except (OSError, TypeError, ValueError) as exc:
            raise PromotionPacketError("threat mesh path is not governed") from exc
        if candidate != task_dir / "cleaned.glb":
            raise PromotionPacketError("threat mesh path must be the fixed cleaned.glb leaf")
        return expected
    if not isinstance(mesh_path, str) or mesh_path != expected:
        raise PromotionPacketError("threat mesh path must be the fixed cleaned.glb leaf")
    return expected


def _load_task_category(root: Path, task_dir: Path) -> str:
    try:
        contract_path = candidate_review._governed_artifact(root, task_dir, "contract.json")
        contract = load_contract(contract_path)
    except (OSError, TypeError, ValueError, candidate_review.ReviewError) as exc:
        raise PromotionPacketError("task-local contract is not valid") from exc
    category = contract.document.get("category")
    if not isinstance(category, str) or IDENTIFIER_RE.fullmatch(category) is None:
        raise PromotionPacketError("task-local contract category is invalid")
    return category


def _require_packet_category(category: str, packet: str) -> None:
    if packet == "prop":
        if category == "gameplay_prop" or category.startswith("prop"):
            return
        raise PromotionPacketError(
            "prop promotion is incompatible with contract category {0}".format(category)
        )
    if packet == "threat":
        if category.startswith("threat"):
            return
        raise PromotionPacketError(
            "threat promotion is incompatible with contract category {0}".format(category)
        )
    raise PromotionPacketError("unknown promotion packet type")


def build_prop_promotion_proposal(
    project_root: PathLike,
    task_dir: PathLike,
    *,
    target_path: Optional[str] = None,
    prop_kind: str = "dressing",
) -> Dict[str, Any]:
    """Build a sidecar overlay from verified evidence without writing it."""
    if prop_kind not in ("component", "dressing", "objective"):
        raise PromotionPacketError("prop_kind must be component, dressing, or objective")
    root, resolved_task, asset_id, task_id, envelope, _generation = _verified_task(
        project_root, task_dir
    )
    _require_packet_category(_load_task_category(root, resolved_task), "prop")
    target = _validate_prop_target(target_path or _default_prop_target(asset_id, prop_kind))
    document: Dict[str, Any] = {
        "asset_id": asset_id,
        "document_kind": PROP_DOCUMENT_KIND,
        "extensions": envelope["extensions"],
        "prop_kind": prop_kind,
        "proposal_only": True,
        "provenance": envelope["provenance"],
        "target_path": target,
        "task_id": task_id,
    }
    security_errors = _security_diagnostics(document)
    if security_errors:
        raise PromotionPacketError("unsafe promotion proposal: " + "; ".join(security_errors))
    _copy_json(document)
    return document


def build_threat_promotion_proposal(
    project_root: PathLike,
    task_dir: PathLike,
    *,
    mesh_path: Optional[PathLike] = None,
    archetype: Optional[str] = None,
) -> Dict[str, Any]:
    """Build a threat catalog patch and provenance record without writing them."""
    root, resolved_task, asset_id, task_id, envelope, _generation = _verified_task(
        project_root, task_dir
    )
    _require_packet_category(_load_task_category(root, resolved_task), "threat")
    logical_mesh_path = _validate_threat_mesh_path(root, resolved_task, mesh_path)
    archetype_id = _archetype_for(asset_id, archetype)
    patch = {
        "archetype": archetype_id,
        "document_kind": THREAT_PATCH_DOCUMENT_KIND,
        "operations": [
            {
                "op": "add",
                "path": "/archetypes/{0}/mesh_path".format(archetype_id),
                "value": logical_mesh_path,
            }
        ],
        "proposal_only": True,
        "target_path": "data/combat/threat_visual_catalog.json",
        "task_id": task_id,
    }
    asset_provenance = {
        "asset_id": asset_id,
        "document_kind": ASSET_PROVENANCE_DOCUMENT_KIND,
        "extensions": envelope["extensions"],
        "proposal_only": True,
        "provenance": envelope["provenance"],
        "task_id": task_id,
    }
    wrapper = {
        "asset_id": asset_id,
        "catalog_patch": patch,
        "document_kind": THREAT_DOCUMENT_KIND,
        "extensions": envelope["extensions"],
        "proposal_only": True,
        "provenance": envelope["provenance"],
        "asset_provenance": asset_provenance,
        "task_id": task_id,
    }
    for value in (patch, asset_provenance, wrapper):
        security_errors = _security_diagnostics(value)
        if security_errors:
            raise PromotionPacketError("unsafe promotion proposal: " + "; ".join(security_errors))
        _copy_json(value)
    return wrapper


def _layout_for_publication(
    project_root: PathLike, task_dir: PathLike
) -> Tuple[Path, Path]:
    try:
        root, resolved_task, _asset_root, _asset_id, _task_id = candidate_review._task_layout(
            project_root, task_dir
        )
        return root, resolved_task
    except (candidate_review.ReviewError, OSError, TypeError, ValueError, RuntimeError) as exc:
        raise PromotionPacketError("task directory is not governed") from exc


def _fixed_leaf(root: Path, task_dir: Path, name: str) -> Path:
    try:
        return governance.governed_task_path(
            root, task_dir / name, "Meshy fixed proposal " + name, allow_missing=True
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise PromotionPacketError("fixed proposal leaf is not governed: " + name) from exc


def _preflight_leaf(path: Path, expected: bytes, label: str) -> bool:
    if not os.path.lexists(path):
        return False
    try:
        info = path.lstat()
    except OSError as exc:
        raise PromotionPacketError("cannot inspect existing {0}".format(label)) from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise PromotionPacketError("existing {0} must be a regular non-symlink file".format(label))
    if stat.S_IMODE(info.st_mode) != 0o600:
        raise PromotionPacketError("existing {0} must use mode 0600".format(label))
    try:
        actual = path.read_bytes()
    except (OSError, UnicodeError) as exc:
        raise PromotionPacketError("existing {0} cannot be read".format(label)) from exc
    if actual != expected:
        raise PromotionPacketError("existing {0} is not the exact canonical proposal".format(label))
    return True


def _publish_missing_leaf(
    root: Path, task_dir: Path, path: Path, value: object, expected: bytes, label: str
) -> None:
    try:
        governance.atomic_write_json(
            path, value, project_root=root, allowed_root=task_dir, mode=0o600
        )
        _preflight_leaf(path, expected, label)
    except PromotionPacketError:
        raise
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise PromotionPacketError("proposal publication failed for {0}".format(label)) from exc


def write_prop_promotion_proposal(
    project_root: PathLike,
    task_dir: PathLike,
    *,
    target_path: Optional[str] = None,
    prop_kind: str = "dressing",
) -> Dict[str, Any]:
    """Build and publish only the fixed task-local prop proposal leaf."""
    proposal = build_prop_promotion_proposal(
        project_root, task_dir, target_path=target_path, prop_kind=prop_kind
    )
    root, resolved_task = _layout_for_publication(project_root, task_dir)
    leaf = _fixed_leaf(root, resolved_task, PROP_OVERLAY_NAME)
    expected = canonical_json_bytes(proposal)
    if not _preflight_leaf(leaf, expected, PROP_OVERLAY_NAME):
        _publish_missing_leaf(root, resolved_task, leaf, proposal, expected, PROP_OVERLAY_NAME)
    return proposal


def write_threat_promotion_proposal(
    project_root: PathLike,
    task_dir: PathLike,
    *,
    mesh_path: Optional[PathLike] = None,
    archetype: Optional[str] = None,
) -> Dict[str, Any]:
    """Build and publish provenance first, then the authoritative catalog patch."""
    proposal = build_threat_promotion_proposal(
        project_root, task_dir, mesh_path=mesh_path, archetype=archetype
    )
    root, resolved_task = _layout_for_publication(project_root, task_dir)
    provenance_leaf = _fixed_leaf(root, resolved_task, ASSET_PROVENANCE_NAME)
    patch_leaf = _fixed_leaf(root, resolved_task, THREAT_PATCH_NAME)
    provenance_bytes = canonical_json_bytes(proposal["asset_provenance"])
    patch_bytes = canonical_json_bytes(proposal["catalog_patch"])

    # Both leaves are checked before either is created or changed.
    provenance_exists = _preflight_leaf(
        provenance_leaf, provenance_bytes, ASSET_PROVENANCE_NAME
    )
    patch_exists = _preflight_leaf(patch_leaf, patch_bytes, THREAT_PATCH_NAME)
    if not provenance_exists:
        _publish_missing_leaf(
            root,
            resolved_task,
            provenance_leaf,
            proposal["asset_provenance"],
            provenance_bytes,
            ASSET_PROVENANCE_NAME,
        )
    if not patch_exists:
        _publish_missing_leaf(
            root,
            resolved_task,
            patch_leaf,
            proposal["catalog_patch"],
            patch_bytes,
            THREAT_PATCH_NAME,
        )
    return proposal


# Names used by callers that refer to the output as an overlay/packet.
build_sidecar_overlay = build_prop_promotion_proposal
write_sidecar_overlay = write_prop_promotion_proposal
build_catalog_promotion_proposal = build_threat_promotion_proposal
write_catalog_promotion_proposal = write_threat_promotion_proposal
validate_provenance = validate_ai_provenance


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    prop = subparsers.add_parser("prop", help="write a staged prop sidecar overlay")
    prop.add_argument("--project-root", type=Path, required=True)
    prop.add_argument("--task-dir", type=Path, required=True)
    prop.add_argument("--target-path")
    prop.add_argument("--prop-kind", choices=("component", "dressing", "objective"), default="dressing")
    threat = subparsers.add_parser("threat", help="write staged threat catalog/provenance proposals")
    threat.add_argument("--project-root", type=Path, required=True)
    threat.add_argument("--task-dir", type=Path, required=True)
    threat.add_argument("--mesh-path")
    threat.add_argument("--archetype")
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        if args.command == "prop":
            result = write_prop_promotion_proposal(
                args.project_root,
                args.task_dir,
                target_path=args.target_path,
                prop_kind=args.prop_kind,
            )
            print("MESHY PROP PROMOTION PROPOSAL PASS asset={0}".format(result["asset_id"]))
        elif args.command == "threat":
            result = write_threat_promotion_proposal(
                args.project_root,
                args.task_dir,
                mesh_path=args.mesh_path,
                archetype=args.archetype,
            )
            print("MESHY THREAT PROMOTION PROPOSAL PASS asset={0}".format(result["asset_id"]))
        else:  # pragma: no cover - argparse owns command choices
            return 2
    except (OSError, PromotionPacketError, TypeError, ValueError) as exc:
        print("error: {0}".format(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "ASSET_PROVENANCE_NAME",
    "ASSET_PROVENANCE_DOCUMENT_KIND",
    "PROP_DOCUMENT_KIND",
    "PROP_OVERLAY_NAME",
    "PromotionPacketError",
    "THREAT_DOCUMENT_KIND",
    "THREAT_PATCH_DOCUMENT_KIND",
    "THREAT_PATCH_NAME",
    "build_catalog_promotion_proposal",
    "build_prop_promotion_proposal",
    "build_sidecar_overlay",
    "build_threat_promotion_proposal",
    "main",
    "validate_ai_provenance",
    "validate_provenance",
    "write_catalog_promotion_proposal",
    "write_prop_promotion_proposal",
    "write_sidecar_overlay",
    "write_threat_promotion_proposal",
]
