import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The build goes straight into the desktop app's wwwroot. In development (npm run dev) the API calls are
// forwarded to a running app or DevHost on the default local port; open http://localhost:5173/?token=dev
export default defineConfig({
  plugins: [react()],
  base: './',
  build: { outDir: '../src/ChestCounter.App/wwwroot', emptyOutDir: true, chunkSizeWarningLimit: 900 },
  server: { proxy: { '/api': 'http://127.0.0.1:47821', '/obs': 'http://127.0.0.1:47821' } },
});
