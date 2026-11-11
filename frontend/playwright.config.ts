import { defineConfig, devices } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { homedir } from 'node:os';
/** Launch two real tiers against a dedicated Mongo database; all journeys use serial workers. */
const password = process.env['E2E_ADMIN_PASSWORD'] ?? randomBytes(24).toString('base64url');
process.env['E2E_ADMIN_PASSWORD'] = password;
const backend = process.env['E2E_BACKEND_URL'] ?? 'http://127.0.0.1:5080';
const frontend = process.env['E2E_FRONTEND_URL'] ?? 'http://127.0.0.1:4200';
export default defineConfig({
  testDir: './e2e', workers: 1, fullyParallel: false, timeout: 60000,
  use: { baseURL: frontend, trace: 'retain-on-failure', screenshot: 'only-on-failure', launchOptions: { args: ['--no-sandbox'], ...(process.env['CHROME_BIN'] ? { executablePath: process.env['CHROME_BIN'] } : {}) } },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'] } }, { name: 'mobile', use: { ...devices['iPhone 13'], defaultBrowserType: 'chromium' } }],
  webServer: [
    { command: `"${homedir()}/.dotnet/dotnet" run --project ../backend/UserManagement.Api.csproj --no-launch-profile`, url: `${backend}/api/health`, reuseExistingServer: false, timeout: 120000,
      env: { ASPNETCORE_URLS: backend, ASPNETCORE_ENVIRONMENT: 'Testing', DOTNET_ROOT: `${homedir()}/.dotnet`, Mongo__Uri: process.env['TEST_MONGO_URI'] ?? 'mongodb://127.0.0.1:27017', Mongo__Database: process.env['E2E_MONGO_DATABASE'] ?? `umm_e2e_${randomBytes(12).toString('hex')}`, Jwt__Secret: randomBytes(64).toString('base64'), Jwt__Issuer: 'umm-e2e', Jwt__Audience: 'umm-e2e-browser', Jwt__ExpiryMinutes: '30', Bootstrap__Enabled: 'true', Bootstrap__Username: 'e2e-admin', Bootstrap__Password: password } },
    { command: `node node_modules/@angular/cli/bin/ng.js serve --host 127.0.0.1 --port ${new URL(frontend).port}`, url: frontend, reuseExistingServer: false, timeout: 120000, env: { API_PROXY_TARGET: backend } }
  ]
});
