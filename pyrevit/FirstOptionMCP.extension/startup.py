# -*- coding: utf-8 -*-
"""FirstOption MCP bridge.

pyRevit runs this file when it loads the extension. It registers a pyRevit
Routes API named "fo-mcp":

    GET  http://127.0.0.1:<port>/fo-mcp/status/          no Revit context
    POST http://127.0.0.1:<port>/fo-mcp/execute/         Python, Revit main thread
    POST http://127.0.0.1:<port>/fo-mcp/execute-csharp/  C#, through the FirstOption add-in
    POST http://127.0.0.1:<port>/fo-mcp/undo-history/    the Revit undo list as the add-in sees it
    POST http://127.0.0.1:<port>/fo-mcp/undo-baseline/   mark the current state
    POST http://127.0.0.1:<port>/fo-mcp/undo/            plan or start an undo of agent runs
    POST http://127.0.0.1:<port>/fo-mcp/undo-status/     progress and check of the running undo
    POST http://127.0.0.1:<port>/fo-mcp/reset/           reopen the last saved file, discard changes

Every run is wrapped in a TransactionGroup (the add-in's RunScope), so one run is one entry in the Revit undo list.

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

BRIDGE_VERSION = "0.2.0"
MAX_OUTPUT = 50000
RUNNER_ASSEMBLY = "FirstOption.RevitMcp.Addin"
RUNNER_TYPE = "FirstOption.RevitMcp.Addin.CSharp.CSharpRunner"
RUN_SCOPE_TYPE = "FirstOption.RevitMcp.Addin.Undo.RunScope"
UNDO_API_TYPE = "FirstOption.RevitMcp.Addin.Undo.UndoApi"

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


def _find_addin_type(type_name):
    for asm in System.AppDomain.CurrentDomain.GetAssemblies():
        try:
            if asm.GetName().Name == RUNNER_ASSEMBLY:
                found = asm.GetType(type_name)
                if found is not None:
                    return found
        except Exception:
            continue
    return None


def _find_runner():
    return _find_addin_type(RUNNER_TYPE)


def _begin_run(uiapp, doc, name, undo_group):
    """One run = one entry in the Revit undo list. The add-in also records the run in its undo journal."""
    if doc is None:
        return None
    scope_type = _find_addin_type(RUN_SCOPE_TYPE)
    if scope_type is not None:
        args = System.Array[System.Object]([uiapp, name, "ironpython", bool(undo_group)])
        return ("addin", scope_type.GetMethod("Begin").Invoke(None, args))
    if undo_group and not doc.IsModifiable:
        group = DB.TransactionGroup(doc, name)
        group.Start()
        return ("plain", group)
    return None


def _end_run(run, ok):
    """Returns (extra response fields, error or None)."""
    if run is None:
        return {}, None
    kind, obj = run
    if kind == "addin":
        obj.End(bool(ok))
        return json.loads(str(obj.DescribeJson())), (str(obj.Error) if obj.Error else None)
    try:
        if obj.HasStarted() and not obj.HasEnded():
            if ok:
                obj.Assimilate()
            else:
                obj.RollBack()
        return {"undoNotes": ["The FirstOption add-in is not loaded: the run is one undo entry, but it is not tracked."]}, None
    except Exception as ex:
        _safe(lambda: obj.RollBack())
        return {}, "The undo group did not close ({}). The run was rolled back.".format(ex)
    finally:
        _safe(lambda: obj.Dispose())


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
        "undoJournal": _find_addin_type(UNDO_API_TYPE) is not None,
    })


@api.route("/execute/", methods=["POST"])
def execute(request, uiapp):
    payload = _payload(request)
    code = payload.get("code") or ""
    use_tx = payload.get("use_transaction", True)
    tx_name = payload.get("transaction_name") or "FirstOption MCP"
    undo_group = payload.get("undo_group", True)
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
    ok, error, tb, tx, run = True, None, None, None, None
    try:
        sys.stdout, sys.stderr = out, out
        compiled = compile(_prepare(code), "<fo-mcp>", "exec")
        run = _begin_run(uiapp, doc, tx_name, undo_group)
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

    run_info, run_error = {}, None
    try:
        run_info, run_error = _end_run(run, ok)
    except Exception as ex:
        run_error = "The undo group did not close: {}".format(ex)
    if run_error and ok:
        ok, error = False, run_error

    data = {
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
    }
    data.update(run_info or {})
    return routes.make_response(data=data)


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


def _undo_api(method_name, request, uiapp):
    api_type = _find_addin_type(UNDO_API_TYPE)
    if api_type is None:
        return routes.make_response(data={
            "ok": False,
            "error": "The FirstOption MCP Revit add-in with undo tracking is not loaded in this Revit.",
        })
    try:
        args = System.Array[System.Object]([uiapp, json.dumps(_payload(request))])
        text = api_type.GetMethod(method_name).Invoke(None, args)
        return routes.make_response(data=json.loads(str(text)))
    except Exception as ex:
        return routes.make_response(data={
            "ok": False,
            "error": "{}: {}".format(type(ex).__name__, ex),
            "traceback": traceback.format_exc(),
        })


@api.route("/undo-history/", methods=["POST"])
def undo_history(request, uiapp):
    return _undo_api("HistoryJson", request, uiapp)


@api.route("/undo-baseline/", methods=["POST"])
def undo_baseline(request, uiapp):
    return _undo_api("BaselineJson", request, uiapp)


@api.route("/undo/", methods=["POST"])
def undo(request, uiapp):
    return _undo_api("UndoJson", request, uiapp)


@api.route("/undo-status/", methods=["POST"])
def undo_status(request, uiapp):
    return _undo_api("StatusJson", request, uiapp)


@api.route("/reset/", methods=["POST"])
def reset(request, uiapp):
    return _undo_api("ResetJson", request, uiapp)
