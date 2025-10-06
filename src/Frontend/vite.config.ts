import { defineConfig } from "vite";

export default defineConfig({
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5240",
      "/hub": {
        target: "http://localhost:5240",
        ws: true
      }
    }
  }
});
