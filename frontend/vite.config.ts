import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "path";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  build: {
    chunkSizeWarningLimit: 900,
    rollupOptions: {
      output: {
        manualChunks: {
          react: ["react", "react-dom", "react-router-dom"],
          charts: ["recharts"],
          query: ["@tanstack/react-query"],
        },
      },
    },
  },
  server: {
    // PORT env var wins when a harness/launcher assigns a free port;
    // plain `npm run dev` still defaults to 5173.
    port: Number(process.env.PORT) || 5173,
    // Allow access from other machines / tunnels (share-demo.ps1 uses cloudflared).
    host: true, // listen on 0.0.0.0, not just localhost
    allowedHosts: true, // accept any Host header (trycloudflare.com etc.)
    proxy: {
      "/api": {
        target: "http://localhost:8080",
        changeOrigin: true,
      },
    },
  },
});