#!/usr/bin/env python3
"""CPU-sample one solution open with dotnet-trace and summarize where the UI thread spends its time.

  tests/bench/profile_open.py ~/src/aspire/Aspire.slnx [--seconds 45] [--out trace.speedscope.json]

Prints the top frames by inclusive and exclusive time on the thread that did the open (the thread
with the most samples), and writes the raw speedscope file for https://speedscope.app.
"""
import argparse, collections, json, os, subprocess, sys, tempfile, threading, time

sys.path.insert(0, os.path.dirname(__file__))
import solution_load_bench as b


def summarize(path, top, thread_marker="SharpDevelopMain.Main"):
    d = json.load(open(path))
    frames = [f["name"] for f in d["shared"]["frames"]]
    profiles = d["profiles"]
    def weight(p):
        return sum(p.get("weights", [])) if p["type"] == "sampled" else p["endValue"] - p["startValue"]
    # Thread-time sampling gives every thread the full wall time, so pick the thread by a frame it runs:
    # SharpDevelopMain.Main = the UI thread, SolutionLoader.ReadSolution = the solution builder.
    marker = [i for i, f in enumerate(frames) if thread_marker in f]
    def loader_weight(p):
        if not marker:
            return 0
        ms = set(marker)
        if p["type"] == "sampled":
            return sum(w for st, w in zip(p["samples"], p["weights"]) if ms.intersection(st))
        return 1 if any(ev["frame"] in ms for ev in p["events"]) else 0
    main = max(profiles, key=loader_weight) if marker else max(profiles, key=weight)
    total = weight(main)
    incl, excl = collections.Counter(), collections.Counter()
    if main["type"] == "sampled":
        for stack, w in zip(main["samples"], main["weights"]):
            for fi in set(stack):
                incl[frames[fi]] += w
            if stack:
                excl[frames[stack[-1]]] += w
    else:  # evented: open/close frame events
        open_at, stack = {}, []
        for ev in main["events"]:
            if ev["type"] == "O":
                stack.append((ev["frame"], ev["at"]))
            else:
                fi, at = stack.pop()
                dur = ev["at"] - at
                if all(f != fi for f, _ in stack):
                    incl[frames[fi]] += dur
                excl[frames[fi]] += dur
                if stack:
                    pf, _ = stack[-1]
                    excl[frames[pf]] -= dur
    print(f"thread: {main['name']}  total: {total:.0f} {main.get('unit', '')}")
    def show(title, c):
        print(f"\n== {title}")
        for name, w in c.most_common(top):
            print(f"{100 * w / total:5.1f}%  {name[:160]}")
    show("inclusive", incl)
    show("exclusive", excl)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("solution")
    ap.add_argument("--seconds", type=int, default=45)
    ap.add_argument("--out", default=os.path.join(tempfile.gettempdir(), "od-open"))
    ap.add_argument("--top", type=int, default=45)
    ap.add_argument("--summarize-only")
    ap.add_argument("--thread", default="SharpDevelopMain.Main", help="a frame name identifying the thread to summarize")
    args = ap.parse_args()
    if args.summarize_only:
        summarize(args.summarize_only, args.top, args.thread)
        return
    solution = os.path.abspath(os.path.expanduser(args.solution))
    configdir = tempfile.mkdtemp(prefix="od-prof-")
    log = open(args.out + ".app.log", "w")
    proc = b.start_app(configdir, log)
    try:
        b.wait_ready(proc)
        time.sleep(5)
        _, cpu = b.invoke("od.process.cpu")
        pid = cpu["processId"]
        env = b.dotnet_env()
        env["PATH"] = os.path.expanduser("~/.dotnet/tools") + os.pathsep + env["PATH"]
        trace = subprocess.Popen(["dotnet-trace", "collect", "-p", str(pid), "--profile", "dotnet-sampled-thread-time",
                                  "--format", "Speedscope", "--duration", f"00:{args.seconds // 60:02d}:{args.seconds % 60:02d}",
                                  "-o", args.out + ".nettrace"], env=env,
                                 stdout=open(args.out + ".trace.log", "w"), stderr=subprocess.STDOUT)
        time.sleep(3)
        t = time.monotonic()
        b.invoke("od.open-solution", solution)
        print(f"open call {time.monotonic() - t:.1f}s (under tracing)", file=sys.stderr)
        trace.wait(args.seconds + 120)
    finally:
        b.stop_app(proc)
    sc = args.out + ".speedscope.json"
    print("speedscope:", sc, file=sys.stderr)
    summarize(sc, args.top, args.thread)


if __name__ == "__main__":
    main()
