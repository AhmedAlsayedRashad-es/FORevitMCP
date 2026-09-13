"""A fake pyRevit Routes server for testing the MCP server without Revit.

It answers the same routes as pyrevit/FirstOptionMCP.extension/startup.py:
  GET  /fo-mcp/status/
  POST /fo-mcp/execute/          runs the code with CPython (doc is None)
  POST /fo-mcp/execute-csharp/   always "runner not loaded"

Usage: python tests/mock_routes_server.py [port]
"""
import contextlib
import io
import json
import os
import sys
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 48884


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _send(self, code, data):
        body = json.dumps(data).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.rstrip("/") == "/fo-mcp/status":
            return self._send(200, {
                "bridge": "mock",
                "pid": os.getpid(),
                "revitVersion": "2026",
                "revitBuild": "mock",
                "document": "Mock.rvt",
                "pyrevit": "mock",
                "python": sys.version.split()[0],
                "csharpRunner": False,
            })
        self._send(404, {"error": "no route"})

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        content_type = self.headers.get("Content-Type")
        payload = json.loads(raw or b"{}")
        path = self.path.rstrip("/")

        if path == "/fo-mcp/execute":
            out = io.StringIO()
            scope = {"doc": None, "uidoc": None, "args": payload.get("args") or {}, "result": None}
            ok, error, tb = True, None, None
            started = time.time()
            try:
                with contextlib.redirect_stdout(out):
                    exec(compile(payload.get("code", ""), "<fo-mcp>", "exec"), scope)
            except Exception as ex:
                ok, error, tb = False, "{}: {}".format(type(ex).__name__, ex), traceback.format_exc()
            result = scope.get("result")
            try:
                json.dumps(result)
            except TypeError:
                result = repr(result)
            return self._send(200, {
                "ok": ok,
                "output": out.getvalue(),
                "result": result,
                "error": error,
                "traceback": tb,
                "durationMs": int((time.time() - started) * 1000),
                "document": "Mock.rvt",
                "contentType": content_type,
                "transactionName": payload.get("transaction_name"),
            })

        if path == "/fo-mcp/execute-csharp":
            return self._send(200, {"ok": False, "error": "The mock has no C# runner."})

        self._send(404, {"error": "no route"})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
