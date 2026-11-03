import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build-Output geht direkt in den WinForms-Host (wird dort als wwwroot ausgeliefert).
// target esnext: noVNC benutzt Top-Level-await; WebView2 ist Chromium und kann das.
export default defineConfig({
  plugins: [react()],
  base: './',
  server: { port: 5173, strictPort: true },
  optimizeDeps: { esbuildOptions: { target: 'esnext' } },
  build: {
    target: 'esnext',
    outDir: '../src/MultiServerView.App/wwwroot',
    emptyOutDir: true,
  },
});
