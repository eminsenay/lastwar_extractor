from __future__ import annotations

import json
from typing import Any


PROTOCOL_VERSION = 1


def response(request_id: Any, payload: Any) -> dict[str, Any]:
    return {"id": request_id, "type": "response", "payload": payload}


def error(request_id: Any, code: str, message: str) -> dict[str, Any]:
    return {"id": request_id, "type": "error", "error": {"code": code, "message": message}}


def encode(message: dict[str, Any]) -> str:
    return json.dumps(message, ensure_ascii=True, separators=(",", ":"))


def decode(line: str) -> dict[str, Any]:
    value = json.loads(line)
    if not isinstance(value, dict):
        raise ValueError("request must be a JSON object")
    if not isinstance(value.get("command"), str):
        raise ValueError("request.command must be a string")
    return value
