#!/usr/bin/env python3
"""Preflight, journal, and securely stage bounded Meshy candidates.

Generation is deliberately a no-promotion workflow.  All provider calls are
made through an injectable client in tests; the real client has a fixed
production origin and never writes secrets or signed URLs to evidence.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import inspect
import json
import math
import os
import re
import shutil
import stat
import struct
import sys
import tempfile
import time
import uuid
import zlib
from datetime import date as _date, datetime, timezone
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Mapping, NamedTuple, Optional, Tuple, Union
from urllib.parse import urlsplit

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools import meshy_governance as governance  # noqa: E402
from tools.meshy_asset_contract import (  # noqa: E402
    IDENTIFIER_RE,
    AssetContract,
    canonical_json_bytes,
    load_contract,
    render_prompt_packet,
)
from tools.meshy_candidate_review import validate_review  # noqa: E402


ENDPOINTS = {
    "image_to_3d": "/openapi/v1/image-to-3d",
    "multi_image_to_3d": "/openapi/v1/multi-image-to-3d",
}
DEFAULT_PRICING_PATH = Path(__file__).resolve().parents[1] / "data/asset_generation/meshy_pricing_v1.json"
STAGING_RELATIVE = Path("assets/_staging/meshy")
PROTECTED_RELATIVE = governance.PROTECTED_RUNTIME_RELATIVE_PATHS
_TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_BATCH_ID_RE = re.compile(r"^[0-9a-f]{32}$")
_HASH_RE = re.compile(r"^[0-9a-f]{64}$")
_REFERENCE_EXTENSIONS = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}
_PNG_SIGNATURE = bytes((137, 80, 78, 71, 13, 10, 26, 10))
_JPEG_SOI = bytes((255, 216))
_JPEG_EOI = bytes((255, 217))
# Dated snapshot ids only. The live approved record is
# data/asset_generation/meshy_pricing_v1.json; do not pin a single calendar day
# in code or a pricing bump is rejected as a false identity failure.
_PRICING_ID_RE = re.compile(r"^meshy_api_[0-9]{4}_[0-9]{2}_[0-9]{2}$")
_PRICING_MAX_BYTES = 1024 * 1024
_REFERENCE_FILE_MAX_BYTES = 16 * 1024 * 1024
_REFERENCE_TOTAL_MAX_BYTES = 48 * 1024 * 1024
_GLB_MAX_BYTES = 64 * 1024 * 1024
_THUMBNAIL_MAX_BYTES = 16 * 1024 * 1024
_DOWNLOAD_TOTAL_MAX_BYTES = 80 * 1024 * 1024
_MAX_POLL_ATTEMPTS = 120
_POLL_DELAYS = (0.0, 0.25, 0.5, 1.0, 2.0)
_DEFAULT_DEADLINE_SECONDS = 1200
_MAX_DEADLINE_SECONDS = 7200
# The checked-in repository contains a large protected assets/imported tree.
# These are explicit, bounded caps for this repository only; governance's
# conservative defaults remain unchanged for other callers.
_REPOSITORY_SNAPSHOT_MAX_FILE_BYTES = 1 * 1024 * 1024 * 1024
_REPOSITORY_SNAPSHOT_MAX_TOTAL_BYTES = 4 * 1024 * 1024 * 1024
_REPOSITORY_SNAPSHOT_MAX_ENTRIES = 20_000
_REPOSITORY_SNAPSHOT_MAX_DEPTH = 128
_ALLOWED_LICENSES = ("paid-private", "free-cc-by-4.0")
_SAFE_ERROR_MESSAGES = {
    "download failed",
    "Meshy task did not reach SUCCEEDED",
    "Meshy task ended with status FAILED",
    "Meshy actual credit consumption exceeded the approved bound",
    "Meshy consumed credit observation decreased and is ambiguous",
    "Meshy generation deadline exceeded",
    "Meshy failure evidence publication failed",
    "Meshy returned a duplicate task id",
    "Meshy task id collision",
    "Meshy task publication durability is uncertain",
}


class MeshyClient:
    """Small requests-backed client with a fixed, production API origin."""

    PRODUCTION_ORIGIN = "https://api.meshy.ai"
    DOWNLOAD_HOST = "assets.meshy.ai"

    def __init__(self, api_key: Optional[str] = None, base_url: Optional[str] = None, session: Any = None) -> None:
        # MESHY_BASE_URL is intentionally ignored: an environment value must
        # never redirect an API key.  A supplied origin is accepted only when it
        # is the exact production origin, including for injected sessions.
        origin = (base_url or self.PRODUCTION_ORIGIN).rstrip("/")
        if origin != self.PRODUCTION_ORIGIN:
            raise ValueError("Meshy base URL must be https://api.meshy.ai")
        if api_key is None:
            api_key = os.environ.get("MESHY_API_KEY")
        if session is None:
            if not api_key:
                raise ValueError("MESHY_API_KEY is required for generation")
            try:
                import requests
            except ImportError as exc:  # pragma: no cover
                raise ValueError("requests is required for generation") from exc
            session = requests.Session()
            try:
                session.trust_env = False
            except AttributeError:
                pass
        self.api_key = api_key or ""
        if not isinstance(self.api_key, str):
            raise ValueError("MESHY_API_KEY must be text")
        self.account_lock_id = hashlib.sha256(self.api_key.encode("utf-8")).hexdigest()
        self.base_url = origin
        self.session = session

    @staticmethod
    def _safe_transport_error() -> RuntimeError:
        return RuntimeError("Meshy transport failed")

    def _request(
        self,
        method: str,
        endpoint: str,
        deadline: Optional[float] = None,
        clock: Optional[Callable[[], float]] = None,
        **kwargs: Any,
    ) -> Any:
        headers = dict(kwargs.pop("headers", {}))
        if self.api_key:
            headers["Authorization"] = "Bearer " + self.api_key
        headers["Accept"] = "application/json"
        now = clock or time.monotonic
        if deadline is not None and (
            not isinstance(deadline, (int, float))
            or isinstance(deadline, bool)
            or not math.isfinite(float(deadline))
        ):
            raise RuntimeError("Meshy request deadline is invalid")
        response = None
        try:
            # POST creation is paid and has no idempotency contract here.  It
            # must never be retried after a transport or retryable response;
            # only safe GET reads retain automatic retries.
            retryable_read = isinstance(method, str) and method.upper() == "GET"
            for attempt in range(3 if retryable_read else 1):
                remaining = 60.0
                if deadline is not None:
                    remaining = float(deadline) - now()
                    if remaining <= 0:
                        raise RuntimeError("Meshy generation deadline exceeded")
                try:
                    response = self.session.request(
                        method,
                        self.base_url + endpoint,
                        headers=headers,
                        timeout=(min(10.0, remaining), min(60.0, remaining)),
                        **kwargs,
                    )
                except Exception as exc:
                    raise self._safe_transport_error() from exc
                if deadline is not None and now() >= float(deadline):
                    raise RuntimeError("Meshy generation deadline exceeded")
                status = getattr(response, "status_code", None)
                if not isinstance(status, int) or isinstance(status, bool):
                    raise RuntimeError("Meshy response had an invalid status")
                if status not in (429, 500, 502, 503, 504):
                    break
                if retryable_read and attempt + 1 < 3:
                    delay = 0.5 * (2 ** attempt)
                    if deadline is not None:
                        remaining = float(deadline) - now()
                        if remaining <= 0:
                            raise RuntimeError("Meshy generation deadline exceeded")
                        delay = min(delay, remaining)
                    time.sleep(delay)
            status = getattr(response, "status_code", None)
            if not isinstance(status, int) or not 200 <= status < 300:
                raise RuntimeError("Meshy request failed with status {0}".format(status if isinstance(status, int) else "unknown"))
            try:
                return response.json()
            except Exception as exc:
                raise RuntimeError("Meshy returned invalid JSON") from exc
        except RuntimeError:
            raise
        except Exception as exc:
            raise self._safe_transport_error() from exc

    def get_balance(
        self,
        deadline: Optional[float] = None,
        clock: Optional[Callable[[], float]] = None,
    ) -> int:
        payload = self._request("GET", "/openapi/v1/balance", deadline=deadline, clock=clock)
        if isinstance(payload, dict):
            for key in ("balance", "credits", "credit_balance"):
                value = payload.get(key)
                if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
                    return value
        raise RuntimeError("Meshy balance response did not contain credits")

    def create_task(
        self,
        endpoint: str,
        payload: Dict[str, Any],
        deadline: Optional[float] = None,
        clock: Optional[Callable[[], float]] = None,
    ) -> str:
        response = self._request("POST", endpoint, json=payload, deadline=deadline, clock=clock)
        if isinstance(response, dict):
            for key in ("result", "task_id", "id"):
                value = response.get(key)
                if isinstance(value, str) and value:
                    return value
        raise RuntimeError("Meshy create response did not contain a task id")

    def poll_task(
        self,
        endpoint: str,
        task_id: str,
        deadline: Optional[float] = None,
        clock: Optional[Callable[[], float]] = None,
    ) -> Dict[str, Any]:
        response = self._request(
            "GET",
            endpoint.rstrip("/") + "/" + task_id,
            deadline=deadline,
            clock=clock,
        )
        if not isinstance(response, dict):
            raise RuntimeError("Meshy task response was not an object")
        return response

    @classmethod
    def _validate_download_url(cls, value: object) -> str:
        if not isinstance(value, str) or len(value) > 4096:
            raise RuntimeError("Meshy download URL is invalid")
        try:
            parsed = urlsplit(value)
            port = parsed.port
        except ValueError as exc:
            raise RuntimeError("Meshy download URL is invalid") from exc
        host = (parsed.hostname or "").lower()
        if parsed.scheme != "https" or port not in (None, 443) or not host or parsed.username or parsed.password or parsed.fragment:
            raise RuntimeError("Meshy download URL is not approved")
        if not (host == cls.DOWNLOAD_HOST or (host.endswith(".meshy.ai") and host != "meshy.ai")):
            raise RuntimeError("Meshy download host is not approved")
        return value

    def download_bytes(
        self,
        url: str,
        max_bytes: int,
        deadline: float,
        clock: Callable[[], float],
    ) -> bytes:
        """Download bounded bytes in memory from an approved Meshy host.

        ``deadline`` is the remaining duration for this individual request;
        the caller derives it from the operation's absolute deadline.
        """

        self._validate_download_url(url)
        if not isinstance(max_bytes, int) or isinstance(max_bytes, bool) or max_bytes <= 0:
            raise RuntimeError("Meshy download size limit is invalid")
        if not isinstance(deadline, (int, float)) or isinstance(deadline, bool) or not math.isfinite(float(deadline)) or float(deadline) <= 0:
            raise RuntimeError("Meshy download deadline is invalid")
        if not callable(clock):
            raise RuntimeError("Meshy download clock is invalid")
        duration = float(deadline)
        try:
            started = clock()
            deadline_at = started + duration
            if started >= deadline_at:
                raise RuntimeError("Meshy download deadline exceeded")
            remaining = deadline_at - clock()
            if remaining <= 0:
                raise RuntimeError("Meshy download deadline exceeded")
            response = None
            try:
                try:
                    response = self.session.get(
                        url,
                        stream=True,
                        timeout=(min(10.0, remaining), min(120.0, remaining)),
                    )
                except Exception as exc:
                    raise self._safe_transport_error() from exc
                try:
                    status = getattr(response, "status_code", None)
                except Exception as exc:
                    raise self._safe_transport_error() from exc
                if not isinstance(status, int) or isinstance(status, bool):
                    raise RuntimeError("Meshy download returned an invalid status")
                if not 200 <= status < 300:
                    raise RuntimeError("Meshy download request failed")
                try:
                    iterator = iter(response.iter_content(chunk_size=1024 * 1024))
                except Exception as exc:
                    raise self._safe_transport_error() from exc
                chunks: List[bytes] = []
                total = 0
                while True:
                    try:
                        chunk = next(iterator)
                    except StopIteration:
                        break
                    except Exception as exc:
                        raise self._safe_transport_error() from exc
                    if clock() >= deadline_at:
                        raise RuntimeError("Meshy download deadline exceeded")
                    if chunk:
                        if not isinstance(chunk, bytes):
                            raise RuntimeError("Meshy download returned invalid bytes")
                        total += len(chunk)
                        if total > max_bytes:
                            raise RuntimeError("Meshy download exceeds maximum size")
                        chunks.append(chunk)
                if clock() >= deadline_at:
                    raise RuntimeError("Meshy download deadline exceeded")
                return b"".join(chunks)
            finally:
                if response is not None:
                    try:
                        response.close()
                    except Exception:
                        pass
        except RuntimeError:
            raise
        except Exception as exc:
            raise self._safe_transport_error() from exc


def _utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _copy_mapping(value: Mapping[str, Any]) -> Dict[str, Any]:
    return json.loads(canonical_json_bytes(dict(value)).decode("utf-8"))


class _SealedCapsule:
    __slots__ = ()

    def __setattr__(self, name: str, value: object) -> None:
        raise AttributeError("capsule records are immutable")

    def __delattr__(self, name: str) -> None:
        raise AttributeError("capsule records are immutable")

    def __reduce_ex__(self, protocol: Any) -> Any:
        raise TypeError("capsule records cannot be pickled")


class ReferenceInput(_SealedCapsule):
    __slots__ = ("_view", "_basename", "_media_type", "_byte_size", "_sha256", "_bytes")

    def __init__(self, view: str, basename: str, media_type: str, byte_size: int, sha256: str, _bytes: bytes) -> None:
        object.__setattr__(self, "_view", view)
        object.__setattr__(self, "_basename", basename)
        object.__setattr__(self, "_media_type", media_type)
        object.__setattr__(self, "_byte_size", byte_size)
        object.__setattr__(self, "_sha256", sha256)
        object.__setattr__(self, "_bytes", _bytes)

    view = property(lambda self: self._view)
    basename = property(lambda self: self._basename)
    media_type = property(lambda self: self._media_type)
    byte_size = property(lambda self: self._byte_size)
    sha256 = property(lambda self: self._sha256)

    @property
    def metadata(self) -> Dict[str, Any]:
        return {"view": self.view, "basename": self.basename, "media_type": self.media_type, "byte_size": self.byte_size, "sha256": self.sha256}

    def __repr__(self) -> str:
        return "ReferenceInput(view={0!r}, basename={1!r}, media_type={2!r}, byte_size={3!r}, sha256={4!r})".format(self.view, self.basename, self.media_type, self.byte_size, self.sha256)


class ReferenceInputs(_SealedCapsule):
    __slots__ = ("_references",)

    def __init__(self, references: Iterable[ReferenceInput]) -> None:
        object.__setattr__(self, "_references", tuple(references))

    @property
    def references(self) -> Tuple[ReferenceInput, ...]:
        return self._references

    def __iter__(self):
        return iter(self.references)

    def __len__(self) -> int:
        return len(self.references)

    def __getitem__(self, index: int) -> ReferenceInput:
        return self.references[index]

    @property
    def metadata(self) -> Tuple[Dict[str, Any], ...]:
        return tuple(item.metadata for item in self.references)

    def __repr__(self) -> str:
        return "ReferenceInputs(count={0})".format(len(self.references))


class PricingRecord(_SealedCapsule):
    __slots__ = ("_pricing_id", "_checked_at", "_expires_at", "_source_url", "_sha256", "_snapshot")

    def __init__(self, pricing_id: str, checked_at: str, expires_at: str, source_url: str, sha256: str, _snapshot: bytes) -> None:
        object.__setattr__(self, "_pricing_id", pricing_id)
        object.__setattr__(self, "_checked_at", checked_at)
        object.__setattr__(self, "_expires_at", expires_at)
        object.__setattr__(self, "_source_url", source_url)
        object.__setattr__(self, "_sha256", sha256)
        object.__setattr__(self, "_snapshot", _snapshot)

    pricing_id = property(lambda self: self._pricing_id)
    checked_at = property(lambda self: self._checked_at)
    expires_at = property(lambda self: self._expires_at)
    source_url = property(lambda self: self._source_url)
    sha256 = property(lambda self: self._sha256)

    @property
    def document(self) -> Dict[str, Any]:
        return json.loads(self._snapshot.decode("utf-8"))

    def snapshot_bytes(self) -> bytes:
        return bytes(self._snapshot)

    def cost_for(self, contract: AssetContract) -> int:
        if not isinstance(contract, AssetContract):
            raise TypeError("cost_for requires an AssetContract")
        generation = contract._snapshot_document().get("generation")
        if not isinstance(generation, dict):
            raise ValueError("contract generation must be an object")
        values = (generation.get("mode"), generation.get("model_type"), generation.get("ai_model"), generation.get("should_texture"))
        if not isinstance(values[0], str) or not isinstance(values[1], str) or not isinstance(values[2], str) or values[3] is not False:
            raise ValueError("unknown Meshy pricing combination")
        try:
            value = self.document["costs"][values[0]][values[1]][values[2]]["untextured"]
        except (KeyError, TypeError):
            raise ValueError("unknown Meshy pricing combination")
        if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
            raise ValueError("unknown Meshy pricing combination")
        return value

    def __repr__(self) -> str:
        return "PricingRecord(pricing_id={0!r}, checked_at={1!r}, expires_at={2!r}, source_url={3!r}, sha256={4!r})".format(self.pricing_id, self.checked_at, self.expires_at, self.source_url, self.sha256)


class TransientProviderRequest(_SealedCapsule):
    __slots__ = ("_provider_payload_sha256", "_payload_snapshot", "_redacted_snapshot")

    def __init__(self, provider_payload_sha256: str, _payload_snapshot: bytes, _redacted_snapshot: bytes) -> None:
        object.__setattr__(self, "_provider_payload_sha256", provider_payload_sha256)
        object.__setattr__(self, "_payload_snapshot", _payload_snapshot)
        object.__setattr__(self, "_redacted_snapshot", _redacted_snapshot)

    provider_payload_sha256 = property(lambda self: self._provider_payload_sha256)

    @property
    def payload(self) -> Dict[str, Any]:
        return json.loads(self._payload_snapshot.decode("utf-8"))

    @property
    def redacted_request(self) -> Dict[str, Any]:
        return json.loads(self._redacted_snapshot.decode("utf-8"))

    def __repr__(self) -> str:
        return "TransientProviderRequest(provider_payload_sha256={0!r})".format(self.provider_payload_sha256)


def _coerce_iso_date(value: object, label: str) -> _date:
    if isinstance(value, _date) and not isinstance(value, datetime):
        return value
    if isinstance(value, str):
        try:
            parsed = _date.fromisoformat(value)
        except ValueError as exc:
            raise ValueError("{0} must be an ISO date".format(label)) from exc
        if parsed.isoformat() != value:
            raise ValueError("{0} must be an ISO date".format(label))
        return parsed
    raise ValueError("{0} must be an ISO date".format(label))


def _validate_pricing_document(document: Mapping[str, Any]) -> None:
    expected = {"schema_version", "document_kind", "pricing_id", "checked_at", "expires_at", "source_url", "costs"}
    if set(document) != expected or document.get("schema_version") != "1.0.0" or document.get("document_kind") != "meshy_pricing":
        raise ValueError("pricing document fields are not exact")
    if not isinstance(document.get("pricing_id"), str) or _PRICING_ID_RE.fullmatch(document["pricing_id"]) is None:
        raise ValueError("pricing_id is not the approved pricing record")
    checked = _coerce_iso_date(document.get("checked_at"), "pricing checked_at")
    expires = _coerce_iso_date(document.get("expires_at"), "pricing expires_at")
    if checked >= expires:
        raise ValueError("pricing checked_at must precede pricing expires_at")
    if document.get("source_url") != "https://docs.meshy.ai/api/pricing.md":
        raise ValueError("pricing source_url is not official")
    expected_costs = {
        "image_to_3d": {"smart-topology": {"meshy-t2": {"untextured": 5}}},
        "multi_image_to_3d": {"standard": {"meshy-7": {"untextured": 20}, "latest": {"untextured": 20}}},
    }
    def exact_equal(left: object, right: object) -> bool:
        if type(left) is not type(right):
            return False
        if isinstance(left, dict) and isinstance(right, dict):
            return set(left) == set(right) and all(exact_equal(left[key], right[key]) for key in left)
        if isinstance(left, list) and isinstance(right, list):
            return len(left) == len(right) and all(exact_equal(a, b) for a, b in zip(left, right))
        return left == right

    if not exact_equal(document.get("costs"), expected_costs):
        raise ValueError("pricing costs contain an unknown combination or non-positive integer")


def load_pricing(path: Optional[Path] = None, today: object = None, date: object = None) -> PricingRecord:
    if today is not None and date is not None:
        raise ValueError("pass only one of today or date")
    source = Path(path) if path is not None else DEFAULT_PRICING_PATH
    try:
        document, raw = governance.strict_load_json_bytes(source, "Meshy pricing", _PRICING_MAX_BYTES)
        _validate_pricing_document(document)
        as_of = _coerce_iso_date(today if today is not None else date if date is not None else _date.today(), "pricing today")
        if as_of >= _coerce_iso_date(document["expires_at"], "pricing expires_at"):
            raise ValueError("Meshy pricing is expired")
    except (OSError, TypeError, ValueError) as exc:
        if isinstance(exc, ValueError) and str(exc) == "Meshy pricing is expired":
            raise
        raise ValueError("Meshy pricing could not be read: {0}".format(exc)) from exc
    return PricingRecord(document["pricing_id"], document["checked_at"], document["expires_at"], document["source_url"], hashlib.sha256(raw).hexdigest(), canonical_json_bytes(document))


def _resolve_reference_root(reference_root: Union[str, os.PathLike]) -> Path:
    candidate = Path(reference_root).expanduser()
    try:
        if candidate.is_symlink():
            raise ValueError("reference root must not be a symlink")
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise ValueError("reference root could not be resolved") from exc
    if not resolved.is_dir():
        raise ValueError("reference root must be a directory")
    return resolved


def _parse_reference_specs(specs: Union[Mapping[str, str], Iterable[Union[str, Tuple[str, str]]]]) -> Dict[str, str]:
    if isinstance(specs, Mapping):
        entries = list(specs.items())
    elif isinstance(specs, str):
        entries = [specs]
    else:
        try:
            entries = list(specs)
        except TypeError as exc:
            raise ValueError("reference specs must be a mapping or view=filename") from exc
    result: Dict[str, str] = {}
    for entry in entries:
        if isinstance(entry, str) and entry.count("=") == 1:
            view, filename = entry.split("=", 1)
        elif isinstance(entry, (tuple, list)) and len(entry) == 2:
            view, filename = entry
        else:
            raise ValueError("reference spec must be view=filename")
        if not isinstance(view, str) or not isinstance(filename, str) or not view or not filename or view in result:
            raise ValueError("reference spec view and filename must be unique strings")
        result[view] = filename
    return result


def _validate_reference_filename(filename: str) -> str:
    if not filename or "/" in filename or chr(92) in filename or filename in (".", "..") or any(ord(c) == 0 for c in filename):
        raise ValueError("reference filename must be basename-only")
    candidate = Path(filename)
    suffix = candidate.suffix.lower()
    if candidate.is_absolute() or candidate.name != filename or suffix not in _REFERENCE_EXTENSIONS:
        raise ValueError("reference filename must be basename-only with a supported extension")
    return suffix


def _png_is_structurally_valid(payload: bytes) -> bool:
    if len(payload) < 33 or not payload.startswith(_PNG_SIGNATURE):
        return False
    offset = 8
    index = 0
    saw_ihdr = False
    saw_iend = False
    while offset + 12 <= len(payload):
        length = int.from_bytes(payload[offset:offset + 4], "big")
        kind = payload[offset + 4:offset + 8]
        data_start = offset + 8
        data_end = data_start + length
        crc_end = data_end + 4
        if crc_end > len(payload):
            return False
        expected_crc = int.from_bytes(payload[data_end:crc_end], "big")
        actual_crc = zlib.crc32(kind + payload[data_start:data_end]) & 0xFFFFFFFF
        if expected_crc != actual_crc:
            return False
        if index == 0:
            if kind != b"IHDR" or length != 13:
                return False
            width = int.from_bytes(payload[data_start:data_start + 4], "big")
            height = int.from_bytes(payload[data_start + 4:data_start + 8], "big")
            bit_depth = payload[data_start + 8]
            color_type = payload[data_start + 9]
            if width == 0 or height == 0 or bit_depth not in (1, 2, 4, 8, 16) or color_type not in (0, 2, 3, 4, 6):
                return False
            saw_ihdr = True
        elif kind == b"IHDR":
            return False
        if kind == b"IEND":
            if length != 0 or crc_end != len(payload):
                return False
            saw_iend = True
            break
        offset = crc_end
        index += 1
    return saw_ihdr and saw_iend


def _jpeg_is_structurally_valid(payload: bytes) -> bool:
    if len(payload) < 6 or payload[:2] != _JPEG_SOI or payload[-2:] != _JPEG_EOI:
        return False
    offset = 2
    saw_sof = False
    saw_sos = False
    while offset < len(payload):
        if payload[offset] != 255:
            return False
        while offset < len(payload) and payload[offset] == 255:
            offset += 1
        if offset >= len(payload):
            return False
        marker = payload[offset]
        offset += 1
        if marker == 0xD9:
            return saw_sof and saw_sos and offset == len(payload)
        if marker == 0xD8:
            return False
        if marker in (0x01, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7):
            continue
        if offset + 2 > len(payload):
            return False
        length = int.from_bytes(payload[offset:offset + 2], "big")
        if length < 2 or offset + length > len(payload):
            return False
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            saw_sof = True
        if marker == 0xDA:
            saw_sos = True
            offset += length
            while offset < len(payload) - 1:
                if payload[offset] == 255 and payload[offset + 1] != 0 and not (0xD0 <= payload[offset + 1] <= 0xD7):
                    break
                offset += 1
            continue
        offset += length
    return False


def _reference_magic_matches(suffix: str, payload: bytes) -> bool:
    if suffix == ".png":
        return _png_is_structurally_valid(payload)
    return suffix in (".jpg", ".jpeg") and _jpeg_is_structurally_valid(payload)


def _provider_required_views(contract: AssetContract) -> List[str]:
    document = contract._snapshot_document()
    refs = document.get("references")
    views = refs.get("required_views") if isinstance(refs, dict) else None
    if not isinstance(views, list) or not views or any(not isinstance(v, str) for v in views) or len(views) != len(set(views)):
        raise ValueError("contract references.required_views must be a non-empty unique list")
    if document.get("generation", {}).get("mode") == "multi_image_to_3d" and views[0] != "front":
        raise ValueError("multi-image provider requires front as the first reference view")
    return views


def resolve_reference_inputs(contract: AssetContract, reference_root: Union[str, os.PathLike], reference_specs: Union[Mapping[str, str], Iterable[Union[str, Tuple[str, str]]]]) -> ReferenceInputs:
    views = _provider_required_views(contract)
    parsed = _parse_reference_specs(reference_specs)
    if set(parsed) != set(views):
        raise ValueError("reference views must match contract exactly")
    root = _resolve_reference_root(reference_root)
    result: List[ReferenceInput] = []
    names = set()
    identities = set()
    hashes = set()
    total = 0
    for view in views:
        filename = parsed[view]
        suffix = _validate_reference_filename(filename)
        if filename.casefold() in names:
            raise ValueError("duplicate reference basename across views")
        names.add(filename.casefold())
        path = root / filename
        try:
            governance._reject_symlink_components_below(root, path, "reference {0}".format(view))
            payload = governance._read_bounded_regular_file(path, "reference {0}".format(view), _REFERENCE_FILE_MAX_BYTES)
            info = path.stat()
        except (OSError, ValueError) as exc:
            raise ValueError("reference {0} could not be read: {1}".format(view, exc)) from exc
        identity = (info.st_dev, info.st_ino)
        if identity in identities:
            raise ValueError("duplicate reference file identity across views")
        identities.add(identity)
        total += len(payload)
        if total > _REFERENCE_TOTAL_MAX_BYTES:
            raise ValueError("references exceed maximum aggregate size")
        if not _reference_magic_matches(suffix, payload):
            raise ValueError("reference {0} has an invalid signature".format(view))
        digest = hashlib.sha256(payload).hexdigest()
        if digest in hashes:
            raise ValueError("duplicate reference content SHA-256 across views")
        hashes.add(digest)
        result.append(ReferenceInput(view, filename, _REFERENCE_EXTENSIONS[suffix], len(payload), digest, bytes(payload)))
    return ReferenceInputs(result)


def _base_provider_request(contract: AssetContract) -> Dict[str, Any]:
    generation = contract._snapshot_document().get("generation")
    if not isinstance(generation, dict) or generation.get("mode") not in ENDPOINTS:
        raise ValueError("unsupported Meshy generation mode")
    fields = ("model_type", "ai_model", "target_polycount", "should_texture", "target_formats")
    if any(name not in generation for name in fields):
        raise ValueError("contract generation is missing a request field")
    return _copy_mapping({name: generation[name] for name in fields})


def _validate_resolved_reference_inputs(contract: AssetContract, references: ReferenceInputs) -> List[ReferenceInput]:
    if type(references) is not ReferenceInputs or len(references) != len(_provider_required_views(contract)):
        raise ValueError("reference inputs must match contract views exactly")
    seen_names, seen_hashes = set(), set()
    total = 0
    for item, expected_view in zip(references.references, _provider_required_views(contract)):
        if type(item) is not ReferenceInput or item.view != expected_view or not isinstance(item._bytes, bytes):
            raise ValueError("reference inputs are not immutable resolver records")
        suffix = _validate_reference_filename(item.basename)
        if item.basename.casefold() in seen_names:
            raise ValueError("duplicate reference basename across views")
        seen_names.add(item.basename.casefold())
        if item.media_type != _REFERENCE_EXTENSIONS[suffix] or item.byte_size != len(item._bytes) or item.byte_size <= 0:
            raise ValueError("reference metadata does not match bytes")
        total += item.byte_size
        if total > _REFERENCE_TOTAL_MAX_BYTES or not _reference_magic_matches(suffix, item._bytes):
            raise ValueError("reference bytes are invalid")
        digest = hashlib.sha256(item._bytes).hexdigest()
        if item.sha256 != digest or digest in seen_hashes:
            raise ValueError("reference sha256 does not match immutable bytes")
        seen_hashes.add(digest)
    return list(references.references)


def build_transient_provider_request(contract: AssetContract, reference_inputs: ReferenceInputs) -> TransientProviderRequest:
    records = _validate_resolved_reference_inputs(contract, reference_inputs)
    payload = _base_provider_request(contract)
    evidence = [item.metadata for item in records]
    data_uris = ["data:{0};base64,{1}".format(item.media_type, base64.b64encode(item._bytes).decode("ascii")) for item in records]
    mode = contract._snapshot_document()["generation"]["mode"]
    if mode == "image_to_3d":
        payload["image_url"] = data_uris[0]
        redacted = _copy_mapping(payload)
        redacted["image_url"] = evidence[0]
    else:
        payload["image_urls"] = data_uris
        redacted = _copy_mapping(payload)
        redacted["image_urls"] = evidence
    payload_bytes = canonical_json_bytes(payload)
    return TransientProviderRequest(hashlib.sha256(payload_bytes).hexdigest(), payload_bytes, canonical_json_bytes(redacted))


def plan_generation(contract: AssetContract, project_root: Path, client: Any = None, pricing_file: Optional[Path] = None, reference_root: Optional[Path] = None, reference_specs: object = None, today: object = None, date: object = None) -> Dict[str, Any]:
    del project_root, client
    document = contract._snapshot_document()
    generation = document.get("generation")
    if not isinstance(generation, dict) or generation.get("mode") not in ENDPOINTS:
        raise ValueError("unsupported Meshy generation mode")
    count = generation.get("candidate_count")
    if not isinstance(count, int) or isinstance(count, bool) or count <= 0:
        raise ValueError("candidate_count must be a positive integer")
    packet = render_prompt_packet(contract)
    pricing = load_pricing(pricing_file, today=today, date=date)
    transient: Optional[TransientProviderRequest] = None
    if (reference_root is None) != (reference_specs is None):
        raise ValueError("reference-root and reference specs must be supplied together")
    result: Dict[str, Any] = {
        "asset_id": contract.asset_id, "required_views": list(_provider_required_views(contract)), "references_resolved": False,
        "candidate_count": count, "endpoint": ENDPOINTS[generation["mode"]], "cost_per_candidate": pricing.cost_for(contract),
        "maximum_credits": count * pricing.cost_for(contract), "pricing_id": pricing.pricing_id, "pricing_sha256": pricing.sha256,
        "pricing_source_url": pricing.source_url, "pricing_checked_at": pricing.checked_at, "pricing_expires_at": pricing.expires_at,
        "contract_sha256": contract.sha256, "prompt_profile_id": packet["prompt_profile_id"], "prompt_profile_sha256": packet["prompt_profile_sha256"],
        "prompt_packet_sha256": hashlib.sha256(canonical_json_bytes(packet)).hexdigest(), "request": _base_provider_request(contract),
    }
    if reference_root is not None and reference_specs is not None:
        resolved = resolve_reference_inputs(contract, reference_root, reference_specs)
        transient = build_transient_provider_request(contract, resolved)
        result.update({"references_resolved": True, "resolved_references": list(resolved.metadata), "request": transient.redacted_request, "provider_payload_sha256": transient.provider_payload_sha256})
    return result


def _safe_task_id(value: object) -> str:
    if not isinstance(value, str) or _TASK_ID_RE.fullmatch(value) is None:
        raise ValueError("Meshy returned an unsafe task id")
    return value


def _validate_staging_paths(project_root: Union[str, os.PathLike], asset_id: str) -> Tuple[Path, Path, Path]:
    root = governance.physical_project_root(project_root)
    if not isinstance(asset_id, str) or IDENTIFIER_RE.fullmatch(asset_id) is None:
        raise ValueError("contract asset_id is not a safe staging identifier")
    stage = root / STAGING_RELATIVE
    asset_root = stage / asset_id
    governance._reject_symlink_components_below(root, stage, "Meshy staging path")
    governance._reject_symlink_components_below(root, asset_root, "Meshy asset staging path")
    for relative in PROTECTED_RELATIVE:
        protected = root / relative
        governance._reject_symlink_components_below(root, protected, "protected surface")
        if governance._contained(protected, stage) or governance._contained(protected, asset_root) or governance._contained(stage, protected):
            raise ValueError("Meshy staging path overlaps a protected runtime surface")
    return root, stage, asset_root


def _safe_error(exc: BaseException) -> str:
    message = str(exc) or exc.__class__.__name__
    if message in _SAFE_ERROR_MESSAGES:
        return message
    if re.fullmatch(r"Meshy task ended with status [A-Z]+", message):
        return message
    # Provider responses and arbitrary exception text are not evidence.  In
    # particular, do not attempt partial substitution: URLs, query strings,
    # paths, data URIs, and credentials can be split across tokens.
    return "Meshy operation failed"


class _Preflight(NamedTuple):
    root: Path
    stage: Path
    asset_root: Path
    batch_root: Path
    contract: AssetContract
    pricing: PricingRecord
    pricing_path: Optional[Path]
    prompt_packet: Dict[str, Any]
    prompt_hash: str
    references: ReferenceInputs
    reference_root: Path
    reference_specs: Tuple[Tuple[str, str], ...]
    transient: TransientProviderRequest
    endpoint: str
    candidate_count: int
    cost_per_candidate: int
    maximum_credits: int
    approved_credits: int
    output_license: str
    protected_snapshot: Tuple[governance.ProtectedSurfaceRecord, ...]
    today: object
    date: object
    clock: Callable[[], float]
    operation_deadline: float


def _validate_positive_int(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValueError("{0} must be a positive integer".format(label))
    return value


def _new_deadline(clock: Callable[[], float], deadline: object) -> float:
    if deadline is None:
        raise ValueError("deadline must be a positive finite number")
    if not isinstance(deadline, (int, float)) or isinstance(deadline, bool) or not math.isfinite(float(deadline)):
        raise ValueError("deadline must be a positive finite number")
    seconds = float(deadline)
    if seconds <= 0 or seconds > _MAX_DEADLINE_SECONDS:
        raise ValueError("deadline must be positive and no more than {0} seconds".format(_MAX_DEADLINE_SECONDS))
    return clock() + seconds


def _check_deadline(preflight: _Preflight) -> None:
    if preflight.clock() >= preflight.operation_deadline:
        raise RuntimeError("Meshy generation deadline exceeded")


def _approval(preflight: _Preflight, created_at: str) -> Dict[str, Any]:
    return {
        "contract_sha256": preflight.contract.sha256,
        "prompt_profile_id": preflight.prompt_packet["prompt_profile_id"],
        "prompt_profile_sha256": preflight.prompt_packet["prompt_profile_sha256"],
        "prompt_packet_sha256": preflight.prompt_hash,
        "pricing_id": preflight.pricing.pricing_id,
        "pricing_sha256": preflight.pricing.sha256,
        "provider_payload_sha256": preflight.transient.provider_payload_sha256,
        "request": preflight.transient.redacted_request,
        "references": list(preflight.references.metadata),
        "endpoint": preflight.endpoint,
        "candidate_count": preflight.candidate_count,
        "cost_per_candidate": preflight.cost_per_candidate,
        "maximum_credits": preflight.maximum_credits,
        "approved_credits": preflight.approved_credits,
        "output_license": preflight.output_license,
        "protected_snapshot": [{"type": item.type, "path": item.path, "sha256": item.sha256, "size": item.size} for item in preflight.protected_snapshot],
        "created_at": created_at,
    }


def _journal_document(preflight: _Preflight, batch_id: str, state: str, tasks: List[Dict[str, Any]], cumulative: int, created_at: str) -> Dict[str, Any]:
    return {"schema_version": "1.0.0", "document_kind": "meshy_batch_journal", "batch_id": batch_id, "asset_id": preflight.contract.asset_id, "approval": _approval(preflight, created_at), "state": state, "tasks": tasks, "cumulative_consumed_credits": cumulative}


def _write_journal(preflight: _Preflight, batch_id: str, document: Dict[str, Any]) -> None:
    errors = validate_batch_journal(document)
    if errors:
        raise ValueError("invalid Meshy batch journal before publication: " + "; ".join(errors))
    path = preflight.batch_root / (batch_id + ".json")
    governance.atomic_write_json(path, document, project_root=preflight.root, allowed_root=preflight.batch_root)


def _load_open_journals(
    preflight: _Preflight,
    contract_sha256: str,
    profile_sha256: str,
    pricing_sha256: str,
    provider_hash: str,
    output_license: Optional[str] = None,
) -> None:
    if not preflight.batch_root.exists():
        return
    if preflight.batch_root.is_symlink() or not preflight.batch_root.is_dir():
        raise ValueError("Meshy batch journal root is not a directory")
    for path in sorted(preflight.batch_root.glob("*.json")):
        if path.is_symlink() or not path.is_file():
            raise ValueError("Meshy batch journal entry is not a regular file")
        document = governance.strict_load_json(path, "Meshy batch journal", 4 * 1024 * 1024)
        errors = validate_batch_journal(document)
        if errors:
            raise ValueError("invalid Meshy batch journal: " + "; ".join(errors))
        if path.stem != document["batch_id"]:
            raise ValueError("Meshy batch journal filename does not match batch_id")
        if document["state"] in ("APPROVED", "SUBMITTING"):
            approval = document["approval"]
            if (approval["contract_sha256"], approval["prompt_profile_sha256"], approval["pricing_sha256"], approval["provider_payload_sha256"]) == (contract_sha256, profile_sha256, pricing_sha256, provider_hash) and (output_license is None or approval["output_license"] == output_license):
                raise ValueError("an open duplicate Meshy batch already exists")


def _preflight(contract: AssetContract, project_root: Path, client: Any, approved_credits: int, *, pricing_file: Optional[Path], reference_root: Path, reference_specs: object, output_license: str, today: object, date: object, clock: Optional[Callable[[], float]], deadline: object) -> _Preflight:
    del client
    clock_fn = clock or time.monotonic
    operation_deadline = _new_deadline(clock_fn, deadline)
    approved = _validate_positive_int(approved_credits, "approved_credits")
    if output_license not in _ALLOWED_LICENSES:
        raise ValueError("output_license must be paid-private or free-cc-by-4.0")
    if reference_root is None or reference_specs is None:
        raise ValueError("reference-root, reference specs, and output-license are required")
    if not isinstance(contract, AssetContract):
        raise TypeError("contract must be an AssetContract")
    source_contract = load_contract(contract.path)
    if source_contract.sha256 != contract.sha256 or source_contract.snapshot_bytes() != contract.snapshot_bytes():
        raise ValueError("asset contract changed since it was loaded")
    packet = render_prompt_packet(source_contract)
    prompt_hash = hashlib.sha256(canonical_json_bytes(packet)).hexdigest()
    pricing = load_pricing(pricing_file, today=today, date=date)
    reference_root_physical = _resolve_reference_root(reference_root)
    parsed_reference_specs = _parse_reference_specs(reference_specs)
    references = resolve_reference_inputs(source_contract, reference_root_physical, parsed_reference_specs)
    transient = build_transient_provider_request(source_contract, references)
    root, stage, asset_root = _validate_staging_paths(project_root, source_contract.asset_id)
    protected = governance.snapshot_protected_surfaces(
        root,
        max_file_bytes=_REPOSITORY_SNAPSHOT_MAX_FILE_BYTES,
        max_total_bytes=_REPOSITORY_SNAPSHOT_MAX_TOTAL_BYTES,
        max_entries=_REPOSITORY_SNAPSHOT_MAX_ENTRIES,
        max_depth=_REPOSITORY_SNAPSHOT_MAX_DEPTH,
    )
    cost = pricing.cost_for(source_contract)
    count = _validate_positive_int(source_contract._snapshot_document()["generation"].get("candidate_count"), "candidate_count")
    maximum = count * cost
    if approved < maximum:
        raise ValueError("approved credit ceiling is below the maximum Meshy batch cost ({0} required)".format(maximum))
    batch_root = asset_root / "_batches"
    governance._reject_symlink_components_below(root, batch_root, "Meshy batch journal root")
    result = _Preflight(root, stage, asset_root, batch_root, source_contract, pricing, Path(pricing_file) if pricing_file is not None else None, packet, prompt_hash, references, reference_root_physical, tuple(parsed_reference_specs.items()), transient, ENDPOINTS[source_contract._snapshot_document()["generation"]["mode"]], count, cost, maximum, approved, output_license, protected, today, date, clock_fn, operation_deadline)
    _check_deadline(result)
    return result


def _refresh_before_provider(preflight: _Preflight) -> None:
    _check_deadline(preflight)
    current_contract = load_contract(preflight.contract.path)
    if current_contract.sha256 != preflight.contract.sha256 or current_contract.snapshot_bytes() != preflight.contract.snapshot_bytes():
        raise ValueError("asset contract changed during Meshy preflight")
    packet = render_prompt_packet(current_contract)
    if packet["prompt_profile_id"] != preflight.prompt_packet["prompt_profile_id"] or packet["prompt_profile_sha256"] != preflight.prompt_packet["prompt_profile_sha256"] or hashlib.sha256(canonical_json_bytes(packet)).hexdigest() != preflight.prompt_hash:
        raise ValueError("prompt profile changed during Meshy preflight")
    pricing = load_pricing(preflight.pricing_path, today=preflight.today, date=preflight.date)
    if pricing.sha256 != preflight.pricing.sha256:
        raise ValueError("Meshy pricing changed during preflight")
    current_references = resolve_reference_inputs(current_contract, preflight.reference_root, dict(preflight.reference_specs))
    if current_references.metadata != preflight.references.metadata:
        raise ValueError("reference inputs changed during preflight")
    transient = build_transient_provider_request(current_contract, current_references)
    if transient.provider_payload_sha256 != preflight.transient.provider_payload_sha256:
        raise ValueError("Meshy provider payload changed during preflight")
    current_snapshot = governance.snapshot_protected_surfaces(
        preflight.root,
        max_file_bytes=_REPOSITORY_SNAPSHOT_MAX_FILE_BYTES,
        max_total_bytes=_REPOSITORY_SNAPSHOT_MAX_TOTAL_BYTES,
        max_entries=_REPOSITORY_SNAPSHOT_MAX_ENTRIES,
        max_depth=_REPOSITORY_SNAPSHOT_MAX_DEPTH,
    )
    if current_snapshot != preflight.protected_snapshot:
        raise ValueError("protected runtime surfaces changed during preflight")


class _TaskFailure(RuntimeError):
    def __init__(
        self,
        message: str,
        consumed: Optional[int] = None,
        *,
        budget_violation: bool = False,
        publication_uncertain: bool = False,
    ) -> None:
        super().__init__(message)
        self.consumed = consumed
        self.budget_violation = budget_violation
        self.publication_uncertain = publication_uncertain


class _TaskCollision(_TaskFailure):
    def __init__(self, task_id: str) -> None:
        super().__init__("Meshy task id collision")
        self.collision_task_id = task_id


def _base_generation(
    preflight: _Preflight,
    batch_id: str,
    task_index: int,
    task_id: str,
    created_at: str,
) -> Dict[str, Any]:
    generation = preflight.contract._snapshot_document()["generation"]
    contract_artifact = canonical_json_bytes(preflight.contract._snapshot_document())
    pricing_artifact = canonical_json_bytes(preflight.pricing.document)
    return {
        "schema_version": "1.0.0", "document_kind": "meshy_generation_record", "asset_id": preflight.contract.asset_id,
        "batch_id": batch_id, "task_index": task_index, "task_id": task_id,
        "status": "PENDING", "endpoint": preflight.endpoint, "contract_sha256": preflight.contract.sha256,
        "prompt_profile_id": preflight.prompt_packet["prompt_profile_id"], "prompt_profile_sha256": preflight.prompt_packet["prompt_profile_sha256"],
        "prompt_packet_sha256": preflight.prompt_hash, "pricing_id": preflight.pricing.pricing_id, "pricing_sha256": preflight.pricing.sha256,
        "approved_credits": preflight.approved_credits, "cost_per_candidate": preflight.cost_per_candidate,
        "maximum_credits": preflight.maximum_credits,
        "contract_artifact_sha256": hashlib.sha256(contract_artifact).hexdigest(),
        "pricing_artifact_sha256": hashlib.sha256(pricing_artifact).hexdigest(),
        "provider_payload_sha256": preflight.transient.provider_payload_sha256, "request": preflight.transient.redacted_request,
        "references": list(preflight.references.metadata), "input_image_hashes": {item.view: item.sha256 for item in sorted(preflight.references, key=lambda item: item.view)},
        "output_license": preflight.output_license, "created_at": created_at, "completed_at": None, "consumed_credits": None,
        "outputs": {}, "provenance": {"provider": "meshy", "model": generation["ai_model"], "license_state": preflight.output_license}, "error": None,
        "budget_violation": False,
    }


def _artifact_path(preflight: _Preflight, task_dir: Path, name: str) -> Path:
    if not isinstance(name, str) or not name or Path(name).name != name or name in (".", ".."):
        raise ValueError("Meshy task artifact name must be a basename")
    path = task_dir / name
    governance.governed_task_path(preflight.root, path, "Meshy task artifact")
    return path


def _validate_artifact_before_publish(
    preflight: _Preflight, task_dir: Path, name: str, value: object
) -> None:
    if name == "generation.json":
        errors = validate_generation_record(value)
    elif name == "review.json":
        errors = validate_review(value)
        if isinstance(value, dict) and (
            value.get("asset_id") != preflight.contract.asset_id
            or not isinstance(value.get("task_id"), str)
        ):
            errors.append("review record identity does not match task")
    elif name == "contract.json":
        errors = [] if value == preflight.contract._snapshot_document() else ["staged contract does not match loaded contract"]
    elif name == "prompt-packet.json":
        errors = []
        if not isinstance(value, dict) or hashlib.sha256(canonical_json_bytes(value)).hexdigest() != preflight.prompt_hash:
            errors.append("staged prompt packet does not match approval")
    elif name == "pricing.json":
        errors = []
        try:
            _validate_pricing_document(value)  # type: ignore[arg-type]
        except (TypeError, ValueError):
            errors.append("staged pricing document is invalid")
        if not errors and canonical_json_bytes(value) != preflight.pricing.snapshot_bytes():
            errors.append("staged pricing does not match approval")
    else:
        return
    if errors:
        raise ValueError("invalid Meshy {0}: {1}".format(name, "; ".join(errors)))


def _write_artifact_json(preflight: _Preflight, task_dir: Path, name: str, value: object) -> None:
    _validate_artifact_before_publish(preflight, task_dir, name, value)
    governance.atomic_write_json(
        _artifact_path(preflight, task_dir, name),
        value,
        project_root=preflight.root,
        allowed_root=preflight.asset_root,
    )


def _write_artifact_bytes(preflight: _Preflight, task_dir: Path, name: str, payload: bytes) -> None:
    governance.atomic_write_bytes(
        _artifact_path(preflight, task_dir, name),
        payload,
        project_root=preflight.root,
        allowed_root=preflight.asset_root,
    )


def _create_artifact_bytes(preflight: _Preflight, task_dir: Path, name: str, payload: bytes) -> None:
    governance.atomic_create_bytes(
        _artifact_path(preflight, task_dir, name),
        payload,
        project_root=preflight.root,
        allowed_root=preflight.asset_root,
    )


def _validate_glb(payload: bytes) -> None:
    """Validate the complete GLB 2.0 container, not merely its magic bytes."""

    if not isinstance(payload, bytes) or len(payload) < 12:
        raise ValueError("raw.glb has an invalid GLB header")
    magic, version, declared_length = struct.unpack_from("<4sII", payload, 0)
    if magic != b"glTF":
        raise ValueError("raw.glb has an invalid GLB magic")
    if version != 2:
        raise ValueError("raw.glb must use GLB version 2")
    if declared_length < 12 or declared_length != len(payload) or declared_length % 4:
        raise ValueError("raw.glb declared length is invalid")
    offset = 12
    json_chunk: Optional[bytes] = None
    json_count = 0
    bin_count = 0
    first = True
    while offset < declared_length:
        if declared_length - offset < 8:
            raise ValueError("raw.glb contains a truncated chunk header")
        chunk_length, chunk_type_value = struct.unpack_from("<II", payload, offset)
        chunk_type = struct.pack("<I", chunk_type_value)
        if chunk_length % 4:
            raise ValueError("raw.glb chunk is not aligned")
        chunk_start = offset + 8
        chunk_end = chunk_start + chunk_length
        if chunk_end > declared_length:
            raise ValueError("raw.glb chunk exceeds declared length")
        if first and chunk_type != b"JSON":
            raise ValueError("raw.glb first chunk must be JSON")
        first = False
        chunk = payload[chunk_start:chunk_end]
        if chunk_type == b"JSON":
            json_count += 1
            if json_count > 1:
                raise ValueError("raw.glb contains more than one JSON chunk")
            json_chunk = chunk
        elif chunk_type == b"BIN\x00":
            bin_count += 1
            if bin_count > 1:
                raise ValueError("raw.glb contains more than one BIN chunk")
        offset = chunk_end
    if offset != declared_length or json_chunk is None:
        raise ValueError("raw.glb must contain a JSON first chunk")
    try:
        decoded = json.loads(json_chunk.rstrip(b" \\t\\r\\n\\x00").decode("utf-8"))
    except (UnicodeDecodeError, TypeError, ValueError) as exc:
        raise ValueError("raw.glb JSON chunk is invalid") from exc
    if not isinstance(decoded, dict):
        raise ValueError("raw.glb JSON chunk must decode to an object")
    asset = decoded.get("asset")
    if not isinstance(asset, dict):
        raise ValueError("raw.glb JSON chunk must contain an asset object")
    if asset.get("version") != "2.0":
        raise ValueError("raw.glb asset.version must be exactly 2.0")


def _download_with_limit(client: Any, url: str, maximum: int, preflight: _Preflight) -> bytes:
    """Use the sealed in-memory client seam with the operation deadline."""

    _check_deadline(preflight)
    remaining = preflight.operation_deadline - preflight.clock()
    if remaining <= 0:
        raise RuntimeError("Meshy generation deadline exceeded")
    _check_deadline(preflight)
    payload = client.download_bytes(
        url,
        maximum,
        remaining,
        preflight.clock,
    )
    if not isinstance(payload, bytes):
        raise RuntimeError("Meshy download returned invalid bytes")
    if len(payload) > maximum:
        raise RuntimeError("Meshy download exceeds maximum size")
    _check_deadline(preflight)
    return payload


def _call_with_deadline(method: Callable[..., Any], preflight: _Preflight, *args: Any) -> Any:
    """Pass the absolute operation deadline to real clients, not rigid fakes."""

    try:
        parameters = inspect.signature(method).parameters
    except (TypeError, ValueError):
        parameters = {}
    accepts_kwargs = any(
        parameter.kind == inspect.Parameter.VAR_KEYWORD for parameter in parameters.values()
    )
    options: Dict[str, Any] = {}
    if "deadline" in parameters or accepts_kwargs:
        options["deadline"] = preflight.operation_deadline
    if "clock" in parameters or accepts_kwargs:
        options["clock"] = preflight.clock
    return method(*args, **options)


def _poll_until_succeeded(client: Any, preflight: _Preflight, task_id: str, on_consumed: Callable[[int], None]) -> Dict[str, Any]:
    last_consumed: Optional[int] = None
    for attempt in range(_MAX_POLL_ATTEMPTS):
        try:
            _check_deadline(preflight)
        except Exception as exc:
            raise _TaskFailure(_safe_error(exc), last_consumed)
        try:
            response = _call_with_deadline(
                client.poll_task,
                preflight,
                preflight.endpoint,
                task_id,
            )
        except Exception as exc:
            raise _TaskFailure(_safe_error(exc), last_consumed)
        if not isinstance(response, dict):
            raise _TaskFailure("Meshy task response must be an object", last_consumed)
        response_task_id = response.get("task_id")
        response_provider_id = response.get("id")
        if "id" in response and response_provider_id != task_id:
            raise _TaskFailure("Meshy task response identity mismatch", last_consumed)
        if "task_id" in response and response_task_id != task_id:
            raise _TaskFailure("Meshy task response identity mismatch", last_consumed)
        if "id" not in response and "task_id" not in response:
            raise _TaskFailure("Meshy task response identity is missing", last_consumed)
        response = dict(response)
        response["task_id"] = task_id
        consumed = response.get("consumed_credits")
        if consumed is not None:
            if not isinstance(consumed, int) or isinstance(consumed, bool) or consumed < 0:
                raise _TaskFailure("Meshy consumed_credits must be a non-negative integer", last_consumed)
            if last_consumed is not None and consumed < last_consumed:
                raise _TaskFailure("Meshy consumed credit observation decreased and is ambiguous", last_consumed)
            last_consumed = consumed
            on_consumed(consumed)
        status = response.get("status")
        if status == "SUCCEEDED":
            if consumed is None and last_consumed is not None:
                response = dict(response)
                response["consumed_credits"] = last_consumed
            return response
        terminal_consumed = consumed if consumed is not None else last_consumed
        if status in ("FAILED", "CANCELED", "CANCELLED", "EXPIRED"):
            raise _TaskFailure("Meshy task ended with status {0}".format(status), terminal_consumed)
        if status not in ("PENDING", "IN_PROGRESS", "QUEUED"):
            raise _TaskFailure("Meshy task returned an unsupported status", terminal_consumed)
        if attempt + 1 >= _MAX_POLL_ATTEMPTS:
            break
        delay = _POLL_DELAYS[min(attempt, len(_POLL_DELAYS) - 1)]
        if delay:
            try:
                _check_deadline(preflight)
            except Exception as exc:
                raise _TaskFailure(_safe_error(exc), last_consumed)
            remaining = preflight.operation_deadline - preflight.clock()
            if remaining <= 0:
                raise _TaskFailure("Meshy generation deadline exceeded", last_consumed)
            time.sleep(min(delay, remaining))
    raise _TaskFailure("Meshy task did not reach SUCCEEDED", last_consumed)


def _stage_task(
    client: Any,
    preflight: _Preflight,
    batch_id: str,
    task_index: int,
    task_id: str,
    created_at: str,
    prior_consumed: int,
) -> Dict[str, Any]:
    task_dir = preflight.asset_root / task_id
    governance.governed_task_path(preflight.root, task_dir, "Meshy task directory", allow_missing=True)
    if os.path.lexists(str(task_dir)):
        raise _TaskCollision(task_id)
    generation_record = _base_generation(preflight, batch_id, task_index, task_id, created_at)
    candidate_dir: Optional[Path] = None
    last_consumed: Optional[int] = None
    static_complete = False
    try:
        candidate_dir = Path(
            tempfile.mkdtemp(
                prefix=".task-{0}-".format(task_id),
                suffix=".tmp",
                dir=str(preflight.asset_root),
            )
        )
        _write_artifact_json(
            preflight,
            candidate_dir,
            "contract.json",
            json.loads(preflight.contract.snapshot_bytes().decode("utf-8")),
        )
        _write_artifact_json(preflight, candidate_dir, "prompt-packet.json", preflight.prompt_packet)
        _write_artifact_json(preflight, candidate_dir, "pricing.json", preflight.pricing.document)
        for item in preflight.references:
            _write_artifact_bytes(
                preflight,
                candidate_dir,
                "source_{0}{1}".format(item.view, Path(item.basename).suffix.lower()),
                item._bytes,
            )
        review = {
            "schema_version": "1.0.0",
            "document_kind": "meshy_candidate_review",
            "asset_id": preflight.contract.asset_id,
            "task_id": task_id,
            "state": "pending",
            "decision": "pending",
            "checks": {
                "silhouette_readable": False,
                "proportions_match_contract": False,
                "functional_volume_present": False,
                "movable_parts_separable": False,
                "cleanup_bounded": False,
                "camera_readability": False,
            },
            "rejection_reasons": [],
            "reviewer": "unassigned",
        }
        _write_artifact_json(preflight, candidate_dir, "review.json", review)
        _write_artifact_json(preflight, candidate_dir, "generation.json", generation_record)
        static_complete = True

        def note_consumed(value: int) -> None:
            nonlocal last_consumed
            last_consumed = value
            if value > preflight.cost_per_candidate or prior_consumed + value > preflight.approved_credits:
                raise _TaskFailure(
                    "Meshy actual credit consumption exceeded the approved bound",
                    value,
                    budget_violation=True,
                )

        response = _poll_until_succeeded(client, preflight, task_id, note_consumed)
        if response.get("task_id") != task_id or response.get("status") != "SUCCEEDED":
            raise _TaskFailure("Meshy success response identity/status mismatch", last_consumed)
        consumed = response.get("consumed_credits")
        if not isinstance(consumed, int) or isinstance(consumed, bool) or consumed < 0:
            raise _TaskFailure("Meshy success response did not contain consumed_credits", last_consumed)
        if consumed > preflight.cost_per_candidate or prior_consumed + consumed > preflight.approved_credits:
            raise _TaskFailure(
                "Meshy actual credit consumption exceeded the approved bound",
                consumed,
                budget_violation=True,
            )
        model_urls = response.get("model_urls")
        glb_url = model_urls.get("glb") if isinstance(model_urls, dict) else None
        thumbnail_url = response.get("thumbnail_url")
        if not isinstance(glb_url, str) or not isinstance(thumbnail_url, str):
            raise _TaskFailure("Meshy success response did not contain required output URLs", consumed)
        try:
            MeshyClient._validate_download_url(glb_url)
            MeshyClient._validate_download_url(thumbnail_url)
        except RuntimeError as exc:
            raise _TaskFailure(_safe_error(exc), consumed)
        glb = _download_with_limit(client, glb_url, _GLB_MAX_BYTES, preflight)
        thumbnail = _download_with_limit(client, thumbnail_url, _THUMBNAIL_MAX_BYTES, preflight)
        try:
            _validate_glb(glb)
        except ValueError as exc:
            raise _TaskFailure(_safe_error(exc), consumed)
        if len(thumbnail) <= len(_PNG_SIGNATURE) or not thumbnail.startswith(_PNG_SIGNATURE):
            raise _TaskFailure("thumbnail.png has an invalid PNG signature", consumed)
        if len(glb) + len(thumbnail) > _DOWNLOAD_TOTAL_MAX_BYTES:
            raise _TaskFailure("Meshy downloads exceed aggregate size", consumed)
        _write_artifact_bytes(preflight, candidate_dir, "raw.glb", glb)
        _write_artifact_bytes(preflight, candidate_dir, "thumbnail.png", thumbnail)
        generation_record.update(
            {
                "status": "SUCCEEDED",
                "completed_at": _utc_timestamp(),
                "consumed_credits": consumed,
                "budget_violation": False,
                "outputs": {
                    "raw.glb": {"sha256": hashlib.sha256(glb).hexdigest(), "byte_size": len(glb)},
                    "thumbnail.png": {
                        "sha256": hashlib.sha256(thumbnail).hexdigest(),
                        "byte_size": len(thumbnail),
                    },
                },
            }
        )
        # The success marker is written last, immediately before publication.
        _write_artifact_json(preflight, candidate_dir, "generation.json", generation_record)
        governance.atomic_publish_directory(
            candidate_dir,
            task_dir,
            project_root=preflight.root,
            allowed_root=preflight.asset_root,
        )
        candidate_dir = None
        return generation_record
    except BaseException as exc:
        if isinstance(exc, governance.PublicationUncertainError) and exc.published:
            raise _TaskFailure(
                "Meshy task publication durability is uncertain",
                last_consumed,
                publication_uncertain=True,
            ) from exc
        if isinstance(exc, _TaskCollision):
            raise
        if isinstance(exc, ValueError) and "final task directory" in str(exc):
            raise _TaskCollision(task_id) from exc
        if static_complete and candidate_dir is not None and candidate_dir.exists():
            failed = dict(generation_record)
            failed["status"] = "FAILED"
            failed["consumed_credits"] = last_consumed
            failed["budget_violation"] = bool(getattr(exc, "budget_violation", False))
            failed["error"] = _safe_error(exc)
            try:
                for output_name in ("raw.glb", "thumbnail.png"):
                    output_path = _artifact_path(preflight, candidate_dir, output_name)
                    if os.path.lexists(str(output_path)):
                        if output_path.is_symlink() or not output_path.is_file():
                            raise ValueError("Meshy failed envelope contains an invalid output")
                        output_path.unlink()
                _write_artifact_json(preflight, candidate_dir, "generation.json", failed)
                governance.atomic_publish_directory(
                    candidate_dir,
                    task_dir,
                    project_root=preflight.root,
                    allowed_root=preflight.asset_root,
                )
                candidate_dir = None
            except BaseException:
                raise _TaskFailure("Meshy failure evidence publication failed", last_consumed) from exc
        if isinstance(exc, _TaskFailure):
            raise
        raise _TaskFailure(_safe_error(exc), last_consumed)
    finally:
        if candidate_dir is not None and candidate_dir.exists():
            shutil.rmtree(candidate_dir, ignore_errors=True)


def _task_entry(
    index: int,
    task_id: Optional[str],
    state: str,
    consumed: Optional[int],
    error: Optional[str],
    collision_task_id: Optional[str] = None,
    budget_violation: bool = False,
) -> Dict[str, Any]:
    return {
        "index": index,
        "task_id": task_id,
        "collision_task_id": collision_task_id,
        "state": state,
        "consumed_credits": consumed,
        "error": error,
        "budget_violation": budget_violation,
    }


def generate_batch(
    contract: AssetContract,
    project_root: Path,
    client: Any,
    approved_credits: int,
    *,
    pricing_file: Optional[Path],
    reference_root: Path,
    reference_specs: object,
    output_license: str,
    today: object = None,
    date: object = None,
    clock: Optional[Callable[[], float]] = None,
    deadline: object = _DEFAULT_DEADLINE_SECONDS,
) -> Dict[str, Any]:
    if client is None or not all(hasattr(client, name) for name in ("get_balance", "create_task", "poll_task", "download_bytes")):
        raise ValueError("a Meshy client is required for generation")
    # No staging, balance, journal, or provider work occurs before this full
    # deterministic preflight completes.
    preflight = _preflight(
        contract, project_root, client, approved_credits,
        pricing_file=pricing_file, reference_root=reference_root,
        reference_specs=reference_specs, output_license=output_license,
        today=today, date=date, clock=clock, deadline=deadline,
    )
    batch_id = uuid.uuid4().hex
    created_at = _utc_timestamp()
    account_lock_id = getattr(client, "account_lock_id", None)
    if not isinstance(account_lock_id, str):
        raise ValueError("Meshy client must expose an account lock id")
    with governance.credit_lock(account_lock_id, preflight.operation_deadline, preflight.clock):
        # The lock covers the duplicate-journal check, balance check, every
        # POST, polling/downloads, and all journal publication for this batch.
        _refresh_before_provider(preflight)
        _load_open_journals(
            preflight,
            preflight.contract.sha256,
            preflight.prompt_packet["prompt_profile_sha256"],
            preflight.pricing.sha256,
            preflight.transient.provider_payload_sha256,
            preflight.output_license,
        )
        tasks = [_task_entry(index, None, "PENDING", None, None) for index in range(preflight.candidate_count)]
        _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "APPROVED", tasks, 0, created_at))
        task_ids: List[str] = []
        cumulative = 0
        for index in range(preflight.candidate_count):
            try:
                _refresh_before_provider(preflight)
            except Exception as exc:
                error = _safe_error(exc)
                tasks[index] = _task_entry(index, None, "FAILED", None, error)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise
            tasks[index] = _task_entry(index, None, "SUBMITTING", None, None)
            _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "SUBMITTING", tasks, cumulative, created_at))
            try:
                balance = _call_with_deadline(client.get_balance, preflight)
            except Exception as exc:
                error = _safe_error(exc)
                tasks[index] = _task_entry(index, None, "FAILED", None, error)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise RuntimeError(error)
            if not isinstance(balance, int) or isinstance(balance, bool) or balance < (preflight.candidate_count - index) * preflight.cost_per_candidate:
                error = "insufficient Meshy balance for remaining reserved maximum"
                tasks[index] = _task_entry(index, None, "FAILED", None, error)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise ValueError(error)
            try:
                _refresh_before_provider(preflight)
            except Exception as exc:
                error = _safe_error(exc)
                tasks[index] = _task_entry(index, None, "FAILED", None, error)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise
            try:
                task_id = _safe_task_id(
                    _call_with_deadline(
                        client.create_task,
                        preflight,
                        preflight.endpoint,
                        preflight.transient.payload,
                    )
                )
            except Exception as exc:
                # Keep SUBMITTING: a crash/transport failure after POST may
                # have created an unknown provider task for later reconciliation.
                error = _safe_error(exc)
                tasks[index] = _task_entry(index, None, "SUBMITTING", None, error)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "SUBMITTING", tasks, cumulative, created_at))
                raise RuntimeError(error)
            if task_id in task_ids:
                error = "Meshy returned a duplicate task id"
                tasks[index] = _task_entry(index, None, "COLLISION", None, error, collision_task_id=task_id)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise ValueError("duplicate task id")
            task_dir = preflight.asset_root / task_id
            if os.path.lexists(str(task_dir)):
                error = "Meshy task id collision"
                tasks[index] = _task_entry(index, None, "COLLISION", None, error, collision_task_id=task_id)
                _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise ValueError("Meshy task id collision")
            task_ids.append(task_id)
            tasks[index] = _task_entry(index, task_id, "PENDING", None, None)
            _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "SUBMITTING", tasks, cumulative, created_at))
            try:
                record = _stage_task(client, preflight, batch_id, index, task_id, created_at, cumulative)
            except Exception as exc:
                consumed = exc.consumed if isinstance(exc, _TaskFailure) else None
                if consumed is not None:
                    cumulative += consumed
                if isinstance(exc, _TaskCollision):
                    tasks[index] = _task_entry(index, None, "COLLISION", None, _safe_error(exc), collision_task_id=exc.collision_task_id)
                    _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                elif isinstance(exc, _TaskFailure) and exc.publication_uncertain:
                    tasks[index] = _task_entry(index, task_id, "UNCERTAIN", consumed, _safe_error(exc))
                    _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "UNCERTAIN", tasks, cumulative, created_at))
                elif isinstance(exc, _TaskFailure) and exc.budget_violation:
                    tasks[index] = _task_entry(index, task_id, "OVERRUN", consumed, _safe_error(exc), budget_violation=True)
                    _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "BUDGET_OVERRUN", tasks, cumulative, created_at))
                else:
                    tasks[index] = _task_entry(index, task_id, "FAILED", consumed, _safe_error(exc))
                    _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, "FAILED", tasks, cumulative, created_at))
                raise
            consumed = record["consumed_credits"]
            cumulative += consumed
            tasks[index] = _task_entry(index, task_id, "SUCCEEDED", consumed, None)
            state = "COMPLETED" if index + 1 == preflight.candidate_count else "SUBMITTING"
            _write_journal(preflight, batch_id, _journal_document(preflight, batch_id, state, tasks, cumulative, created_at))
    return {
        "asset_id": preflight.contract.asset_id,
        "batch_id": batch_id,
        "candidate_count": preflight.candidate_count,
        "maximum_credits": preflight.maximum_credits,
        "approved_credits": preflight.approved_credits,
        "consumed_credits": cumulative,
        "task_ids": task_ids,
    }


def _valid_positive_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _valid_nonnegative_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _valid_hash(value: object) -> bool:
    return isinstance(value, str) and _HASH_RE.fullmatch(value) is not None


def _validate_reference_item(value: object, label: str) -> List[str]:
    errors: List[str] = []
    fields = {"view", "basename", "media_type", "byte_size", "sha256"}
    if not isinstance(value, dict) or set(value) != fields:
        return [label + " fields are not exact"]
    if not isinstance(value.get("view"), str) or re.fullmatch(r"[a-z0-9][a-z0-9_-]*", value.get("view", "")) is None:
        errors.append(label + " view is invalid")
    basename = value.get("basename")
    if not isinstance(basename, str):
        errors.append(label + " basename is invalid")
    else:
        try:
            suffix = _validate_reference_filename(basename)
            if value.get("media_type") != _REFERENCE_EXTENSIONS[suffix]:
                errors.append(label + " media_type does not match basename")
        except (TypeError, ValueError):
            errors.append(label + " basename is invalid")
    if value.get("media_type") not in _REFERENCE_EXTENSIONS.values():
        errors.append(label + " media_type is invalid")
    if not _valid_positive_int(value.get("byte_size")):
        errors.append(label + " byte_size is invalid")
    if not _valid_hash(value.get("sha256")):
        errors.append(label + " sha256 is invalid")
    return errors


def _validate_reference_list(value: object, label: str) -> List[str]:
    if not isinstance(value, list) or not value:
        return [label + " must be a non-empty list"]
    errors: List[str] = []
    views = set()
    basenames = set()
    for index, item in enumerate(value):
        errors.extend(_validate_reference_item(item, "%s[%d]" % (label, index)))
        if isinstance(item, dict):
            view = item.get("view")
            basename = item.get("basename")
            if view in views:
                errors.append(label + " contains duplicate views")
            views.add(view)
            if isinstance(basename, str) and basename.casefold() in basenames:
                errors.append(label + " contains duplicate basenames")
            if isinstance(basename, str):
                basenames.add(basename.casefold())
    return sorted(set(errors))


def _validate_provider_request(
    value: object, endpoint: object, references: object, label: str
) -> List[str]:
    errors: List[str] = []
    if endpoint == ENDPOINTS["image_to_3d"]:
        reference_field = "image_url"
    elif endpoint == ENDPOINTS["multi_image_to_3d"]:
        reference_field = "image_urls"
    else:
        return [label + " endpoint is invalid"]
    fields = {"model_type", "ai_model", "target_polycount", "should_texture", "target_formats", reference_field}
    if not isinstance(value, dict) or set(value) != fields:
        return [label + " fields are not exact"]
    for name in ("model_type", "ai_model"):
        if not isinstance(value.get(name), str) or not value[name]:
            errors.append(label + " %s is invalid" % name)
    expected_models = (
        ("smart-topology", "meshy-t2")
        if endpoint == ENDPOINTS["image_to_3d"]
        else ("standard", "meshy-7", "latest")
    )
    if endpoint == ENDPOINTS["image_to_3d"] and (value.get("model_type"), value.get("ai_model")) != expected_models:
        errors.append(label + " model combination is invalid")
    if endpoint == ENDPOINTS["multi_image_to_3d"] and (value.get("model_type") != expected_models[0] or value.get("ai_model") not in expected_models[1:]):
        errors.append(label + " model combination is invalid")
    if not _valid_positive_int(value.get("target_polycount")):
        errors.append(label + " target_polycount is invalid")
    if type(value.get("should_texture")) is not bool or value["should_texture"] is not False:
        errors.append(label + " should_texture must be false")
    if type(value.get("target_formats")) is not list or value["target_formats"] != ["glb"]:
        errors.append(label + " target_formats must be exactly [glb]")
    request_value = value.get(reference_field)
    if reference_field == "image_urls":
        request_values = request_value if isinstance(request_value, list) else []
        if not isinstance(request_value, list):
            errors.append(label + " image_urls must be a list")
    else:
        request_values = [request_value]
    if request_values and isinstance(request_values[0], dict) and request_values[0].get("view") != "front":
        errors.append(label + " first image evidence must be front")
    for index, item in enumerate(request_values):
        errors.extend(_validate_reference_item(item, "%s.%s[%d]" % (label, reference_field, index)))
    if isinstance(references, list) and not any("fields are not exact" in error for error in errors):
        expected = references if reference_field == "image_urls" else references[:1]
        if request_values != expected:
            errors.append(label + " references do not match evidence")
    return sorted(set(errors))


def _validate_protected_snapshot(value: object, label: str) -> List[str]:
    if not isinstance(value, list) or not value:
        return [label + " must be a non-empty list"]
    errors: List[str] = []
    for index, item in enumerate(value):
        item_label = "%s[%d]" % (label, index)
        fields = {"type", "path", "sha256", "size"}
        if not isinstance(item, dict) or set(item) != fields:
            errors.append(item_label + " fields are not exact")
            continue
        if item.get("type") not in ("missing", "file", "directory"):
            errors.append(item_label + " type is invalid")
        expected_paths = tuple(relative.as_posix() for relative in PROTECTED_RELATIVE)
        if index >= len(expected_paths) or item.get("path") != expected_paths[index]:
            errors.append(item_label + " path is invalid")
        if len(value) != len(expected_paths):
            errors.append(label + " entries are not exact")
        if item.get("type") == "missing":
            if item.get("sha256") is not None or item.get("size") != 0:
                errors.append(item_label + " missing record is invalid")
        elif not _valid_hash(item.get("sha256")):
            errors.append(item_label + " sha256 is invalid")
        if not _valid_nonnegative_int(item.get("size")):
            errors.append(item_label + " size is invalid")
    return sorted(set(errors))


def _validate_output_map(value: object, label: str) -> List[str]:
    if not isinstance(value, dict):
        return [label + " must be an object"]
    errors: List[str] = []
    if set(value) - {"raw.glb", "thumbnail.png"}:
        errors.append(label + " names are invalid")
    for name, item in value.items():
        if not isinstance(item, dict) or set(item) != {"sha256", "byte_size"}:
            errors.append(label + " evidence is invalid")
            continue
        if not _valid_hash(item.get("sha256")) or not _valid_positive_int(item.get("byte_size")):
            errors.append(label + " evidence is invalid")
    return sorted(set(errors))


def _forbidden_evidence_string(value: str) -> bool:
    if "data:" in value or "base64" in value or "?" in value:
        return True
    if re.search(r"(?i)\b[a-z][a-z0-9+.-]*:", value):
        return True
    if re.search(r"(?i)\b(?:authorization|bearer|api[_ -]?key|token|secret|signature)\b", value):
        return True
    if re.search(r"(?:^|[^A-Za-z0-9])/(?:Users|Volumes|private|tmp|var)(?:/|$)", value):
        return True
    if re.search(r"(?i)(?:^|[^A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)", value):
        return True
    if not (_valid_hash(value) or _BATCH_ID_RE.fullmatch(value)) and re.search(r"(?:^|[\s,])[A-Za-z0-9+/]{16,}(?:={0,2})(?:$|[\s,])", value):
        return True
    return False


def _validate_evidence_privacy(value: object, label: str, errors: List[str]) -> None:
    if isinstance(value, float) and not math.isfinite(value):
        errors.append(label + " contains a non-finite number")
    elif isinstance(value, dict):
        for key, child in value.items():
            _validate_evidence_privacy(child, label + "." + str(key), errors)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _validate_evidence_privacy(child, "%s[%d]" % (label, index), errors)
    elif isinstance(value, str) and _forbidden_evidence_string(value):
        errors.append(label + " contains forbidden evidence material")


def _validate_generation_record_strict(document: object) -> List[str]:
    if not isinstance(document, dict):
        return ["generation record must be an object"]
    required = {
        "schema_version", "document_kind", "asset_id", "batch_id", "task_index", "task_id",
        "status", "endpoint", "contract_sha256", "prompt_profile_id", "prompt_profile_sha256",
        "prompt_packet_sha256", "pricing_id", "pricing_sha256", "provider_payload_sha256",
        "approved_credits", "cost_per_candidate", "maximum_credits", "contract_artifact_sha256",
        "pricing_artifact_sha256", "request", "references", "input_image_hashes", "output_license",
        "created_at", "completed_at", "consumed_credits", "outputs", "provenance", "error", "budget_violation",
    }
    errors: List[str] = []
    if set(document) != required:
        errors.append("generation record fields are not exact")
    if document.get("schema_version") != "1.0.0" or document.get("document_kind") != "meshy_generation_record":
        errors.append("generation record kind/version is invalid")
    if not isinstance(document.get("asset_id"), str) or re.fullmatch(r"[a-z0-9][a-z0-9_-]*", document.get("asset_id", "")) is None:
        errors.append("generation asset_id is invalid")
    if not isinstance(document.get("batch_id"), str) or _BATCH_ID_RE.fullmatch(document.get("batch_id", "")) is None:
        errors.append("generation batch_id is invalid")
    if not _valid_nonnegative_int(document.get("task_index")):
        errors.append("generation task_index is invalid")
    if not isinstance(document.get("task_id"), str) or _TASK_ID_RE.fullmatch(document.get("task_id", "")) is None:
        errors.append("generation task_id is invalid")
    if document.get("status") not in ("PENDING", "SUCCEEDED", "FAILED"):
        errors.append("generation status is invalid")
    if document.get("endpoint") not in ENDPOINTS.values():
        errors.append("generation endpoint is invalid")
    for name in (
        "contract_sha256", "prompt_profile_sha256", "prompt_packet_sha256", "pricing_sha256",
        "provider_payload_sha256", "contract_artifact_sha256", "pricing_artifact_sha256",
    ):
        if not _valid_hash(document.get(name)):
            errors.append("generation %s is invalid" % name)
    if not isinstance(document.get("prompt_profile_id"), str) or not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", document.get("prompt_profile_id", "")):
        errors.append("generation prompt_profile_id is invalid")
    if not isinstance(document.get("pricing_id"), str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", document.get("pricing_id", "")):
        errors.append("generation pricing_id is invalid")
    for name in ("approved_credits", "cost_per_candidate", "maximum_credits"):
        if not _valid_positive_int(document.get(name)):
            errors.append("generation %s is invalid" % name)
    expected_cost = 5 if document.get("endpoint") == ENDPOINTS["image_to_3d"] else 20 if document.get("endpoint") == ENDPOINTS["multi_image_to_3d"] else None
    if expected_cost is not None and document.get("cost_per_candidate") != expected_cost:
        errors.append("generation cost_per_candidate is invalid")
    if all(_valid_positive_int(document.get(name)) for name in ("approved_credits", "cost_per_candidate", "maximum_credits")):
        if document["approved_credits"] < document["maximum_credits"]:
            errors.append("generation approved credit bound is invalid")
    if _valid_nonnegative_int(document.get("consumed_credits")) and _valid_positive_int(document.get("approved_credits")) and document["consumed_credits"] > document["approved_credits"] and document.get("budget_violation") is not True:
        errors.append("generation consumed credits exceed approval")
    if not isinstance(document.get("created_at"), str) or not document["created_at"]:
        errors.append("generation created_at is invalid")
    if document.get("completed_at") is not None and (not isinstance(document.get("completed_at"), str) or not document["completed_at"]):
        errors.append("generation completed_at is invalid")
    if document.get("consumed_credits") is not None and not _valid_nonnegative_int(document.get("consumed_credits")):
        errors.append("generation consumed_credits is invalid")
    if type(document.get("budget_violation")) is not bool:
        errors.append("generation budget_violation is invalid")
    if document.get("error") is not None and (not isinstance(document.get("error"), str) or not document["error"] or len(document["error"]) > 240):
        errors.append("generation error is invalid")
    references = document.get("references")
    errors.extend(_validate_reference_list(references, "generation references"))
    errors.extend(_validate_provider_request(document.get("request"), document.get("endpoint"), references, "generation request"))
    image_hashes = document.get("input_image_hashes")
    if not isinstance(image_hashes, dict) or any(not isinstance(key, str) or not _valid_hash(value) for key, value in image_hashes.items()):
        errors.append("generation input image hashes are invalid")
    elif isinstance(references, list):
        expected_views = [item.get("view") for item in references if isinstance(item, dict)]
        if list(image_hashes) != sorted(expected_views) or set(image_hashes) != set(expected_views):
            errors.append("generation input image hash set/order is invalid")
        elif any(image_hashes[item["view"]] != item["sha256"] for item in references if isinstance(item, dict)):
            errors.append("generation input image hashes do not match references")
    if document.get("output_license") not in _ALLOWED_LICENSES:
        errors.append("generation output_license is invalid")
    provenance = document.get("provenance")
    if not isinstance(provenance, dict) or set(provenance) != {"provider", "model", "license_state"}:
        errors.append("generation provenance is invalid")
    elif provenance.get("provider") != "meshy" or not isinstance(provenance.get("model"), str) or not provenance["model"] or provenance.get("license_state") != document.get("output_license"):
        errors.append("generation provenance is invalid")
    outputs = document.get("outputs")
    errors.extend(_validate_output_map(outputs, "generation outputs"))
    output_names = set(outputs) if isinstance(outputs, dict) else set()
    status = document.get("status")
    if status == "PENDING":
        if document.get("completed_at") is not None or document.get("consumed_credits") is not None or outputs != {} or document.get("error") is not None or document.get("budget_violation") is not False:
            errors.append("pending generation state is inconsistent")
    elif status == "SUCCEEDED":
        if not isinstance(document.get("completed_at"), str) or not document["completed_at"] or not _valid_nonnegative_int(document.get("consumed_credits")) or output_names != {"raw.glb", "thumbnail.png"} or document.get("error") is not None or document.get("budget_violation") is not False:
            errors.append("succeeded generation state is inconsistent")
        if _valid_nonnegative_int(document.get("consumed_credits")) and _valid_positive_int(document.get("cost_per_candidate")) and document["consumed_credits"] > document["cost_per_candidate"]:
            errors.append("succeeded generation consumption exceeds per-task cost")
    elif status == "FAILED":
        if not isinstance(document.get("error"), str) or not document["error"] or document.get("completed_at") is not None:
            errors.append("failed generation state is inconsistent")
        if outputs != {}:
            errors.append("failed generation must not contain outputs")
        consumed = document.get("consumed_credits")
        if document.get("budget_violation") is True:
            if not _valid_nonnegative_int(consumed):
                errors.append("overrun generation must preserve consumed credits")
        elif document.get("budget_violation") is False and _valid_nonnegative_int(consumed) and _valid_positive_int(document.get("cost_per_candidate")) and consumed > document["cost_per_candidate"]:
            errors.append("failed generation consumption exceeds per-task cost")
    _validate_evidence_privacy(document, "generation", errors)
    return sorted(set(errors))


def validate_generation_record(document: object) -> List[str]:
    return _validate_generation_record_strict(document)


def _validate_batch_journal_strict(document: object) -> List[str]:
    if not isinstance(document, dict):
        return ["batch journal must be an object"]
    required = {"schema_version", "document_kind", "batch_id", "asset_id", "approval", "state", "tasks", "cumulative_consumed_credits"}
    errors: List[str] = []
    if set(document) != required:
        errors.append("batch journal fields are not exact")
    if document.get("schema_version") != "1.0.0" or document.get("document_kind") != "meshy_batch_journal":
        errors.append("batch journal kind/version is invalid")
    if not isinstance(document.get("batch_id"), str) or _BATCH_ID_RE.fullmatch(document.get("batch_id", "")) is None:
        errors.append("batch_id is invalid")
    if not isinstance(document.get("asset_id"), str) or re.fullmatch(r"[a-z0-9][a-z0-9_-]*", document.get("asset_id", "")) is None:
        errors.append("batch journal asset_id is invalid")
    state = document.get("state")
    if state not in ("APPROVED", "SUBMITTING", "COMPLETED", "FAILED", "BUDGET_OVERRUN", "UNCERTAIN"):
        errors.append("batch journal state is invalid")
    approval = document.get("approval")
    approval_fields = {
        "contract_sha256", "prompt_profile_id", "prompt_profile_sha256", "prompt_packet_sha256",
        "pricing_id", "pricing_sha256", "provider_payload_sha256", "request", "references", "endpoint",
        "candidate_count", "cost_per_candidate", "maximum_credits", "approved_credits", "output_license",
        "protected_snapshot", "created_at",
    }
    if not isinstance(approval, dict) or set(approval) != approval_fields:
        errors.append("batch journal approval fields are not exact")
    else:
        for name in ("contract_sha256", "prompt_profile_sha256", "prompt_packet_sha256", "pricing_sha256", "provider_payload_sha256"):
            if not _valid_hash(approval.get(name)):
                errors.append("batch journal approval hash is invalid")
        if not isinstance(approval.get("prompt_profile_id"), str) or re.fullmatch(r"[a-z0-9][a-z0-9_-]*", approval.get("prompt_profile_id", "")) is None:
            errors.append("batch journal approval prompt profile is invalid")
        if not isinstance(approval.get("pricing_id"), str) or not approval["pricing_id"]:
            errors.append("batch journal approval pricing id is invalid")
        for name in ("candidate_count", "cost_per_candidate", "maximum_credits", "approved_credits"):
            if not _valid_positive_int(approval.get(name)):
                errors.append("batch journal approval credit field is invalid")
        expected_cost = 5 if approval.get("endpoint") == ENDPOINTS["image_to_3d"] else 20 if approval.get("endpoint") == ENDPOINTS["multi_image_to_3d"] else None
        if expected_cost is not None and approval.get("cost_per_candidate") != expected_cost:
            errors.append("batch journal approval cost is invalid")
        if all(_valid_positive_int(approval.get(name)) for name in ("candidate_count", "cost_per_candidate", "maximum_credits", "approved_credits")):
            if approval["maximum_credits"] != approval["candidate_count"] * approval["cost_per_candidate"] or approval["approved_credits"] < approval["maximum_credits"]:
                errors.append("batch journal approval credit bound is invalid")
        if approval.get("endpoint") not in ENDPOINTS.values():
            errors.append("batch journal approval endpoint is invalid")
        errors.extend(_validate_reference_list(approval.get("references"), "batch journal approval references"))
        errors.extend(_validate_provider_request(approval.get("request"), approval.get("endpoint"), approval.get("references"), "batch journal approval request"))
        if approval.get("output_license") not in _ALLOWED_LICENSES:
            errors.append("batch journal approval license is invalid")
        errors.extend(_validate_protected_snapshot(approval.get("protected_snapshot"), "batch journal protected snapshot"))
        if not isinstance(approval.get("created_at"), str) or not approval["created_at"]:
            errors.append("batch journal approval created_at is invalid")
    tasks = document.get("tasks")
    task_ids = set()

    if not isinstance(tasks, list) or not tasks:
        errors.append("batch journal tasks must be a non-empty list")
        tasks = []
    indexes = []
    consumed_values = []
    for index, item in enumerate(tasks):
        label = "batch journal task[%d]" % index
        fields = {"index", "task_id", "collision_task_id", "state", "consumed_credits", "error", "budget_violation"}
        if not isinstance(item, dict) or set(item) != fields:
            errors.append(label + " fields are not exact")
            continue
        task_index = item.get("index")
        task_id = item.get("task_id")
        collision_id = item.get("collision_task_id")
        task_state = item.get("state")
        indexes.append(task_index)
        if not _valid_nonnegative_int(task_index):
            errors.append(label + " index is invalid")
        if task_id is not None and (not isinstance(task_id, str) or _TASK_ID_RE.fullmatch(task_id) is None):
            errors.append(label + " task_id is invalid")
        if collision_id is not None and (not isinstance(collision_id, str) or _TASK_ID_RE.fullmatch(collision_id) is None):
            errors.append(label + " collision_task_id is invalid")
        if task_id is not None and collision_id is not None:
            errors.append(label + " cannot contain task_id and collision_task_id")
        if task_id is not None:
            if task_id in task_ids:
                errors.append("batch journal contains duplicate task ids")
            task_ids.add(task_id)

        if task_state not in ("PENDING", "SUBMITTING", "SUCCEEDED", "FAILED", "OVERRUN", "UNCERTAIN", "COLLISION"):
            errors.append(label + " state is invalid")
        consumed = item.get("consumed_credits")
        if consumed is not None and not _valid_nonnegative_int(consumed):
            errors.append(label + " consumed_credits is invalid")
        error = item.get("error")
        if error is not None and (not isinstance(error, str) or not error or len(error) > 240):
            errors.append(label + " error is invalid")
        if type(item.get("budget_violation")) is not bool:
            errors.append(label + " budget_violation is invalid")
        if task_state == "PENDING" and (consumed is not None or error is not None):
            errors.append(label + " pending state is inconsistent")
        if task_state == "PENDING" and item.get("budget_violation") is not False:
            errors.append(label + " pending budget state is inconsistent")
        if task_state == "SUBMITTING" and (consumed is not None or collision_id is not None or item.get("budget_violation") is not False or task_id is not None and error is not None):
            errors.append(label + " submitting state is inconsistent")
        if task_state == "SUCCEEDED" and (task_id is None or collision_id is not None or not _valid_nonnegative_int(consumed) or error is not None or item.get("budget_violation") is not False):
            errors.append(label + " succeeded state is inconsistent")
        if task_state == "SUCCEEDED" and _valid_nonnegative_int(consumed) and isinstance(approval, dict) and _valid_positive_int(approval.get("cost_per_candidate")) and consumed > approval["cost_per_candidate"]:
            errors.append(label + " succeeded consumption exceeds per-task cost")
        if task_state == "FAILED" and (not isinstance(error, str) or item.get("budget_violation") is not False):
            errors.append(label + " failed state requires error")
        if task_state == "FAILED" and _valid_nonnegative_int(consumed) and isinstance(approval, dict) and _valid_positive_int(approval.get("cost_per_candidate")) and consumed > approval["cost_per_candidate"]:
            errors.append(label + " failed consumption exceeds per-task cost")
        if task_state == "OVERRUN" and (task_id is None or collision_id is not None or not _valid_nonnegative_int(consumed) or not isinstance(error, str) or item.get("budget_violation") is not True):
            errors.append(label + " overrun state is inconsistent")
        if task_state == "UNCERTAIN" and (task_id is None or collision_id is not None or not _valid_nonnegative_int(consumed) or not isinstance(error, str) or item.get("budget_violation") is not False):
            errors.append(label + " uncertain state is inconsistent")
        if task_state == "UNCERTAIN" and _valid_nonnegative_int(consumed) and isinstance(approval, dict) and _valid_positive_int(approval.get("cost_per_candidate")) and consumed > approval["cost_per_candidate"]:
            errors.append(label + " uncertain consumption exceeds per-task cost")
        if task_state == "COLLISION" and (task_id is not None or collision_id is None or consumed is not None or not isinstance(error, str) or item.get("budget_violation") is not False):
            errors.append(label + " collision state is inconsistent")
        if task_state not in ("COLLISION",) and collision_id is not None:
            errors.append(label + " collision is only valid for COLLISION")
        if _valid_nonnegative_int(consumed):
            consumed_values.append(consumed)
    if indexes != list(range(len(indexes))):
        errors.append("batch journal task indexes are not exact")
    if isinstance(approval, dict) and _valid_positive_int(approval.get("candidate_count")) and len(tasks) != approval["candidate_count"]:
        errors.append("batch journal task count does not match approval")
    cumulative = document.get("cumulative_consumed_credits")
    if not _valid_nonnegative_int(cumulative):
        errors.append("batch journal cumulative credits are invalid")
    elif cumulative != sum(consumed_values):
        errors.append("batch journal cumulative credits do not match tasks")
    if state != "BUDGET_OVERRUN" and isinstance(approval, dict) and _valid_positive_int(approval.get("approved_credits")) and _valid_nonnegative_int(cumulative) and cumulative > approval["approved_credits"]:
        errors.append("batch journal cumulative credits exceed approval")
    if state == "BUDGET_OVERRUN" and isinstance(approval, dict) and _valid_positive_int(approval.get("cost_per_candidate")) and _valid_positive_int(approval.get("approved_credits")):
        if not any(
            isinstance(item, dict)
            and item.get("state") == "OVERRUN"
            and _valid_nonnegative_int(item.get("consumed_credits"))
            and (item["consumed_credits"] > approval["cost_per_candidate"] or (_valid_nonnegative_int(cumulative) and cumulative > approval["approved_credits"]))
            for item in tasks
        ):
            errors.append("budget overrun journal does not contain truthful overrun evidence")
    if tasks:
        task_states = [item.get("state") for item in tasks if isinstance(item, dict)]
        all_succeeded = bool(task_states) and all(item == "SUCCEEDED" for item in task_states)
        if (state == "COMPLETED") != all_succeeded:
            errors.append("batch journal COMPLETED state does not match task states")
        if state == "FAILED" and not any(item in ("FAILED", "COLLISION") for item in task_states):
            errors.append("failed batch journal must contain a failed or collision task")
        if state == "BUDGET_OVERRUN" and "OVERRUN" not in task_states:
            errors.append("budget overrun journal must contain an overrun task")
        if state == "UNCERTAIN" and "UNCERTAIN" not in task_states:
            errors.append("uncertain journal must contain an uncertain task")
        if state == "APPROVED" and any(item != "PENDING" for item in task_states):
            errors.append("approved batch journal must contain only pending tasks")
        if state == "SUBMITTING" and (all_succeeded or any(item in ("FAILED", "COLLISION", "OVERRUN", "UNCERTAIN") for item in task_states) or not any(item in ("PENDING", "SUBMITTING") for item in task_states)):
            errors.append("submitting batch journal task states are inconsistent")
    _validate_evidence_privacy(document, "batch journal", errors)
    return sorted(set(errors))


def validate_batch_journal(document: object) -> List[str]:
    return _validate_batch_journal_strict(document)


def _read_adjacent_json(path: Path, label: str) -> Tuple[Dict[str, Any], bytes]:
    try:
        return governance.strict_load_json_bytes(path, label, 4 * 1024 * 1024)
    except (OSError, ValueError) as exc:
        raise ValueError("Meshy adjacent artifact is unavailable: " + label) from exc


def _verify_generation_journal_binding(
    document: Dict[str, Any],
    journal: Dict[str, Any],
    allow_pending_reconciliation: bool = False,
    allow_failed_reconciliation: bool = False,
) -> None:
    approval = journal["approval"]
    for name in (
        "contract_sha256", "prompt_profile_sha256", "prompt_packet_sha256",
        "pricing_sha256", "provider_payload_sha256",
    ):
        if document[name] != approval[name]:
            raise ValueError("Meshy generation and journal approval hash does not match")
    for name in (
        "prompt_profile_id", "pricing_id", "endpoint", "request", "references",
        "approved_credits", "cost_per_candidate", "maximum_credits", "output_license",
    ):
        if document[name] != approval[name]:
            raise ValueError("Meshy generation and journal approval does not match")
    if document["asset_id"] != journal["asset_id"] or document["batch_id"] != journal["batch_id"]:
        raise ValueError("Meshy generation and journal identity does not match")
    if document["created_at"] != approval["created_at"]:
        raise ValueError("Meshy generation and journal creation time does not match")
    index = document["task_index"]
    if index >= len(journal["tasks"]):
        raise ValueError("Meshy generation task index is outside journal")
    task = journal["tasks"][index]
    if task["index"] != index or task.get("task_id") != document["task_id"]:
        raise ValueError("Meshy generation task identity does not match journal")
    if allow_pending_reconciliation and task["state"] == "PENDING":
        # A crash after publication can leave a terminal generation record
        # beside a journal that still says PENDING.  Identity and approval
        # fields above still bind the record; resume reconciles the terminal
        # state and observed credits below.
        return
    if allow_failed_reconciliation and task["state"] == "FAILED" and document["status"] == "SUCCEEDED":
        # A crash after the generation CAS can leave a succeeded generation
        # beside a journal that still says FAILED.  The continuation path
        # separately binds the immutable failed archive and reconciles credits.
        return
    if document["status"] == "PENDING":
        if task["state"] not in ("PENDING", "SUBMITTING"):
            raise ValueError("Meshy pending generation state does not match journal")
    elif document["status"] == "SUCCEEDED" and task["state"] not in ("SUCCEEDED", "UNCERTAIN"):
        raise ValueError("Meshy generation state does not match journal")
    elif document["status"] == "FAILED" and task["state"] not in ("FAILED", "OVERRUN"):
        raise ValueError("Meshy generation state does not match journal")
    if bool(document.get("budget_violation")) != (task["state"] == "OVERRUN"):
        raise ValueError("Meshy generation budget state does not match journal")
    if task.get("consumed_credits") != document.get("consumed_credits"):
        raise ValueError("Meshy generation consumption does not match journal")


def _verify_generation_adjacent_artifacts(
    path: Path,
    document: Dict[str, Any],
    journal_path: Optional[Union[str, os.PathLike]],
    allow_pending_reconciliation: bool = False,
    allow_failed_output_recovery: bool = False,
    allow_failed_reconciliation: bool = False,
) -> None:
    task_dir = path.parent
    contract_path = task_dir / "contract.json"
    contract, contract_raw = _read_adjacent_json(contract_path, "contract.json")
    contract_digest = hashlib.sha256(contract_raw).hexdigest()
    if contract_raw != canonical_json_bytes(contract) or contract_digest != document["contract_artifact_sha256"]:
        raise ValueError("Meshy contract artifact hash does not match generation record")
    if contract.get("asset_id") != document["asset_id"]:
        raise ValueError("Meshy contract artifact asset identity does not match")
    contract_generation = contract.get("generation")
    if not isinstance(contract_generation, dict):
        raise ValueError("Meshy contract generation section is invalid")
    contract_mode = contract_generation.get("mode")
    expected_endpoint = ENDPOINTS.get(contract_mode) if isinstance(contract_mode, str) else None
    if expected_endpoint != document["endpoint"]:
        raise ValueError("Meshy contract endpoint does not match generation record")
    for name in ("model_type", "ai_model", "target_polycount", "should_texture", "target_formats"):
        if document["request"].get(name) != contract_generation.get(name):
            raise ValueError("Meshy contract request does not match generation record")
    contract_count = contract_generation.get("candidate_count")
    if not _valid_positive_int(contract_count) or document["maximum_credits"] != contract_count * document["cost_per_candidate"]:
        raise ValueError("Meshy contract candidate credit bound does not match generation record")
    contract_references = contract.get("references")
    contract_views = contract_references.get("required_views") if isinstance(contract_references, dict) else None
    record_views = [item["view"] for item in document["references"]]
    if not isinstance(contract_views, list) or record_views != contract_views:
        raise ValueError("Meshy contract reference views do not match generation record")

    prompt, prompt_raw = _read_adjacent_json(task_dir / "prompt-packet.json", "prompt-packet.json")
    if prompt_raw != canonical_json_bytes(prompt) or hashlib.sha256(prompt_raw).hexdigest() != document["prompt_packet_sha256"] or prompt.get("prompt_profile_id") != document["prompt_profile_id"] or prompt.get("prompt_profile_sha256") != document["prompt_profile_sha256"]:
        raise ValueError("Meshy prompt packet artifact does not match generation record")

    pricing, pricing_raw = _read_adjacent_json(task_dir / "pricing.json", "pricing.json")
    try:
        _validate_pricing_document(pricing)
    except (TypeError, ValueError) as exc:
        raise ValueError("Meshy pricing artifact is invalid") from exc
    if pricing_raw != canonical_json_bytes(pricing) or hashlib.sha256(pricing_raw).hexdigest() != document["pricing_artifact_sha256"]:
        raise ValueError("Meshy pricing artifact hash does not match generation record")

    expected_sources = set()
    for reference in document["references"]:
        suffix = Path(reference["basename"]).suffix.lower()
        name = "source_{0}{1}".format(reference["view"], suffix)
        expected_sources.add(name)
        source_path = task_dir / name
        try:
            payload = governance._read_bounded_regular_file(source_path, "Meshy " + name, _REFERENCE_FILE_MAX_BYTES)
        except (OSError, ValueError) as exc:
            raise ValueError("Meshy source artifact is unavailable: " + name) from exc
        if len(payload) != reference["byte_size"] or hashlib.sha256(payload).hexdigest() != reference["sha256"]:
            raise ValueError("Meshy source artifact hash/size does not match: " + name)
        if not _reference_magic_matches(suffix, payload):
            raise ValueError("Meshy source artifact signature is invalid: " + name)
    actual_sources = {item.name for item in task_dir.iterdir() if item.name.startswith("source_")}
    if actual_sources != expected_sources:
        raise ValueError("Meshy source artifact set does not match generation record")

    review, _review_raw = _read_adjacent_json(task_dir / "review.json", "review.json")
    review_errors = validate_review(review)
    if review_errors:
        raise ValueError("Meshy review record is invalid: " + "; ".join(review_errors))
    if review.get("asset_id") != document["asset_id"] or review.get("task_id") != document["task_id"]:
        raise ValueError("Meshy review identity does not match generation record")

    if document["status"] == "SUCCEEDED":
        for name, maximum in (("raw.glb", _GLB_MAX_BYTES), ("thumbnail.png", _THUMBNAIL_MAX_BYTES)):
            artifact = task_dir / name
            try:
                payload = governance._read_bounded_regular_file(artifact, "Meshy " + name, maximum)
            except (OSError, ValueError) as exc:
                raise ValueError("Meshy succeeded artifact is unavailable: " + name) from exc
            if name == "raw.glb":
                try:
                    _validate_glb(payload)
                except ValueError as exc:
                    raise ValueError("Meshy succeeded artifact container is invalid: " + name) from exc
            elif len(payload) <= len(_PNG_SIGNATURE) or not payload.startswith(_PNG_SIGNATURE):
                raise ValueError("Meshy succeeded artifact signature is invalid: " + name)
            evidence = document["outputs"][name]
            if evidence["byte_size"] != len(payload) or evidence["sha256"] != hashlib.sha256(payload).hexdigest():
                raise ValueError("Meshy succeeded artifact hash/size does not match: " + name)
    else:
        actual_outputs = {item.name for item in task_dir.iterdir() if item.name in {"raw.glb", "thumbnail.png"}}
        if actual_outputs and not allow_failed_output_recovery:
            raise ValueError("Meshy non-success generation contains output artifacts")

    if journal_path is None:
        journal_path = task_dir.parent / "_batches" / (document["batch_id"] + ".json")
    assert journal_path is not None
    journal = load_batch_journal(journal_path)
    _verify_generation_journal_binding(
        document,
        journal,
        allow_pending_reconciliation=allow_pending_reconciliation,
        allow_failed_reconciliation=allow_failed_reconciliation,
    )


def load_generation_record(
    path: Union[str, os.PathLike],
    journal_path: Optional[Union[str, os.PathLike]] = None,
    *,
    allow_pending_reconciliation: bool = False,
    allow_failed_output_recovery: bool = False,
    allow_failed_reconciliation: bool = False,
) -> Dict[str, Any]:
    """Strictly parse, validate, and bind one generated record from disk."""

    record_path = Path(path)
    document = governance.strict_load_json(record_path, "Meshy generation record", 4 * 1024 * 1024)
    errors = validate_generation_record(document)
    if errors:
        raise ValueError("invalid Meshy generation record: " + "; ".join(errors))
    if record_path.name == "generation.json" and record_path.parent.name != document["task_id"]:
        raise ValueError("Meshy generation filename does not match task_id")
    _verify_generation_adjacent_artifacts(
        record_path,
        document,
        journal_path,
        allow_pending_reconciliation=allow_pending_reconciliation,
        allow_failed_output_recovery=allow_failed_output_recovery,
        allow_failed_reconciliation=allow_failed_reconciliation,
    )
    return document


def load_batch_journal(path: Union[str, os.PathLike]) -> Dict[str, Any]:
    """Strictly parse and validate one batch journal from disk."""

    document = governance.strict_load_json(path, "Meshy batch journal", 4 * 1024 * 1024)
    errors = validate_batch_journal(document)
    if errors:
        raise ValueError("invalid Meshy batch journal: " + "; ".join(errors))
    if Path(path).suffix == ".json" and Path(path).stem != document["batch_id"]:
        raise ValueError("Meshy batch journal filename does not match batch_id")
    return document


def _resume_journal_path(
    project_root: Union[str, os.PathLike],
    asset_id: str,
    batch_journal: Union[str, os.PathLike],
) -> Tuple[Path, Path, Path, Path]:
    """Resolve a resume journal to the one physical, governed journal root."""

    root, stage, asset_root = _validate_staging_paths(project_root, asset_id)
    batch_root = asset_root / "_batches"
    governance._reject_symlink_components_below(root, batch_root, "Meshy batch journal root")
    candidate = governance._path_from_explicit_root(project_root, root, batch_journal)
    candidate = Path(os.path.abspath(os.fspath(candidate)))
    governance._reject_symlink_components_below(root, candidate, "Meshy batch journal")
    if candidate.parent != batch_root:
        raise ValueError("Meshy batch journal must be physically under the asset _batches directory")
    if candidate.suffix != ".json" or candidate.name == ".json":
        raise ValueError("Meshy batch journal filename must be the batch id")
    return root, stage, asset_root, candidate


def _load_resume_journal(
    project_root: Union[str, os.PathLike],
    contract: AssetContract,
    batch_journal: Union[str, os.PathLike],
) -> Tuple[Path, Path, Path, Path, Dict[str, Any]]:
    root, stage, asset_root, path = _resume_journal_path(project_root, contract.asset_id, batch_journal)
    journal = load_batch_journal(path)
    if journal.get("asset_id") != contract.asset_id:
        raise ValueError("Meshy batch journal asset identity does not match contract")
    if path.stem != journal.get("batch_id"):
        raise ValueError("Meshy batch journal filename does not match batch id")
    return root, stage, asset_root, path, journal


def _approval_matches_preflight(preflight: _Preflight, journal: Dict[str, Any]) -> bool:
    approval = journal.get("approval")
    if not isinstance(approval, dict):
        return False
    created_at = approval.get("created_at")
    expected = _approval(preflight, created_at if isinstance(created_at, str) else "")
    return approval == expected


def _approval_creation_matches_evidence(preflight: _Preflight, journal: Dict[str, Any]) -> bool:
    """Bind the journal creation time when a task record already exists."""

    created_at = journal["approval"]["created_at"]
    for task in journal["tasks"]:
        task_id = task.get("task_id")
        if task.get("state") not in ("SUCCEEDED", "UNCERTAIN") or not isinstance(task_id, str):
            continue
        record_path = preflight.asset_root / task_id / "generation.json"
        if not record_path.exists():
            continue
        try:
            record = governance.strict_load_json(record_path, "Meshy generation record", 4 * 1024 * 1024)
        except (OSError, TypeError, ValueError):
            continue
        if record.get("created_at") != created_at:
            return False
    return True


def _resume_state(tasks: List[Dict[str, Any]]) -> str:
    states = [task.get("state") for task in tasks]
    if states and all(state == "SUCCEEDED" for state in states):
        return "COMPLETED"
    if "OVERRUN" in states:
        return "BUDGET_OVERRUN"
    if "UNCERTAIN" in states:
        return "UNCERTAIN"
    if "FAILED" in states or "COLLISION" in states:
        return "FAILED"
    if states and all(state == "PENDING" and task.get("task_id") is None for state, task in zip(states, tasks)):
        return "APPROVED"
    return "SUBMITTING"


def _resume_journal_update(
    preflight: _Preflight,
    journal: Dict[str, Any],
    tasks: List[Dict[str, Any]],
    cumulative: int,
) -> None:
    updated = _copy_mapping(journal)
    updated["tasks"] = tasks
    updated["cumulative_consumed_credits"] = cumulative
    updated["state"] = _resume_state(tasks)
    _write_journal(preflight, journal["batch_id"], updated)


def _unresolved_item(index: int, task: Mapping[str, Any], reason: str) -> Dict[str, Any]:
    return {
        "index": index,
        "task_id": task.get("task_id"),
        "state": task.get("state"),
        "reason": reason,
    }


def resume_batch(
    contract: AssetContract,
    project_root: Path,
    client: Any,
    batch_journal: Union[str, os.PathLike],
    approved_credits: int,
    *,
    pricing_file: Optional[Path],
    reference_root: Path,
    reference_specs: object,
    output_license: str,
    today: object = None,
    date: object = None,
    clock: Optional[Callable[[], float]] = None,
    deadline: object = _DEFAULT_DEADLINE_SECONDS,
) -> Dict[str, Any]:
    """Resume only provider tasks already named by a validated batch journal.

    This function deliberately has no create-task seam.  A journal without a
    task id is an unresolved provider POST window, never permission to retry a
    request that may already have been accepted by Meshy.
    """

    if client is None or not all(hasattr(client, name) for name in ("get_balance", "poll_task", "download_bytes")):
        raise ValueError("a Meshy resume client must expose balance, polling, and download operations")
    if not isinstance(contract, AssetContract):
        raise TypeError("contract must be an AssetContract")
    account_lock_id = getattr(client, "account_lock_id", None)
    if not isinstance(account_lock_id, str):
        raise ValueError("Meshy client must expose an account lock id")

    root, _stage, _asset_root, journal_path, journal = _load_resume_journal(project_root, contract, batch_journal)
    approval = journal["approval"]
    preflight = _preflight(
        contract,
        root,
        client,
        approved_credits,
        pricing_file=pricing_file,
        reference_root=reference_root,
        reference_specs=reference_specs,
        output_license=output_license,
        today=today,
        date=date,
        clock=clock,
        deadline=deadline,
    )
    if not _approval_matches_preflight(preflight, journal):
        raise ValueError("Meshy batch journal approval does not match the recomputed request")
    if not _approval_creation_matches_evidence(preflight, journal):
        raise ValueError("Meshy batch journal creation time does not match staged evidence")
    if approval.get("approved_credits") != approved_credits:
        raise ValueError("Meshy approved credit ceiling does not match the original journal")

    with governance.credit_lock(account_lock_id, preflight.operation_deadline, preflight.clock):
        _refresh_before_provider(preflight)
        reloaded = load_batch_journal(journal_path)
        if reloaded != journal:
            raise ValueError("Meshy batch journal changed during resume preflight")
        if not _approval_matches_preflight(preflight, reloaded):
            raise ValueError("Meshy batch journal approval changed during resume preflight")
        if not _approval_creation_matches_evidence(preflight, reloaded):
            raise ValueError("Meshy batch journal creation time changed during resume preflight")
        journal = reloaded

        balance_checked = False
        tasks = [_copy_mapping(task) for task in journal["tasks"]]
        cumulative = journal["cumulative_consumed_credits"]
        resumed: List[str] = []
        skipped: List[str] = []
        reconciled: List[str] = []
        unresolved: List[Dict[str, Any]] = []
        unresolved_submission: List[Dict[str, Any]] = []
        errors: List[str] = []
        changed = False

        for index, task in enumerate(tasks):
            state = task["state"]
            task_id = task.get("task_id")
            if state == "SUCCEEDED":
                if not isinstance(task_id, str):
                    item = _unresolved_item(index, task, "succeeded task has no task id")
                    unresolved.append(item)
                    errors.append("Meshy succeeded journal task has no task id")
                    continue
                try:
                    load_generation_record(
                        preflight.asset_root / task_id / "generation.json",
                        journal_path=journal_path,
                    )
                except (OSError, TypeError, ValueError) as exc:
                    unresolved.append(_unresolved_item(index, task, "invalid_succeeded_evidence"))
                    errors.append(_safe_error(exc))
                else:
                    skipped.append(task_id)
                continue

            if state == "UNCERTAIN":
                if not isinstance(task_id, str):
                    unresolved.append(_unresolved_item(index, task, "uncertain task has no task id"))
                    errors.append("Meshy uncertain journal task has no task id")
                    continue
                try:
                    record = load_generation_record(
                        preflight.asset_root / task_id / "generation.json",
                        journal_path=journal_path,
                    )
                except (OSError, TypeError, ValueError) as exc:
                    unresolved.append(_unresolved_item(index, task, "uncertain_evidence_unresolved"))
                    errors.append(_safe_error(exc))
                    continue
                if record.get("status") != "SUCCEEDED":
                    unresolved.append(_unresolved_item(index, task, "uncertain_evidence_unresolved"))
                    errors.append("Meshy uncertain task has no bound succeeded generation")
                    continue
                tasks[index] = _task_entry(index, task_id, "SUCCEEDED", record["consumed_credits"], None)
                reconciled.append(task_id)
                changed = True
                continue

            if state in ("FAILED", "OVERRUN", "COLLISION"):
                unresolved.append(_unresolved_item(index, task, "terminal_manual"))
                continue

            if state == "SUBMITTING":
                reason = "unresolved_submission" if task_id is None else "submission_state_unresolved"
                item = _unresolved_item(index, task, reason)
                unresolved.append(item)
                if task_id is None:
                    unresolved_submission.append(item)
                continue

            if state != "PENDING":
                unresolved.append(_unresolved_item(index, task, "unsupported_resume_state"))
                errors.append("Meshy journal contains an unsupported resume state")
                continue

            if task_id is None:
                unresolved.append(_unresolved_item(index, task, "unsubmitted"))
                continue

            existing_task_dir = preflight.asset_root / task_id
            if os.path.lexists(str(existing_task_dir)):
                try:
                    governance.governed_task_path(
                        preflight.root,
                        existing_task_dir,
                        "Meshy existing task directory",
                        allow_missing=False,
                    )
                    record = load_generation_record(
                        existing_task_dir / "generation.json",
                        journal_path=journal_path,
                        allow_pending_reconciliation=True,
                    )
                except (OSError, TypeError, ValueError) as exc:
                    # An existing final name is never overwritten.  Invalid or
                    # mismatched evidence remains unresolved for an operator.
                    unresolved.append(_unresolved_item(index, task, "invalid_existing_evidence"))
                    errors.append(_safe_error(exc))
                    continue
                if record.get("status") not in ("SUCCEEDED", "FAILED"):
                    unresolved.append(_unresolved_item(index, task, "existing_evidence_not_terminal"))
                    errors.append("Meshy existing task evidence is non-terminal")
                    continue
                consumed = record.get("consumed_credits")
                if consumed is not None and cumulative + consumed > preflight.approved_credits:
                    unresolved.append(_unresolved_item(index, task, "existing_evidence_overrun"))
                    errors.append("Meshy existing task evidence exceeds the approved bound")
                    continue
                reconciled_state = "SUCCEEDED" if record["status"] == "SUCCEEDED" else (
                    "OVERRUN" if record.get("budget_violation") is True else "FAILED"
                )
                tasks[index] = _task_entry(
                    index,
                    task_id,
                    reconciled_state,
                    consumed,
                    None if reconciled_state == "SUCCEEDED" else record.get("error"),
                    budget_violation=reconciled_state == "OVERRUN",
                )
                if consumed is not None:
                    cumulative += consumed
                reconciled.append(task_id)
                changed = True
                _resume_journal_update(preflight, journal, tasks, cumulative)
                journal = load_batch_journal(journal_path)
                continue

            _check_deadline(preflight)
            _refresh_before_provider(preflight)
            if not balance_checked:
                try:
                    balance = _call_with_deadline(client.get_balance, preflight)
                except Exception as exc:
                    raise RuntimeError(_safe_error(exc)) from exc
                if not _valid_nonnegative_int(balance):
                    raise ValueError("Meshy balance response did not contain credits")
                balance_checked = True
            try:
                record = _stage_task(
                    client,
                    preflight,
                    journal["batch_id"],
                    index,
                    task_id,
                    journal["approval"]["created_at"],
                    cumulative,
                )
            except BaseException as exc:
                consumed = exc.consumed if isinstance(exc, _TaskFailure) else None
                if consumed is not None:
                    cumulative += consumed
                if isinstance(exc, _TaskCollision):
                    tasks[index] = _task_entry(index, None, "COLLISION", None, _safe_error(exc), collision_task_id=exc.collision_task_id)
                    unresolved.append(_unresolved_item(index, tasks[index], "collision"))
                elif isinstance(exc, _TaskFailure) and exc.publication_uncertain:
                    tasks[index] = _task_entry(index, task_id, "UNCERTAIN", consumed, _safe_error(exc))
                    unresolved.append(_unresolved_item(index, tasks[index], "publication_uncertain"))
                elif isinstance(exc, _TaskFailure) and exc.budget_violation:
                    tasks[index] = _task_entry(index, task_id, "OVERRUN", consumed, _safe_error(exc), budget_violation=True)
                    unresolved.append(_unresolved_item(index, tasks[index], "budget_overrun"))
                else:
                    tasks[index] = _task_entry(index, task_id, "FAILED", consumed, _safe_error(exc))
                    unresolved.append(_unresolved_item(index, tasks[index], "failed"))
                errors.append(_safe_error(exc))
                changed = True
                _resume_journal_update(preflight, journal, tasks, cumulative)
                journal = load_batch_journal(journal_path)
                continue

            consumed = record.get("consumed_credits")
            if not _valid_nonnegative_int(consumed):
                raise ValueError("Meshy resumed generation did not contain consumed credits")
            cumulative += consumed
            tasks[index] = _task_entry(index, task_id, "SUCCEEDED", consumed, None)
            resumed.append(task_id)
            changed = True
            _resume_journal_update(preflight, journal, tasks, cumulative)
            journal = load_batch_journal(journal_path)

        if changed:
            _resume_journal_update(preflight, journal, tasks, cumulative)
            journal = load_batch_journal(journal_path)

        final_state = journal["state"]
        passed = final_state == "COMPLETED" and not unresolved and not errors and all(
            task.get("state") == "SUCCEEDED" for task in journal["tasks"]
        )
        return {
            "asset_id": contract.asset_id,
            "batch_id": journal["batch_id"],
            "state": final_state,
            "pass": passed,
            "resumed": resumed,
            "skipped": skipped,
            "reconciled": reconciled,
            "unresolved": unresolved,
            "unresolved_submission": unresolved_submission,
            "errors": sorted(set(errors)),
            "consumed_credits": journal["cumulative_consumed_credits"],
        }



class _RecoveryManual(RuntimeError):
    """A bound provider task is terminal and needs operator intervention."""


def _pinned_generation_bytes(
    path: Path, identities: Mapping[Tuple[str, ...], Tuple[int, int]]
) -> bytes:
    parent_fd: Optional[int] = None
    descriptor: Optional[int] = None
    try:
        parent_fd = governance._open_pinned_parent(path, dict(identities), governance._pinned_directory_flags())
        descriptor = os.open(path.name, governance._read_leaf_flags(), dir_fd=parent_fd)
        opened = os.fstat(descriptor)
        governance._check_fd_identity(descriptor, identities.get(path.parts), "Meshy generation record")
        if not stat.S_ISREG(opened.st_mode):
            raise ValueError("Meshy generation record must be a regular file")
        return governance._read_descriptor(
            descriptor, "Meshy generation record", 4 * 1024 * 1024, opened
        )
    except FileNotFoundError as exc:
        raise ValueError("Meshy generation record changed during compare-and-swap") from exc
    except OSError as exc:
        raise ValueError("Meshy generation record identity changed during compare-and-swap") from exc
    finally:
        if descriptor is not None:
            os.close(descriptor)
        if parent_fd is not None:
            os.close(parent_fd)


def _cas_replace_generation(
    preflight: _Preflight,
    path: Path,
    identities: Mapping[Tuple[str, ...], Tuple[int, int]],
    failed_bytes: bytes,
    success_bytes: bytes,
) -> None:
    """Replace generation.json only while its pinned failed bytes still match."""

    parent_fd: Optional[int] = None
    temporary_fd: Optional[int] = None
    temporary_name: Optional[str] = None
    try:
        parent_fd = governance._open_pinned_parent(
            path, dict(identities), governance._pinned_directory_flags()
        )
        if _pinned_generation_bytes(path, identities) != failed_bytes:
            raise ValueError("Meshy generation compare-and-swap source changed")
        temporary_fd, temporary_name = governance._create_sibling_temp(
            parent_fd, path.name, 0o600
        )
        governance._write_all(temporary_fd, success_bytes)
        os.fsync(temporary_fd)
        os.close(temporary_fd)
        temporary_fd = None
        if _pinned_generation_bytes(path, identities) != failed_bytes:
            raise ValueError("Meshy generation compare-and-swap source changed")
        os.replace(
            temporary_name,
            path.name,
            src_dir_fd=parent_fd,
            dst_dir_fd=parent_fd,
        )
        temporary_name = None
        os.fsync(parent_fd)
    finally:
        if temporary_fd is not None:
            os.close(temporary_fd)
        if temporary_name is not None and parent_fd is not None:
            try:
                os.unlink(temporary_name, dir_fd=parent_fd)
            except FileNotFoundError:
                pass
        if parent_fd is not None:
            os.close(parent_fd)


def _validate_failed_archive(
    preflight: _Preflight,
    task_dir: Path,
    failed_document: Dict[str, Any],
    failed_bytes: bytes,
) -> Path:
    archive = task_dir / "generation.failed.json"
    governance.governed_task_path(
        preflight.root,
        archive,
        "Meshy generation.failed.json",
        allow_missing=True,
    )
    if not os.path.lexists(str(archive)):
        return archive
    try:
        info = os.lstat(archive)
    except OSError as exc:
        raise ValueError("Meshy generation.failed.json could not be inspected") from exc
    if stat.S_ISLNK(info.st_mode):
        raise ValueError("Meshy generation.failed.json must not be a symlink")
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("Meshy generation.failed.json must be a regular file")
    if stat.S_IMODE(info.st_mode) != 0o600:
        raise ValueError("Meshy generation.failed.json has the wrong mode")
    try:
        archived, archived_bytes = governance.strict_load_json_bytes(
            archive, "Meshy generation.failed.json", 4 * 1024 * 1024
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy generation.failed.json is malformed") from exc
    if archived_bytes != failed_bytes or archived != failed_document:
        raise ValueError("Meshy generation.failed.json does not preserve the failed record")
    errors = validate_generation_record(archived)
    if errors:
        raise ValueError("Meshy generation.failed.json is invalid")
    return archive



def _load_failed_archive_record(
    preflight: _Preflight,
    task_dir: Path,
    journal: Dict[str, Any],
    task_index: int,
    task_id: str,
) -> Tuple[Dict[str, Any], bytes]:
    archive = _artifact_path(preflight, task_dir, "generation.failed.json")
    if not os.path.lexists(str(archive)):
        raise ValueError("Meshy generation.failed.json is unavailable")
    try:
        info = os.lstat(archive)
    except OSError as exc:
        raise ValueError("Meshy generation.failed.json could not be inspected") from exc
    if stat.S_ISLNK(info.st_mode):
        raise ValueError("Meshy generation.failed.json must not be a symlink")
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("Meshy generation.failed.json must be a regular file")
    if stat.S_IMODE(info.st_mode) != 0o600:
        raise ValueError("Meshy generation.failed.json has the wrong mode")
    try:
        document, raw = governance.strict_load_json_bytes(
            archive, "Meshy generation.failed.json", 4 * 1024 * 1024
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy generation.failed.json is malformed") from exc
    if raw != canonical_json_bytes(document):
        raise ValueError("Meshy generation.failed.json is not canonical")
    errors = validate_generation_record(document)
    if errors or document.get("status") != "FAILED":
        raise ValueError("Meshy generation.failed.json is invalid")
    if (
        document.get("asset_id") != preflight.contract.asset_id
        or document.get("batch_id") != journal["batch_id"]
        or document.get("task_index") != task_index
        or document.get("task_id") != task_id
    ):
        raise ValueError("Meshy generation.failed.json identity does not match journal")
    _verify_generation_journal_binding(document, journal)
    task = journal["tasks"][task_index]
    if document.get("error") != task.get("error"):
        raise ValueError("Meshy generation.failed.json error does not match journal")
    return document, raw


def _require_generation_static_match(
    failed_document: Mapping[str, Any], succeeded_document: Mapping[str, Any]
) -> None:
    mutable = {"status", "completed_at", "consumed_credits", "outputs", "error", "budget_violation"}
    if set(failed_document) != set(succeeded_document):
        raise ValueError("Meshy generation archive fields do not match current generation")
    for name in failed_document:
        if name not in mutable and failed_document.get(name) != succeeded_document.get(name):
            raise ValueError("Meshy generation archive static fields do not match current generation")


def _load_succeeded_after_failed(
    preflight: _Preflight,
    journal_path: Path,
    journal: Dict[str, Any],
    task_index: int,
    task_id: str,
) -> Optional[Dict[str, Any]]:
    task_dir = preflight.asset_root / task_id
    generation_path = _artifact_path(preflight, task_dir, "generation.json")
    if not os.path.lexists(str(generation_path)):
        return None
    try:
        document, raw = governance.strict_load_json_bytes(
            generation_path, "Meshy generation record", 4 * 1024 * 1024
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy succeeded generation evidence is malformed") from exc
    if document.get("status") != "SUCCEEDED":
        return None
    if raw != canonical_json_bytes(document) or validate_generation_record(document):
        raise ValueError("Meshy succeeded generation evidence is not canonical")
    failed_document, failed_bytes = _load_failed_archive_record(
        preflight, task_dir, journal, task_index, task_id
    )
    _require_generation_static_match(failed_document, document)
    try:
        current = load_generation_record(
            generation_path,
            journal_path=journal_path,
            allow_failed_reconciliation=True,
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy succeeded generation evidence is invalid") from exc
    if current != document:
        raise ValueError("Meshy succeeded generation evidence changed during reconciliation")
    # Keep the exact archive bytes live through this validation boundary so a
    # concurrent replacement cannot be mistaken for the fixed failed record.
    _validate_failed_archive(preflight, task_dir, failed_document, failed_bytes)
    return current


def _validate_recovery_outputs(
    preflight: _Preflight,
    task_dir: Path,
    outputs: Mapping[str, bytes],
) -> List[str]:
    missing: List[str] = []
    for name, expected in outputs.items():
        output_path = _artifact_path(preflight, task_dir, name)
        if not os.path.lexists(str(output_path)):
            missing.append(name)
            continue
        try:
            info = os.lstat(output_path)
        except OSError as exc:
            raise ValueError("Meshy recovery output could not be inspected: " + name) from exc
        if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
            raise ValueError("Meshy recovery output must be a regular file: " + name)
        if stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError("Meshy recovery output has the wrong mode: " + name)
        try:
            observed = governance._read_bounded_regular_file(
                output_path,
                "Meshy recovery " + name,
                _GLB_MAX_BYTES if name == "raw.glb" else _THUMBNAIL_MAX_BYTES,
            )
        except (OSError, ValueError) as exc:
            raise ValueError("Meshy recovery output is invalid: " + name) from exc
        if observed != expected:
            raise ValueError("Meshy recovery output does not match fresh download: " + name)
    return missing


def _publish_recovery_outputs(
    preflight: _Preflight,
    task_dir: Path,
    outputs: Mapping[str, bytes],
    missing: Iterable[str],
) -> None:
    for name in missing:
        # Re-check a leaf that appeared after the initial all-leaves read;
        # never replace an existing artifact during recovery.
        if _validate_recovery_outputs(preflight, task_dir, {name: outputs[name]}):
            _create_artifact_bytes(preflight, task_dir, name, outputs[name])




def _load_pending_succeeded(
    preflight: _Preflight,
    journal_path: Path,
    task_id: str,
) -> Optional[Dict[str, Any]]:
    task_dir = preflight.asset_root / task_id
    if not os.path.lexists(str(task_dir)):
        return None
    governance.governed_task_path(
        preflight.root,
        task_dir,
        "Meshy persisted task directory",
        allow_missing=False,
    )
    try:
        record = load_generation_record(
            task_dir / "generation.json",
            journal_path=journal_path,
            allow_pending_reconciliation=True,
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy persisted task evidence is invalid") from exc
    if record.get("status") != "SUCCEEDED":
        raise ValueError("Meshy persisted task evidence is not succeeded")
    return record


def _load_failed_recovery_source(
    preflight: _Preflight,
    journal_path: Path,
    journal: Dict[str, Any],
    task_index: int,
    task_id: str,
) -> Tuple[Path, Dict[str, Any], bytes, Dict[Tuple[str, ...], Tuple[int, int]]]:
    task_dir = preflight.asset_root / task_id
    generation_path = _artifact_path(preflight, task_dir, "generation.json")
    try:
        document, raw = governance.strict_load_json_bytes(
            generation_path, "Meshy generation record", 4 * 1024 * 1024
        )
    except (OSError, TypeError, ValueError) as exc:
        raise ValueError("Meshy failed generation evidence is unavailable") from exc
    errors = validate_generation_record(document)
    if errors or raw != canonical_json_bytes(document):
        raise ValueError("Meshy failed generation evidence is not canonical")
    if (
        document.get("status") != "FAILED"
        or document.get("asset_id") != preflight.contract.asset_id
        or document.get("batch_id") != journal["batch_id"]
        or document.get("task_index") != task_index
        or document.get("task_id") != task_id
    ):
        raise ValueError("Meshy failed generation evidence identity does not match journal")
    _verify_generation_adjacent_artifacts(
        generation_path,
        document,
        journal_path,
        allow_failed_output_recovery=True,
    )
    identities = governance._snapshot_identities(
        generation_path, "Meshy failed generation record"
    )
    if identities.get(generation_path.parts) is None:
        raise ValueError("Meshy failed generation record identity is missing")
    return generation_path, document, raw, identities


def _continue_result(
    contract: AssetContract,
    journal: Dict[str, Any],
    recovered: List[str],
    continued: List[str],
    unresolved: List[Dict[str, Any]],
    unresolved_submission: List[Dict[str, Any]],
    errors: List[str],
) -> Dict[str, Any]:
    passed = journal["state"] == "COMPLETED" and not unresolved and not errors and all(
        task.get("state") == "SUCCEEDED" for task in journal["tasks"]
    )
    return {
        "asset_id": contract.asset_id,
        "batch_id": journal["batch_id"],
        "state": journal["state"],
        "pass": passed,
        "recovered": recovered,
        "continued": continued,
        "created": list(continued),
        "unresolved": unresolved,
        "unresolved_submission": unresolved_submission,
        "errors": sorted(set(errors)),
        "consumed_credits": journal["cumulative_consumed_credits"],
    }


def _recover_failed_task(
    client: Any,
    preflight: _Preflight,
    journal_path: Path,
    journal: Dict[str, Any],
    task_index: int,
    task: Mapping[str, Any],
) -> Dict[str, Any]:
    task_id = task.get("task_id")
    if not isinstance(task_id, str):
        raise ValueError("Meshy recovery task has no trusted task id")
    generation_path, failed_document, failed_bytes, identities = _load_failed_recovery_source(
        preflight, journal_path, journal, task_index, task_id
    )
    task_dir = generation_path.parent
    _validate_failed_archive(preflight, task_dir, failed_document, failed_bytes)
    old_consumed = task.get("consumed_credits")
    prior_cumulative = journal["cumulative_consumed_credits"]
    if _valid_nonnegative_int(old_consumed):
        prior_cumulative -= old_consumed
    if prior_cumulative < 0:
        raise ValueError("Meshy recovery journal cumulative credits are invalid")

    def note_consumed(value: int) -> None:
        if value > preflight.cost_per_candidate or prior_cumulative + value > preflight.approved_credits:
            raise _TaskFailure(
                "Meshy actual credit consumption exceeded the approved bound",
                value,
                budget_violation=True,
            )

    try:
        response = _poll_until_succeeded(client, preflight, task_id, note_consumed)
    except _TaskFailure as exc:
        if re.fullmatch(r"Meshy task ended with status [A-Z]+", str(exc)):
            raise _RecoveryManual(_safe_error(exc)) from exc
        raise
    consumed = response.get("consumed_credits")
    if not _valid_nonnegative_int(consumed):
        raise _TaskFailure("Meshy success response did not contain consumed_credits")
    model_urls = response.get("model_urls")
    glb_url = model_urls.get("glb") if isinstance(model_urls, dict) else None
    thumbnail_url = response.get("thumbnail_url")
    if not isinstance(glb_url, str) or not isinstance(thumbnail_url, str):
        raise _TaskFailure("Meshy success response did not contain required output URLs", consumed)
    try:
        MeshyClient._validate_download_url(glb_url)
        MeshyClient._validate_download_url(thumbnail_url)
    except RuntimeError as exc:
        raise _TaskFailure(_safe_error(exc), consumed) from exc
    if _pinned_generation_bytes(generation_path, identities) != failed_bytes:
        raise ValueError("Meshy generation compare-and-swap source changed")
    glb = _download_with_limit(client, glb_url, _GLB_MAX_BYTES, preflight)
    thumbnail = _download_with_limit(client, thumbnail_url, _THUMBNAIL_MAX_BYTES, preflight)
    try:
        _validate_glb(glb)
    except ValueError as exc:
        raise _TaskFailure(_safe_error(exc), consumed) from exc
    if len(thumbnail) <= len(_PNG_SIGNATURE) or not thumbnail.startswith(_PNG_SIGNATURE):
        raise _TaskFailure("thumbnail.png has an invalid PNG signature", consumed)
    if len(glb) + len(thumbnail) > _DOWNLOAD_TOTAL_MAX_BYTES:
        raise _TaskFailure("Meshy downloads exceed aggregate size", consumed)
    outputs = {"raw.glb": glb, "thumbnail.png": thumbnail}
    missing = _validate_recovery_outputs(preflight, task_dir, outputs)
    archive = _validate_failed_archive(preflight, task_dir, failed_document, failed_bytes)
    if not os.path.lexists(str(archive)):
        governance.atomic_create_bytes(
            archive,
            failed_bytes,
            project_root=preflight.root,
            allowed_root=preflight.asset_root,
            mode=0o600,
        )
    _validate_failed_archive(preflight, task_dir, failed_document, failed_bytes)
    if _pinned_generation_bytes(generation_path, identities) != failed_bytes:
        raise ValueError("Meshy generation compare-and-swap source changed")
    _publish_recovery_outputs(preflight, task_dir, outputs, missing)
    recovered = dict(failed_document)
    recovered.update(
        status="SUCCEEDED",
        completed_at=_utc_timestamp(),
        consumed_credits=consumed,
        outputs={
            "raw.glb": {"sha256": hashlib.sha256(glb).hexdigest(), "byte_size": len(glb)},
            "thumbnail.png": {
                "sha256": hashlib.sha256(thumbnail).hexdigest(),
                "byte_size": len(thumbnail),
            },
        },
        error=None,
        budget_violation=False,
    )
    errors = validate_generation_record(recovered)
    if errors:
        raise ValueError("invalid recovered Meshy generation record: " + "; ".join(errors))
    _cas_replace_generation(
        preflight,
        generation_path,
        identities,
        failed_bytes,
        canonical_json_bytes(recovered),
    )
    return recovered


def continue_batch(
    contract: AssetContract,
    project_root: Path,
    client: Any,
    batch_journal: Union[str, os.PathLike],
    approved_credits: int,
    *,
    pricing_file: Optional[Path],
    reference_root: Path,
    reference_specs: object,
    output_license: str,
    today: object = None,
    date: object = None,
    clock: Optional[Callable[[], float]] = None,
    deadline: object = _DEFAULT_DEADLINE_SECONDS,
) -> Dict[str, Any]:
    """Recover one bound failure, then create only its pending journal suffix."""

    required = ("get_balance", "poll_task", "download_bytes", "create_task")
    if client is None or not all(hasattr(client, name) for name in required):
        raise ValueError("a Meshy continue client must expose balance, polling, download, and create operations")
    if not isinstance(contract, AssetContract):
        raise TypeError("contract must be an AssetContract")
    account_lock_id = getattr(client, "account_lock_id", None)
    if not isinstance(account_lock_id, str):
        raise ValueError("Meshy client must expose an account lock id")
    root, _stage, _asset_root, journal_path, journal = _load_resume_journal(
        project_root, contract, batch_journal
    )
    approval = journal["approval"]
    preflight = _preflight(
        contract,
        root,
        client,
        approved_credits,
        pricing_file=pricing_file,
        reference_root=reference_root,
        reference_specs=reference_specs,
        output_license=output_license,
        today=today,
        date=date,
        clock=clock,
        deadline=deadline,
    )
    if not _approval_matches_preflight(preflight, journal):
        raise ValueError("Meshy batch journal approval does not match the recomputed request")
    if not _approval_creation_matches_evidence(preflight, journal):
        raise ValueError("Meshy batch journal creation time does not match staged evidence")
    if approval.get("approved_credits") != approved_credits:
        raise ValueError("Meshy approved credit ceiling does not match the original journal")

    with governance.credit_lock(account_lock_id, preflight.operation_deadline, preflight.clock):
        _refresh_before_provider(preflight)
        reloaded = load_batch_journal(journal_path)
        if reloaded != journal:
            raise ValueError("Meshy batch journal changed during continue preflight")
        if not _approval_matches_preflight(preflight, reloaded):
            raise ValueError("Meshy batch journal approval changed during continue preflight")
        if not _approval_creation_matches_evidence(preflight, reloaded):
            raise ValueError("Meshy batch journal creation time changed during continue preflight")
        journal = reloaded
        tasks = [_copy_mapping(task) for task in journal["tasks"]]
        first = next((index for index, task in enumerate(tasks) if task["state"] != "SUCCEEDED"), None)
        if first is None:
            for index, task in enumerate(tasks):
                task_id = task.get("task_id")
                if not isinstance(task_id, str):
                    raise ValueError("Meshy succeeded continuation task has no task id")
                load_generation_record(
                    preflight.asset_root / task_id / "generation.json",
                    journal_path=journal_path,
                )
            return _continue_result(contract, journal, [], [], [], [], [])
        for index in range(first):
            task_id = tasks[index].get("task_id")
            if tasks[index]["state"] != "SUCCEEDED" or not isinstance(task_id, str):
                raise ValueError("Meshy continuation prefix is not a trusted succeeded sequence")
            load_generation_record(
                preflight.asset_root / task_id / "generation.json",
                journal_path=journal_path,
            )
        null_suffix_started = False
        for index in range(first, len(tasks)):
            task = tasks[index]
            if index == first and task["state"] == "SUBMITTING":
                unresolved = [_unresolved_item(index, task, "unresolved_submission" if task.get("task_id") is None else "submission_state_unresolved")]
                return _continue_result(
                    contract,
                    journal,
                    [],
                    [],
                    unresolved,
                    unresolved if task.get("task_id") is None else [],
                    [],
                )
            if index == first and task["state"] == "FAILED":
                if not isinstance(task.get("task_id"), str):
                    raise ValueError("Meshy failed continuation task has no trusted task id")
                continue
            if task["state"] != "PENDING":
                raise ValueError("Meshy continuation suffix is not contiguous pending work")
            if task.get("task_id") is None:
                null_suffix_started = True
            elif null_suffix_started:
                raise ValueError("Meshy continuation suffix is not contiguous pending work")

        recovered: List[str] = []
        continued: List[str] = []
        unresolved: List[Dict[str, Any]] = []
        unresolved_submission: List[Dict[str, Any]] = []
        errors: List[str] = []
        cumulative = journal["cumulative_consumed_credits"]
        if tasks[first]["state"] == "FAILED":
            failed_task_id = tasks[first].get("task_id")
            if not isinstance(failed_task_id, str):
                raise ValueError("Meshy failed continuation task has no trusted task id")
            recovered_record = _load_succeeded_after_failed(
                preflight,
                journal_path,
                journal,
                first,
                failed_task_id,
            )
            if recovered_record is None:
                try:
                    recovered_record = _recover_failed_task(
                        client, preflight, journal_path, journal, first, tasks[first]
                    )
                except _RecoveryManual as exc:
                    item = _unresolved_item(first, tasks[first], "terminal_manual")
                    return _continue_result(contract, journal, [], [], [item], [], [_safe_error(exc)])
                except _TaskFailure as exc:
                    raise RuntimeError(_safe_error(exc)) from exc
            old_consumed = tasks[first].get("consumed_credits")
            if _valid_nonnegative_int(old_consumed):
                cumulative -= old_consumed
            new_consumed = recovered_record.get("consumed_credits")
            if not _valid_nonnegative_int(new_consumed):
                raise ValueError("Meshy recovered generation did not contain consumed credits")
            cumulative += new_consumed
            if cumulative < 0 or cumulative > preflight.approved_credits:
                raise ValueError("Meshy recovery journal cumulative credits are invalid")
            tasks[first] = _task_entry(first, failed_task_id, "SUCCEEDED", new_consumed, None)
            recovered.append(failed_task_id)
            _resume_journal_update(preflight, journal, tasks, cumulative)
            journal = load_batch_journal(journal_path)
            tasks = [_copy_mapping(task) for task in journal["tasks"]]
            cumulative = journal["cumulative_consumed_credits"]
            first += 1

        balance_checked = False
        for index in range(first, len(tasks)):
            task = tasks[index]
            if task["state"] != "PENDING":
                raise ValueError("Meshy continuation suffix changed before submission")
            persisted_task_id = task.get("task_id")
            if persisted_task_id is not None and not isinstance(persisted_task_id, str):
                raise ValueError("Meshy continuation task id is invalid")

            if persisted_task_id is not None:
                existing_record = _load_pending_succeeded(
                    preflight, journal_path, persisted_task_id
                )
                if existing_record is not None:
                    consumed = existing_record.get("consumed_credits")
                    if not _valid_nonnegative_int(consumed):
                        raise ValueError("Meshy persisted generation did not contain consumed credits")
                    old_consumed = task.get("consumed_credits")
                    if _valid_nonnegative_int(old_consumed):
                        cumulative -= old_consumed
                    if cumulative < 0 or cumulative + consumed > preflight.approved_credits:
                        raise ValueError("Meshy persisted generation exceeds the approved bound")
                    cumulative += consumed
                    tasks[index] = _task_entry(index, persisted_task_id, "SUCCEEDED", consumed, None)
                    _resume_journal_update(preflight, journal, tasks, cumulative)
                    journal = load_batch_journal(journal_path)
                    tasks = [_copy_mapping(item) for item in journal["tasks"]]
                    cumulative = journal["cumulative_consumed_credits"]
                    continue
                stage_task_id = persisted_task_id
            else:
                _check_deadline(preflight)
                _refresh_before_provider(preflight)
                if not balance_checked:
                    try:
                        balance = _call_with_deadline(client.get_balance, preflight)
                    except Exception as exc:
                        raise RuntimeError(_safe_error(exc)) from exc
                    if not _valid_nonnegative_int(balance):
                        raise ValueError("Meshy balance response did not contain credits")
                    balance_checked = True
                tasks[index] = _task_entry(index, None, "SUBMITTING", None, None)
                _resume_journal_update(preflight, journal, tasks, cumulative)
                journal = load_batch_journal(journal_path)
                try:
                    stage_task_id = _safe_task_id(
                        _call_with_deadline(
                            client.create_task,
                            preflight,
                            preflight.endpoint,
                            preflight.transient.payload,
                        )
                    )
                except Exception as exc:
                    unresolved_item = _unresolved_item(index, tasks[index], "unresolved_submission")
                    unresolved.append(unresolved_item)
                    unresolved_submission.append(unresolved_item)
                    errors.append(_safe_error(exc))
                    return _continue_result(
                        contract, journal, recovered, continued, unresolved, unresolved_submission, errors
                    )
                existing_ids = {
                    item.get("task_id") for item in tasks if isinstance(item.get("task_id"), str)
                }
                task_dir = preflight.asset_root / stage_task_id
                if stage_task_id in existing_ids or os.path.lexists(str(task_dir)):
                    tasks[index] = _task_entry(
                        index,
                        None,
                        "COLLISION",
                        None,
                        "Meshy task id collision",
                        collision_task_id=stage_task_id,
                    )
                    _resume_journal_update(preflight, journal, tasks, cumulative)
                    journal = load_batch_journal(journal_path)
                    item = _unresolved_item(index, tasks[index], "collision")
                    return _continue_result(
                        contract, journal, recovered, continued, [item], [], ["Meshy task id collision"]
                    )
                tasks[index] = _task_entry(index, stage_task_id, "PENDING", None, None)
                _resume_journal_update(preflight, journal, tasks, cumulative)
                journal = load_batch_journal(journal_path)

            _check_deadline(preflight)
            _refresh_before_provider(preflight)
            if not balance_checked:
                try:
                    balance = _call_with_deadline(client.get_balance, preflight)
                except Exception as exc:
                    raise RuntimeError(_safe_error(exc)) from exc
                if not _valid_nonnegative_int(balance):
                    raise ValueError("Meshy balance response did not contain credits")
                balance_checked = True
            try:
                record = _stage_task(
                    client,
                    preflight,
                    journal["batch_id"],
                    index,
                    stage_task_id,
                    journal["approval"]["created_at"],
                    cumulative,
                )
            except BaseException as exc:
                consumed = exc.consumed if isinstance(exc, _TaskFailure) else None
                if _valid_nonnegative_int(consumed):
                    cumulative += consumed
                if isinstance(exc, _TaskCollision):
                    tasks[index] = _task_entry(index, None, "COLLISION", None, _safe_error(exc), collision_task_id=exc.collision_task_id)
                    reason = "collision"
                elif isinstance(exc, _TaskFailure) and exc.budget_violation:
                    tasks[index] = _task_entry(index, stage_task_id, "OVERRUN", consumed, _safe_error(exc), budget_violation=True)
                    reason = "budget_overrun"
                elif isinstance(exc, _TaskFailure) and exc.publication_uncertain:
                    tasks[index] = _task_entry(index, stage_task_id, "UNCERTAIN", consumed, _safe_error(exc))
                    reason = "publication_uncertain"
                else:
                    tasks[index] = _task_entry(index, stage_task_id, "FAILED", consumed, _safe_error(exc))
                    reason = "failed"
                item = _unresolved_item(index, tasks[index], reason)
                _resume_journal_update(preflight, journal, tasks, cumulative)
                journal = load_batch_journal(journal_path)
                return _continue_result(contract, journal, recovered, continued, [item], [], [_safe_error(exc)])
            consumed = record.get("consumed_credits")
            if not _valid_nonnegative_int(consumed):
                raise ValueError("Meshy continued generation did not contain consumed credits")
            cumulative += consumed
            if cumulative > preflight.approved_credits:
                raise ValueError("Meshy continued generation exceeds the approved bound")
            tasks[index] = _task_entry(index, stage_task_id, "SUCCEEDED", consumed, None)
            continued.append(stage_task_id)
            _resume_journal_update(preflight, journal, tasks, cumulative)
            journal = load_batch_journal(journal_path)
            tasks = [_copy_mapping(item) for item in journal["tasks"]]
            cumulative = journal["cumulative_consumed_credits"]
        return _continue_result(contract, journal, recovered, continued, unresolved, unresolved_submission, errors)
def _visible_task_directories(
    root: Path,
    asset_root: Path,
    journal_task_ids: set,
    errors: List[str],
) -> Dict[str, Path]:
    """Inventory visible task roots, excluding journals and hidden temp files."""

    result: Dict[str, Path] = {}
    if not asset_root.exists():
        errors.append("Meshy task root is missing")
        return result
    if asset_root.is_symlink() or not asset_root.is_dir():
        errors.append("Meshy task root is not a regular directory")
        return result
    try:
        children = sorted(asset_root.iterdir(), key=lambda item: item.name)
    except OSError:
        errors.append("Meshy task root could not be enumerated")
        return result
    for child in children:
        if child.name == "_batches":
            if child.is_symlink():
                errors.append("Meshy batch journal root contains a symlink")
            elif not child.is_dir():
                errors.append("Meshy batch journal root is not a regular directory")
            continue
        if child.name.startswith("."):
            errors.append("Meshy task root contains a hidden entry")
            continue
        if child.is_symlink():
            errors.append("Meshy task root contains a symlink entry")
            continue
        if not child.is_dir():
            errors.append("Meshy task root contains a non-directory entry")
            continue
        try:
            governance.governed_task_path(root, child, "Meshy task directory", allow_missing=False)
        except (OSError, ValueError):
            errors.append("Meshy task directory is outside governed staging")
            continue
        if child.name not in journal_task_ids:
            errors.append("Meshy task directory is not named by the batch journal")
            continue
        result[child.name] = child
    return result


def _staged_reference_inputs(
    task_dir: Path,
    references: List[Dict[str, Any]],
) -> ReferenceInputs:
    records: List[ReferenceInput] = []
    for reference in references:
        suffix = Path(reference["basename"]).suffix.lower()
        source = task_dir / ("source_{0}{1}".format(reference["view"], suffix))
        payload = governance._read_bounded_regular_file(source, "Meshy staged source", _REFERENCE_FILE_MAX_BYTES)
        records.append(
            ReferenceInput(
                reference["view"],
                reference["basename"],
                reference["media_type"],
                reference["byte_size"],
                reference["sha256"],
                payload,
            )
        )
    return ReferenceInputs(records)


def _pricing_record_from_document(document: Dict[str, Any], raw: bytes) -> PricingRecord:
    return PricingRecord(
        document["pricing_id"],
        document["checked_at"],
        document["expires_at"],
        document["source_url"],
        hashlib.sha256(raw).hexdigest(),
        canonical_json_bytes(document),
    )


def _verify_current_approval(
    root: Path,
    contract: AssetContract,
    journal: Dict[str, Any],
    task_dirs: Mapping[str, Path],
    errors: List[str],
    pricing_file: Optional[Union[str, os.PathLike]],
) -> None:
    """Recompute approval fields from current sources and staged evidence."""

    approval = journal["approval"]
    try:
        current_contract = load_contract(contract.path)
        if current_contract.sha256 != contract.sha256 or current_contract.snapshot_bytes() != contract.snapshot_bytes():
            errors.append("asset contract changed since approval")
        packet = render_prompt_packet(current_contract)
        prompt_hash = hashlib.sha256(canonical_json_bytes(packet)).hexdigest()
        if approval["prompt_profile_id"] != packet["prompt_profile_id"] or approval["prompt_profile_sha256"] != packet["prompt_profile_sha256"]:
            errors.append("prompt profile changed since approval")
        if approval["prompt_packet_sha256"] != prompt_hash:
            errors.append("prompt packet changed since approval")
        if approval["contract_sha256"] != current_contract.sha256:
            errors.append("contract hash does not match current contract")

        generation = current_contract._snapshot_document().get("generation", {})
        expected_endpoint = ENDPOINTS.get(generation.get("mode"))
        if approval["endpoint"] != expected_endpoint:
            errors.append("provider endpoint changed since approval")
        expected_count = generation.get("candidate_count")
        if approval["candidate_count"] != expected_count:
            errors.append("candidate count changed since approval")

        pricing_document: Optional[Dict[str, Any]] = None
        pricing_raw: Optional[bytes] = None
        pricing_source = Path(pricing_file) if pricing_file is not None else DEFAULT_PRICING_PATH
        try:
            pricing_document, pricing_raw = governance.strict_load_json_bytes(
                pricing_source,
                "Meshy pricing",
                _PRICING_MAX_BYTES,
            )
            _validate_pricing_document(pricing_document)
        except (OSError, TypeError, ValueError) as exc:
            pricing_document = None
            pricing_raw = None
            if pricing_file is None:
                errors.append("Meshy checked-in default pricing could not be read")
            else:
                errors.append("Meshy supplied pricing file could not be read")
        else:
            pricing_hash = hashlib.sha256(pricing_raw).hexdigest()
            if pricing_file is None:
                if approval["pricing_sha256"] != pricing_hash:
                    errors.append("non-default pricing requires --pricing-file")
            elif approval["pricing_sha256"] != pricing_hash:
                errors.append("pricing hash does not match supplied pricing")
            if approval["pricing_id"] != pricing_document["pricing_id"]:
                errors.append("pricing id does not match supplied pricing")

        source_task_dir = next(iter(task_dirs.values()), None)
        if source_task_dir is not None:
            staged_pricing, _staged_raw = _read_adjacent_json(source_task_dir / "pricing.json", "pricing.json")
            try:
                _validate_pricing_document(staged_pricing)
            except (TypeError, ValueError):
                errors.append("staged pricing document is invalid")
            else:
                if approval["pricing_id"] != staged_pricing.get("pricing_id"):
                    errors.append("pricing id does not match approval")
                if pricing_document is not None and staged_pricing != pricing_document:
                    errors.append("supplied pricing does not match staged pricing artifact")

        if pricing_document is not None:
            pricing = _pricing_record_from_document(pricing_document, pricing_raw or canonical_json_bytes(pricing_document))
            expected_cost = pricing.cost_for(current_contract)
            if approval["cost_per_candidate"] != expected_cost:
                errors.append("provider cost changed since approval")
            if approval["maximum_credits"] != expected_count * expected_cost:
                errors.append("maximum credits changed since approval")


        if source_task_dir is not None:
            staged_references = _staged_reference_inputs(source_task_dir, approval["references"])
            if list(staged_references.metadata) != approval["references"]:
                errors.append("reference evidence changed since approval")
            transient = build_transient_provider_request(current_contract, staged_references)
            if approval["provider_payload_sha256"] != transient.provider_payload_sha256:
                errors.append("provider payload changed since approval")
            if approval["request"] != transient.redacted_request:
                errors.append("provider request changed since approval")
        else:
            if journal["state"] == "COMPLETED":
                errors.append("provider payload cannot be recomputed without staged reference evidence")
    except (OSError, TypeError, ValueError) as exc:
        errors.append(_safe_error(exc))


def verify_batch(
    project_root: Path,
    contract: AssetContract,
    batch_journal: Union[str, os.PathLike],
    pricing_file: Optional[Union[str, os.PathLike]] = None,
) -> Dict[str, Any]:
    """Verify a Meshy batch entirely from local journal and staged evidence."""

    if not isinstance(contract, AssetContract):
        raise TypeError("contract must be an AssetContract")
    root, _stage, asset_root, journal_path, journal = _load_resume_journal(project_root, contract, batch_journal)
    errors: List[str] = []
    unresolved: List[Dict[str, Any]] = []
    verified_ids: List[str] = []
    journal_task_ids = {task["task_id"] for task in journal["tasks"] if task.get("task_id") is not None}
    task_dirs = _visible_task_directories(root, asset_root, journal_task_ids, errors)
    for task_id in sorted(task_dirs):
        if task_id not in journal_task_ids:
            errors.append("Meshy task directory is not named by the batch journal")

    _verify_current_approval(root, contract, journal, task_dirs, errors, pricing_file)

    for index, task in enumerate(journal["tasks"]):
        state = task["state"]
        task_id = task.get("task_id")
        task_dir = task_dirs.get(task_id) if isinstance(task_id, str) else None
        if state == "SUCCEEDED":
            if task_dir is None:
                unresolved.append(_unresolved_item(index, task, "missing_succeeded_evidence"))
                errors.append("Meshy succeeded task directory is missing")
                continue
            try:
                record = load_generation_record(task_dir / "generation.json", journal_path=journal_path)
                if record.get("status") != "SUCCEEDED":
                    raise ValueError("Meshy succeeded task record is not succeeded")
            except (OSError, TypeError, ValueError) as exc:
                unresolved.append(_unresolved_item(index, task, "invalid_succeeded_evidence"))
                errors.append(_safe_error(exc))
            else:
                verified_ids.append(task_id)
            continue

        if state == "FAILED":
            if task_id is None:
                if task.get("error") not in {
                    "Meshy operation failed",
                    "insufficient Meshy balance for remaining reserved maximum",
                    "Meshy balance response did not contain credits",
                }:
                    errors.append("Meshy journal-only failure is not a documented early static failure")
                if task_dir is not None:
                    errors.append("Meshy journal-only failure has an unexpected task directory")
            elif task_dir is None:
                unresolved.append(_unresolved_item(index, task, "missing_failed_evidence"))
                errors.append("Meshy failed task directory is missing")
            else:
                try:
                    record = load_generation_record(task_dir / "generation.json", journal_path=journal_path)
                    if record.get("status") != "FAILED" or record.get("budget_violation") is not False:
                        raise ValueError("Meshy failed task record is invalid")
                except (OSError, TypeError, ValueError) as exc:
                    unresolved.append(_unresolved_item(index, task, "invalid_failed_evidence"))
                    errors.append(_safe_error(exc))
                else:
                    verified_ids.append(task_id)
            continue

        if state == "OVERRUN":
            if task_dir is None:
                unresolved.append(_unresolved_item(index, task, "missing_overrun_evidence"))
                errors.append("Meshy overrun task directory is missing")
            else:
                try:
                    record = load_generation_record(task_dir / "generation.json", journal_path=journal_path)
                    if record.get("status") != "FAILED" or record.get("budget_violation") is not True:
                        raise ValueError("Meshy overrun task record is invalid")
                except (OSError, TypeError, ValueError) as exc:
                    unresolved.append(_unresolved_item(index, task, "invalid_overrun_evidence"))
                    errors.append(_safe_error(exc))
                else:
                    verified_ids.append(task_id)
            continue

        if state == "UNCERTAIN":
            if task_dir is not None:
                try:
                    record = load_generation_record(task_dir / "generation.json", journal_path=journal_path)
                    if record.get("status") == "SUCCEEDED":
                        verified_ids.append(task_id)
                    else:
                        raise ValueError("Meshy uncertain task has no succeeded generation")
                except (OSError, TypeError, ValueError) as exc:
                    errors.append(_safe_error(exc))
            unresolved.append(_unresolved_item(index, task, "uncertain"))
            continue

        if state == "COLLISION":
            if task_dir is not None:
                errors.append("Meshy collision task has a final directory")
            unresolved.append(_unresolved_item(index, task, "collision"))
            continue

        if state in ("PENDING", "SUBMITTING"):
            reason = "unresolved_submission" if state == "SUBMITTING" and task_id is None else "unresolved"
            if task_dir is not None:
                errors.append("Meshy unresolved task has a final directory")
            unresolved.append(_unresolved_item(index, task, reason))
            continue

        errors.append("Meshy journal contains an unsupported task state")
        unresolved.append(_unresolved_item(index, task, "unsupported"))

    protected = governance.snapshot_protected_surfaces(
        root,
        max_file_bytes=_REPOSITORY_SNAPSHOT_MAX_FILE_BYTES,
        max_total_bytes=_REPOSITORY_SNAPSHOT_MAX_TOTAL_BYTES,
        max_entries=_REPOSITORY_SNAPSHOT_MAX_ENTRIES,
        max_depth=_REPOSITORY_SNAPSHOT_MAX_DEPTH,
    )
    expected_protected = [
        {"type": item.type, "path": item.path, "sha256": item.sha256, "size": item.size}
        for item in protected
    ]
    if expected_protected != journal["approval"]["protected_snapshot"]:
        errors.append("protected runtime surfaces changed since approval")

    errors = sorted(set(errors))
    passed = (
        journal["state"] == "COMPLETED"
        and all(task.get("state") == "SUCCEEDED" for task in journal["tasks"])
        and not errors
        and not unresolved
    )
    return {
        "asset_id": contract.asset_id,
        "batch_id": journal["batch_id"],
        "terminal_state": journal["state"],
        "pass": passed,
        "verified_ids": verified_ids,
        "unresolved": unresolved,
        "errors": errors,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    plan = subparsers.add_parser("plan", help="build a read-only Meshy request plan")
    plan.add_argument("--project-root", type=Path, required=True)
    plan.add_argument("--contract", type=Path, required=True)
    plan.add_argument("--pricing-file", type=Path, default=None)
    plan.add_argument("--reference-root", type=Path, default=None)
    plan.add_argument("--reference", action="append", default=None, metavar="VIEW=FILENAME")
    generate = subparsers.add_parser("generate", help="create and stage a new Meshy batch")
    generate.add_argument("--project-root", type=Path, required=True)
    generate.add_argument("--contract", type=Path, required=True)
    generate.add_argument("--approved-credits", type=int, required=True)
    generate.add_argument("--pricing-file", type=Path, default=None, required=False)
    generate.add_argument("--reference-root", type=Path, required=True)
    generate.add_argument("--reference", action="append", required=True, metavar="VIEW=FILENAME")
    generate.add_argument("--output-license", choices=_ALLOWED_LICENSES, required=True)
    generate.add_argument("--deadline-seconds", type=float, default=_DEFAULT_DEADLINE_SECONDS)
    resume = subparsers.add_parser(
        "resume",
        help="resume existing Meshy task ids without creating tasks",
        description="Resume existing Meshy task ids without creating provider tasks or retrying an unknown POST.",
    )
    resume.add_argument("--project-root", type=Path, required=True)
    resume.add_argument("--contract", type=Path, required=True)
    resume.add_argument("--batch-journal", type=Path, required=True)
    resume.add_argument("--approved-credits", type=int, required=True)
    resume.add_argument("--pricing-file", type=Path, default=None, required=False)
    resume.add_argument("--reference-root", type=Path, required=True)
    resume.add_argument("--reference", action="append", required=True, metavar="VIEW=FILENAME")
    resume.add_argument("--output-license", choices=_ALLOWED_LICENSES, required=True)
    resume.add_argument("--deadline-seconds", type=float, default=_DEFAULT_DEADLINE_SECONDS)
    continuation = subparsers.add_parser(
        "continue",
        help="recover one bound Meshy failure and continue its pending suffix",
        description="Recover a bound failed Meshy task by safe GETs, then create only its contiguous pending journal suffix.",
    )
    continuation.add_argument("--project-root", type=Path, required=True)
    continuation.add_argument("--contract", type=Path, required=True)
    continuation.add_argument("--batch-journal", type=Path, required=True)
    continuation.add_argument("--approved-credits", type=int, required=True)
    continuation.add_argument("--pricing-file", type=Path, default=None, required=False)
    continuation.add_argument("--reference-root", type=Path, required=True)
    continuation.add_argument("--reference", action="append", required=True, metavar="VIEW=FILENAME")
    continuation.add_argument("--output-license", choices=_ALLOWED_LICENSES, required=True)
    continuation.add_argument("--deadline-seconds", type=float, default=_DEFAULT_DEADLINE_SECONDS)
    verify = subparsers.add_parser(
        "verify",
        help="verify a Meshy batch offline with no API client",
        description="Verify a Meshy batch offline from its journal and staged artifacts; no API key is used.",
    )
    verify.add_argument("--project-root", type=Path, required=True)
    verify.add_argument("--contract", type=Path, required=True)
    verify.add_argument("--batch-journal", type=Path, required=True)
    verify.add_argument("--pricing-file", type=Path, default=None)
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        contract = load_contract(args.contract)
        if args.command == "plan":
            result = plan_generation(contract, args.project_root, pricing_file=args.pricing_file, reference_root=args.reference_root, reference_specs=args.reference)
        elif args.command == "generate":
            if args.approved_credits <= 0:
                raise ValueError("approved credit ceiling must be positive")
            result = generate_batch(contract, args.project_root, MeshyClient(), args.approved_credits, pricing_file=args.pricing_file, reference_root=args.reference_root, reference_specs=args.reference, output_license=args.output_license, deadline=args.deadline_seconds)
        elif args.command == "resume":
            if args.approved_credits <= 0:
                raise ValueError("approved credit ceiling must be positive")
            result = resume_batch(contract, args.project_root, MeshyClient(), args.batch_journal, args.approved_credits, pricing_file=args.pricing_file, reference_root=args.reference_root, reference_specs=args.reference, output_license=args.output_license, deadline=args.deadline_seconds)
        elif args.command == "continue":
            if args.approved_credits <= 0:
                raise ValueError("approved credit ceiling must be positive")
            result = continue_batch(contract, args.project_root, MeshyClient(), args.batch_journal, args.approved_credits, pricing_file=args.pricing_file, reference_root=args.reference_root, reference_specs=args.reference, output_license=args.output_license, deadline=args.deadline_seconds)
        else:
            result = verify_batch(args.project_root, contract, args.batch_journal, pricing_file=args.pricing_file)
    except (OSError, RuntimeError, TypeError, ValueError) as exc:
        print("error: {0}".format(_safe_error(exc)), file=sys.stderr)
        return 1
    print(json.dumps(result, ensure_ascii=False, sort_keys=True, indent=2))
    if args.command == "resume":
        if result.get("pass"):
            print("MESHY RESUME PASS batch={0}".format(result["batch_id"]))
            return 0
        print("MESHY RESUME UNRESOLVED batch={0}".format(result["batch_id"]))
        return 1
    if args.command == "continue":
        if result.get("pass"):
            print("MESHY CONTINUE PASS batch={0}".format(result["batch_id"]))
            return 0
        print("MESHY CONTINUE UNRESOLVED batch={0}".format(result["batch_id"]))
        return 1
    if args.command == "verify":
        if result.get("pass"):
            print("MESHY VERIFY PASS batch={0}".format(result["batch_id"]))
            return 0
        print("MESHY VERIFY FAIL batch={0}".format(result["batch_id"]))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "DEFAULT_PRICING_PATH", "ENDPOINTS", "MeshyClient", "PricingRecord", "ReferenceInput", "ReferenceInputs", "TransientProviderRequest",
    "build_transient_provider_request", "generate_batch", "resume_batch", "continue_batch", "verify_batch", "load_batch_journal", "load_generation_record", "load_pricing", "plan_generation", "resolve_reference_inputs", "validate_batch_journal", "validate_generation_record",
]
