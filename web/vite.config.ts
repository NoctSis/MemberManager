import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath, URL } from 'node:url'

// 相对 base：宿主经 WebView2 虚拟域(app.local) 或本地文件加载均可
export default defineConfig({
  base: './',
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    outDir: 'dist',
    // O5：分包后主包已降到 500KB 以下；保留告警，防止体积回退而无人察觉。
    chunkSizeWarningLimit: 500,
  },
})
