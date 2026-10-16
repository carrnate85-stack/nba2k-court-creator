import { defineConfig, type Plugin } from 'vite';
import { fileURLToPath } from 'node:url';

// Shared 2K Canvas UI files (theme tokens, icons, editor components and styles) are imported from
// the sibling Canvas checkout at build time and bundled; nothing from that checkout is needed at runtime.
const canvasUi = fileURLToPath(new URL('../../source/native/ui', import.meta.url));

/** Scopes Canvas's editor stylesheet (`editor.css?scoped`) to the Edit Artwork window so its
 * page-level rules never restyle the court studio. Keyframes stay global. */
function scopedCanvasCss(): Plugin {
  return {
    name: 'court-scoped-canvas-css',
    enforce: 'pre',
    transform(code, id) {
      if (!id.replace(/\\/g, '/').endsWith('/editor.css?scoped')) return null;
      const keyframes: string[] = [];
      let rest = '', i = 0;
      while (i < code.length) {
        const at = code.indexOf('@keyframes', i);
        if (at < 0) { rest += code.slice(i); break; }
        rest += code.slice(i, at);
        let depth = 0, j = code.indexOf('{', at);
        for (; j < code.length; j++) { if (code[j] === '{') depth++; else if (code[j] === '}' && --depth === 0) break; }
        keyframes.push(code.slice(at, j + 1)); i = j + 1;
      }
      return { code: `${keyframes.join('\n')}\n.artwork-editor{${rest}}`, map: null };
    },
  };
}

export default defineConfig({
  plugins: [scopedCanvasCss()],
  resolve: { alias: { '@canvas': canvasUi }, dedupe: ['react', 'react-dom'] },
  server: { fs: { allow: ['.', canvasUi] } },
  // chrome107 makes esbuild flatten the nested (scoped) Canvas rules into plain selectors.
  build: { target: 'es2022', cssTarget: 'chrome107', chunkSizeWarningLimit: 2048 },
});
