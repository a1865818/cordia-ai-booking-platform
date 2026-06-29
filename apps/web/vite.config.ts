import { defineConfig } from "vitest/config";
import { loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_");
  // Point `bun nx serve web` at any API (local or hosted) via VITE_API_PROXY_TARGET.
  const proxyTarget = env.VITE_API_PROXY_TARGET ?? "https://localhost:8001";

  // Production builds can be served under a path prefix (e.g. behind a CDN).
  // Override via VITE_BASE_PATH — process.env for CI shell injection,
  // .env files for local dev, default for everything else.
  const base = process.env.VITE_BASE_PATH ?? env.VITE_BASE_PATH ?? "/";

  return {
    base,
    plugins: [react(), tailwindcss()],
    server: {
      port: 4200,
      proxy: {
        "/api": {
          target: proxyTarget,
          changeOrigin: true,
          secure: false,
        },
      },
    },
    build: {
      outDir: "../../dist/apps/web",
      emptyOutDir: true,
    },
    test: {
      globals: true,
      environment: "jsdom",
      setupFiles: ["./src/test-setup.ts"],
      coverage: {
        provider: "v8",
        reporter: ["text", "lcov"],
      },
    },
  };
});
