import { defineConfig } from '@playwright/test'

// Drives the REAL app: the API on :8080, the SPA served by Vite with the proxy in vite.config.ts. CI
// instead boots the compose stack and sets E2E_BASE_URL to point at the shipped image — see Task 18.
// This is also what closes spec §12.3's headless-browser-driver requirement, which M0-M4 substituted
// away by speaking Collabora's WOPI conversation directly.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
  },
  webServer: process.env.E2E_BASE_URL
    ? undefined
    : { command: 'npm run dev', url: 'http://localhost:5173', reuseExistingServer: true },
})
