#!/usr/bin/env python3
"""Patch windows for security advisories (SRS SEC-11; docs/security/SECURITY.md, "Patching security advisories").

An advisory against a package HC360 uses is patched within 7 days of the day it was first seen when it is critical,
and within 30 days otherwise. .github/security-advisories.json records the day each open advisory was first seen. This
check reads what the audits report now, and fails when:

  - an advisory is reported that the file does not record (record it with --record, in the same change), or
  - a critical advisory was first seen more than 7 days ago, or any other more than 30 days ago.

It warns 3 days before a window ends, and names recorded advisories the audits no longer report (--record removes
them). An advisory accepted as not affecting HC360 is left out, as the audits leave it out: a NuGetAuditSuppress item
in Directory.Packages.props, or an entry under auditConfig in src/web/pnpm-workspace.yaml (pnpm audit applies that
one itself).

  dotnet list HyFib.HealthCare360.slnx package --vulnerable --include-transitive --format json > nuget-audit.json
  (cd src/web && pnpm audit --json) > npm-audit.json
  python3 .github/scripts/patch_windows.py --nuget nuget-audit.json --npm npm-audit.json [--record] [--today YYYY-MM-DD]

Exit codes: 0 every advisory is within its window; 1 one is not, or is not recorded; 2 an audit could not be read.
Python 3.9 or later, standard library only. Tests: python3 -m unittest discover -s .github/scripts -p "test_*.py"
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_RECORD = REPO / ".github" / "security-advisories.json"
DEFAULT_SUPPRESSIONS = [REPO / "Directory.Packages.props", REPO / "Directory.Build.props"]

SEVERITIES = ["critical", "high", "moderate", "low"]
CRITICAL_DAYS = 7
OTHER_DAYS = 30
WARN_DAYS = 3

ABOUT = (
    "The day each open security advisory was first seen (SRS SEC-11). Written by .github/scripts/patch_windows.py "
    "--record; see docs/security/SECURITY.md, 'Patching security advisories'. Do not change a firstSeen date by hand."
)


class AuditError(Exception):
    """An audit's output that cannot be read: the check cannot say what is open."""


def normalize_severity(value) -> str:
    severity = str(value or "").strip().lower()
    if severity == "medium":
        return "moderate"
    return severity if severity in SEVERITIES else "unknown"


def worst(a: str, b: str) -> str:
    rank = lambda s: SEVERITIES.index(s) if s in SEVERITIES else len(SEVERITIES)  # noqa: E731
    return a if rank(a) <= rank(b) else b


def window_days(severity: str) -> int:
    # An advisory of unknown severity gets the shorter window: it may be critical.
    return CRITICAL_DAYS if severity in ("critical", "unknown") else OTHER_DAYS


def advisory_id(url) -> str:
    """The GHSA id in an advisory URL (https://github.com/advisories/GHSA-xxxx-xxxx-xxxx), or the URL itself."""
    match = re.search(r"GHSA(-[0-9a-z]{4}){3}", str(url or ""), re.IGNORECASE)
    return "GHSA" + match.group(0)[4:].lower() if match else str(url)


def _add(found: dict, aid: str, severity: str, package: str) -> None:
    entry = found.setdefault(aid, {"severity": severity, "packages": set()})
    entry["severity"] = worst(entry["severity"], severity)
    entry["packages"].add(package)


def nuget_advisories(listing, suppressed=()) -> dict:
    """Advisories in `dotnet list package --vulnerable --include-transitive --format json`, less suppressed ones."""
    if not isinstance(listing, dict) or not isinstance(listing.get("projects"), list):
        raise AuditError("not the output of dotnet list package --vulnerable --format json")
    errors = [p.get("text", "") for p in listing.get("problems") or [] if p.get("level") == "error"]
    if errors:
        raise AuditError("dotnet list could not check every project: " + "; ".join(errors))
    skip = {advisory_id(url) for url in suppressed}
    found: dict = {}
    for project in listing["projects"]:
        for framework in project.get("frameworks") or []:
            for pkg in (framework.get("topLevelPackages") or []) + (framework.get("transitivePackages") or []):
                for vulnerability in pkg.get("vulnerabilities") or []:
                    aid = advisory_id(vulnerability.get("advisoryurl"))
                    if aid not in skip:
                        _add(found, aid, normalize_severity(vulnerability.get("severity")),
                             f"nuget:{pkg.get('id')}@{pkg.get('resolvedVersion')}")
    return found


def npm_advisories(audit) -> dict:
    """Advisories in `pnpm audit --json`. Those of severity info do not fail the audit, so they are left out too."""
    if not isinstance(audit, dict) or not isinstance(audit.get("advisories"), dict):
        reason = (audit or {}).get("error", {}) if isinstance(audit, dict) else {}
        raise AuditError("not the output of pnpm audit --json" + (f": {reason.get('summary') or reason}" if reason else ""))
    found: dict = {}
    for advisory in audit["advisories"].values():
        if str(advisory.get("severity", "")).lower() == "info":
            continue
        aid = advisory.get("github_advisory_id") or advisory_id(advisory.get("url")) or f"npm-{advisory.get('id')}"
        versions = sorted({f.get("version") for f in advisory.get("findings") or [] if f.get("version")}) or ["?"]
        for version in versions:
            _add(found, aid, normalize_severity(advisory.get("severity")), f"npm:{advisory.get('module_name')}@{version}")
    return found


def nuget_suppressions(paths) -> list:
    """The advisory URLs of the NuGetAuditSuppress items in MSBuild files."""
    urls = []
    for path in paths:
        if Path(path).is_file():
            text = Path(path).read_text(encoding="utf-8")
            text = re.sub(r"<!--.*?-->", "", text, flags=re.DOTALL)
            for match in re.finditer(r"<NuGetAuditSuppress\b[^>]*?\bInclude\s*=\s*([\"'])(.*?)\1", text, re.DOTALL):
                urls += [u.strip() for u in match.group(2).split(";") if u.strip()]
    return urls


def read_json(path) -> object:
    """An audit's JSON, which may follow a line of noise (a banner or a warning)."""
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
    except OSError as error:
        raise AuditError(f"{path}: {error.strerror}") from error
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end < start:
            raise AuditError(f"{path}: no JSON in the file") from None
        try:
            return json.loads(text[start:end + 1])
        except json.JSONDecodeError as error:
            raise AuditError(f"{path}: {error}") from None


def evaluate(open_advisories: dict, recorded: dict, today: dt.date) -> dict:
    """What the check finds: failures, warnings, and recorded advisories that are no longer open."""
    failures, warnings, rows = [], [], []
    for aid in sorted(open_advisories):
        found = open_advisories[aid]
        packages = ", ".join(sorted(found["packages"]))
        entry = recorded.get(aid)
        if entry is None:
            failures.append(f"{aid} ({found['severity']}) in {packages} is not recorded in .github/security-advisories.json:"
                            " run patch_windows.py with --record and commit the file.")
            rows.append((aid, found["severity"], packages, "–", "–", "not recorded"))
            continue
        # The stricter of what was recorded and what the audit says now: a raised severity shortens the window.
        severity = worst(found["severity"], normalize_severity(entry.get("severity")))
        first_seen = dt.date.fromisoformat(entry["firstSeen"])
        due = first_seen + dt.timedelta(days=window_days(severity))
        left = (due - today).days
        if left < 0:
            failures.append(f"{aid} ({severity}) in {packages}, first seen {first_seen}, was due {due}: "
                            f"{-left} day{'s' if left != -1 else ''} overdue.")
            status = "overdue"
        elif left <= WARN_DAYS:
            warnings.append(f"{aid} ({severity}) in {packages} is due {due}, in {left} day{'s' if left != 1 else ''}.")
            status = "due soon"
        else:
            status = "in time"
        rows.append((aid, severity, packages, str(first_seen), str(due), status))
    gone = sorted(set(recorded) - set(open_advisories))
    return {"failures": failures, "warnings": warnings, "rows": rows, "gone": gone}


def load_record(path: Path) -> dict:
    if not path.is_file():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    recorded = {}
    for entry in data.get("advisories", []):
        dt.date.fromisoformat(entry["firstSeen"])  # a bad date fails here, not later
        recorded[entry["id"]] = entry
    return recorded


def write_record(path: Path, open_advisories: dict, recorded: dict, today: dt.date) -> None:
    """Adds the open advisories not yet recorded, first seen today, and removes those no longer open."""
    entries = []
    for aid in sorted(open_advisories):
        found = open_advisories[aid]
        entry = dict(recorded.get(aid) or {"id": aid, "firstSeen": today.isoformat()})
        entry["severity"] = worst(found["severity"], normalize_severity(entry.get("severity", found["severity"])))
        entry["packages"] = sorted(found["packages"])
        entries.append({k: entry[k] for k in ("id", "severity", "firstSeen", "packages")} | {
            k: v for k, v in entry.items() if k not in ("id", "severity", "firstSeen", "packages")})
    path.write_text(json.dumps({"about": ABOUT, "advisories": entries}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def report(result: dict, github: bool) -> None:
    for message in result["failures"]:
        print(f"::error title=Security patch overdue or not recorded::{message}" if github else f"FAIL  {message}")
    for message in result["warnings"]:
        print(f"::warning title=Security patch due soon::{message}" if github else f"WARN  {message}")
    for aid in result["gone"]:
        print(f"note  {aid} is recorded but no longer reported: remove it with --record.")
    if not result["rows"]:
        print("No open security advisories.")
    elif not result["failures"]:
        print(f"{len(result['rows'])} open security advisor{'y is' if len(result['rows']) == 1 else 'ies are'} within the patch window.")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if github and summary:
        lines = ["### Security patch windows (SEC-11)", ""]
        if result["rows"]:
            lines += ["| Advisory | Severity | Packages | First seen | Due | Status |", "|---|---|---|---|---|---|"]
            lines += ["| " + " | ".join(c.replace("|", "\\|") for c in row) + " |" for row in result["rows"]]
        else:
            lines.append("No open security advisories.")
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n\n")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--nuget", help="output of dotnet list package --vulnerable --include-transitive --format json")
    parser.add_argument("--npm", help="output of pnpm audit --json")
    parser.add_argument("--record-file", type=Path, default=DEFAULT_RECORD, help="default: .github/security-advisories.json")
    parser.add_argument("--suppressions", type=Path, action="append",
                        help="MSBuild files with NuGetAuditSuppress items (default: Directory.Packages.props, Directory.Build.props)")
    parser.add_argument("--today", type=dt.date.fromisoformat, default=dt.datetime.now(dt.timezone.utc).date(),
                        help="the day to check against, YYYY-MM-DD (default: today, UTC)")
    parser.add_argument("--record", action="store_true", help="record new advisories as first seen today; remove closed ones")
    args = parser.parse_args(argv)
    github = os.environ.get("GITHUB_ACTIONS") == "true"
    if not args.nuget and not args.npm:
        parser.error("give --nuget, --npm or both")
    try:
        open_advisories: dict = {}
        if args.nuget:
            suppressed = nuget_suppressions(args.suppressions or DEFAULT_SUPPRESSIONS)
            for aid, found in nuget_advisories(read_json(args.nuget), suppressed).items():
                for package in found["packages"]:
                    _add(open_advisories, aid, found["severity"], package)
        if args.npm:
            for aid, found in npm_advisories(read_json(args.npm)).items():
                for package in found["packages"]:
                    _add(open_advisories, aid, found["severity"], package)
        recorded = load_record(args.record_file)
    except (AuditError, ValueError, KeyError) as error:
        message = f"The open advisories could not be read: {error}"
        print(f"::error title=Security patch windows not checked::{message}" if github else f"ERROR {message}")
        return 2
    if args.record:
        write_record(args.record_file, open_advisories, recorded, args.today)
        recorded = load_record(args.record_file)
        print(f"Recorded {len(open_advisories)} open advisor{'y' if len(open_advisories) == 1 else 'ies'} in {args.record_file}.")
    result = evaluate(open_advisories, recorded, args.today)
    report(result, github)
    return 1 if result["failures"] else 0


if __name__ == "__main__":
    sys.exit(main())
