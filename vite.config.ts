import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vitejs.dev/config/
export default defineConfig({
  base: '/GitDiff/',
  plugins: [react()],
  optimizeDeps: {
    include: ['@isomorphic-git/lightning-fs']
  },
  define: {
    global: 'globalThis',
  },
  resolve: {
    alias: {
      buffer: 'buffer',
    }
  }
})
