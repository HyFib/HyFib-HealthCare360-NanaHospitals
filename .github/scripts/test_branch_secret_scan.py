"""Tests of branch_secret_scan.py: throwaway repositories in, and a stub in place of gitleaks that records what it is
given and answers as each test tells it, so these tests hold no secret-like value for the real scan to find.

Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"
"""

import contextlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import branch_secret_scan as bss

HERE = Path(__file__).resolve().parent
MAIN = "refs/remotes/origin/main"
GIT_ENV = {"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1", "GIT_AUTHOR_NAME": "t",
           "GIT_AUTHOR_EMAIL": "t@example.invalid", "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.invalid"}

# Stands in for gitleaks: records its arguments and the files they name, then answers as the plan says for the ref it
# was given. By default it reports scanning the commits git has for it, as gitleaks does.
STUB = """#!{python}
import json, os, sys
args = sys.argv[1:]
def value(flag):
    return args[args.index(flag) + 1]
ref, _, main = value("--log-opts").split()
ignore = os.path.join(value("--gitleaks-ignore-path"), ".gitleaksignore")
with open(os.environ["STUB_LOG"], "a") as log:
    log.write(json.dumps({{"args": args, "ref": ref, "config": open(value("--config")).read(),
                           "ignore": open(ignore).read() if os.path.exists(ignore) else None}}) + "\\n")
plan = json.load(open(os.environ["STUB_PLAN"])).get(ref, {{}})
sys.path.insert(0, os.environ["STUB_SCRIPTS"])
import branch_secret_scan
scanned = plan.get("scanned", "auto")
if scanned == "auto":
    scanned = branch_secret_scan.scannable_commits(args[-1], ref, main)
print(f"INF {{scanned}} commits scanned.", file=sys.stderr)
if plan.get("exit"):
    print("FTL it went wrong", file=sys.stderr)
    sys.exit(plan["exit"])
with open(value("--report-path"), "w") as report:
    json.dump(plan.get("findings", []), report)
"""


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True,
                          env={**os.environ, **GIT_ENV}).stdout


def commit(repo, message, **files):
    """Writes the files (None deletes one; a '__' in a name stands for '/') and commits them."""
    for name, text in files.items():
        path = repo / name.replace("__", "/")
        if text is None:
            path.unlink()
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "--no-gpg-sign", "-m", message)
    return git(repo, "rev-parse", "HEAD").strip()


def finding(**overrides):
    return {"RuleID": "generic-api-key", "Commit": "0123456789abcdef0123456789abcdef01234567", "File": "src/a.txt",
            "StartLine": 3, "Secret": "REDACTED", "Fingerprint": "0123456789abcdef:src/a.txt:generic-api-key:3",
            **overrides}


@unittest.skipIf(os.name == "nt", "the stub gitleaks is a script run through its #! line")
class BranchScanTests(unittest.TestCase):
    """A clone whose main holds its own .gitleaks.toml and .gitleaksignore, with these refs beside it:

    feature   adds text, and its own .gitleaks.toml and .gitleaksignore
    noconfig  adds text, and deletes both files
    cleanup   only deletes a file: nothing for gitleaks to read
    merged    nothing main lacks
    v0.1      an annotated tag on a commit main lacks
    """

    def setUp(self):
        self.work = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.work)
        repo = self.repo = self.work / "repo"
        repo.mkdir()
        git(repo, "init", "-q", "-b", "main")
        base = commit(repo, "main", **{".gitleaks.toml": "title = 'main'\n", ".gitleaksignore": "main-ignore\n",
                                       "README": "hello\n"})
        git(repo, "update-ref", MAIN, base)
        git(repo, "symbolic-ref", "refs/remotes/origin/HEAD", MAIN)
        git(repo, "update-ref", "refs/remotes/origin/merged", base)
        for name, files in [
            ("feature", {".gitleaks.toml": "title = 'feature'\n", ".gitleaksignore": "feature-ignore\n",
                         "src__feature.txt": "a feature\n"}),
            ("noconfig", {".gitleaks.toml": None, ".gitleaksignore": None, "notes.txt": "notes\n"}),
            ("cleanup", {"README": None}),
        ]:
            git(repo, "checkout", "-q", "-b", name, base)
            git(repo, "update-ref", f"refs/remotes/origin/{name}", commit(repo, name, **files))
        git(repo, "checkout", "-q", "-b", "release", base)
        commit(repo, "release", **{"src__release.txt": "a release\n"})
        git(repo, "tag", "-a", "v0.1", "-m", "v0.1")
        git(repo, "checkout", "-q", "--detach", base)
        self.stub = self.work / "gitleaks"
        self.stub.write_text(STUB.format(python=sys.executable))
        self.stub.chmod(0o755)
        self.log = self.work / "stub.log"
        self.summary = self.work / "summary.md"

    def run_scan(self, plan=None, github=False, repo=None, *extra):
        (self.work / "plan.json").write_text(json.dumps(plan or {}))
        out = io.StringIO()
        env = {**GIT_ENV, "GITHUB_ACTIONS": "true" if github else "false", "GITHUB_STEP_SUMMARY": str(self.summary),
               "STUB_LOG": str(self.log), "STUB_PLAN": str(self.work / "plan.json"), "STUB_SCRIPTS": str(HERE)}
        with mock.patch.dict(os.environ, env), contextlib.redirect_stdout(out):
            code = bss.main(["--gitleaks", str(self.stub), "--repo", str(repo or self.repo), *extra])
        return code, out.getvalue()

    def calls(self):
        if not self.log.exists():
            return {}
        return {call["ref"]: call for call in map(json.loads, self.log.read_text().splitlines())}

    def test_lists_only_the_refs_holding_commits_main_lacks(self):
        self.assertEqual(bss.refs_to_scan(self.repo, MAIN), [
            "refs/remotes/origin/cleanup", "refs/remotes/origin/feature", "refs/remotes/origin/noconfig",
            "refs/tags/v0.1"])

    def test_counts_only_the_commits_that_add_text_to_a_file_they_keep(self):
        self.assertEqual(bss.scannable_commits(self.repo, "refs/remotes/origin/feature", MAIN), 1)
        self.assertEqual(bss.scannable_commits(self.repo, "refs/remotes/origin/cleanup", MAIN), 0)

    def test_scans_each_ref_against_its_own_allowlist(self):
        code, out = self.run_scan()
        self.assertEqual(code, 0, out)
        calls = self.calls()
        self.assertEqual(sorted(calls), ["refs/remotes/origin/feature", "refs/remotes/origin/noconfig", "refs/tags/v0.1"])
        feature = calls["refs/remotes/origin/feature"]
        self.assertIn("refs/remotes/origin/feature --not refs/remotes/origin/main", feature["args"])
        self.assertEqual((feature["config"], feature["ignore"]), ("title = 'feature'\n", "feature-ignore\n"))
        noconfig = calls["refs/remotes/origin/noconfig"]
        self.assertEqual((noconfig["config"], noconfig["ignore"]), ("title = 'main'\n", None))
        tag = calls["refs/tags/v0.1"]
        self.assertEqual((tag["config"], tag["ignore"]), ("title = 'main'\n", "main-ignore\n"))
        self.assertIn("4 branch(es) or tag(s) hold commits main does not; gitleaks read 3", out)

    def test_a_finding_fails_the_scan_and_is_listed(self):
        code, out = self.run_scan({"refs/remotes/origin/feature": {"findings": [finding()]}}, github=True)
        self.assertEqual(code, 1, out)
        self.assertIn("::error title=Secret in a commit main does not hold::origin/feature: src/a.txt:3 in commit "
                      "0123456789ab (generic-api-key); fingerprint 0123456789abcdef:src/a.txt:generic-api-key:3", out)
        summary = self.summary.read_text()
        self.assertIn("| origin/feature | 1 | own | 1 |", summary)
        self.assertIn("| origin/feature | 0123456789ab | src/a.txt | 3 | generic-api-key |", summary)

    def test_a_crafted_file_name_cannot_start_a_workflow_command(self):
        crafted = finding(File="x.txt\n::add-mask::hidden|<b>")
        code, out = self.run_scan({"refs/remotes/origin/feature": {"findings": [crafted]}}, github=True)
        self.assertEqual(code, 1, out)
        commands = [line for line in out.splitlines() if line.startswith("::")]
        self.assertTrue(commands and all(line.startswith("::error title=") for line in commands), out)
        self.assertIn("x.txt%0A::add-mask::hidden|<b>", out)
        self.assertIn("| x.txt ::add-mask::hidden\\|&lt;b&gt; |", self.summary.read_text())

    def test_gitleaks_reading_fewer_commits_than_git_has_is_an_error(self):
        code, out = self.run_scan({"refs/remotes/origin/feature": {"scanned": 0}})
        self.assertEqual(code, 2, out)
        self.assertIn("ERROR origin/feature: gitleaks read 0 commits, but git has 1 for it to read", out)

    def test_a_gitleaks_failure_is_reported_and_the_other_refs_are_still_scanned(self):
        code, out = self.run_scan({"refs/remotes/origin/feature": {"exit": 1},
                                   "refs/tags/v0.1": {"findings": [finding(File="src/release.txt")]}})
        self.assertEqual(code, 2, out)
        self.assertIn("ERROR origin/feature: gitleaks exited with 1: FTL it went wrong", out)
        self.assertIn("FOUND tags/v0.1: src/release.txt:3", out)
        self.assertIn("refs/remotes/origin/noconfig", self.calls())

    def test_a_shallow_clone_is_refused(self):
        shallow = self.work / "shallow"
        subprocess.run(["git", "clone", "-q", "--depth", "1", f"file://{self.repo}", str(shallow)], check=True,
                       capture_output=True, env={**os.environ, **GIT_ENV})
        code, out = self.run_scan(repo=shallow)
        self.assertEqual(code, 2, out)
        self.assertIn("the clone is shallow", out)
        self.assertEqual(self.calls(), {})

    def test_main_must_be_in_the_clone(self):
        code, out = self.run_scan(None, False, None, "--main", "refs/remotes/origin/nowhere")
        self.assertEqual(code, 2, out)
        self.assertIn("refs/remotes/origin/nowhere is not in the clone", out)


if __name__ == "__main__":
    unittest.main()
