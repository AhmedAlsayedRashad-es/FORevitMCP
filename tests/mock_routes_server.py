"""A fake pyRevit Routes server for testing the MCP server without Revit.

It answers the same routes as pyrevit/FirstOptionMCP.extension/startup.py:
  GET  /fo-mcp/status/
  POST /fo-mcp/execute/          runs the code with CPython (doc is None)
  POST /fo-mcp/execute-csharp/   always "runner not loaded"
  POST /fo-mcp/undo-history/, /undo-baseline/, /undo/, /undo-status/, /reset/   a small fake undo journal

Test-only route:
  POST /fo-mcp/mock-user-change/ adds a change "by the user" to the fake undo list

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

ENTRIES = []        # bottom first: {"seq", "name", "source", "runId", "state"}
STATE = {"seq": 0, "run": 0, "baseline": None, "job": None}
AGENT, USER = "agent", "user or other add-in"


def push(name, source, run_id=None):
    ENTRIES[:] = [e for e in ENTRIES if e["state"] == "done"]
    STATE["seq"] += 1
    ENTRIES.append({"seq": STATE["seq"], "name": name, "source": source, "runId": run_id, "state": "done"})


def done_top_down():
    return [e for e in reversed(ENTRIES) if e["state"] == "done"]


def refuse(error, hint):
    return {"ok": False, "error": error, "hint": hint}


def undo(payload):
    if STATE["job"] and STATE["job"]["state"] == "running":
        return refuse("An undo is already running.", "")
    done = done_top_down()
    if payload.get("toBaseline"):
        if STATE["baseline"] is None:
            return refuse("There is no baseline.", "Call revit_baseline.")
        count = len([e for e in done if e["seq"] > STATE["baseline"]])
    elif payload.get("toRunId"):
        indexes = [i for i, e in enumerate(done) if e["runId"] == payload["toRunId"]]
        if not indexes:
            return refuse("Run " + payload["toRunId"] + " is not in the undo list.", "")
        count = indexes[-1] + 1
    else:
        runs = int(payload.get("runs") or 1)
        seen = []
        for e in done:
            if e["source"] == AGENT and e["runId"] not in seen:
                seen.append(e["runId"])
                if len(seen) == runs:
                    break
        if len(seen) < runs:
            return refuse("The undo list has only {} agent run(s).".format(len(seen)), "")
        count = max(i for i, e in enumerate(done) if e["runId"] == seen[-1]) + 1
    if count == 0:
        return {"ok": True, "nothingToUndo": True, "message": "Nothing to undo."}
    targets = done[:count]
    plan = [dict(e) for e in targets]
    instructions = "Select '{}' in the Undo drop-down.".format(targets[-1]["name"])
    others = [e for e in targets if e["source"] != AGENT]
    if others and not payload.get("includeUserChanges"):
        r = refuse("Undo would also remove {} change(s) that are not from the agent.".format(len(others)), "Ask the user.")
        r["plan"] = plan
        return r
    result = {"ok": True, "document": "Mock.rvt", "plan": plan, "instructions": instructions}
    if not payload.get("execute", True):
        result["mode"] = "manual"
        return result
    STATE["job"] = {"state": "running", "targets": targets, "polls": 0, "instructions": instructions}
    result["mode"] = "auto"
    result["state"] = "running"
    return result


def undo_status():
    job = STATE["job"]
    if job is None:
        return {"ok": True, "state": "none"}
    job["polls"] += 1
    if job["state"] == "running" and job["polls"] >= 2:
        for e in job["targets"]:
            e["state"] = "undone"
        job["state"] = "done"
    summary = {"ok": True, "state": job["state"], "document": "Mock.rvt", "undoSteps": [e["name"] for e in job["targets"]] if job["state"] == "done" else []}
    if job["state"] == "done":
        summary["verification"] = {"ok": True, "entries": [{"name": e["name"], "ok": True, "checkedIds": 1} for e in job["targets"]]}
    return summary


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
                "undoJournal": True,
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
            STATE["run"] += 1
            run_id = "run-{}".format(STATE["run"])
            undo_name = "{} #{}".format(payload.get("transaction_name") or "FirstOption MCP", STATE["run"])
            if ok:
                push(undo_name, AGENT, run_id)
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
                "undoGroup": payload.get("undo_group"),
                "runId": run_id,
                "undoName": undo_name if ok else None,
            })

        if path == "/fo-mcp/execute-csharp":
            return self._send(200, {"ok": False, "error": "The mock has no C# runner."})

        if path == "/fo-mcp/mock-user-change":
            push(payload.get("name") or "Wall", USER)
            return self._send(200, {"ok": True})

        if path == "/fo-mcp/undo-history":
            entries = list(reversed(ENTRIES))[: int(payload.get("limit") or 30)]
            baseline = None if STATE["baseline"] is None else {"entriesAfter": len([e for e in done_top_down() if e["seq"] > STATE["baseline"]])}
            return self._send(200, {"ok": True, "document": "Mock.rvt", "entries": entries, "total": len(ENTRIES), "baseline": baseline})

        if path == "/fo-mcp/undo-baseline":
            done = done_top_down()
            STATE["baseline"] = done[0]["seq"] if done else 0
            return self._send(200, {"ok": True, "document": "Mock.rvt"})

        if path == "/fo-mcp/undo":
            return self._send(200, undo(payload))

        if path == "/fo-mcp/undo-status":
            return self._send(200, undo_status())

        if path == "/fo-mcp/reset":
            ENTRIES[:] = []
            STATE["baseline"] = None
            return self._send(200, {"ok": True, "reopened": "C:\\mock\\Mock.rvt", "document": "Mock.rvt"})

        self._send(404, {"error": "no route"})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
