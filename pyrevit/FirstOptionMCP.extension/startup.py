# -*- coding: utf-8 -*-
"""FirstOption MCP bridge.

pyRevit runs this file when it loads the extension. It registers a pyRevit
Routes API named "fo-mcp":

    GET  http://127.0.0.1:<port>/fo-mcp/status/          no Revit context
    POST http://127.0.0.1:<port>/fo-mcp/execute/         Python, Revit main thread
    POST http://127.0.0.1:<port>/fo-mcp/execute-csharp/  C#, through the FirstOption add-in

The FirstOption Revit MCP server (FirstOption.RevitMcp.exe) calls these routes.
Keep this file Python 2.7 and Python 3 compatible (no f-strings).
"""
import sys
import json
import time
import re
import traceback

from pyrevit import routes
from pyrevit import HOST_APP

import System
from Autodesk.Revit import DB, UI

try:
    from StringIO import StringIO  # IronPython 2.7: accepts str and unicode
except ImportError:
    from io import StringIO

BRIDGE_VERSION = "0.1.0"
MAX_OUTPUT = 50000
RUNNER_ASSEMBLY = "FirstOption.RevitMcp.Addin"
RUNNER_TYPE = "FirstOption.RevitMcp.Addin.CSharp.CSharpRunner"

api = routes.API("fo-mcp")


def _safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def _payload(request):
    data = request.data
    if data is None:
        return {}
    if isinstance(data, dict):
        return data
    try:
        if not isinstance(data, str):
            data = data.decode("utf-8")
    except Exception:
        pass
    try:
        return json.loads(data)
    except Exception:
        return {}


def _pyrevit_version():
    def get():
        from pyrevit import versionmgr
        return versionmgr.get_pyrevit_version().get_formatted()
    return _safe(get)


def _doc_title():
    def get():
        uidoc = HOST_APP.uiapp.ActiveUIDocument
        return uidoc.Document.Title if uidoc else None
    return _safe(get)


def _find_runner():
    for asm in System.AppDomain.CurrentDomain.GetAssemblies():
        try:
            if asm.GetName().Name == RUNNER_ASSEMBLY:
                runner = asm.GetType(RUNNER_TYPE)
                if runner is not None:
                    return runner
        except Exception:
            continue
    return None


def _element_id(eid):
    value = _safe(lambda: eid.Value)
    return value if value is not None else _safe(lambda: eid.IntegerValue)


def _jsonable(value, depth=0):
    if value is None:
        return None
    try:
        json.dumps(value)
        return value
    except Exception:
        pass
    if depth > 3:
        return str(value)
    if isinstance(value, dict):
        return dict((str(k), _jsonable(v, depth + 1)) for k, v in value.items())
    if isinstance(value, (list, tuple, set)):
        return [_jsonable(v, depth + 1) for v in list(value)[:500]]
    if isinstance(value, DB.ElementId):
        return _element_id(value)
    if isinstance(value, DB.XYZ):
        return [value.X, value.Y, value.Z]
    if isinstance(value, DB.Element):
        return {
            "id": _element_id(value.Id),
            "name": _safe(lambda: value.Name),
            "category": _safe(lambda: value.Category.Name),
            "class": value.GetType().Name,
        }
    if isinstance(value, System.Collections.IEnumerable) and not isinstance(value, System.String):
        items = []
        for item in value:
            if len(items) >= 500:
                break
            items.append(_jsonable(item, depth + 1))
        return items
    return str(value)


_CODING_LINE = re.compile(r"^[ \t\f]*#.*?coding[:=]")


def _prepare(code):
    # Python 2 refuses an encoding line inside a unicode string.
    lines = code.replace("\r\n", "\n").split("\n")
    for i in range(min(2, len(lines))):
        if _CODING_LINE.match(lines[i]):
            lines[i] = ""
    return "\n".join(lines)


def _cut(text):
    if text and len(text) > MAX_OUTPUT:
        return text[:MAX_OUTPUT] + "\n... (cut)"
    return text


@api.route("/status/", methods=["GET"])
def status(request):
    return routes.make_response(data={
        "bridge": BRIDGE_VERSION,
        "pid": System.Diagnostics.Process.GetCurrentProcess().Id,
        "revitVersion": _safe(lambda: str(HOST_APP.version)),
        "revitBuild": _safe(lambda: str(HOST_APP.build)),
        "username": _safe(lambda: HOST_APP.username),
        "document": _doc_title(),
        "pyrevit": _pyrevit_version(),
        "python": sys.version.split("\n")[0],
        "csharpRunner": _find_runner() is not None,
    })


@api.route("/execute/", methods=["POST"])
def execute(request, uiapp):
    payload = _payload(request)
    code = payload.get("code") or ""
    use_tx = payload.get("use_transaction", True)
    tx_name = payload.get("transaction_name") or "FirstOption MCP"
    args = payload.get("args") or {}

    uidoc = uiapp.ActiveUIDocument
    doc = uidoc.Document if uidoc else None
    scope = {
        "__name__": "__fo_mcp__",
        "__revit__": uiapp,
        "uiapp": uiapp,
        "uidoc": uidoc,
        "doc": doc,
        "app": uiapp.Application,
        "DB": DB,
        "UI": UI,
        "args": args,
        "result": None,
    }

    out = StringIO()
    old_out, old_err = sys.stdout, sys.stderr
    started = time.time()
    ok, error, tb, tx = True, None, None, None
    try:
        sys.stdout, sys.stderr = out, out
        compiled = compile(_prepare(code), "<fo-mcp>", "exec")
        if use_tx and doc is not None and not doc.IsModifiable:
            tx = DB.Transaction(doc, tx_name)
            tx.Start()
        exec(compiled, scope)
        if tx is not None:
            tx_status = tx.Commit()
            if tx_status != DB.TransactionStatus.Committed:
                ok, error = False, "The transaction ended with status " + str(tx_status)
    except Exception as ex:
        ok = False
        error = "{}: {}".format(type(ex).__name__, ex)
        tb = traceback.format_exc()
        if tx is not None and _safe(lambda: tx.HasStarted() and not tx.HasEnded(), False):
            _safe(lambda: tx.RollBack())
    finally:
        sys.stdout, sys.stderr = old_out, old_err
        if tx is not None:
            _safe(lambda: tx.Dispose())

    return routes.make_response(data={
        "ok": ok,
        "language": "ironpython" if "IronPython" in sys.version else "python",
        "output": _cut(out.getvalue()),
        "result": _safe(lambda: _jsonable(scope.get("result")), None),
        "error": error,
        "traceback": _cut(tb),
        "durationMs": int((time.time() - started) * 1000),
        "document": _safe(lambda: doc.Title) if doc is not None else None,
        "revitVersion": _safe(lambda: str(HOST_APP.version)),
        "transaction": tx_name if tx is not None else None,
    })


@api.route("/execute-csharp/", methods=["POST"])
def execute_csharp(request, uiapp):
    payload = _payload(request)
    runner = _find_runner()
    if runner is None:
        return routes.make_response(data={
            "ok": False,
            "language": "csharp",
            "error": "The FirstOption MCP Revit add-in is not loaded in this Revit. C# needs the add-in.",
        })
    try:
        method = runner.GetMethod("RunJson")
        text = method.Invoke(None, System.Array[System.Object]([uiapp, json.dumps(payload)]))
        data = json.loads(str(text))
        data["revitVersion"] = _safe(lambda: str(HOST_APP.version))
        return routes.make_response(data=data)
    except Exception as ex:
        return routes.make_response(data={
            "ok": False,
            "language": "csharp",
            "error": "{}: {}".format(type(ex).__name__, ex),
            "traceback": traceback.format_exc(),
        })
