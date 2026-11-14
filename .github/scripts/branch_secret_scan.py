#!/usr/bin/env python3
"""Secret scan of the commits main does not hold (docs/security/SECURITY.md, "Secret scan").

CI scans the history of each change, so a commit on a branch that never has a pull request is scanned by nothing there.
This scans, for every branch and tag, the commits main does not have, with gitleaks, against that ref's own
.gitleaks.toml and .gitleaksignore (main's .gitleaks.toml when the ref has none), as its pull request would be. It scans
every ref before it fails, and lists each finding (redacted) in the job summary and as an annotation. Branch names and
file paths are escaped there, as whoever pushes a branch chooses them.

gitleaks passes when git cannot read what it was asked to (an unknown ref is "no leaks found"), so each scan is checked:
gitleaks must report scanning exactly the commits git has for it, those that add text to a file they keep.

  python3 .github/scripts/branch_secret_scan.py --gitleaks path/to/gitleaks [--main refs/remotes/origin/main]

Run it in a clone with every branch's whole history (actions/checkout with fetch-depth: 0). Exit codes: 0 nothing found;
1 a finding; 2 a ref, or the clone, could not be scanned. Python 3.9 or later, standard library only.
Tests: python3 -m unittest discover -s .github/scripts -p "test_*.py"
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

CONFIG = ".gitleaks.toml"
IGNORE = ".gitleaksignore"
SCANNED = re.compile(r"(\d+) commits scanned")


class ScanError(Exception):
    """Something the scan could not read: what it says cannot be trusted."""


@dataclass
class RefResult:
    ref: str
    commits: int = 0  # the commits gitleaks should read: those that add text to a file they keep
    allowlist: str = ""  # "own" or "main's"
    findings: list = field(default_factory=list)
    error: str = ""


def git(repo: Path, *args: str) -> bytes:
    proc = subprocess.run(["git", "-C", str(repo), *args], capture_output=True)
    if proc.returncode != 0:
        raise ScanError(f"git {' '.join(args)}: {proc.stderr.decode('utf-8', 'replace').strip()}")
    return proc.stdout


def is_commit(repo: Path, ref: str) -> bool:
    return subprocess.run(["git", "-C", str(repo), "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"],
                          capture_output=True).returncode == 0


def refs_to_scan(repo: Path, main: str) -> list:
    """The branches and tags that hold a commit main does not."""
    refs = []
    listed = git(repo, "for-each-ref", "--format=%(refname)", "refs/remotes/origin", "refs/tags")
    for ref in listed.decode("utf-8", "surrogateescape").split():
        if ref in (main, "refs/remotes/origin/HEAD"):
            continue
        if int(git(repo, "rev-list", "--count", ref, "--not", main)) > 0:
            refs.append(ref)
    return refs


def scannable_commits(repo: Path, ref: str, main: str) -> int:
    """The commits in `ref --not main` that gitleaks reads: those that add text to a file they keep. It does not count a
    merge (git log -p shows it no diff), nor a commit that only deletes, renames or changes a mode, or that adds an empty
    or a binary file."""
    count, adds_text, deleted = 0, False, False
    for line in git(repo, "log", "-p", "-U0", "--format=COMMIT %H", ref, "--not", main).splitlines():
        if line.startswith(b"COMMIT "):
            count += adds_text
            adds_text = False
        elif line.startswith(b"diff --git "):
            deleted = False
        elif line.startswith(b"deleted file mode"):
            deleted = True
        elif line.startswith(b"@@ ") and not deleted:
            adds_text = True
    return count + adds_text


def ref_file(repo: Path, ref: str, name: str):
    """The file as the ref holds it, or None."""
    proc = subprocess.run(["git", "-C", str(repo), "show", f"{ref}:{name}"], capture_output=True)
    return proc.stdout if proc.returncode == 0 else None


def scan_ref(repo: Path, gitleaks: str, ref: str, main: str, main_config: bytes, workdir: Path) -> RefResult:
    result = RefResult(ref, scannable_commits(repo, ref, main))
    own = ref_file(repo, ref, CONFIG)
    result.allowlist = "own" if own is not None else "main's"
    if result.commits == 0:
        return result
    work = Path(tempfile.mkdtemp(dir=workdir))
    config, ignore_dir, report = work / CONFIG, work / "ignore", work / "report.json"
    config.write_bytes(own if own is not None else main_config)
    ignore_dir.mkdir()
    ignore = ref_file(repo, ref, IGNORE)
    if ignore is not None:
        (ignore_dir / IGNORE).write_bytes(ignore)
    proc = subprocess.run(
        [gitleaks, "git", "--config", str(config), "--gitleaks-ignore-path", str(ignore_dir), "--redact", "--no-banner",
         "--no-color", "--exit-code", "0", "--report-format", "json", "--report-path", str(report),
         "--log-opts", f"{ref} --not {main}", str(repo)],
        capture_output=True)
    output = (proc.stdout + proc.stderr).decode("utf-8", "replace")
    scanned = SCANNED.search(output)
    if proc.returncode != 0:
        last = output.strip().splitlines()[-1:] or [""]
        result.error = f"gitleaks exited with {proc.returncode}: {last[0]}"
    elif scanned is None or int(scanned.group(1)) != result.commits:
        result.error = (f"gitleaks read {scanned.group(1) if scanned else 'no'} commits, but git has {result.commits} for "
                        "it to read")
    try:
        result.findings = json.loads(report.read_text(encoding="utf-8")) if report.exists() else []
    except ValueError as error:
        result.error = result.error or f"its report could not be read: {error}"
    if not result.error and not report.exists():
        result.error = "gitleaks wrote no report"
    return result


def oneline(text) -> str:
    """Text from a branch on one line: a newline in a file name must not start a line of the log."""
    return str(text).replace("\r", "\\r").replace("\n", "\\n")


def command_data(text) -> str:
    """Text for a workflow command (::error::...), escaped as @actions/core escapes it, so it cannot end the command
    early or start another one."""
    return str(text).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def cell(text) -> str:
    """Text for a cell of a Markdown table in the job summary."""
    return (str(text).replace("|", "\\|").replace("\r", " ").replace("\n", " ")
            .replace("<", "&lt;").replace(">", "&gt;"))


def short(ref: str) -> str:
    """origin/<branch> for a branch, tags/<tag> for a tag."""
    return re.sub(r"^refs/(remotes/)?", "", ref)


def report(results: list, github: bool) -> None:
    for result in results:
        for finding in result.findings:
            where = (f"{short(result.ref)}: {finding.get('File')}:{finding.get('StartLine')} in commit "
                     f"{str(finding.get('Commit'))[:12]} ({finding.get('RuleID')}); fingerprint {finding.get('Fingerprint')}")
            print(f"::error title=Secret in a commit main does not hold::{command_data(where)}" if github
                  else f"FOUND {oneline(where)}")
        if result.error:
            message = f"{short(result.ref)}: {result.error}"
            print(f"::error title=Branch not scanned for secrets::{command_data(message)}" if github
                  else f"ERROR {oneline(message)}")
    found = sum(len(r.findings) for r in results)
    print(f"{len(results)} branch(es) or tag(s) hold commits main does not; gitleaks read {sum(r.commits for r in results)} "
          f"of their commits and found {found} secret(s).")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if not (github and summary):
        return
    lines = ["### Secrets in the commits main does not hold", ""]
    if not results:
        lines.append("No branch or tag holds a commit main does not.")
    else:
        lines += ["| Branch or tag | Commits read | Allowlist | Secrets found |", "|---|---|---|---|"]
        lines += [f"| {cell(short(r.ref))} | {r.commits} | {r.allowlist} | "
                  f"{cell('not scanned: ' + r.error) if r.error else len(r.findings)} |" for r in results]
    if found:
        lines += ["", "| Branch or tag | Commit | File | Line | Rule | Fingerprint |", "|---|---|---|---|---|---|"]
        lines += [f"| {cell(short(r.ref))} | {cell(str(f.get('Commit'))[:12])} | {cell(f.get('File'))} | "
                  f"{cell(f.get('StartLine'))} | {cell(f.get('RuleID'))} | {cell(f.get('Fingerprint'))} |"
                  for r in results for f in r.findings]
        lines += ["", "A real secret is revoked at its provider first, then removed from the branch's history. A value "
                      "reviewed as not secret goes into that branch's .gitleaks.toml, or the branch is deleted."]
    with open(summary, "a", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n\n")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--gitleaks", required=True, help="the gitleaks binary")
    parser.add_argument("--main", default="refs/remotes/origin/main",
                        help="the ref whose commits CI has scanned already (default: %(default)s)")
    parser.add_argument("--repo", type=Path, default=Path("."), help="the clone (default: the current directory)")
    args = parser.parse_args(argv)
    github = os.environ.get("GITHUB_ACTIONS") == "true"
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    try:
        if git(args.repo, "rev-parse", "--is-shallow-repository").strip() != b"false":
            raise ScanError("the clone is shallow, so older commits would go unscanned: fetch every branch's whole history "
                            "(fetch-depth: 0)")
        if not is_commit(args.repo, args.main):
            raise ScanError(f"{args.main} is not in the clone")
        main_config = ref_file(args.repo, args.main, CONFIG)
        if main_config is None:
            raise ScanError(f"{args.main} has no {CONFIG}")
        refs = refs_to_scan(args.repo, args.main)
    except ScanError as error:
        print(f"::error title=Secret scan not run::{command_data(error)}" if github else f"ERROR {oneline(error)}")
        return 2
    results = []
    with tempfile.TemporaryDirectory() as workdir:
        for ref in refs:
            try:
                results.append(scan_ref(args.repo, args.gitleaks, ref, args.main, main_config, Path(workdir)))
            except ScanError as error:
                results.append(RefResult(ref, error=str(error)))
    report(results, github)
    if any(r.error for r in results):
        return 2
    return 1 if any(r.findings for r in results) else 0


if __name__ == "__main__":
    sys.exit(main())
