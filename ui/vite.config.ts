import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

// Strict CSP for production builds only: the dev server needs inline scripts for HMR.
// Model output is rendered in this page, so nothing but our own bundle may execute (docs/SECURITY-MODEL.md).
const csp: Plugin = {
  name: "kairo-csp",
  apply: "build",
  transformIndexHtml: () => [
    {
      tag: "meta",
      attrs: {
        "http-equiv": "Content-Security-Policy",
        content:
          "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'",
      },
      injectTo: "head-prepend",
    },
  ],
};

export default defineConfig({
  plugins: [react(), csp],
  base: "./",
  server: { port: 5173, strictPort: true },
  build: { outDir: "dist", emptyOutDir: true, target: "es2022", chunkSizeWarningLimit: 1500 },
});
