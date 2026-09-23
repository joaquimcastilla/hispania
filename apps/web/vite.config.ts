import { defineConfig } from "vitest/config";
import { loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const environment = loadEnv(mode, process.cwd(), "");

  return {
    plugins: [react()],
    server: {
      proxy: {
        "/api": {
          target: environment.HISPANIA_API_URL ?? "http://127.0.0.1:5070",
          changeOrigin: true
        }
      }
    },
    test: {
      environment: "jsdom",
      setupFiles: "./src/test/setup.ts"
    }
  };
});
