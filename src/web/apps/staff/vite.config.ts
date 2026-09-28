/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The staff UI is served by the HC360 Server (same origin as the API) in production. During development Vite
// proxies /api, /hubs and /health to the local server (self-signed LAN certificate → secure: false).
const server = process.env.HC360_SERVER ?? 'https://localhost:7360';

export default defineConfig({
  plugins: [tanstackRouter({ target: 'react', autoCodeSplitting: true }), react()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5360,
    strictPort: true,
    proxy: {
      '/api': { target: server, changeOrigin: true, secure: false },
      '/hubs': { target: server, changeOrigin: true, secure: false, ws: true },
      '/health': { target: server, changeOrigin: true, secure: false },
    },
  },
  build: {
    outDir: '../../../server/HyFib.HealthCare360.Server/wwwroot',
    emptyOutDir: false,
    sourcemap: false,
    chunkSizeWarningLimit: 1200,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
