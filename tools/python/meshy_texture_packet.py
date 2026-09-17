#!/usr/bin/env python3
"""Build a credit-gated, proposal-only Meshy texture request.

Meshy texturing is downstream of candidate selection and Blender validation.  A
request is written beside the selected candidate as ``texture_request.json``;
this tool never changes ``cleaned.glb`` or any runtime asset surface.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import sys
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Tuple, Union

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools import meshy_governance as governance  # noqa: E402
from tools.meshy_asset_contract import (  # noqa: E402
    AssetContract,
    canonical_json_bytes,
    load_contract,
    render_prompt_packet,
)


MATERIAL_VOCABULARY_PATH = (
    Path(__file__).resolve().parents[1] / "data/asset_generation/material_vocabulary.json"
)
TEXTURE_REQUEST_NAME = "texture_request.json"
TEXTURE_MODEL = "meshy-7"
ESTIMATED_TEXTURE_CREDITS = 10
_CANONICAL_JSON_MAX_BYTES = 16 * 1024 * 1024

PathLike = Union[str, os.PathLike]


class TexturePacketError(ValueError):
    """Raised when a Meshy texture request fails a governance gate."""


def _reject_duplicate_keys(pairs: List[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key: " + key)
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise ValueError("non-finite JSON constant: " + value)


def _load_json(path: Path, label: str) -> object:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise TexturePacketError("{0} could not be read: {1}".format(label, exc)) from exc
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise TexturePacketError("{0} is not valid UTF-8".format(label)) from exc
    try:
        return json.loads(
            text,
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_constant,
        )
    except (json.JSONDecodeError, RecursionError, ValueError) as exc:
        raise TexturePacketError("invalid JSON in {0}: {1}".format(label, exc)) from exc


def _finite_unit_range(value: object, field_name: str) -> Tuple[float, float]:
    if not isinstance(value, list) or len(value) != 2:
        raise TexturePacketError("{0} must contain exactly two numbers".format(field_name))
    try:
        minimum, maximum = float(value[0]), float(value[1])
    except (TypeError, ValueError) as exc:
        raise TexturePacketError("{0} must contain exactly two numbers".format(field_name)) from exc
    if not all(0.0 <= item <= 1.0 for item in (minimum, maximum)):
        raise TexturePacketError("{0} must be bounded between 0 and 1".format(field_name))
    if minimum > maximum:
        raise TexturePacketError("{0} minimum must not exceed maximum".format(field_name))
    return minimum, maximum


def _validate_family(family_id: str, family: object) -> Dict[str, Any]:
    if not isinstance(family, dict):
        raise TexturePacketError("material family {0!r} must be an object".format(family_id))
    required = (
        "canonical_name",
        "base_traits",
        "allowed_accent_colors",
        "roughness_range",
        "metallic_range",
        "manual_emission_required",
    )
    missing = [field for field in required if field not in family]
    if missing:
        raise TexturePacketError(
            "material family {0!r} missing field(s): {1}".format(family_id, ", ".join(missing))
        )
    if family.get("canonical_name") != family_id:
        raise TexturePacketError("material family canonical_name must match its identifier")
    for field in ("canonical_name", "base_traits"):
        if not isinstance(family.get(field), str) or not family[field].strip():
            raise TexturePacketError("material family {0} has invalid {1}".format(family_id, field))
    accents = family.get("allowed_accent_colors")
    if not isinstance(accents, list) or not all(
        isinstance(color, str) and color.strip() for color in accents
    ):
        raise TexturePacketError("material family {0} has invalid accent colors".format(family_id))
    _finite_unit_range(family.get("roughness_range"), "roughness_range")
    _finite_unit_range(family.get("metallic_range"), "metallic_range")
    if not isinstance(family.get("manual_emission_required"), bool):
        raise TexturePacketError(
            "material family {0} has invalid manual_emission_required".format(family_id)
        )
    return dict(family)


def load_material_vocabulary(path: Optional[PathLike] = None) -> Dict[str, Dict[str, Any]]:
    """Load and validate the shared material vocabulary."""
    vocabulary_path = Path(path or MATERIAL_VOCABULARY_PATH).expanduser()
    document = _load_json(vocabulary_path, "material vocabulary")
    if not isinstance(document, dict):
        raise TexturePacketError("material vocabulary must be an object")
    if "schema_version" in document and document["schema_version"] != "1.0.0":
        raise TexturePacketError("material vocabulary schema_version must be 1.0.0")

    families_value = document.get("families", document)
    if not isinstance(families_value, dict):
        raise TexturePacketError("material vocabulary families must be an object")
    families: Dict[str, Dict[str, Any]] = {}
    for family_id, family in families_value.items():
        if not isinstance(family_id, str) or not family_id:
            raise TexturePacketError("material vocabulary family identifiers must be strings")
        families[family_id] = _validate_family(family_id, family)
    return families


def _coerce_contract(contract: Union[AssetContract, PathLike], root: Path) -> AssetContract:
    if isinstance(contract, AssetContract):
        return contract
    contract_path = Path(contract).expanduser()
    if not contract_path.is_absolute():
        contract_path = root / contract_path
    try:
        return load_contract(contract_path)
    except (OSError, ValueError) as exc:
        raise TexturePacketError("contract is invalid: {0}".format(exc)) from exc


def _load_canonical_artifact(
    root: Path, task_dir: Path, name: str, label: str
) -> Tuple[Path, Dict[str, Any], bytes]:
    if not isinstance(name, str) or not name or Path(name).name != name or name in (".", ".."):
        raise TexturePacketError("{0} name must be a basename".format(label))
    try:
        path = governance.governed_task_path(
            root, task_dir / name, "Meshy texture " + label, allow_missing=False
        )
        record, raw = governance.strict_load_json_bytes(
            path, "Meshy texture " + label, _CANONICAL_JSON_MAX_BYTES
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise TexturePacketError("{0} is not available: {1}".format(label, exc)) from exc
    try:
        canonical = canonical_json_bytes(record)
    except (TypeError, ValueError, RecursionError) as exc:
        raise TexturePacketError("{0} cannot be canonicalized".format(label)) from exc
    if raw != canonical:
        raise TexturePacketError("{0} is not canonical JSON".format(label))
    return path, record, raw


def _load_bound_task(
    project_root: PathLike, contract: Union[AssetContract, PathLike], task_dir: PathLike,
    material_family: str, vocabulary_path: Optional[PathLike] = None,
) -> Tuple[Path, AssetContract, Path, Dict[str, Any], Dict[str, Any], AssetContract]:
    """Load the selected task and every canonical upstream/R4 evidence record."""
    try:
        root = governance.physical_project_root(project_root)
    except (OSError, TypeError, ValueError) as exc:
        raise TexturePacketError("project root is invalid: {0}".format(exc)) from exc
    caller_contract = _coerce_contract(contract, root)

    try:
        from tools import meshy_candidate_review as candidate_review

        review_path, review, generation, loaded_root, _asset_root = candidate_review._load_task_record(
            project_root, task_dir
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise TexturePacketError("task evidence is not fully governed: {0}".format(exc)) from exc
    if loaded_root != root:
        raise TexturePacketError("task evidence resolved to a different project root")
    resolved_task = Path(review_path).parent
    if review.get("state") != "selected":
        raise TexturePacketError("texture request requires selected review evidence")
    if generation.get("status") != "SUCCEEDED":
        raise TexturePacketError("texture request requires SUCCEEDED generation evidence")

    _review_path, bound_review, review_raw = _load_canonical_artifact(
        root, resolved_task, "review.json", "review evidence"
    )
    _generation_path, bound_generation, generation_raw = _load_canonical_artifact(
        root, resolved_task, "generation.json", "generation evidence"
    )
    if bound_review != review or bound_generation != generation:
        raise TexturePacketError("canonical task evidence changed during loading")

    task_contract_path, task_document, task_contract_raw = _load_canonical_artifact(
        root, resolved_task, "contract.json", "task contract"
    )
    try:
        task_contract = load_contract(task_contract_path)
    except (OSError, TypeError, ValueError) as exc:
        raise TexturePacketError("task contract is invalid: {0}".format(exc)) from exc
    if task_contract_raw != task_contract.snapshot_bytes():
        raise TexturePacketError("task contract snapshot is not canonical")
    if task_contract.document != task_document:
        raise TexturePacketError("task contract authority changed during loading")
    if caller_contract.asset_id != task_contract.asset_id or caller_contract.document != task_contract.document:
        raise TexturePacketError("caller contract does not match the task contract")
    if generation.get("contract_sha256") != caller_contract.sha256:
        raise TexturePacketError("generation contract hash does not match caller contract")
    if generation.get("contract_artifact_sha256") != task_contract.sha256:
        raise TexturePacketError("generation contract artifact is not bound to task contract")

    report_path, report, report_raw = _load_canonical_artifact(
        root, resolved_task, "blender-validation.json", "Blender validation evidence"
    )
    try:
        from tools.meshy_blender_validate import _validate_report_record

        _validate_report_record(report)
    except (ImportError, OSError, TypeError, ValueError, RuntimeError) as exc:
        raise TexturePacketError("Blender validation evidence is not canonical R4: {0}".format(exc)) from exc
    if report.get("task_id") != resolved_task.name:
        raise TexturePacketError("validation report task_id does not match task")
    if report.get("asset_id") != task_contract.asset_id or report.get("asset_id") != generation.get("asset_id"):
        raise TexturePacketError("validation report asset_id does not match task evidence")
    if report.get("contract_sha256") != caller_contract.sha256 or report.get("contract_sha256") != generation.get("contract_sha256"):
        raise TexturePacketError("validation report contract hash does not match task evidence")
    if (
        report.get("status") != "PASS"
        or report.get("uvs_present") is not True
        or not isinstance(report.get("uv_evidence"), list)
        or not report["uv_evidence"]
        or report.get("blender_reimport_passed") is not True
    ):
        raise TexturePacketError("Blender validation does not contain canonical PASS/UV/re-import evidence")

    cleaned = resolved_task / "cleaned.glb"
    try:
        cleaned_info = os.lstat(cleaned)
    except OSError as exc:
        raise TexturePacketError("cleaned.glb evidence is unavailable") from exc
    if not (os.path.isfile(cleaned) and not os.path.islink(cleaned)):
        raise TexturePacketError("cleaned.glb must be a regular file")
    if cleaned_info.st_size <= 0:
        raise TexturePacketError("cleaned.glb must be non-empty")
    try:
        cleaned_hash = governance.file_sha256(cleaned)
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise TexturePacketError("cleaned.glb evidence is unavailable: {0}".format(exc)) from exc
    if report.get("sha256") != cleaned_hash or report.get("byte_size") != cleaned_info.st_size:
        raise TexturePacketError("Blender validation report does not match cleaned.glb")

    outputs = generation.get("outputs")
    raw_output = outputs.get("raw.glb") if isinstance(outputs, dict) else None
    if not isinstance(raw_output, dict) or set(raw_output) != {"sha256", "byte_size"}:
        raise TexturePacketError("generation raw.glb output evidence is not canonical")
    evidence = {
        "generation": {
            "basename": "generation.json",
            "sha256": hashlib.sha256(generation_raw).hexdigest(),
            "status": generation["status"],
            "raw_glb": {
                "basename": "raw.glb",
                "sha256": raw_output["sha256"].lower(),
                "byte_size": raw_output["byte_size"],
            },
        },
        "review": {
            "basename": "review.json",
            "sha256": hashlib.sha256(review_raw).hexdigest(),
            "state": review["state"],
        },
        "blender_validation": {
            "basename": "blender-validation.json",
            "sha256": hashlib.sha256(report_raw).hexdigest(),
            "status": report["status"],
            "uvs_present": report["uvs_present"],
            "uv_evidence": True,
            "blender_reimport_passed": report["blender_reimport_passed"],
        },
        "cleaned_glb": {
            "basename": "cleaned.glb",
            "sha256": cleaned_hash.lower(),
            "byte_size": cleaned_info.st_size,
        },
    }
    vocabulary = load_material_vocabulary(vocabulary_path)
    if not isinstance(material_family, str) or material_family not in vocabulary:
        raise TexturePacketError("unknown material family: {0}".format(material_family))
    return root, caller_contract, resolved_task, vocabulary[material_family], evidence, task_contract


def _require_request_options(
    asset_contract: AssetContract,
    material_family: str,
    family: Mapping[str, Any],
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool,
    pbr_enabled: bool,
    meshy_emission: bool,
    approved_credits: Optional[int],
    client: Any,
) -> None:
    if not isinstance(remove_lighting, bool) or not remove_lighting:
        raise TexturePacketError("remove_lighting must be true")
    if not isinstance(pbr_enabled, bool) or not pbr_enabled:
        raise TexturePacketError("PBR must be enabled")
    if not isinstance(meshy_emission, bool) or meshy_emission:
        raise TexturePacketError("Meshy 7 emission claims are forbidden")
    if not isinstance(resolution, int) or isinstance(resolution, bool) or resolution <= 0:
        raise TexturePacketError("texture resolution must be a positive integer")

    budget = asset_contract.document.get("budget")
    budget_resolution = budget.get("texture_resolution") if isinstance(budget, dict) else None
    if not isinstance(budget_resolution, int) or isinstance(budget_resolution, bool):
        raise TexturePacketError("contract texture resolution budget is invalid")
    if resolution > budget_resolution:
        raise TexturePacketError(
            "texture resolution {0} exceeds contract budget {1}".format(
                resolution, budget_resolution
            )
        )
    if not isinstance(reviewer, str) or not reviewer.strip():
        raise TexturePacketError("reviewer must be non-empty text")
    _require_credit_gate(approved_credits, client)


def _require_credit_gate(approved_credits: Optional[int], client: Any) -> None:
    if (
        not isinstance(approved_credits, int)
        or isinstance(approved_credits, bool)
        or approved_credits < ESTIMATED_TEXTURE_CREDITS
    ):
        raise TexturePacketError(
            "approved credit ceiling is below the estimated Meshy texture cost "
            "({0} required)".format(ESTIMATED_TEXTURE_CREDITS)
        )
    if client is None:
        return
    if not hasattr(client, "get_balance"):
        raise TexturePacketError("Meshy client must provide get_balance")
    balance = client.get_balance()
    if not isinstance(balance, int) or isinstance(balance, bool) or balance < ESTIMATED_TEXTURE_CREDITS:
        raise TexturePacketError(
            "insufficient Meshy balance for the estimated texture cost ({0} required)".format(
                ESTIMATED_TEXTURE_CREDITS
            )
        )


def _texture_prompt(profile_prompt: str, family_id: str, family: Mapping[str, Any]) -> str:
    roughness = family["roughness_range"]
    metallic = family["metallic_range"]
    accents = family["allowed_accent_colors"]
    emission = (
        "Manual emission is required after Meshy texturing."
        if family["manual_emission_required"]
        else "Do not add emission; Meshy 7 is not an emission source."
    )
    return (
        "{0} Material family {1}: {2}. Allowed accent colors: {3}. "
        "Roughness range {4}-{5}; metallic range {6}-{7}. {8}"
    ).format(
        profile_prompt,
        family_id,
        family["base_traits"],
        ", ".join(accents) if accents else "none",
        roughness[0],
        roughness[1],
        metallic[0],
        metallic[1],
        emission,
    )


def _validated_inputs(
    project_root: PathLike,
    contract: Union[AssetContract, PathLike],
    task_dir: PathLike,
    material_family: str,
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool,
    pbr_enabled: bool,
    meshy_emission: bool,
    approved_credits: Optional[int],
    client: Any,
    vocabulary_path: Optional[PathLike],
) -> Tuple[Path, AssetContract, Path, Dict[str, Any], Dict[str, Any]]:
    root, asset_contract, task_path, family, evidence, _task_contract = _load_bound_task(
        project_root, contract, task_dir, material_family, vocabulary_path
    )
    _require_request_options(
        asset_contract,
        material_family,
        family,
        resolution,
        reviewer,
        remove_lighting=remove_lighting,
        pbr_enabled=pbr_enabled,
        meshy_emission=meshy_emission,
        approved_credits=approved_credits,
        client=client,
    )
    return root, asset_contract, task_path, family, evidence


def validate_texture_inputs(
    project_root: PathLike,
    contract: Union[AssetContract, PathLike],
    task_dir: PathLike,
    material_family: str,
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool = True,
    pbr_enabled: bool = True,
    meshy_emission: bool = False,
    approved_credits: Optional[int] = None,
    client: Any = None,
    vocabulary_path: Optional[PathLike] = None,
) -> Tuple[AssetContract, Path, Dict[str, Any]]:
    """Validate governed task evidence and all non-network request gates."""
    if vocabulary_path is not None:
        # Keep vocabulary override behavior for focused vocabulary tests, while
        # all task/generation/review/R4 evidence remains repository-governed.
        vocabulary = load_material_vocabulary(vocabulary_path)
        if not isinstance(material_family, str) or material_family not in vocabulary:
            raise TexturePacketError("unknown material family: {0}".format(material_family))
    _root, asset_contract, task_path, family, _evidence = _validated_inputs(
        project_root,
        contract,
        task_dir,
        material_family,
        resolution,
        reviewer,
        remove_lighting=remove_lighting,
        pbr_enabled=pbr_enabled,
        meshy_emission=meshy_emission,
        approved_credits=approved_credits,
        client=client,
        vocabulary_path=vocabulary_path,
    )
    return asset_contract, task_path, family


def _build_texture_request(
    project_root: PathLike,
    contract: Union[AssetContract, PathLike],
    task_dir: PathLike,
    material_family: str,
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool = True,
    pbr_enabled: bool = True,
    meshy_emission: bool = False,
    approved_credits: Optional[int] = None,
    client: Any = None,
    vocabulary_path: Optional[PathLike] = None,
) -> Tuple[Dict[str, Any], Path, Path]:
    if vocabulary_path is not None:
        vocabulary = load_material_vocabulary(vocabulary_path)
        if not isinstance(material_family, str) or material_family not in vocabulary:
            raise TexturePacketError("unknown material family: {0}".format(material_family))
    root, asset_contract, task_path, family, evidence = _validated_inputs(
        project_root,
        contract,
        task_dir,
        material_family,
        resolution,
        reviewer,
        remove_lighting=remove_lighting,
        pbr_enabled=pbr_enabled,
        meshy_emission=meshy_emission,
        approved_credits=approved_credits,
        client=client,
        vocabulary_path=vocabulary_path,
    )
    profile_packet = render_prompt_packet(asset_contract)
    prompt = _texture_prompt(profile_packet["texture_prompt"], material_family, family)
    request = {
        "asset_id": asset_contract.asset_id,
        "contract": {
            "asset_id": asset_contract.asset_id,
            "sha256": asset_contract.sha256,
        },
        "contract_sha256": asset_contract.sha256,
        "evidence": evidence,
        "material_family": material_family,
        "pbr": {
            "enabled": True,
            "emission": False,
            "model": TEXTURE_MODEL,
        },
        "prompt": prompt,
        "proposal_only": True,
        "remove_lighting": True,
        "resolution": resolution,
        "reviewer": reviewer.strip(),
        "task_id": task_path.name,
    }
    return request, root, task_path


def build_texture_request(
    project_root: PathLike,
    contract: Union[AssetContract, PathLike],
    task_dir: PathLike,
    material_family: str,
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool = True,
    pbr_enabled: bool = True,
    meshy_emission: bool = False,
    approved_credits: Optional[int] = None,
    client: Any = None,
    vocabulary_path: Optional[PathLike] = None,
) -> Dict[str, Any]:
    """Build a proposal-only canonical texture request from governed evidence."""
    request, _root, _task_path = _build_texture_request(
        project_root,
        contract,
        task_dir,
        material_family,
        resolution,
        reviewer,
        remove_lighting=remove_lighting,
        pbr_enabled=pbr_enabled,
        meshy_emission=meshy_emission,
        approved_credits=approved_credits,
        client=client,
        vocabulary_path=vocabulary_path,
    )
    return request


def write_texture_request(
    project_root: PathLike,
    contract: Union[AssetContract, PathLike],
    task_dir: PathLike,
    material_family: str,
    resolution: int,
    reviewer: str,
    *,
    remove_lighting: bool = True,
    pbr_enabled: bool = True,
    meshy_emission: bool = False,
    approved_credits: Optional[int] = None,
    client: Any = None,
    vocabulary_path: Optional[PathLike] = None,
) -> Dict[str, Any]:
    """Validate and atomically write the fixed task-local request leaf."""
    request, root, resolved_task = _build_texture_request(
        project_root,
        contract,
        task_dir,
        material_family,
        resolution,
        reviewer,
        remove_lighting=remove_lighting,
        pbr_enabled=pbr_enabled,
        meshy_emission=meshy_emission,
        approved_credits=approved_credits,
        client=client,
        vocabulary_path=vocabulary_path,
    )
    destination = resolved_task / TEXTURE_REQUEST_NAME
    try:
        if os.path.lexists(destination):
            existing_destination = governance.governed_task_path(
                root, destination, "Meshy texture request output", allow_missing=False
            )
            existing_request, raw = governance.strict_load_json_bytes(
                existing_destination,
                "Meshy texture request output",
                _CANONICAL_JSON_MAX_BYTES,
            )
            canonical_existing = canonical_json_bytes(existing_request)
            if raw != canonical_existing:
                raise ValueError("Meshy texture request output is not canonical JSON")
            if raw != canonical_json_bytes(request):
                raise ValueError("Meshy texture request output differs from the requested content")
            info = os.lstat(existing_destination)
            if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
                raise ValueError("Meshy texture request output must be a regular non-symlink file")
            if stat.S_IMODE(info.st_mode) != 0o600:
                raise ValueError("Meshy texture request output must have mode 0600")
            return request

        governance.governed_task_path(
            root, destination, "Meshy texture request output", allow_missing=True
        )
        governance.atomic_write_json(
            destination,
            request,
            project_root=root,
            allowed_root=resolved_task,
            mode=0o600,
        )
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise TexturePacketError("texture request publication failed: {0}".format(exc)) from exc
    return request


# Keep packet terminology available to callers that use the plan's name.  These
# are aliases only; they intentionally expose the same explicit-root API.
build_texture_packet = build_texture_request
write_texture_packet = write_texture_request
create_texture_request = build_texture_request


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--contract", type=Path, required=True)
    parser.add_argument("--task-dir", type=Path, required=True)
    parser.add_argument("--material-family", required=True)
    parser.add_argument("--resolution", type=int, required=True)
    parser.add_argument("--reviewer", required=True)
    parser.add_argument("--approved-credits", type=int, required=True)
    parser.add_argument(
        "--keep-lighting",
        action="store_true",
        help="test/diagnostic override; governance rejects this request",
    )
    parser.add_argument(
        "--disable-pbr",
        action="store_true",
        help="test/diagnostic override; governance rejects this request",
    )
    parser.add_argument(
        "--meshy-emission",
        action="store_true",
        help="test/diagnostic override; Meshy 7 cannot claim emission",
    )
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        request = write_texture_request(
            args.project_root,
            args.contract,
            args.task_dir,
            args.material_family,
            args.resolution,
            args.reviewer,
            remove_lighting=not args.keep_lighting,
            pbr_enabled=not args.disable_pbr,
            meshy_emission=args.meshy_emission,
            approved_credits=args.approved_credits,
        )
    except (OSError, TexturePacketError, RuntimeError, TypeError, ValueError) as exc:
        print("error: {0}".format(exc), file=sys.stderr)
        return 1
    print(
        "MESHY TEXTURE PACKET PASS asset={0} family={1} resolution={2}".format(
            request["asset_id"], request["material_family"], request["resolution"]
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "ESTIMATED_TEXTURE_CREDITS",
    "MATERIAL_VOCABULARY_PATH",
    "TEXTURE_MODEL",
    "TEXTURE_REQUEST_NAME",
    "TexturePacketError",
    "build_texture_packet",
    "build_texture_request",
    "create_texture_request",
    "load_material_vocabulary",
    "validate_texture_inputs",
    "write_texture_packet",
    "write_texture_request",
]
