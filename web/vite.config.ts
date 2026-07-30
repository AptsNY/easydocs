import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const target = 'http://localhost:8080'
const proxy = { target, changeOrigin: true }

// In production the API serves the built SPA from wwwroot, so these paths are same-origin. In dev the
// Vite server must forward them or nothing works: the session lives in an httpOnly cookie, which a
// cross-origin fetch would not send.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Vite matches string keys as PREFIXES, so plain '/s' would also capture '/src/main.tsx' (breaking
      // the dev server outright) and the client-side '/settings' route — hence the regexes below. The
      // string prefixes here have no such collision; note '/docs' does not prefix-match '/documents/…'.
      ...Object.fromEntries(['/api', '/wopi', '/health', '/openapi', '/docs'].map((p) => [p, proxy])),

      '^/s/[^/]+/download$': proxy,

      // Mirrors the API's own content negotiation (ShareEndpoints.PublicView, spec §11): a browser
      // navigating to a share link gets the SPA shell, and the SPA then re-requests the same URL as
      // JSON. In dev the shell has to come from Vite so the app has HMR and current sources, rather
      // than from the wwwroot/index.html of whenever someone last ran `npm run build`.
      '^/s/[^/]+$': {
        ...proxy,
        bypass: (req) => (req.headers.accept?.includes('text/html') ? '/index.html' : undefined),
      },
    },
  },
})
