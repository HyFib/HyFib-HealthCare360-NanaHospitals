// Files whose names differ only in letter case are two files on Linux, where CI runs most checks, and one file on
// Windows, where the release is built. AbdmReceivedRecords.tsx beside abdmReceivedRecords.ts did that: on Windows the
// import of either found the other, and the staff app no longer type-checked. This lists every such pair among the files
// git tracks, and exits 1 when there is one:
// - two modules whose names differ only in case once the code extension is left out (X.tsx and x.ts), since an import
//   names a module without its extension;
// - two files, or two folders, whose paths differ only in case.
//
// Usage: node .github/scripts/case-clashes.mjs   (from anywhere in the repository)
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const codeExtension = /\.(?:tsx?|jsx?|mjs|cjs|mts|cts)$/;

/** Groups of tracked paths that are the same on a file system that ignores case. */
export function caseClashes(paths) {
  const spellings = new Map();
  const add = (key, spelling) => {
    const seen = spellings.get(key) ?? new Set();
    seen.add(spelling);
    spellings.set(key, seen);
  };
  for (const path of paths) {
    const parts = path.split("/");
    for (let i = 1; i < parts.length; i++) {
      const folder = parts.slice(0, i).join("/");
      add(`folder:${folder.toLowerCase()}`, `${folder}/`);
    }
    add(`file:${path.toLowerCase()}`, path);
    if (codeExtension.test(path)) {
      add(`module:${path.replace(codeExtension, "").toLowerCase()}`, path);
    }
  }
  const clashes = [];
  for (const [key, seen] of spellings) {
    const names = [...seen].sort();
    const differ = key.startsWith("module:")
      ? new Set(names.map((name) => name.replace(codeExtension, ""))).size > 1
      : names.length > 1;
    if (differ) {
      clashes.push(names);
    }
  }
  return clashes;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const paths = execFileSync("git", ["ls-files", "-z"], {
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  })
    .split("\0")
    .filter(Boolean);
  const clashes = caseClashes(paths);
  for (const names of clashes) {
    console.error(
      `These are one file on Windows, where the release is built: ${names.join(", ")}. Rename one.`,
    );
  }
  if (clashes.length > 0) {
    process.exit(1);
  }
  console.log(
    `No two of the ${paths.length} tracked files differ only in case.`,
  );
}
