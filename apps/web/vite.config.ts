import { defineConfig } from "vitest/config";
import { loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const environment = loadEnv(mode, process.cwd(), "");
  const buildVariant = mode === "classic" ? "classic" : "modern";

  return {
    build: {
      outDir: `dist/${buildVariant}`,
      emptyOutDir: false,
      manifest: true,
      rollupOptions: { input: buildVariant === "classic" ? "index.classic.html" : "index.html" }
    },
    define: {
      __HISPANIA_WEB_VARIANT__: JSON.stringify(buildVariant)
    },
    plugins: [react()],
    appType: "mpa",
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
