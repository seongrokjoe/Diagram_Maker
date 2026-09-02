import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { copyFileSync, createReadStream, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const projectDirectory = dirname(fileURLToPath(import.meta.url));
const mermaidRuntime = resolve(projectDirectory, "node_modules/mermaid/dist/mermaid.min.js");

export default defineConfig({
  plugins: [
    react(),
    {
      name: "local-mermaid-runtime",
      configureServer(server) {
        server.middlewares.use("/vendor/mermaid.min.js", (_request, response) => {
          response.setHeader("Content-Type", "text/javascript; charset=utf-8");
          createReadStream(mermaidRuntime).pipe(response);
        });
      },
      closeBundle() {
        const vendorDirectory = resolve(projectDirectory, "dist/vendor");
        mkdirSync(vendorDirectory, { recursive: true });
        copyFileSync(mermaidRuntime, resolve(vendorDirectory, "mermaid.min.js"));
      },
    },
  ],
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5080",
      "/health": "http://localhost:5080",
    },
  },
  build: {
    sourcemap: false,
    reportCompressedSize: false,
    minify: false,
    cssMinify: false,
  },
});
