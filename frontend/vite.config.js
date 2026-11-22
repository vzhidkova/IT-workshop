import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  // Адрес backend LogHive. По умолчанию это http-адрес из документации API (порт 5300).
  //   VITE_API_TARGET=http://localhost:5300
  //   VITE_API_TARGET=https://localhost:7205
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_TARGET || 'http://localhost:5300';

  return {
    plugins: [react()],
    server: {
      // Запросы /api/... уходят на backend через Vite. 
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          // У dev-сертификата ASP.NET нет доверенной подписи (адрес https://localhost:7205).
          secure: false
        }
      },
      watch: {
        ignored: ['**/.vs/**']
      }
    }
  };
});
