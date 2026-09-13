"""End-to-end test of the MCP server over stdio, against tests/mock_routes_server.py.

It uses a temporary home (FO_REVIT_MCP_HOME), a temporary library, and a local bare git repo
as the "GitHub" remote, so it does not touch your real settings.

Usage:
  dotnet build src/Server -c Release
  python tests/smoke_mcp.py
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / "src/Server/bin/Release/net8.0-windows/win-x64/FirstOption.RevitMcp.exe"
PORT = 48990

failures = []


def check(name, condition, detail=""):
    print(("PASS " if condition else "FAIL ") + name + ("" if condition else "  -> " + str(detail)[:600]))
    if not condition:
        failures.append(name)


def main():
    home = tempfile.mkdtemp(prefix="fo-mcp-test-")
    library = os.path.join(home, "library")
    bare = os.path.join(home, "remote.git")
    subprocess.run(["git", "init", "--bare", bare], check=True, capture_output=True)
    with open(os.path.join(home, "settings.json"), "w") as f:
        json.dump({
            "libraryPath": library, "routesHost": "127.0.0.1", "portStart": PORT, "portCount": 2,
            "githubOwner": "test", "githubRepo": "library", "branch": "main", "remoteUrl": bare,
            "autoPush": False, "notifyOnPush": True, "authorName": "Smoke Test", "authorEmail": "smoke@test.local",
        }, f)

    mock = subprocess.Popen([sys.executable, str(ROOT / "tests/mock_routes_server.py"), str(PORT)])
    time.sleep(1.0)
    env = dict(os.environ, FO_REVIT_MCP_HOME=home)
    server = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=(open(os.environ["FO_SMOKE_LOG"], "w") if os.environ.get("FO_SMOKE_LOG") else subprocess.DEVNULL),
                              env=env, text=True, encoding="utf-8")
    next_id = [0]

    def send(message):
        server.stdin.write(json.dumps(message) + "\n")
        server.stdin.flush()

    def request(method, params):
        next_id[0] += 1
        send({"jsonrpc": "2.0", "id": next_id[0], "method": method, "params": params})
        while True:
            line = server.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout")
            message = json.loads(line)
            if message.get("id") == next_id[0]:
                return message

    def call(name, arguments):
        response = request("tools/call", {"name": name, "arguments": arguments})
        if "error" in response:
            return {"ok": False, "rpcError": response["error"]}
        return json.loads(response["result"]["content"][0]["text"])

    try:
        init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                      "clientInfo": {"name": "claude-code-smoke", "version": "1.0"}})
        check("initialize", init.get("result", {}).get("serverInfo", {}).get("name") == "firstoption-revit", init)
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        tools = {t["name"] for t in request("tools/list", {})["result"]["tools"]}
        expected = {"revit_instances", "revit_status", "revit_execute_python", "revit_execute_csharp", "library_search",
                    "library_get", "library_save", "library_run", "library_info", "github_status", "github_push"}
        check("tools/list has all tools", expected <= tools, sorted(tools))

        r = call("revit_instances", {})
        check("revit_instances finds the mock", r.get("count") == 1 and r["instances"][0]["port"] == PORT, r)

        r = call("revit_status", {})
        check("revit_status lists languages", "csharp" in r.get("languages", {}), r)

        r = call("revit_execute_python", {"code": "print('hello ' + str(args['a']))\nresult = 6 * 7", "args_json": "{\"a\": 5}"})
        check("execute python ok", r.get("ok") is True and "hello 5" in r.get("output", "") and r.get("result") == 42, r)
        check("content type is exactly application/json", r.get("contentType") == "application/json", r.get("contentType"))

        r = call("revit_execute_python", {"code": "1/0"})
        check("execute python error comes back", r.get("ok") is False and "ZeroDivisionError" in (r.get("error") or ""), r)

        r = call("revit_execute_python", {"code": "x", "args_json": "[1,2]"})
        check("bad args_json is refused", r.get("ok") is False and "object" in r.get("error", ""), r)

        r = call("revit_execute_csharp", {"code": "return 1;"})
        check("csharp without runner is refused", r.get("ok") is False and "C# runner" in r.get("error", ""), r)

        r = call("library_save", {"name": "hello_world", "description": "Prints hello and the args.", "language": "ironpython",
                                  "code": "print('hello')\nresult = args.get('n', 0) + 1", "tags": ["Demo", "hello"],
                                  "inputs": "args: n (int)"})
        check("library_save python", r.get("ok") is True and r.get("saved") == "hello_world", r)

        r = call("library_save", {"name": "hello_world", "description": "x", "language": "ironpython", "code": "print(1)"})
        check("library_save refuses duplicate", r.get("ok") is False and "exists" in r.get("error", ""), r)

        r = call("library_save", {"name": "Bad Name", "description": "x", "language": "ironpython", "code": "print(1)"})
        check("library_save refuses bad name", r.get("ok") is False, r)

        cs_code = "using System.IO;\nvar n = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();\nConsole.WriteLine(n);\nreturn n;"
        r = call("library_save", {"name": "count_walls", "description": "Counts the walls in the model.", "language": "csharp",
                                  "code": cs_code, "tags": ["walls"]})
        check("library_save csharp", r.get("ok") is True, r)

        r = call("library_search", {"query": "hello"})
        check("library_search finds hello_world", r.get("count") == 1 and r["results"][0]["name"] == "hello_world", r)

        r = call("library_search", {"language": "csharp"})
        check("library_search by language", r.get("count") == 1 and r["results"][0]["name"] == "count_walls", r)

        r = call("library_get", {"name": "hello_world"})
        check("library_get returns code", "print('hello')" in r.get("code", ""), r)

        r = call("library_run", {"name": "hello_world", "args_json": "{\"n\": 41}"})
        check("library_run ok", r.get("ok") is True and r.get("result") == 42, r)
        check("library_run names the transaction", "hello_world" in (r.get("transactionName") or ""), r)

        panel = pathlib.Path(library) / "FirstOptionLibrary.extension" / "FO Library.tab" / "Commands.panel"
        py = panel / "hello_world.pushbutton"
        cs = panel / "count_walls.pushbutton"
        check("python button files", all((py / f).exists() for f in ["script.py", "body.py", "command.json", "bundle.yaml"]), list(py.iterdir()) if py.exists() else "missing")
        check("csharp button files", (cs / "script.cs").exists() and (cs / "body.cs").exists(), list(cs.iterdir()) if cs.exists() else "missing")
        script_cs = (cs / "script.cs").read_text(encoding="utf-8") if (cs / "script.cs").exists() else ""
        check("script.cs has IExternalCommand and the using", "IExternalCommand" in script_cs and "using System.IO;" in script_cs, script_cs[:400])
        meta = json.loads((py / "command.json").read_text(encoding="utf-8"))
        check("run count recorded", meta.get("runs") == 1 and "2026" in meta.get("testedRevitVersions", []), meta)
        check("index.json and README.md", (pathlib.Path(library) / "index.json").exists() and (pathlib.Path(library) / "README.md").exists())

        r = call("github_status", {})
        check("github_status configured", r.get("configured") is True and r.get("gitRepo") is False, r)

        r = call("github_push", {"message": "Add hello_world and count_walls"})
        check("github_push to bare remote", r.get("ok") is True and r.get("commit") and r.get("files", 0) > 0, r)
        log = subprocess.run(["git", "--git-dir", bare, "log", "--oneline", "main"], capture_output=True, text=True)
        check("remote has the commit", "Add hello_world" in log.stdout, log.stdout + log.stderr)

        r = call("github_push", {"message": "again"})
        check("second push says nothing new", r.get("ok") is True and r.get("nothingToPush") is True, r)

        # auto-push on save
        settings_path = os.path.join(home, "settings.json")
        s = json.load(open(settings_path))
        s["autoPush"] = True
        json.dump(s, open(settings_path, "w"))
        r = call("library_save", {"name": "hello_world", "description": "Prints hello (v2).", "language": "ironpython",
                                  "code": "print('hello v2')", "overwrite": True})
        check("auto-push after save", r.get("ok") is True and (r.get("push") or {}).get("ok") is True, r)

        lines = [json.loads(x) for x in open(os.path.join(home, "activity.jsonl"), encoding="utf-8") if x.strip()]
        kinds = [x["kind"] for x in lines]
        check("activity log has executes, saves, pushes",
              kinds.count("execute") == 3 and kinds.count("library_save") == 3 and kinds.count("github_push") == 2, kinds)
        check("activity client is claude", all(x.get("client") == "claude" for x in lines), {x.get("client") for x in lines})
    finally:
        server.kill()
        mock.kill()
        shutil.rmtree(home, ignore_errors=True)

    print("\n" + ("ALL PASSED" if not failures else "FAILED: " + ", ".join(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
