import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { brotliCompressSync, constants } from 'node:zlib';
import { serveFakeApi } from '@hc360/ui/testing/browser';
import { expect, test } from '@playwright/test';
import { SHELL_SCREEN_ANSWERS } from '../src/test/accessibility/fixtures/shell';
import { createFakeFetch, problem } from '../src/test/fakeApi';

// SRS PERF-03: the staff app loads within 3 s the first time and within 1 s from the cache. The time is the browser's
// work on what it fetches: over a 20 Mbit/s link the sign-in page's files (516 KB as the server compresses them, October
// 2026) take a quarter of a second, and parsing and running them on a slow workstation the rest. Measured against the
// server on a WiFi-like link with the CPU four times slower, that page was ready in 1.6 s the first time and 0.82 s from
// the cache (docs/testing/LOAD-TESTS.md), so the cached second is the tighter of the two, and it grows with the code the
// page runs: about 630 KB would take it to a second. A screen's own code loads when it is opened, and is not counted.

/** At most this many bytes, Brotli-compressed at the server's level, for everything the sign-in page fetches. */
const BUDGET_BYTES = 620 * 1024;

const DIST = fileURLToPath(new URL('../dist/', import.meta.url));

/** The bytes the server would send for a file of the built app: Brotli at its fastest level (HospitalCompression). */
async function sentBytes(pathname: string): Promise<number> {
  const file = await readFile(DIST + (pathname === '/' || pathname === '/login' ? 'index.html' : pathname.slice(1)));
  return brotliCompressSync(file, { params: { [constants.BROTLI_PARAM_QUALITY]: 1 } }).length;
}

test('the sign-in page fetches no more than its budget', async ({ page, baseURL }) => {
  const server = createFakeFetch({
    'GET /api/v1/system/info': SHELL_SCREEN_ANSWERS['GET /api/v1/system/info'],
    'POST /api/v1/auth/refresh': problem(401, 'Sign in again'),
    'GET /health/live': 'Healthy',
  });
  await serveFakeApi(page, server.fetch);
  const fetched = new Set<string>();
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (
      url.origin === new URL(baseURL!).origin &&
      !url.pathname.startsWith('/api/') &&
      !url.pathname.startsWith('/health')
    ) {
      fetched.add(url.pathname);
    }
  });

  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.waitForLoadState('networkidle');

  const sizes = await Promise.all([...fetched].map(async (path) => [path, await sentBytes(path)] as const));
  const total = sizes.reduce((sum, [, bytes]) => sum + bytes, 0);
  const largest = [...sizes]
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)
    .map(([path, bytes]) => `${path} ${(bytes / 1024).toFixed(1)} KB`);
  test.info().annotations.push({
    type: 'sign-in page',
    description: `${sizes.length} files, ${(total / 1024).toFixed(1)} KB as sent; largest: ${largest.join(', ')}`,
  });
  expect(
    total,
    `The sign-in page fetches ${(total / 1024).toFixed(1)} KB; largest: ${largest.join(', ')}`,
  ).toBeLessThanOrEqual(BUDGET_BYTES);
});
