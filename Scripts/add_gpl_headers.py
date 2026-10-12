# <copyright file="add_gpl_headers.py" company="River-Mochi">
# Copyright (C) 2026 River-Mochi.
# Licensed under the GNU General Public License v3.0 or later,
# with the Cities: Skylines II Linking Exception.
# See LICENSE and LICENSE-EXCEPTION in the project root.
# Copyright and license notices MUST be preserved.
# ================= </copyright> ======================

# version 0.7.0
"""
Add or replace standard River-Mochi GPL source-file headers.

This script is intended to be shared across River-Mochi CS2 mods that use 
GPL-3.0-or-later plus the Cities: Skylines II Linking Exception.

Examples below to run it assume the script is at <path-to-script>/add_gpl_headers.py:

  # Preview only. No files are changed.
  py -3 <path-to-script>/add_gpl_headers.py

  # Add missing headers and normalize UTF-8/LF where needed.
  py -3 <path-to-script>/add_gpl_headers.py --apply

  # Replace old/different River-Mochi headers with the current GPL header.
  py -3 <path-to-script>/add_gpl_headers.py --apply --replace-existing

  # Check for missing headers, BOM/CRLF, invalid UTF-8, or old/different headers.
  py -3 <path-to-script>/add_gpl_headers.py --check

Supported source files:
  C#:          .cs
  Scripts:     .py, .ps1
  UI source:   .ts, .tsx, .js, .jsx, .mjs, .scss when inside a src folder

Not supported on purpose:
  .json, .xml, .css, generated declaration files (*.d.ts), and binary assets.

Scan behavior:
  Uses git ls-files when available, so ignored folders such as bin, obj,
  node_modules, .git, and .vs are not scanned.

Repo-root behavior:
  The script finds the repo root by walking upward from its own location.
  This works for root-level or nested project Scripts folders.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


UTF8_BOM = b"\xef\xbb\xbf"

SKIP_DIRS = {
    ".git",
    ".vs",
    "bin",
    "obj",
    "generated",
    "node_modules",
    "packages",
}

# These source types can safely use line comments for the shared header.
SUPPORTED_SUFFIXES = {
    ".cs": "//",
    ".py": "#",
    ".ps1": "#",
    ".ts": "//",
    ".tsx": "//",
    ".js": "//",
    ".jsx": "//",
    ".mjs": "//",
    ".scss": "//",
}

# Web/UI files are only edited when they are real source under a src folder.
# This avoids stamping generated/copied SDK type declarations and tooling files.
WEB_SOURCE_SUFFIXES = {
    ".ts",
    ".tsx",
    ".js",
    ".jsx",
    ".mjs",
    ".scss",
}


@dataclass
class FileResult:
    """Result from processing one file."""

    changed: bool
    new_text: str
    had_header: bool
    header_matches: bool
    header_replaced: bool
    header_added: bool
    had_bom: bool
    had_crlf: bool
    utf8_error: bool


@dataclass
class RunStats:
    """Summary counters for one run."""

    candidate_files: int = 0
    supported_files: int = 0
    skipped_files: int = 0
    updated_files: int = 0
    unchanged_files: int = 0
    header_added: int = 0
    header_replaced: int = 0
    header_mismatch: int = 0
    bom_found: int = 0
    crlf_found: int = 0
    utf8_errors: int = 0


def find_repo_root(script_path: Path) -> Path:
    """Find the repository root by walking upward from this script."""
    for parent in [script_path.parent, *script_path.parents]:
        if (parent / ".git").exists():
            return parent

    for parent in [script_path.parent, *script_path.parents]:
        if (parent / ".editorconfig").exists() or (parent / ".gitattributes").exists():
            return parent

    return Path.cwd().resolve()


def should_skip(path: Path) -> bool:
    """Return true for files this GPL-header tool must not edit."""
    name = path.name.lower()

    if name.endswith(".g.cs") or name.endswith(".d.ts"):
        return True

    parts = {part.lower() for part in path.parts}
    return any(skip_dir in parts for skip_dir in SKIP_DIRS)


def is_supported_source_file(path: Path) -> bool:
    """Return true if the file is an intended source target."""
    suffix = path.suffix.lower()

    if suffix not in SUPPORTED_SUFFIXES:
        return False

    if suffix in WEB_SOURCE_SUFFIXES:
        return "src" in {part.lower() for part in path.parts}

    return True


def normalize_lf(text: str) -> str:
    """Normalize line endings in files the script writes."""
    return text.replace("\r\n", "\n").replace("\r", "\n")


def read_utf8_text(path: Path) -> tuple[str, bool, bool, bool]:
    """Read UTF-8 text and report BOM, CRLF, and UTF-8 errors."""
    raw = path.read_bytes()
    had_bom = raw.startswith(UTF8_BOM)
    had_crlf = b"\r\n" in raw

    if had_bom:
        raw = raw[len(UTF8_BOM):]

    try:
        return raw.decode("utf-8"), had_bom, had_crlf, False
    except UnicodeDecodeError:
        return "", had_bom, had_crlf, True


def get_comment_prefix(path: Path) -> str:
    """Return the line-comment prefix for this source file."""
    return SUPPORTED_SUFFIXES[path.suffix.lower()]


def make_header(path: Path, year: int) -> str:
    """Create the exact shared GPL header for this source file."""
    prefix = get_comment_prefix(path)

    return (
        f'{prefix} <copyright file="{path.name}" company="River-Mochi">\n'
        f"{prefix} Copyright (C) {year} River-Mochi.\n"
        f"{prefix} Licensed under the GNU General Public License v3.0 or later,\n"
        f"{prefix} with the Cities: Skylines II Linking Exception.\n"
        f"{prefix} See LICENSE and LICENSE-EXCEPTION in the project root.\n"
        f"{prefix} Copyright and license notices MUST be preserved.\n"
        f"{prefix} ================= </copyright> ======================\n"
        "\n"
    )


def has_copyright_header(text: str) -> bool:
    """Return true if a River-Mochi copyright header appears near the top."""
    top = text[:2000].lower()
    return "copyright" in top and ("river-mochi" in top or "river mochi" in top or "rivermochi" in top)


def has_exact_header(text: str, path: Path, year: int) -> bool:
    """Return true when the file starts with the current exact GPL header."""
    return text.startswith(make_header(path, year))


def is_comment_line(line: str, prefix: str) -> bool:
    """Return true if the line is a comment line for this source type."""
    return line.lstrip().startswith(prefix)


def is_copyright_block_line(line: str) -> bool:
    """Return true if the line looks like part of a copyright/license block."""
    lower = line.lower()

    return (
        "copyright" in lower
        or "license" in lower
        or "river-mochi" in lower
        or "<copyright" in lower
        or "</copyright>" in lower
        or "all rights reserved" in lower
        or "all copies" in lower
        or "substantial portions" in lower
        or "project root" in lower
        or "license file" in lower
        or "license notice" in lower
        or "full license information" in lower
        or "full license info" in lower
        or "linking exception" in lower
        or "must be preserved" in lower
    )


def find_existing_header_range(text: str, prefix: str) -> tuple[int, int] | None:
    """Find a top-of-file River-Mochi copyright/license block to remove."""
    lines = text.split("\n")

    start = 0
    while start < len(lines) and lines[start].strip() == "":
        start += 1

    if start >= len(lines) or not is_comment_line(lines[start], prefix):
        return None

    end = start
    saw_copyright_text = False
    saw_explicit_end = False

    while end < len(lines):
        line = lines[end]

        if not is_comment_line(line, prefix):
            break

        if is_copyright_block_line(line):
            saw_copyright_text = True

        if "</copyright>" in line.lower():
            saw_explicit_end = True
            end += 1
            break

        end += 1

    if not saw_copyright_text:
        return None

    # Older files may have a short comment-only license header without the
    # explicit XML-style closing marker. Remove only the license portion.
    if not saw_explicit_end:
        while end > start and not is_copyright_block_line(lines[end - 1]):
            end -= 1

    while end < len(lines) and lines[end].strip() == "":
        end += 1

    return start, end


def remove_existing_header(text: str, prefix: str) -> tuple[str, bool]:
    """Remove an existing top-of-file copyright/license block."""
    header_range = find_existing_header_range(text, prefix)

    if header_range is None:
        return text, False

    start, end = header_range
    lines = text.split("\n")
    return "\n".join(lines[:start] + lines[end:]), True


def process_file(path: Path, year: int, replace_existing: bool) -> FileResult:
    """Return whether the file would change and the resulting text."""
    original_text, had_bom, had_crlf, utf8_error = read_utf8_text(path)

    if utf8_error:
        return FileResult(
            changed=False,
            new_text="",
            had_header=False,
            header_matches=False,
            header_replaced=False,
            header_added=False,
            had_bom=had_bom,
            had_crlf=had_crlf,
            utf8_error=True,
        )

    text = normalize_lf(original_text)
    prefix = get_comment_prefix(path)
    had_header = has_copyright_header(text)
    header_matches = has_exact_header(text, path, year)

    if header_matches:
        return FileResult(
            changed=had_bom or had_crlf or text != original_text,
            new_text=text,
            had_header=True,
            header_matches=True,
            header_replaced=False,
            header_added=False,
            had_bom=had_bom,
            had_crlf=had_crlf,
            utf8_error=False,
        )

    if had_header and not replace_existing:
        # Do not overwrite a different existing header unless explicitly asked.
        return FileResult(
            changed=had_bom or had_crlf or text != original_text,
            new_text=text,
            had_header=True,
            header_matches=False,
            header_replaced=False,
            header_added=False,
            had_bom=had_bom,
            had_crlf=had_crlf,
            utf8_error=False,
        )

    header_replaced = False
    header_added = False

    if had_header:
        text, header_replaced = remove_existing_header(text, prefix)

    if not header_replaced:
        header_added = True

    text = text.lstrip("\n")
    new_text = make_header(path, year) + text

    return FileResult(
        changed=had_bom or had_crlf or new_text != original_text,
        new_text=new_text,
        had_header=had_header,
        header_matches=False,
        header_replaced=header_replaced,
        header_added=header_added,
        had_bom=had_bom,
        had_crlf=had_crlf,
        utf8_error=False,
    )


def try_git_ls_files(root: Path) -> list[Path] | None:
    """Return tracked and untracked non-ignored files using git, or None on failure."""
    try:
        completed = subprocess.run(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            cwd=root,
            check=True,
            capture_output=True,
        )
    except (FileNotFoundError, subprocess.CalledProcessError):
        return None

    raw_paths = completed.stdout.split(b"\0")
    paths: list[Path] = []

    for raw_path in raw_paths:
        if not raw_path:
            continue

        try:
            relative_text = raw_path.decode("utf-8")
        except UnicodeDecodeError:
            relative_text = raw_path.decode(sys.getfilesystemencoding(), errors="replace")

        paths.append(root / relative_text)

    return paths


def walk_source_files(root: Path) -> list[Path]:
    """Fallback scanner that prunes skipped directories before entering them."""
    paths: list[Path] = []

    for current_root, dirnames, filenames in os.walk(root):
        current_path = Path(current_root)

        dirnames[:] = [
            dirname
            for dirname in dirnames
            if dirname.lower() not in SKIP_DIRS
        ]

        for filename in filenames:
            paths.append(current_path / filename)

    return paths


def iter_candidate_files(root: Path, use_git: bool) -> tuple[list[Path], str]:
    """Return candidate files and the scan method used."""
    if use_git:
        git_paths = try_git_ls_files(root)

        if git_paths is not None:
            return git_paths, "git ls-files"

    return walk_source_files(root), "os.walk fallback"


def print_summary(stats: RunStats, apply: bool) -> None:
    """Print a scan summary."""
    action_word = "Fixed" if apply else "Would fix"

    print()
    print("Summary")
    print("-------")
    print(f"Candidate files:      {stats.candidate_files}")
    print(f"Supported files:      {stats.supported_files}")
    print(f"Skipped files:        {stats.skipped_files}")
    print(f"Updated files:        {stats.updated_files}")
    print(f"Unchanged files:      {stats.unchanged_files}")
    print(f"Header added:         {stats.header_added}")
    print(f"Header replaced:      {stats.header_replaced}")
    print(f"Header mismatch:      {stats.header_mismatch}")
    print(f"UTF-8 BOM {action_word}:    {stats.bom_found}")
    print(f"CRLF {action_word}:         {stats.crlf_found}")
    print(f"UTF-8 decode errors:  {stats.utf8_errors}")
    print(f"Valid UTF-8:          {stats.utf8_errors == 0}")


def main() -> int:
    """Run the file-header tool."""
    default_root = find_repo_root(Path(__file__).resolve())

    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="Write changes.")
    parser.add_argument(
        "--check",
        action="store_true",
        help="Fail if headers, UTF-8, or LF formatting need attention.",
    )
    parser.add_argument(
        "--replace-existing",
        action="store_true",
        help="Replace old/different top-of-file River-Mochi headers.",
    )
    parser.add_argument(
        "--root",
        default=str(default_root),
        help="Repo root. Default: auto-detected from this script.",
    )
    parser.add_argument("--year", type=int, default=2026)
    parser.add_argument(
        "--no-git",
        action="store_true",
        help="Do not use git ls-files; use directory walk fallback instead.",
    )
    args = parser.parse_args()

    if args.apply and args.check:
        print("ERROR: Use either --apply or --check, not both.", file=sys.stderr)
        return 2

    root = Path(args.root).resolve()
    stats = RunStats()

    candidate_files, scan_method = iter_candidate_files(
        root=root,
        use_git=not args.no_git,
    )

    print(f"Scan root: {root}")
    print(f"Script path: {Path(__file__).resolve()}")
    print(f"Scan method: {scan_method}")

    for path in sorted(candidate_files):
        stats.candidate_files += 1

        if not path.is_file():
            continue

        try:
            rel = path.relative_to(root)
        except ValueError:
            stats.skipped_files += 1
            continue

        if not is_supported_source_file(rel):
            continue

        if should_skip(rel):
            stats.skipped_files += 1
            continue

        stats.supported_files += 1

        result = process_file(
            path=path,
            year=args.year,
            replace_existing=args.replace_existing,
        )

        if result.utf8_error:
            stats.utf8_errors += 1
            print(f"ERROR: Invalid UTF-8: {rel}")
            continue

        if result.had_header and not result.header_matches:
            stats.header_mismatch += 1

            if not args.replace_existing:
                print(f"Header differs: {rel}")

        if result.header_added:
            stats.header_added += 1

        if result.header_replaced:
            stats.header_replaced += 1

        if result.had_bom:
            stats.bom_found += 1

        if result.had_crlf:
            stats.crlf_found += 1

        if not result.changed:
            stats.unchanged_files += 1
            continue

        stats.updated_files += 1

        if args.apply:
            path.write_text(result.new_text, encoding="utf-8", newline="\n")
            print(f"Updated: {rel}")
        else:
            print(f"Would update: {rel}")

    print_summary(stats, apply=args.apply)

    if args.check:
        failures = stats.updated_files + stats.header_mismatch + stats.utf8_errors

        if failures:
            print()
            print("Header check failed.")
            return 1

        print()
        print("Header check passed.")
        return 0

    if not args.apply:
        print()
        print("Dry run only. Re-run with --apply to write safe changes.")
        if stats.header_mismatch:
            print("Use --replace-existing to replace old/different River-Mochi headers.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
