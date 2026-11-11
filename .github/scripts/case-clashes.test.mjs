import { test } from "node:test";
import assert from "node:assert/strict";
import { caseClashes } from "./case-clashes.mjs";

test("a component and a module whose names differ only in case clash", () => {
  assert.deepEqual(
    caseClashes([
      "web/patients/AbdmReceivedRecords.tsx",
      "web/patients/abdmReceivedRecords.ts",
    ]),
    [
      [
        "web/patients/AbdmReceivedRecords.tsx",
        "web/patients/abdmReceivedRecords.ts",
      ],
    ],
  );
});

test("a module and its test, or the same name with another extension, do not clash", () => {
  assert.deepEqual(
    caseClashes([
      "web/a/records.ts",
      "web/a/records.test.ts",
      "web/a/Records.css",
      "web/a/view.ts",
      "web/a/view.tsx",
    ]),
    [],
  );
});

test("two files whose paths differ only in case clash, as do two folders", () => {
  assert.deepEqual(caseClashes(["docs/Readme.md", "docs/README.md"]), [
    ["docs/README.md", "docs/Readme.md"],
  ]);
  assert.deepEqual(caseClashes(["src/Modules/a.cs", "src/modules/b.cs"]), [
    ["src/Modules/", "src/modules/"],
  ]);
});

test("names in different folders do not clash", () => {
  assert.deepEqual(caseClashes(["web/a/Records.tsx", "web/b/records.ts"]), []);
});
