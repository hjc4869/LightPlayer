import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import subprocess
import tarfile
import tempfile
from urllib.parse import unquote, urlsplit


SOURCE_DIRECTORIES = {"build", "src", "tools", "packaging"}
SOURCE_FILES = {
    ".dockerignore", ".gitignore", "LICENSE", "Makefile", "nuget.config",
    "README.md", "THIRDPARTY.txt",
}
EXCLUDED_DOCUMENTS = {"AGENTS.md", "DEV.md"}
MARKDOWN_TARGETS = re.compile(
    r"\[[^\]\n]*\]\(\s*(?:<([^>\n]+)>|([^\s)]+))"
    r"|^ {0,3}\[[^\]\n]+\]:\s*(?:<([^>\n]+)>|(\S+))",
    re.MULTILINE,
)


def collect_files(repository):
    tracked = {
        PurePosixPath(os.fsdecode(name))
        for name in subprocess.check_output(
            ["git", "-C", str(repository), "ls-files", "-z"],
        ).split(b"\0") if name
    }
    tracked = {path for path in tracked if path.name not in EXCLUDED_DOCUMENTS}
    included = {
        path for path in tracked
        if (path.parts[0] in SOURCE_DIRECTORIES and path.suffix.lower() != ".md")
        or str(path) in SOURCE_FILES
        or (len(path.parts) == 1 and path.suffix in {".props", ".targets", ".slnx"})
    }
    pending = [PurePosixPath("README.md")]
    visited = set()
    while pending:
        document = pending.pop()
        if document in visited:
            continue
        visited.add(document)
        text = (repository / document).read_text(encoding="utf-8")
        text = re.sub(r"^ {0,3}(```|~~~)[^\n]*\n.*?^ {0,3}\1[^\n]*$", "", text,
                      flags=re.MULTILINE | re.DOTALL)
        for match in MARKDOWN_TARGETS.finditer(text):
            target = next(group for group in match.groups() if group is not None)
            url = urlsplit(target)
            if url.scheme or url.netloc or not url.path:
                continue
            path = PurePosixPath(posixpath.normpath(posixpath.join(
                str(document.parent), unquote(url.path),
            )))
            if path not in tracked:
                path /= "README.md"
            if path not in tracked or (
                path not in included and path.parts[0] not in SOURCE_DIRECTORIES | {"docs"}
            ):
                continue
            included.add(path)
            if path.suffix.lower() == ".md":
                pending.append(path)
    return sorted(included)


def main():
    repository = Path(__file__).resolve().parents[1]
    files = collect_files(repository)
    output = repository / "artifacts" / "LightPlayer.tar.gz"
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        archive_path = Path(staging) / output.name
        with tarfile.open(archive_path, "w:gz") as archive:
            for path in files:
                archive.add(repository / path, arcname=f"LightPlayer/{path}", recursive=False)
        archive_path.replace(output)
    print(f"Wrote {output} ({len(files)} files)")


if __name__ == "__main__":
    main()
