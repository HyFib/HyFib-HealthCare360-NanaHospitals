import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

// Every module of the staff app has its own folder under src/modules, and automated tests in it (SRS MNT-01). A new
// module folder without a single test file fails the build here.

const MODULES = join(import.meta.dirname, '..', 'modules');

/** Test files anywhere below `folder` (*.test.ts, *.test.tsx). */
function testFilesIn(folder: string): string[] {
  return readdirSync(folder, { withFileTypes: true, recursive: true })
    .filter((entry) => entry.isFile() && /\.test\.tsx?$/.test(entry.name))
    .map((entry) => join(entry.parentPath, entry.name));
}

const modules = readdirSync(MODULES, { withFileTypes: true })
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name)
  .sort();

describe('every staff module has tests (MNT-01)', () => {
  it('finds the module folders', () => {
    expect(modules.length).toBeGreaterThan(30);
    expect(modules).toContain('billing');
    expect(modules).toContain('doctor-fees');
  });

  it('has at least one test file in each module folder', () => {
    const untested = modules.filter((name) => testFilesIn(join(MODULES, name)).length === 0);
    expect(untested, `Module folders under src/modules with no *.test.ts(x): ${untested.join(', ')}`).toEqual([]);
  });
});
