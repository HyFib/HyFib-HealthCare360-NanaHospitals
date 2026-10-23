"""Tests of patch_windows.py: audit fixtures and recorded dates in, pass or fail out (SRS SEC-11).

Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"
"""

import contextlib
import datetime as dt
import io
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import patch_windows as pw

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "patch-windows"
TODAY = "2026-10-23"


def run(*args):
    """Runs the check as CI does; returns its exit code and what it printed."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = pw.main(["--suppressions", str(FIXTURES / "suppress.props"), "--today", TODAY, *args])
    return code, out.getvalue()


def fixture(name):
    return str(FIXTURES / name)


class CheckTests(unittest.TestCase):
    def setUp(self):
        self.work = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.work)

    def record(self, name):
        path = self.work / "security-advisories.json"
        shutil.copy(FIXTURES / name, path)
        return str(path)

    def test_no_advisories_passes_without_a_record(self):
        code, out = run("--nuget", fixture("nuget-clean.json"), "--npm", fixture("npm-clean.json"),
                        "--record-file", str(self.work / "none.json"))
        self.assertEqual(code, 0, out)
        self.assertIn("No open security advisories.", out)

    def test_critical_advisory_first_seen_8_days_ago_fails(self):
        code, out = run("--nuget", fixture("nuget-high.json"), "--npm", fixture("npm-critical.json"),
                        "--record-file", self.record("record-overdue.json"))
        self.assertEqual(code, 1, out)
        self.assertIn("GHSA-crit-ical-0001 (critical) in npm:example-parser@2.1.0, first seen 2026-10-15, was due 2026-10-22: 1 day overdue.", out)
        # The high advisory, 20 days old, and the moderate one, 8 days old, are within 30 days.
        self.assertEqual(out.count("FAIL"), 1, out)

    def test_last_day_of_each_window_passes_with_a_warning(self):
        code, out = run("--nuget", fixture("nuget-high.json"), "--npm", fixture("npm-critical.json"),
                        "--record-file", self.record("record-in-time.json"))
        self.assertEqual(code, 0, out)
        self.assertIn("WARN  GHSA-crit-ical-0001 (critical) in npm:example-parser@2.1.0 is due 2026-10-23, in 0 days.", out)
        self.assertIn("WARN  GHSA-aaaa-bbbb-cccc (high) in nuget:System.Text.Example@8.0.0 is due 2026-10-23, in 0 days.", out)
        self.assertIn("3 open security advisories are within the patch window.", out)

    def test_other_advisory_31_days_old_fails(self):
        code, out = run("--nuget", fixture("nuget-high.json"), "--record-file", self.record("record-in-time.json"),
                        "--today", "2026-10-24")
        self.assertEqual(code, 1, out)
        self.assertIn("GHSA-aaaa-bbbb-cccc (high) in nuget:System.Text.Example@8.0.0, first seen 2026-09-23, was due 2026-10-23: 1 day overdue.", out)

    def test_unrecorded_advisory_fails_and_record_starts_its_clock(self):
        path = str(self.work / "security-advisories.json")
        code, out = run("--npm", fixture("npm-critical.json"), "--record-file", path)
        self.assertEqual(code, 1, out)
        self.assertIn("GHSA-crit-ical-0001 (critical) in npm:example-parser@2.1.0 is not recorded", out)

        code, out = run("--npm", fixture("npm-critical.json"), "--record-file", path, "--record")
        self.assertEqual(code, 0, out)
        saved = json.loads(Path(path).read_text(encoding="utf-8"))
        self.assertEqual([(a["id"], a["severity"], a["firstSeen"]) for a in saved["advisories"]],
                         [("GHSA-crit-ical-0001", "critical", TODAY), ("GHSA-mode-rate-0003", "moderate", TODAY)])

        # Recording again keeps the first day; 8 days later the critical one is overdue.
        run("--npm", fixture("npm-critical.json"), "--record-file", path, "--record")
        self.assertEqual(json.loads(Path(path).read_text(encoding="utf-8"))["advisories"][0]["firstSeen"], TODAY)
        code, out = run("--npm", fixture("npm-critical.json"), "--record-file", path, "--today", "2026-10-31")
        self.assertEqual(code, 1, out)

    def test_record_removes_advisories_no_longer_reported(self):
        path = self.record("record-overdue.json")
        code, out = run("--nuget", fixture("nuget-clean.json"), "--npm", fixture("npm-clean.json"), "--record-file", path)
        self.assertEqual(code, 0, out)
        self.assertIn("GHSA-crit-ical-0001 is recorded but no longer reported", out)
        run("--nuget", fixture("nuget-clean.json"), "--npm", fixture("npm-clean.json"), "--record-file", path, "--record")
        self.assertEqual(json.loads(Path(path).read_text(encoding="utf-8"))["advisories"], [])

    def test_unreadable_audit_fails_the_check(self):
        for args in (["--nuget", fixture("nuget-problem.json")], ["--npm", fixture("npm-error.json")],
                     ["--npm", str(self.work / "missing.json")]):
            code, out = run(*args, "--record-file", str(self.work / "none.json"))
            self.assertEqual(code, 2, out)
            self.assertIn("could not be read", out)


class ParsingTests(unittest.TestCase):
    def test_nuget_merges_projects_and_skips_suppressed_advisories(self):
        listing = json.loads(Path(fixture("nuget-high.json")).read_text(encoding="utf-8"))
        suppressed = pw.nuget_suppressions([fixture("suppress.props")])
        self.assertEqual(suppressed, ["https://github.com/advisories/GHSA-supp-ress-edxx"])
        found = pw.nuget_advisories(listing, suppressed)
        self.assertEqual(list(found), ["GHSA-aaaa-bbbb-cccc"])
        self.assertEqual(found["GHSA-aaaa-bbbb-cccc"]["packages"], {"nuget:System.Text.Example@8.0.0"})
        self.assertIn("GHSA-supp-ress-edxx", pw.nuget_advisories(listing))

    def test_npm_leaves_out_info_advisories(self):
        found = pw.npm_advisories(json.loads(Path(fixture("npm-critical.json")).read_text(encoding="utf-8")))
        self.assertEqual(sorted(found), ["GHSA-crit-ical-0001", "GHSA-mode-rate-0003"])

    def test_json_after_a_line_of_noise(self):
        with tempfile.TemporaryDirectory() as work:
            path = Path(work) / "audit.json"
            path.write_text("WARN  some banner\n" + Path(fixture("npm-clean.json")).read_text(encoding="utf-8"), encoding="utf-8")
            self.assertEqual(pw.npm_advisories(pw.read_json(path)), {})

    def test_windows(self):
        self.assertEqual(pw.window_days("critical"), 7)
        self.assertEqual(pw.window_days("unknown"), 7)
        self.assertEqual(pw.window_days(pw.normalize_severity("Medium")), 30)
        self.assertEqual(pw.window_days("low"), 30)

    def test_a_raised_severity_shortens_the_window(self):
        recorded = {"GHSA-x": {"id": "GHSA-x", "severity": "high", "firstSeen": "2026-10-10"}}
        open_now = {"GHSA-x": {"severity": "critical", "packages": {"npm:a@1.0.0"}}}
        result = pw.evaluate(open_now, recorded, dt.date(2026, 10, 18))
        self.assertEqual(len(result["failures"]), 1)

    def test_the_repository_record_is_valid(self):
        data = json.loads(pw.DEFAULT_RECORD.read_text(encoding="utf-8"))
        ids = [a["id"] for a in data["advisories"]]
        self.assertEqual(len(ids), len(set(ids)))
        for advisory in data["advisories"]:
            dt.date.fromisoformat(advisory["firstSeen"])
            self.assertIn(advisory["severity"], pw.SEVERITIES + ["unknown"])


if __name__ == "__main__":
    unittest.main()
