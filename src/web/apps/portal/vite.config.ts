import { fileURLToPath, URL } from 'node:url';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { configDefaults } from 'vitest/config';

// The patient portal is served by the HC360 Server at /portal on an address of its own (port 7361; the staff app's is
// 7360), which answers only the portal's API. During development Vite proxies /api, /hubs and /health to that address
// of the local server (self-signed LAN certificate → secure: false), so the portal and the staff app can run side by
// side (ports 5361 and 5360).
const server = process.env.HC360_PORTAL_SERVER ?? 'https://localhost:7361';

export default defineConfig({
  base: '/portal/',
  plugins: [tanstackRouter({ target: 'react', autoCodeSplitting: true }), react()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5361,
    strictPort: true,
    proxy: {
      '/api': { target: server, changeOrigin: true, secure: false },
      '/hubs': { target: server, changeOrigin: true, secure: false, ws: true },
      '/health': { target: server, changeOrigin: true, secure: false },
    },
  },
  build: {
    // The portal owns this folder only; the staff app builds into the parent wwwroot and never touches it.
    outDir: '../../../server/HyFib.HealthCare360.Server/wwwroot/portal',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1200,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // a11y/*.spec.ts are Playwright's: the built app in a browser (pnpm test:a11y).
    exclude: [...configDefaults.exclude, 'a11y/**'],
    // Page tests boot the whole router in jsdom and wait up to 8 s for a page (SLOW in app.test.tsx). When the staff
    // and portal suites run side by side (pnpm -r test, as CI does) that can take longer than the 5 s default.
    testTimeout: 20_000,
  },
});
