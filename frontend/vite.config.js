import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// In development the API runs on https://localhost:7001 and Vite proxies /api to it,
// so the browser sees a single origin (cookies stay SameSite=Strict, no CORS needed).
// `npm run build` writes straight into the API's wwwroot for a single deployable app.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:7001',
        changeOrigin: true,
        secure: false, // accept the ASP.NET dev certificate
      },
    },
  },
  build: {
    outDir: '../backend/ComplaintPortal.Api/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
});
