#!/usr/bin/env python3
"""Pin the NuGet packages a project restores into a flatpak-builder sources file.

Same idea as flatpak-builder-tools' flatpak-dotnet-generator.py (MIT): restore inside
the dotnet SDK extension sandbox, then emit one `file` source per package so the
in-sandbox build can restore offline. The difference is that the download URL is
resolved from the feed each package was *actually* restored from (recorded by NuGet in
`.nupkg.metadata`) instead of assuming nuget.org. GitHub release ZIPs and individual
NuGet packages are supplied as checksum-pinned assets from the evaluated MSBuild items.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import subprocess
import tempfile
import urllib.request
from pathlib import Path

FLAT_CONTAINER = "PackageBaseAddress/3.0.0"
REPO_ROOT = Path(__file__).resolve().parents[2]


def restore(
    project: Path,
    packages_dir: Path,
    runtime: str | None,
    dotnet: str,
    freedesktop: str,
    dotnet_args: list[str],
) -> tuple[Path, list[dict[str, str]]]:
    """Run `dotnet restore` with the SDK extension, against the host file system."""
    shell_command = (
        f'PATH="$PATH:/usr/lib/sdk/dotnet{dotnet}/bin" '
        f'LD_LIBRARY_PATH="$LD_LIBRARY_PATH:/usr/lib/sdk/dotnet{dotnet}/lib" '
        'exec dotnet "$@"'
    )
    argv = [
        "flatpak",
        "run",
        "--env=DOTNET_CLI_TELEMETRY_OPTOUT=true",
        "--env=DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true",
        "--command=sh",
        f"--runtime=org.freedesktop.Sdk//{freedesktop}",
        "--share=network",
        "--filesystem=host",
        f"org.freedesktop.Sdk.Extension.dotnet{dotnet}//{freedesktop}",
        "-c",
        shell_command,
        "--",
    ]
    restore_argv = argv + [
        "restore",
        "--packages",
        str(packages_dir),
        str(project),
    ]
    if runtime:
        restore_argv += ["-r", runtime]
    restore_argv += dotnet_args
    subprocess.run(restore_argv, check=True)

    property_args = [
        arg for arg in dotnet_args
        if arg.lower().startswith(("-p:", "/p:", "-property:", "/property:"))
    ]
    if runtime:
        property_args += [f"-p:RuntimeIdentifier={runtime}"]
    evaluation = json.loads(subprocess.check_output(
        argv + [
            "msbuild", str(project),
            "-getItem:GitHubNuGetAsset", "-getProperty:GitHubNuGetFeed",
        ] + property_args,
        text=True,
    ))
    return (
        Path(evaluation["Properties"]["GitHubNuGetFeed"]).resolve(),
        evaluation["Items"].get("GitHubNuGetAsset", []),
    )


def flat_container(source: str, cache: dict[str, str]) -> str:
    """Resolve a v3 service index to its flat container (package download) base URL."""
    if source not in cache:
        if not source.startswith(("http://", "https://")):
            raise SystemExit(f"error: cannot build a download URL for source {source}")
        with urllib.request.urlopen(source) as response:  # noqa: S310 - scheme checked
            index = json.load(response)
        addresses = [
            resource["@id"]
            for resource in index["resources"]
            if resource["@type"].startswith(FLAT_CONTAINER)
        ]
        if not addresses:
            raise SystemExit(f"error: source {source} exposes no {FLAT_CONTAINER}")
        cache[source] = addresses[0].rstrip("/")
    return cache[source]


def collect_sources(
    packages_dir: Path,
    destdir: str,
    release_feed: Path,
    release_assets: list[dict[str, str]],
) -> list[dict[str, str]]:
    """Turn a restored packages folder into flatpak-builder `file` sources."""
    cache: dict[str, str] = {}
    sources = []
    release_packages = set()
    for asset in release_assets:
        filename = asset["Identity"]
        sha256 = asset["Sha256"]
        marker = release_feed / f"{filename}.{sha256}.complete"
        release_packages.update(name.lower() for name in marker.read_text().splitlines())
        sources.append(
            {
                "type": "file",
                "url": asset["Url"],
                "sha256": sha256,
                "dest": (release_feed.parent / "downloads").relative_to(REPO_ROOT).as_posix(),
                "dest-filename": filename,
            }
        )
    # Layout: <packages>/<id>/<version>/<id>.<version>.nupkg.sha512 (both lower-cased,
    # which is also what the flat container expects).
    for hash_path in sorted(packages_dir.glob("*/*/*.nupkg.sha512")):
        name = hash_path.parent.parent.name
        version = hash_path.parent.name
        filename = f"{name}.{version}.nupkg"
        metadata = json.loads((hash_path.parent / ".nupkg.metadata").read_text())
        if metadata["source"] == str(release_feed):
            if filename not in release_packages:
                raise SystemExit(f"error: no pinned GitHub release asset supplies {filename}")
            continue
        base = flat_container(metadata["source"], cache)
        sha512 = binascii.hexlify(base64.b64decode(hash_path.read_text())).decode("ascii")
        sources.append(
            {
                "type": "file",
                "url": f"{base}/{name}/{version}/{filename}",
                "sha512": sha512,
                "dest": destdir,
                "dest-filename": filename,
            }
        )
    return sources


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="The output JSON sources file")
    parser.add_argument("project", type=Path, help="The project file to restore")
    parser.add_argument(
        "--runtime", "-r", help="The target runtime to restore packages for"
    )
    parser.add_argument(
        "--freedesktop",
        "-f",
        default="26.08",
        help="The version of the freedesktop SDK to restore with",
    )
    parser.add_argument(
        "--dotnet",
        "-d",
        default="10",
        help="The major version of the dotnet SDK extension to restore with",
    )
    parser.add_argument(
        "--destdir",
        default="nuget-sources",
        help="The directory the generated sources file will save packages to",
    )
    parser.add_argument(
        "--dotnet-args",
        "-a",
        nargs=argparse.REMAINDER,
        default=[],
        help="Additional arguments to pass to `dotnet restore` (must come last)",
    )
    args = parser.parse_args()

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    # The sandbox has a private /tmp, so the packages folder has to live somewhere the
    # host file system share (--filesystem=host) actually reaches.
    with tempfile.TemporaryDirectory(dir=output.parent) as tmp:
        packages_dir = Path(tmp).resolve()
        release_feed, release_assets = restore(
            args.project.resolve(),
            packages_dir,
            args.runtime,
            args.dotnet,
            args.freedesktop,
            args.dotnet_args,
        )
        sources = collect_sources(packages_dir, args.destdir, release_feed, release_assets)

    if not sources:
        raise SystemExit("error: the restore produced no packages")
    output.write_text(json.dumps(sources, indent=4) + "\n")
    print(f"{output}: pinned {len(sources)} packages")


if __name__ == "__main__":
    main()
