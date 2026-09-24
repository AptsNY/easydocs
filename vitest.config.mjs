import { defineConfig } from 'vitest/config';
import TestDriver from 'testdriverai/vitest';

// Note: dotenv is loaded automatically by the TestDriver SDK
export default defineConfig({
  test: {
    // Only run the TestDriver end-to-end tests in tests/testdriver/.
    // This keeps vitest away from the repo's existing .NET test project.
    include: ['tests/testdriver/**/*.test.mjs'],
    testTimeout: 900000,
    hookTimeout: 900000,
    reporters: [
      'default',
      TestDriver(),
    ],
    setupFiles: ['testdriverai/vitest/setup'],
  },
});
