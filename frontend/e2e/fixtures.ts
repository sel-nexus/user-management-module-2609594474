import { test as base, expect } from '@playwright/test';
/** Capture all unhandled browser errors; expected credential HTTP 401 resources are explicitly classified. */
export const test = base.extend<{ browserErrors: string[] }>({
  browserErrors: [async ({ page }, use) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && !/^Failed to load resource: the server responded with a status of 401/.test(message.text())) errors.push(message.text()); });
    await use(errors); expect(errors).toEqual([]);
  }, { auto: true }]
});
export { expect };
/** Require the explicitly provisioned test credential, never a hardcoded shipped secret. */
export function adminPassword(): string {
  const password = process.env['E2E_ADMIN_PASSWORD'];
  if (!password) throw new Error('Playwright config must provide E2E_ADMIN_PASSWORD.');
  return password;
}
