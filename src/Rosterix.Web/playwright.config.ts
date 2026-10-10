import { defineConfig } from '@playwright/test';

// Starts the API (in-memory DB, seeded) and the SPA if they are not already running.
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:4200',
    launchOptions: { executablePath: process.env['PW_CHROMIUM'] || undefined, args: ['--no-sandbox'] },
    trace: 'retain-on-failure',
  },
  webServer: [
    { command: 'dotnet run --project ../Rosterix.Api --urls http://localhost:5199', cwd: '.', url: 'http://localhost:5199/health', reuseExistingServer: true, timeout: 180_000, env: { UseInMemory: 'true', ASPNETCORE_ENVIRONMENT: 'Development' } },
    { command: 'npm start -- --port 4200 --host 127.0.0.1', url: 'http://localhost:4200', reuseExistingServer: true, timeout: 240_000 },
  ],
});
