/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The patient portal is served by the HC360 Server at /portal (same origin as the API). During development Vite
// proxies /api, /hubs and /health to the local server (self-signed LAN certificate → secure: false), exactly like
// the staff app, so the portal and the staff app can run side by side (ports 5361 and 5360).
const server = process.env.HC360_SERVER ?? 'https://localhost:7360';

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
  },
});
