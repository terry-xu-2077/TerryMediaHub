import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 前端开发/构建配置。构建产物输出到 server 的 web 目录，由 Go 服务静态托管。
// 开发时通过 /api 代理到后端（默认 8090）。
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../server/internal/webui/dist',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8090',
        changeOrigin: true,
      },
    },
  },
})
