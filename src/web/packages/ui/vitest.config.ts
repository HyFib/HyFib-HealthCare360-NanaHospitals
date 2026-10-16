import { configDefaults, defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // a11y/*.spec.ts are Playwright's: the browser checks in a browser (pnpm test:a11y).
    exclude: [...configDefaults.exclude, 'a11y/**'],
  },
});
