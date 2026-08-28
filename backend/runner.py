from __future__ import annotations

import json
import sys
import threading
from pathlib import Path
from typing import Any

from backend.protocol import PROTOCOL_VERSION, decode, encode, error, response
from backend.service import WorkflowService


_write_lock = threading.Lock()


def _log_error(message: str) -> None:
    encoded = (message + "\n").encode("utf-8", errors="backslashreplace")
    stream = getattr(sys.stderr, "buffer", None)
    if stream is not None:
        stream.write(encoded)
        stream.flush()
    else:
        sys.stderr.write(encoded.decode("ascii"))
        sys.stderr.flush()


def _write(message: dict[str, Any]) -> None:
    with _write_lock:
        data = (encode(message) + "\n").encode("ascii")
        stream = getattr(sys.stdout, "buffer", None)
        if stream is not None:
            stream.write(data)
            stream.flush()
        else:
            sys.stdout.write(data.decode("ascii"))
            sys.stdout.flush()


def handle(service: WorkflowService, request: dict[str, Any]) -> dict[str, Any]:
    request_id = request.get("id")
    command = request["command"]
    payload = request.get("payload") or {}
    if not isinstance(payload, dict):
        raise ValueError("request.payload must be an object")
    if command == "hello":
        return response(request_id, {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": ["state", "members", "screenshots"],
        })
    if command == "get_state":
        return response(request_id, service.snapshot())
    if command == "set_config":
        return response(request_id, service.set_config(payload))
    if command == "load_members":
        return response(request_id, service.load_members(
            str(payload.get("sourceType", "")),
            str(payload.get("source", "")),
            str(payload.get("sheetName", "Members")),
        ))
    if command == "add_screenshots":
        paths = payload.get("paths")
        if not isinstance(paths, list) or not all(isinstance(path, str) for path in paths):
            raise ValueError("payload.paths must be an array of strings")
        return response(request_id, service.add_screenshots(paths))
    if command == "clear_screenshots":
        return response(request_id, service.clear_screenshots())
    if command == "extract":
        operation_id = str(payload.get("operationId", request_id or "extract"))
        service.start_extraction(operation_id, _write)
        return response(request_id, {"operationId": operation_id, "started": True})
    if command == "cancel":
        service.cancel_extraction()
        return response(request_id, {"cancelRequested": True})
    if command == "assign_observation":
        observation_id = str(payload.get("observationId", ""))
        member_id = int(payload["memberId"])
        return response(request_id, service.assign_observation(
            observation_id, member_id, bool(payload.get("rememberAlias", True))
        ))
    if command == "export":
        output_path = str(payload.get("outputPath", ""))
        return response(request_id, service.export(output_path))
    raise KeyError(f"Unknown command: {command}")


def main() -> int:
    service = WorkflowService(Path.home() / ".lastwar_weekly_extractor")
    for line in sys.stdin:
        if not line.strip():
            continue
        request_id = None
        try:
            request = decode(line)
            request_id = request.get("id")
            _write(handle(service, request))
        except json.JSONDecodeError:
            _write(error(request_id, "invalid_json", "Request is not valid JSON."))
        except KeyError as exc:
            _write(error(request_id, "unknown_command", str(exc)))
        except Exception as exc:
            _log_error(f"backend error: {exc}")
            _write(error(request_id, "backend_error", str(exc)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())