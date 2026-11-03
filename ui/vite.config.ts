import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build-Output geht direkt in den WinForms-Host (wird dort als wwwroot ausgeliefert).
export default defineConfig({
  plugins: [react()],
  base: './',
  server: { port: 5173, strictPort: true },
  build: {
    outDir: '../src/MultiServerView.App/wwwroot',
    emptyOutDir: true,
  },
});
