"""Non-destructive Windows setup and read-only runtime diagnostics."""
from __future__ import annotations

import argparse
import csv
from io import StringIO
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent
NATIVE_FILES = ("NBA2KCourtCreator.exe", "NBA2KCourtCreator.dll", "NBA2KCourtCreator.deps.json",
                "NBA2KCourtCreator.runtimeconfig.json", "TwoK.Studio.dll")
PROBE = r'''
import importlib, importlib.metadata, importlib.util, json, pathlib, struct, sys
result = {"version": list(sys.version_info[:3]), "bits": struct.calcsize("P") * 8,
          "prefix": sys.prefix, "executable": sys.executable, "pipAvailable": importlib.util.find_spec("pip") is not None,
          "packages": {}, "issues": [], "requirementsValid": None, "brokenImports": []}
Requirement = None
try:
    try:
        from packaging.requirements import Requirement
    except ImportError:
        from pip._vendor.packaging.requirements import Requirement
except ImportError as error:
    result["issues"].append("Package requirement verifier is unavailable: " + str(error))
if Requirement is not None:
    try:
        with pathlib.Path(sys.argv[1]).open("rb") as stream:
            data = stream.read(65537)
        if len(data) > 65536:
            raise ValueError("requirements.txt exceeds the setup inspection limit")
        text = data.decode("utf-8-sig")
        requirements = []
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            requirement = Requirement(line)
            if requirement.url is not None:
                raise ValueError("Setup requires named package constraints, not direct URLs")
            if requirement.marker and not requirement.marker.evaluate():
                continue
            requirements.append(requirement)
        names = {item.name.casefold().replace("_", "-").replace(".", "-") for item in requirements}
        if not {"pillow", "shapely"}.issubset(names):
            raise ValueError("requirements.txt must include active Pillow and Shapely constraints")
        result["requirementsValid"] = True
        for requirement in requirements:
            try:
                version = importlib.metadata.version(requirement.name)
                result["packages"][requirement.name] = version
                if not requirement.specifier.contains(version):
                    result["issues"].append(requirement.name + " " + version + " does not satisfy " + str(requirement.specifier))
            except importlib.metadata.PackageNotFoundError:
                result["issues"].append(requirement.name + " is not installed")
    except Exception as error:
        result["requirementsValid"] = False
        result["issues"].append("Cannot verify requirements: " + str(error))
for name in ("PIL.Image", "shapely.geometry"):
    try:
        module = importlib.import_module(name)
        if name == "PIL.Image":
            image = module.new("RGBA", (2, 2), (1, 2, 3, 255))
            try:
                assert image.getpixel((0, 0)) == (1, 2, 3, 255)
            finally:
                image.close()
        else:
            assert module.Polygon([(0, 0), (1, 0), (0, 1)]).area == .5
    except ModuleNotFoundError as error:
        distribution = "Pillow" if name == "PIL.Image" else "Shapely"
        if any(package.casefold() == distribution.casefold() for package in result["packages"]):
            result["brokenImports"].append(name)
        result["issues"].append(name + " is unavailable: " + str(error))
    except Exception as error:
        result["brokenImports"].append(name)
        result["issues"].append(name + " cannot load: " + str(error))
print(json.dumps(result))
'''


class SetupError(RuntimeError):
    pass


def run(command, root, *, capture=True, timeout=30):
    environment = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en", DOTNET_NOLOGO="1")
    try:
        result = subprocess.run([str(item) for item in command], cwd=root, env=environment,
                                stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None,
                                text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SetupError("Could not run " + str(command[0]) + ": " + str(error)) from error
    if result.returncode:
        detail = ((result.stderr or "") + "\n" + (result.stdout or ""))[-4000:] if capture else "See the command output above."
        raise SetupError("Command failed: " + str(command[0]) + "\n" + detail.strip())
    return result.stdout or ""


def runtime_python(root):
    root = Path(root).resolve()
    directory = root / "runtime" / "python"
    if not directory.resolve().is_relative_to(root):
        raise SetupError("The Python runtime folder points outside this project. It was not changed.")
    for relative in ("python.exe", "Scripts/python.exe"):
        executable = directory / relative
        if executable.is_file():
            if not executable.resolve().is_relative_to(directory.resolve()):
                raise SetupError("The Python executable points outside its runtime folder. It was not changed.")
            return executable
    if directory.exists() and (not directory.is_dir() or any(directory.iterdir())):
        raise SetupError("runtime/python contains files but no usable Python executable. Preserve or repair that folder before setup; it will not be overwritten.")
    return None


def probe_python(executable, root):
    try:
        result = json.loads(run([executable, "-I", "-B", "-c", PROBE, Path(root) / "requirements.txt"], root))
    except (json.JSONDecodeError, TypeError) as error:
        raise SetupError("The Python runtime returned invalid diagnostic data.") from error
    if (not isinstance(result, dict) or not isinstance(result.get("version"), list) or len(result["version"]) != 3
            or any(type(value) is not int for value in result["version"]) or result.get("bits") not in (32, 64)
            or not isinstance(result.get("prefix"), str) or type(result.get("pipAvailable")) is not bool
            or not isinstance(result.get("packages"), dict) or not isinstance(result.get("issues"), list)
            or any(not isinstance(value, str) for value in result["issues"])
            or "requirementsValid" not in result or result.get("requirementsValid") is not None and type(result["requirementsValid"]) is not bool
            or not isinstance(result.get("brokenImports"), list) or any(value not in ("PIL.Image", "shapely.geometry") for value in result["brokenImports"])):
        raise SetupError("The Python runtime returned invalid diagnostic data.")
    if tuple(result["version"]) < (3, 12, 0) or result["bits"] != 64:
        raise SetupError("Court Creator setup requires 64-bit Python 3.12 or newer. The existing runtime was not changed.")
    if Path(result["prefix"]).resolve() != (Path(root) / "runtime" / "python").resolve():
        raise SetupError("The selected Python executable uses an environment outside this project. Setup will not modify it.")
    if result["requirementsValid"] is False:
        raise SetupError("requirements.txt cannot be safely verified: " + "; ".join(result["issues"]))
    return result


def dotnet_candidates():
    candidates = []
    for name in ("DOTNET_ROOT_X64", "DOTNET_ROOT"):
        if os.environ.get(name):
            candidates.append(Path(os.environ[name]) / "dotnet.exe")
    for name in ("ProgramW6432", "ProgramFiles"):
        if os.environ.get(name):
            candidates.append(Path(os.environ[name]) / "dotnet" / "dotnet.exe")
    if path := shutil.which("dotnet"):
        candidates.append(Path(path))
    return list(dict.fromkeys(candidate.resolve() for candidate in candidates if candidate.is_file()))


def inspect_dotnet(root):
    failures = []
    for executable in dotnet_candidates():
        try:
            info = run([executable, "--info"], root)
            if not re.search(r"^\s*Architecture:\s*x64\s*$", info, re.MULTILINE):
                failures.append(str(executable) + " is not an x64 .NET host")
                continue
            runtimes = run([executable, "--list-runtimes"], root)
            sdks = run([executable, "--list-sdks"], root)
            return {"path": str(executable), "runtimes": runtimes.splitlines(), "sdks": sdks.splitlines()}
        except SetupError as error:
            failures.append(str(error))
    return {"path": None, "runtimes": [], "sdks": [], "errors": failures}


def has_framework(dotnet, name):
    return any(re.match(re.escape(name) + r" 8\.0\.\d+ \[", line) for line in dotnet["runtimes"])


def inspect_setup(root):
    root = Path(root).resolve()
    if not (root / "requirements.txt").is_file():
        raise SetupError("requirements.txt is missing. Restore the complete Court Creator project or published folder.")
    executable = runtime_python(root)
    python = probe_python(executable, root) if executable is not None else None
    dotnet = inspect_dotnet(root)
    missing = [name for name in NATIVE_FILES if not (root / "desktop" / name).is_file()]
    if not missing:
        try:
            path = root / "desktop" / "NBA2KCourtCreator.runtimeconfig.json"
            if path.stat().st_size > 65536:
                raise ValueError("oversized runtime configuration")
            config = json.loads(path.read_text(encoding="utf-8-sig"))["runtimeOptions"]
            frameworks = config["frameworks"]
            if {item["name"] for item in frameworks} != {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"} or any(item["version"] != "8.0.0" for item in frameworks):
                raise ValueError("unsupported desktop runtime configuration")
        except (OSError, ValueError, TypeError, KeyError):
            missing.append("valid NBA2KCourtCreator.runtimeconfig.json")
    issues = []
    if python is None:
        issues.append("The app-owned Python runtime is missing; setup can create it from 64-bit Python 3.12 or newer.")
    else:
        issues.extend(python["issues"])
    if not has_framework(dotnet, "Microsoft.NETCore.App") or not has_framework(dotnet, "Microsoft.WindowsDesktop.App"):
        issues.append("Install the x64 .NET 8 Desktop Runtime. The console/ASP.NET runtime alone is not enough for this WPF app.")
    if missing:
        issues.append("The published desktop build is incomplete: " + ", ".join(missing))
        if not (root / "src" / "NBA2KCourtCreator" / "NBA2KCourtCreator.csproj").is_file():
            issues.append("This published copy has no build sources. Restore its complete desktop folder.")
        elif not any(re.match(r"(?:[89]|[1-9]\d+)\.\d+\.\d+ \[", line) for line in dotnet["sdks"]):
            issues.append("Install an x64 .NET SDK 8 or newer to build this source checkout.")
    return {"ready": not issues, "scope": "runtime-and-build-only", "root": str(root), "pythonPath": str(executable) if executable else None,
            "python": python, "dotnet": dotnet, "nativeMissing": missing, "issues": issues}


def app_running(root):
    if os.name != "nt":
        return False
    output = run(["tasklist.exe", "/FI", "IMAGENAME eq NBA2KCourtCreator.exe", "/FO", "CSV", "/NH"], root)
    return any(row and row[0].casefold() == "nba2kcourtcreator.exe" for row in csv.reader(StringIO(output)))


def validate_build_paths(root):
    paths = [root / "desktop", root / "src/NBA2KCourtCreator", root / "src/TwoK.Studio"]
    paths.extend(root / "desktop" / name for name in (*NATIVE_FILES, "NBA2KCourtCreator.pdb", "TwoK.Studio.pdb"))
    for path in paths:
        if not path.resolve().is_relative_to(root):
            raise SetupError("A desktop build path points outside this project: " + str(path) + ". No build was started.")


def setup(root, *, check_only=False):
    root = Path(root).resolve()
    report = inspect_setup(root)
    if check_only or report["ready"]:
        return report
    dotnet = report["dotnet"]
    if not has_framework(dotnet, "Microsoft.NETCore.App") or not has_framework(dotnet, "Microsoft.WindowsDesktop.App"):
        raise SetupError("Install the x64 .NET 8 Desktop Runtime, then run setup again. No Python runtime was changed.")
    if report["nativeMissing"] and (not (root / "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj").is_file()
            or not any(re.match(r"(?:[89]|[1-9]\d+)\.\d+\.\d+ \[", line) for line in dotnet["sdks"])):
        raise SetupError("Restore the complete published desktop folder, or install an x64 .NET SDK 8 or newer for this source checkout. No Python runtime was changed.")
    if report["nativeMissing"]:
        validate_build_paths(root)
    if app_running(root):
        raise SetupError("Close Court Creator before setup changes its dependencies or desktop build. No runtime files were changed.")
    executable = runtime_python(root)
    if executable is None:
        if sys.version_info < (3, 12) or sys.maxsize <= 2 ** 32:
            raise SetupError("Install 64-bit Python 3.12 or newer to create the app-owned runtime.")
        directory = root / "runtime" / "python"
        run([sys.executable, "-I", "-B", "-m", "venv", "--copies", directory], root, capture=False, timeout=180)
        executable = runtime_python(root)
        if executable is None:
            raise SetupError("Python environment creation did not produce an executable. Its files were preserved for diagnosis.")
    python = probe_python(executable, root)
    if python["issues"]:
        if not python["pipAvailable"] or python["requirementsValid"] is None:
            run([executable, "-I", "-B", "-m", "ensurepip", "--upgrade"], root, capture=False, timeout=180)
            python = probe_python(executable, root)
            if python["requirementsValid"] is None:
                raise SetupError("The Python package verifier could not be repaired. No packages were installed.")
        install = [executable, "-I", "-B", "-m", "pip", "install", "--disable-pip-version-check"]
        if python["brokenImports"]:
            install.append("--force-reinstall")
        run([*install, "-r", root / "requirements.txt"],
            root, capture=False, timeout=600)
        if probe_python(executable, root)["issues"]:
            raise SetupError("Python dependencies still failed their version/import checks. The existing runtime was preserved.")
    if report["nativeMissing"]:
        run([dotnet["path"], "publish", root / "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj", "-c", "Release",
             "--self-contained", "false", "-o", root / "desktop", "--nologo"], root, capture=False, timeout=600)
    result = inspect_setup(root)
    if not result["ready"]:
        raise SetupError("Setup did not complete: " + "; ".join(result["issues"]))
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-only", action="store_true", help="Inspect without installing, building or changing files")
    parser.add_argument("--json", action="store_true", help="Print structured diagnostics (check-only mode)")
    parser.add_argument("--project-root", type=Path, default=ROOT, help="Project folder to inspect/setup (the batch wrapper passes its own folder)")
    args = parser.parse_args(argv)
    if args.json and not args.check_only:
        parser.error("--json requires --check-only")
    try:
        report = setup(args.project_root, check_only=args.check_only)
    except (SetupError, OSError) as error:
        if args.json:
            print(json.dumps({"ready": False, "issues": [str(error)]}))
        else:
            print("Setup failed: " + str(error), file=sys.stderr)
        return 1
    if args.json:
        print(json.dumps(report, indent=2))
    else:
        if report["ready"]:
            print("Court Creator runtime checks passed. Use your desktop launcher; setup does not open the app.")
            print("Python: " + report["pythonPath"])
            print(".NET: " + report["dotnet"]["path"])
        else:
            for issue in report["issues"]:
                print("- " + issue)
    return 0 if report["ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
