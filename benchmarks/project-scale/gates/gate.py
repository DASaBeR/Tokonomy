"""Gate for the project-scale benchmark. Usage: python gate.py backend|frontend   (run from the worktree root).

Passes trivially while the corresponding part does not exist yet (so a unit that only adds docs or the other
half is not blocked), and otherwise builds and tests it. Identical for both experiments.
"""
import glob, os, subprocess, sys

root = os.getcwd()


def run(args, cwd):
    print("$", " ".join(args), flush=True)
    return subprocess.run(args, cwd=cwd).returncode


def backend():
    d = os.path.join(root, "backend")
    sln = glob.glob(os.path.join(d, "*.sln")) + glob.glob(os.path.join(d, "*.slnx"))
    target = sln[0] if sln else None
    if target is None:
        projs = glob.glob(os.path.join(d, "**", "*.csproj"), recursive=True)
        if not projs:
            print("backend: nothing to build yet")
            return 0
        target = projs[0] if len(projs) == 1 else None
    if target is None:
        print("backend: several projects but no solution file in backend/ (a .sln or .slnx is required)")
        return 1
    if run(["dotnet", "build", target, "-nologo", "-v", "q"], root) != 0:
        return 1
    tests = glob.glob(os.path.join(d, "**", "*Tests*.csproj"), recursive=True)
    if not tests:
        print("backend: no test project yet")
        return 0
    return run(["dotnet", "test", target, "--no-build", "-nologo", "-v", "q"], root)


def frontend():
    d = os.path.join(root, "frontend")
    if not os.path.isfile(os.path.join(d, "package.json")):
        print("frontend: nothing to build yet")
        return 0
    npm = "npm.cmd" if os.name == "nt" else "npm"
    if not os.path.isdir(os.path.join(d, "node_modules")):
        if run([npm, "install", "--no-audit", "--no-fund"], d) != 0:
            return 1
    import json
    scripts = json.load(open(os.path.join(d, "package.json"), encoding="utf-8")).get("scripts", {})
    for name in ("typecheck", "lint", "build", "test"):
        if name in scripts:
            env_ok = run([npm, "run", name, "--silent"], d) == 0
            if not env_ok:
                return 1
    if "build" not in scripts:
        print("frontend: package.json has no build script")
        return 1
    return 0


if __name__ == "__main__":
    which = sys.argv[1] if len(sys.argv) > 1 else ""
    sys.exit({"backend": backend, "frontend": frontend}[which]())
