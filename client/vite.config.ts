import { defineConfig } from 'vitest/config';

export default defineConfig({
  base: process.env.GITHUB_ACTIONS === 'true' ? '/pixel-mmo-web/' : '/',
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
});
