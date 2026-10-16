import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

// Shared 2K Canvas UI files (theme tokens, icons) are imported from the sibling Canvas checkout
// at build time and bundled; nothing from that checkout is needed at runtime.
const canvasUi = fileURLToPath(new URL('../../source/native/ui', import.meta.url));

export default defineConfig({
  resolve: { alias: { '@canvas': canvasUi }, dedupe: ['react', 'react-dom'] },
  server: { fs: { allow: ['.', canvasUi] } },
  build: { target: 'es2022', chunkSizeWarningLimit: 2048 },
});
