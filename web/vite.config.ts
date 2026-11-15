import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The build goes straight into the engine host's wwwroot. In development (npm run dev) the API calls are
// forwarded to a running app or DevHost on the default local port; open http://localhost:5173/?token=dev
// Two pages: the interface (index.html) and the in-game overlay drawn by the Tauri shell (overlay.html).
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../src/ChestCounter.Host/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 900,
    rollupOptions: { input: { main: 'index.html', overlay: 'overlay.html' } },
  },
  server: { proxy: { '/api': 'http://127.0.0.1:47821', '/obs': 'http://127.0.0.1:47821', '/treasure-img': 'http://127.0.0.1:47821' } },
});
