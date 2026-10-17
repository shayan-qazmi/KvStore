import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // Anything the page sends to /api/... is forwarded to the C# API on port 5080.
      // "/api/kv/name" becomes "/kv/name". This avoids the browser's CORS block.
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
})