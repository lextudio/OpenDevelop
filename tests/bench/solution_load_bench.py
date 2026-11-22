#!/usr/bin/env python3
"""Solution-load benchmark for OpenDevelop.

Each run starts a fresh OpenDevelop (isolated -configdir, DevFlow on), opens a solution through
DevFlow `od.open-solution`, and records:

  open_call_ms      wall time until the solution is open (current solution set and published)
  ui_*              (open window only) a probe thread calls a trivial UI-thread action (`od.solution.status`) every
                    PROBE_INTERVAL; its latency is the UI thread's responsiveness while loading.
                    ui_max_stall_ms = longest single wait, ui_blocked_ms = summed time the UI
                    thread was unresponsive for more than STALL_THRESHOLD. DevFlow gives up on a
                    UI-thread call after ~10 s, so a fully frozen UI shows as ~10 s samples.
  milestones        PerfTimeline marks of the solution-open scope (ms since the open began),
                    e.g. projects-evaluated, solution-opened-handlers-done, roslyn-*.
  ready_ms          first appearance of the readiness milestone (--ready-mark, default:
                    the Roslyn bulk push finishing), polled until --timeout.
  rss / heap        working set and managed heap after the load (from od.perf.timeline).

Usage:
  tests/bench/solution_load_bench.py ~/src/aspire/Aspire.slnx --runs 3 [--cold] [--out result.json]

  --cold     delete the solution's .od/ caches (Roslyn snapshots, reference cache) before each run
"""
import argparse, json, os, shutil, signal, statistics, subprocess, sys, tempfile, threading, time
import urllib.request, urllib.error

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_PROJECT = os.path.join(REPO, "src", "Main", "SharpDevelop", "SharpDevelop.csproj")
PORT = int(os.environ.get("DEVFLOW_AGENT_PORT", "9299"))
BASE = f"http://localhost:{PORT}/api/v1/invoke/actions/"
PROBE_INTERVAL = 0.05
STALL_THRESHOLD_MS = 100


def invoke(action, *args, timeout=3600):
    req = urllib.request.Request(BASE + action, data=json.dumps({"args": list(args)}).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        body = json.loads(r.read().decode())
    # DevFlow wraps the action's JSON string in {"success":..,"returnValue":"<json>"}
    res = body.get("returnValue", body.get("result", body)) if isinstance(body, dict) else body
    if isinstance(res, str):
        try:
            res = json.loads(res)
        except ValueError:
            pass
    return body, res


def dotnet_env():
    env = dict(os.environ)
    root = env.get("DOTNET_ROOT") or os.path.expanduser("~/.dotnet")
    env.update(DOTNET_ROOT=root, DOTNET_HOST_PATH=os.path.join(root, "dotnet"),
               PATH=root + os.pathsep + env.get("PATH", ""), OD_TEST_MODE="1",
               LANG="en_US.UTF-8", LC_ALL="en_US.UTF-8", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    return env


def start_app(configdir, log):
    env = dotnet_env()
    env["OD_TEST_ILSPY_SETTINGS_FILE"] = os.path.join(configdir, "ILSpy.xml")
    env["OD_TEST_PROFILER_STORE"] = os.path.join(configdir, "profiling")
    cmd = [os.path.join(env["DOTNET_ROOT"], "dotnet"), "run", "--project", APP_PROJECT,
           "-f", "net11.0-windows", "--no-build", "--", "-configdir:" + configdir]
    return subprocess.Popen(cmd, cwd=os.path.dirname(APP_PROJECT), env=env, stdout=log,
                            stderr=subprocess.STDOUT, start_new_session=True)


def wait_ready(proc, timeout=300):
    t0 = time.monotonic()
    while time.monotonic() - t0 < timeout:
        if proc.poll() is not None:
            raise RuntimeError(f"OpenDevelop exited early (code {proc.returncode})")
        try:
            invoke("od.solution.status", timeout=5)
            return time.monotonic() - t0
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            time.sleep(0.5)
    raise TimeoutError("DevFlow agent did not come up")


def stop_app(proc):
    try:
        os.killpg(proc.pid, signal.SIGTERM)
        proc.wait(15)
    except Exception:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except Exception:
            pass


class UiProbe(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True)
        self.samples = []  # (start_offset_s, latency_ms)
        self.stop = threading.Event()
        self.t0 = time.monotonic()

    def run(self):
        while not self.stop.is_set():
            s = time.monotonic()
            try:
                invoke("od.solution.status", timeout=3600)
            except Exception:
                pass
            lat = (time.monotonic() - s) * 1000
            self.samples.append((s - self.t0, lat))
            time.sleep(PROBE_INTERVAL)

    def summary(self, until_s=None, from_s=None, prefix="ui"):
        lats = [l for (t, l) in self.samples
                if (until_s is None or t <= until_s) and (from_s is None or t > from_s)]
        if not lats:
            return {}
        return {
            prefix + "_samples": len(lats),
            prefix + "_max_stall_ms": round(max(lats)),
            prefix + "_p95_ms": round(sorted(lats)[int(0.95 * (len(lats) - 1))]),
            prefix + "_blocked_ms": round(sum(l for l in lats if l > STALL_THRESHOLD_MS)),
        }


def clear_caches(solution):
    od = os.path.join(os.path.dirname(solution), ".od")
    for sub in ("roslyn-solution-snapshots", "roslyn-reference-cache"):
        shutil.rmtree(os.path.join(od, sub), ignore_errors=True)


def one_run(solution, args, idx):
    if args.cold:
        clear_caches(solution)
    configdir = tempfile.mkdtemp(prefix="od-bench-")
    logpath = os.path.join(args.logdir, f"run{idx}.log")
    with open(logpath, "w") as log:
        proc = start_app(configdir, log)
        try:
            startup_s = wait_ready(proc)
            time.sleep(args.settle)  # let startup work drain so it does not pollute the open
            probe = UiProbe()
            probe.start()
            # In-process input-latency probe (od.ui-latency.*): what a key press would wait for.
            # The DevFlow probe above also includes DevFlow's own dispatcher queuing.
            invoke("od.ui-latency.start", timeout=30)
            time.sleep(0.5)
            t_open = time.monotonic()
            if args.open_mode == "blocking":
                # One DevFlow call for the whole load. DevFlow runs one UI action at a time, so the probe
                # queues behind it: this measures the action, not the UI thread.
                body, res = invoke("od.open-solution", solution)
                if not (isinstance(res, dict) and res.get("success")):
                    raise RuntimeError(f"open failed: {body}")
            else:
                # Start the load, then wait until it is the current solution. DevFlow is free in between,
                # so the probe's latency is the UI thread's.
                body, res = invoke("od.open-solution-begin", solution)
                if not (isinstance(res, dict) and res.get("success")):
                    raise RuntimeError(f"open failed: {body}")
                while True:
                    if time.monotonic() - t_open > args.timeout:
                        raise TimeoutError("solution did not open")
                    try:
                        _, st = invoke("od.solution.status", timeout=60)
                        if isinstance(st, dict) and st.get("path") == solution:
                            break
                    except (urllib.error.URLError, TimeoutError, OSError):
                        pass
                    time.sleep(0.1)
            open_ms = (time.monotonic() - t_open) * 1000
            _, input_open = invoke("od.ui-latency.read", True, timeout=30)
            # Poll the timeline for the readiness milestone.
            deadline = t_open + args.timeout
            timeline, ready_ms = {}, None
            while time.monotonic() < deadline:
                # The UI thread can stay busy after the open returns (Roslyn snapshot phase);
                # a timed-out poll is not a failed run - try again until the deadline.
                try:
                    _, timeline = invoke("od.perf.timeline", timeout=max(1, min(60, deadline - time.monotonic())))
                except (urllib.error.URLError, TimeoutError, OSError):
                    continue
                marks = timeline.get("scopes", {}).get("solution-open", [])
                hit = [m for m in marks if args.ready_mark in m["name"]]
                if hit:
                    ready_ms = hit[0]["ms"]
                    break
                time.sleep(1)
            # Keep probing after the open: post-open work (Solution Explorer, language service, pads)
            # runs on the UI thread too.
            post_end = t_open + open_ms / 1000 + args.post_open_seconds
            while time.monotonic() < post_end:
                time.sleep(0.5)
            _, input_post = invoke("od.ui-latency.read", True, timeout=30)
            invoke("od.ui-latency.stop", timeout=30)
            probe.stop.set()
            probe.join(10)
            marks = timeline.get("scopes", {}).get("solution-open", [])
            result = {
                "run": idx,
                "startup_s": round(startup_s, 1),
                "open_call_ms": round(open_ms),
                "ready_ms": ready_ms,
                "rss_mb": timeline.get("workingSetMB"),
                "heap_mb": timeline.get("managedHeapMB"),
                "milestones": [{"name": m["name"], "ms": m["ms"], "detail": m.get("detail")} for m in marks],
            }
            # UI responsiveness only during the open call itself, for a like-for-like number
            open_end_s = (t_open - probe.t0) + open_ms / 1000
            result.update(probe.summary(until_s=open_end_s))
            result.update(probe.summary(from_s=open_end_s, prefix="post"))
            for prefix, stats in (("input_open", input_open), ("input_post", input_post)):
                for key in ("count", "maxMs", "p95Ms", "p99Ms", "stalledMs"):
                    result[f"{prefix}_{key}"] = stats.get(key)
            return result
        finally:
            stop_app(proc)
            shutil.rmtree(configdir, ignore_errors=True)


def first(marks, name):
    for m in marks:
        if m["name"] == name:
            return m["ms"]
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("solution")
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--cold", action="store_true")
    ap.add_argument("--timeout", type=float, default=900, help="seconds to wait for --ready-mark")
    ap.add_argument("--ready-mark", default="projects-pushed")
    ap.add_argument("--settle", type=float, default=5)
    ap.add_argument("--post-open-seconds", type=float, default=60,
                    help="keep probing the UI thread this long after the open (post-open work)")
    ap.add_argument("--out")
    ap.add_argument("--logdir", default=tempfile.mkdtemp(prefix="od-bench-logs-"))
    ap.add_argument("--label", default="")
    ap.add_argument("--open-mode", choices=["begin", "blocking"], default="begin",
                    help="begin: od.open-solution-begin + poll (measures the UI thread); blocking: od.open-solution")
    args = ap.parse_args()
    solution = os.path.abspath(os.path.expanduser(args.solution))

    results = []
    for i in range(1, args.runs + 1):
        print(f"run {i}/{args.runs} ...", file=sys.stderr, flush=True)
        r = one_run(solution, args, i)
        results.append(r)
        ms = r["milestones"]
        print(f"  open call {r['open_call_ms']} ms | projects-evaluated {first(ms, 'projects-evaluated')} ms"
              f" | handlers-done {first(ms, 'solution-opened-handlers-done')} ms | ready {r['ready_ms']} ms"
              f" | UI during open: p95 {r.get('ui_p95_ms')} ms, max {r.get('ui_max_stall_ms')} ms"
              f" | after open: p95 {r.get('post_p95_ms')} ms, max {r.get('post_max_stall_ms')} ms, blocked {r.get('post_blocked_ms')} ms"
              f" | RSS {r['rss_mb']} MB", file=sys.stderr, flush=True)
        print(f"  input latency (in-process, Input priority): during open p95 {r.get('input_open_p95Ms')} / max {r.get('input_open_maxMs')} ms,"
              f" stalled {r.get('input_open_stalledMs')} ms | after open p95 {r.get('input_post_p95Ms')} / max {r.get('input_post_maxMs')} ms,"
              f" stalled {r.get('input_post_stalledMs')} ms", file=sys.stderr, flush=True)

    def med(key, src=lambda r, k: r.get(k)):
        vals = [src(r, key) for r in results if src(r, key) is not None]
        return round(statistics.median(vals)) if vals else None

    summary = {
        "label": args.label,
        "solution": solution,
        "cold": args.cold,
        "runs": len(results),
        "median_open_call_ms": med("open_call_ms"),
        "median_projects_evaluated_ms": med("projects-evaluated", lambda r, k: first(r["milestones"], k)),
        "median_handlers_done_ms": med("solution-opened-handlers-done", lambda r, k: first(r["milestones"], k)),
        "median_ready_ms": med("ready_ms"),
        "median_ui_max_stall_ms": med("ui_max_stall_ms"),
        "median_ui_blocked_ms": med("ui_blocked_ms"),
        "median_post_max_stall_ms": med("post_max_stall_ms"),
        "median_post_blocked_ms": med("post_blocked_ms"),
        "median_input_open_max_ms": med("input_open_maxMs"),
        "median_input_open_stalled_ms": med("input_open_stalledMs"),
        "median_input_post_max_ms": med("input_post_maxMs"),
        "median_input_post_stalled_ms": med("input_post_stalledMs"),
        "median_rss_mb": med("rss_mb"),
        "results": results,
        "logs": args.logdir,
    }
    print(json.dumps({k: v for k, v in summary.items() if k != "results"}, indent=2))
    if args.out:
        with open(args.out, "w") as f:
            json.dump(summary, f, indent=2)


if __name__ == "__main__":
    main()
