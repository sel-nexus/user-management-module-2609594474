import { randomBytes } from 'node:crypto';
import { test, expect, adminPassword } from './fixtures';
import { DashboardSummary } from '../src/app/shared/api.models';
import { Page } from '@playwright/test';

/** Let the initial real grid finish rendering before a filter submit can cancel it. */
async function openUsers(page: Page): Promise<void> {
  const initialGrid = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'GET');
  await page.getByRole('link', { name: 'Users', exact: true }).click();
  const response = await initialGrid; expect(response.status()).toBe(200);
  const data: { items: { username: string }[]; limit: number } = await response.json();
  expect(data.limit).toBe(20);
  expect(data.items.length).toBeGreaterThan(0);
  await expect(page.getByRole('status').filter({ hasText: 'Loading accounts' })).toHaveCount(0);
  for (const user of data.items) await expect(page.getByRole('cell', { name: user.username, exact: true })).toBeVisible();
}

/** Verify every displayed metric and country against the real HTTP summary. */
async function assertSummary(page: Page, data: DashboardSummary): Promise<void> {
  for (const [id, value] of Object.entries({ total: data.total, active: data.active, inactive: data.inactive, activity: data.activity, 'login-success': data.login_success, 'login-failure': data.login_failure })) await expect(page.getByTestId(id)).toHaveText(String(value));
  await expect(page.getByTestId('generated-at')).toHaveAttribute('datetime', data.generated_at);
  await expect(page.getByTestId('window-start')).toHaveAttribute('datetime', data.window_start);
  expect(Date.parse(data.generated_at) - Date.parse(data.window_start)).toBe(7 * 86400000);
  expect(data.total).toBe(data.active + data.inactive);
  expect(data.country_distribution.reduce((sum, row) => sum + row.count, 0)).toBe(data.total);
  await expect(page.locator('[data-country]')).toHaveCount(data.country_distribution.length);
  for (const row of data.country_distribution) {
    const country = page.locator('[data-country]').filter({ has: page.getByRole('cell', { name: row.country, exact: true }) });
    await expect(country.getByRole('cell').nth(1)).toHaveText(String(row.count));
  }
  if (data.activity === 0) await expect(page.getByRole('status').filter({ hasText: 'No recorded activity' })).toBeVisible();
  if (data.total === 0) await expect(page.getByRole('status').filter({ hasText: 'No accounts' })).toBeVisible();
  if (!data.country_distribution.length) await expect(page.getByRole('status').filter({ hasText: 'No country data' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

/** Desktop and mobile projects use the shared error fixture, live tiers and actual clicks after entry. */
test('login → create → dashboard → edit country → deactivate → updated persisted totals; loading and offline retry', async ({ page, browserErrors, expectedOfflineError }) => {
  const username = `dashboard-${randomBytes(8).toString('hex')}`;
  const country = `Country-${randomBytes(8).toString('hex')}`;
  const dashboardRequests: string[] = [];
  page.on('request', r => { if (new URL(r.url()).pathname === '/api/dashboard') dashboardRequests.push(r.method()); });
  await page.goto('/');
  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin'); await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const login = page.waitForResponse(r => new URL(r.url()).pathname === '/api/login' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); expect((await login).status()).toBe(200);
  const initialRequest = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard');
  await page.getByRole('link', { name: 'View dashboard', exact: true }).click(); const initial = await initialRequest; expect(initial.status()).toBe(200); const baseline: DashboardSummary = await initial.json(); await assertSummary(page, baseline);
  await expect(page.getByRole('link', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-current', 'page');
  await page.getByRole('link', { name: 'Back to session' }).click(); await page.getByRole('link', { name: 'Provision a new account' }).click();
  await page.getByLabel('Username (required)', { exact: true }).fill(username); await page.getByLabel('Password (required)', { exact: true }).fill(randomBytes(24).toString('base64url'));
  // The blank optional country is persisted as an empty string and must increment the Unknown group.
  const create = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Create account', exact: true }).click(); const created = await create; expect(created.status()).toBe(201); const account = await created.json(); expect(account.country).toBe('');
  const network = await page.context().newCDPSession(page); await network.send('Network.enable');
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 700, downloadThroughput: -1, uploadThroughput: -1 });
  const afterCreateRequest = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard');
  await page.getByRole('link', { name: 'Dashboard', exact: true }).click(); await expect(page.getByRole('status').filter({ hasText: 'Loading dashboard' })).toBeVisible(); await expect(page.getByTestId('total')).toHaveCount(0); await expect(page.getByTestId('generated-at')).toHaveCount(0);
  const afterCreateResponse = await afterCreateRequest; expect(afterCreateResponse.status()).toBe(200); expect(afterCreateResponse.request().method()).toBe('GET'); expect(new URL(afterCreateResponse.url()).search).toBe(''); expect(afterCreateResponse.request().headers()['authorization']).toMatch(/^Bearer /);
  const afterCreate: DashboardSummary = await afterCreateResponse.json(); await assertSummary(page, afterCreate); expect(afterCreate.total).toBe(baseline.total + 1); expect(afterCreate.active).toBe(baseline.active + 1); expect(afterCreate.inactive).toBe(baseline.inactive);
  expect(afterCreate.country_distribution.find(x => x.country === 'Unknown')?.count).toBe((baseline.country_distribution.find(x => x.country === 'Unknown')?.count ?? 0) + 1);
  expect(afterCreate.activity).toBe(baseline.activity + 1); expect(afterCreate.login_success).toBe(baseline.login_success); expect(afterCreate.login_failure).toBe(baseline.login_failure);
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await openUsers(page); await page.getByLabel('Search username or city').fill(username);
  const search = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'GET'); await page.getByRole('button', { name: 'Apply filters' }).click(); expect((await search).status()).toBe(200);
  await page.getByRole('link', { name: username, exact: true }).click(); await page.getByLabel('Country', { exact: true }).fill(country);
  const edit = page.waitForResponse(r => new URL(r.url()).pathname === `/api/users/${account.id}` && r.request().method() === 'PUT'); await page.getByRole('button', { name: 'Save changes', exact: true }).click(); const edited = await edit; expect(edited.status()).toBe(200); expect((await edited.json()).country).toBe(country);
  const afterEditRequest = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard'); await page.getByRole('link', { name: 'Dashboard', exact: true }).click(); const afterEditResponse = await afterEditRequest; expect(afterEditResponse.status()).toBe(200); const afterEdit: DashboardSummary = await afterEditResponse.json(); await assertSummary(page, afterEdit);
  expect(afterEdit.total).toBe(afterCreate.total); expect(afterEdit.active).toBe(afterCreate.active); expect(afterEdit.country_distribution.find(x => x.country === country)?.count).toBe(1); expect(afterEdit.country_distribution.find(x => x.country === 'Unknown')?.count ?? 0).toBe(baseline.country_distribution.find(x => x.country === 'Unknown')?.count ?? 0);
  await openUsers(page); await page.getByLabel('Search username or city').fill(username);
  const searchAgain = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'GET'); await page.getByRole('button', { name: 'Apply filters' }).click(); expect((await searchAgain).status()).toBe(200);
  await page.getByRole('link', { name: username, exact: true }).click(); await page.getByRole('button', { name: 'Deactivate account', exact: true }).click();
  const deactivate = page.waitForResponse(r => new URL(r.url()).pathname === `/api/users/${account.id}` && r.request().method() === 'DELETE'); await page.getByRole('button', { name: 'Confirm deactivate', exact: true }).click(); expect((await deactivate).status()).toBe(204); await expect(page.getByTestId('account-status')).toHaveText('Inactive');
  const afterDeactivateRequest = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard'); await page.getByRole('link', { name: 'Dashboard', exact: true }).click(); const afterDeactivateResponse = await afterDeactivateRequest; expect(afterDeactivateResponse.status()).toBe(200); const final: DashboardSummary = await afterDeactivateResponse.json(); await assertSummary(page, final);
  // Three read_user events (edit entry, deactivate entry, post-DELETE reload), plus update_user and deactivate_user.
  expect(final.total).toBe(afterCreate.total); expect(final.active).toBe(baseline.active); expect(final.inactive).toBe(baseline.inactive + 1); expect(final.country_distribution).toEqual(afterEdit.country_distribution); expect(final.activity).toBe(afterCreate.activity + 5);
  // A real network disconnection, not a route stub: classify only the expected failed-resource console entry.
  await page.context().setOffline(true);
  await expectedOfflineError({ path: '/api/dashboard', method: 'GET', count: 1 }, async () => {
    const [failed] = await Promise.all([
      page.waitForEvent('requestfailed', request => new URL(request.url()).pathname === '/api/dashboard' && request.method() === 'GET'),
      page.getByRole('button', { name: 'Refresh dashboard' }).click()
    ]);
    await expect(page.getByRole('alert')).toContainText('Dashboard is unavailable');
    await expect(page.getByTestId('total')).toHaveCount(0);
    await expect(page.getByTestId('generated-at')).toHaveCount(0);
    return failed;
  });
  await page.context().setOffline(false); const retry = page.waitForResponse(r => new URL(r.url()).pathname === '/api/dashboard'); await page.getByRole('button', { name: 'Retry', exact: true }).click(); const recovered = await retry; expect(recovered.status()).toBe(200); const refreshed: DashboardSummary = await recovered.json(); await assertSummary(page, refreshed); expect(refreshed.total).toBe(final.total); expect(refreshed.activity).toBe(final.activity); await expect(page.getByRole('alert')).toHaveCount(0);
  expect(dashboardRequests.every(method => method === 'GET')).toBe(true); expect(browserErrors).toEqual([]); await network.detach();
});
