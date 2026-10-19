import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET


def generate_version(repository, timestamp):
    def git(*arguments, check=True):
        return subprocess.run(
            ["git", "-C", str(repository), *arguments],
            check=check, capture_output=True, text=True,
        )

    location = git("rev-parse", "--show-toplevel", check=False) if shutil.which("git") else None
    if location is None or location.returncode != 0:
        version = ET.parse(repository / "Directory.Build.props").findtext(".//Version")
        if not version:
            raise ValueError("Directory.Build.props must define Version.")
        return f"{version}-{timestamp:%Y%m%dT%H%M%SZ}"

    if git("tag", "--merged", "HEAD").stdout.strip():
        tag = git("describe", "--tags", "--abbrev=0").stdout.strip()
        count = git("rev-list", "--count", f"{tag}..HEAD").stdout.strip()
        version = f"{tag}+{count}"
    else:
        branch = git("symbolic-ref", "--short", "-q", "HEAD", check=False)
        if branch.returncode not in (0, 1):
            branch.check_returncode()
        name = branch.stdout.strip() if branch.returncode == 0 else "HEAD"
        commit = git("rev-parse", "--short", "HEAD").stdout.strip()
        dirty = "-dirty" if git("status", "--porcelain").stdout else ""
        version = f"{name}-{commit}{dirty}"
    return re.sub(r"[^A-Za-z0-9._+-]", "-", version)


def package(wwwroot, version, timestamp):
    if not re.fullmatch(r"[A-Za-z0-9_+-][A-Za-z0-9._+-]*", version):
        raise ValueError(f"Invalid web version: {version!r}")
    for name in ("index.html", "version-loader.js"):
        if not (wwwroot / name).is_file():
            raise FileNotFoundError(wwwroot / name)
    if (wwwroot / "versions").exists():
        raise ValueError("The web output is already versioned. Publish a fresh output before packaging.")

    assets = list(wwwroot.iterdir())
    destination = wwwroot / version
    destination.mkdir()
    for asset in assets:
        if asset.name not in ("index.html", "version-loader.js"):
            shutil.move(str(asset), destination / asset.name)
    catalog = [{"version": version, "buildTimestamp": timestamp.isoformat(timespec="seconds").replace("+00:00", "Z")}]
    (wwwroot / "versions").write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="Package the browser client under its deployment version.")
    parser.add_argument("wwwroot", nargs="?", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--print-version", action="store_true")
    arguments = parser.parse_args()
    timestamp = datetime.now(timezone.utc)
    version = arguments.version or generate_version(Path(__file__).resolve().parents[1], timestamp)
    if arguments.print_version:
        print(version)
        return
    if arguments.wwwroot is None:
        parser.error("wwwroot is required unless --print-version is used")
    package(arguments.wwwroot, version, timestamp)
    print(f"Packaged web version {version} in {arguments.wwwroot}")


if __name__ == "__main__":
    main()