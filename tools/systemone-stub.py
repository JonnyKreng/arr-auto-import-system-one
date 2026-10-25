#!/usr/bin/env python3
"""Tiny System One protocol stub for local UI/plumbing tests.

This is NOT the Mica model. It answers choice questions with the first offered option so the
ArrImportSystemOne pipeline and UI can be exercised on machines without a GPU (including Docker
Desktop on macOS). Run it only through compose.local.yaml and keep LIDARR_DRY_RUN=true.
"""

import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL = "systemone-stub"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, code, obj):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        if code != 200:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.rstrip("/") in ("/health", "/v1/models"):
            self._send(200, {"status": "ok", "model": MODEL, "warming": False})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path.rstrip("/") not in ("/v1/systemone", "/v1/decide"):
            self._send(404, {"error": "not found"})
            return

        try:
            length = int(self.headers.get("Content-Length", 0))
            body = json.loads(self.rfile.read(length) or b"{}")
            questions = body.get("questions") or {}
            if not isinstance(questions, dict):
                raise ValueError("questions must be an object")

            answers = {}
            for name, question in questions.items():
                qtype = question.get("type", "choice")
                criteria = question.get("criteria")

                if qtype == "choice":
                    if not isinstance(criteria, dict) or not criteria:
                        raise ValueError("choice question needs criteria")
                    keys = list(criteria.keys())
                    choice = next((key for key in keys if key != "no_match"), keys[0])
                    answers[name] = {
                        "type": "choice",
                        "choice": choice,
                        "probabilities": {key: 1.0 if key == choice else 0.0 for key in keys},
                        "confidence": 0.99,
                    }
                elif qtype == "noul":
                    answers[name] = {"type": "noul", "noul": True}
                elif qtype == "score":
                    if not isinstance(criteria, list) or not criteria:
                        raise ValueError("score question needs criteria")
                    answers[name] = {
                        "type": "score",
                        "score": 0,
                        "level": 0,
                        "expected_level": 0,
                        "probabilities": {str(i): 1.0 if i == 0 else 0.0 for i in range(len(criteria))},
                        "legend": {str(i): value for i, value in enumerate(criteria)},
                        "confidence": 0.99,
                        "margin": 0.99,
                    }
                else:
                    raise ValueError(f"unsupported question type {qtype!r}")

            self._send(
                200,
                {
                    "model": MODEL,
                    "answers": answers,
                    "usage": {"input_tokens": 0, "output_tokens": 0},
                    "latency_ms": 0,
                },
            )
        except Exception as exc:  # noqa: BLE001 - protocol stub
            self._send(400, {"error": str(exc)})

    def log_message(self, fmt, *args):
        print(fmt % args, flush=True)


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "8010"))
    print(f"Serving System One stub on 0.0.0.0:{port} (not a real model)", flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
