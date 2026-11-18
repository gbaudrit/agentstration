import { defineConfig } from 'vite';
export default defineConfig({define:{"process.env.NODE_ENV":JSON.stringify("production")},build:{outDir:'../wwwroot',emptyOutDir:true,lib:{entry:'island.tsx',formats:['es'],fileName:'system-explorer',cssFileName:'agentstration-system-explorer'},rollupOptions:{output:{chunkFileNames:'chunks/[name]-[hash].js'}}}});
