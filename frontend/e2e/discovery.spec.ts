import { randomBytes } from 'node:crypto';
import { Page, Response } from '@playwright/test';
import { test, expect, adminPassword } from './fixtures';

/** Match only real GET discovery responses, never create or another API feature. */
const listResponse = (response: Response): boolean => new URL(response.url()).pathname === '/api/users' && response.request().method() === 'GET';
/** Finish the real grid response and wait for its backend usernames before another activation. */
async function assertGridReady(page: Page, response: Response): Promise<void> {
  expect(response.status()).toBe(200);
  const data: { items: { username: string }[]; limit: number } = await response.json();
  expect(data.limit).toBe(20);
  expect(data.items.length).toBeGreaterThan(0);
  await expect(page.getByRole('status').filter({ hasText: 'Loading accounts' })).toHaveCount(0);
  for (const user of data.items) await expect(page.getByRole('cell', { name: user.username, exact: true })).toBeVisible();
}
/** Navigate through the real login form and assert that the backend issued an Admin session. */
async function login(page: Page): Promise<void> {
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
  await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const response = page.waitForResponse(r => r.url().endsWith('/api/login') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const result = await response; expect(result.status()).toBe(200); expect((await result.json()).user.role).toBe('Admin');
  await expect(page.getByRole('heading', { name: 'Signed in as e2e-admin' })).toBeVisible();
}
/** Submit real filter controls and return the server page for content assertions. */
async function apply(page: Page): Promise<{ items: { id: string; username: string; city: string; country: string; education: string }[]; total: number; limit: number; skip: number }> {
  const response = page.waitForResponse(listResponse);
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
  const result = await response; expect(result.status()).toBe(200); return await result.json();
}

/** Prove real login, create, discover, filter, sort, page and selection against persisted data on both configured viewports. */
test('Admin discovers persisted accounts through real filters, sorting and paging without bulk/export', async ({ page, browserErrors, expectedOfflineError }) => {
  const prefix = `discover-${randomBytes(6).toString('hex')}`;
  await page.goto('/'); await page.getByRole('link', { name: 'Go to sign in' }).click(); await login(page);
  // Provision through the existing UI, not a fixture repository or mocked API response.
  const provisionGrid = page.waitForResponse(listResponse);
  await page.getByRole('link', { name: 'Users', exact: true }).click();
  await assertGridReady(page, await provisionGrid);
  await page.getByRole('link', { name: 'Create account', exact: true }).click();
  const created: { id: string; username: string; city: string; country: string; education: string }[] = [];
  for (const suffix of ['a', 'b']) {
    await page.getByLabel('Username (required)', { exact: true }).fill(`${prefix}-${suffix}`);
    await page.getByLabel('Password (required)', { exact: true }).fill(randomBytes(20).toString('base64url'));
    await page.getByLabel('City', { exact: true }).fill(suffix === 'a' ? 'Example A' : 'Example B');
    await page.getByLabel('Country', { exact: true }).fill('Example country');
    await page.getByLabel('Education', { exact: true }).fill('Example degree');
    const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Create account', exact: true }).click(); const result = await response; expect(result.status()).toBe(201);
    created.push(await result.json()); await expect(page.getByRole('status')).toContainText(created[created.length - 1].id);
  }
  // Apply actual network latency, not response interception, to make the real pending state observable.
  const network = await page.context().newCDPSession(page);
  await network.send('Network.enable');
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 800, downloadThroughput: -1, uploadThroughput: -1 });
  const initial = page.waitForResponse(listResponse);
  await page.getByRole('link', { name: 'Users', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Loading accounts' })).toBeVisible();
  const first = await initial; expect(first.status()).toBe(200); expect((await first.json()).limit).toBe(20);
  await assertGridReady(page, first);
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await expect(page.getByRole('heading', { name: 'Users', exact: true })).toBeVisible();
  await page.getByLabel('Search username or city', { exact: true }).fill(prefix.toUpperCase());
  const searched = await apply(page); expect(searched.total).toBe(2); expect(searched.items.map(u => u.id)).toEqual(created.map(u => u.id));
  for (const u of created) await expect(page.getByRole('cell', { name: u.username, exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const region = page.getByRole('region', { name: 'User results' }); await expect(region).toHaveAttribute('tabindex', '0');
  // On mobile the table scrolls inside its region rather than widening the document.
  expect(await region.evaluate(element => getComputedStyle(element).overflowX)).toBe('auto');
  await page.getByLabel(`Select ${created[0].username}`, { exact: true }).check(); await expect(page.getByText('1 selected on this page', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Export', exact: true })).toBeDisabled(); await expect(page.getByRole('button', { name: 'Bulk actions', exact: true })).toBeDisabled();
  const unsupported: string[] = [];
  page.on('request', request => { if (/export|bulk/i.test(new URL(request.url()).pathname)) unsupported.push(request.url()); });
  await page.getByLabel('Country (exact)', { exact: true }).fill('Example country');
  await page.getByLabel('Education (exact)', { exact: true }).fill('Example degree');
  await page.getByLabel('Activity', { exact: true }).selectOption('true');
  const filtered = await apply(page); expect(filtered.total).toBe(2); expect(filtered.items.every(u => u.country === 'Example country' && u.education === 'Example degree')).toBe(true);
  await expect(page.getByText('0 selected on this page', { exact: true })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('is_active')).toBe('true');
  for (const field of ['username', 'city', 'country', 'education']) {
    for (const turn of [0, 1]) {
      const response = page.waitForResponse(listResponse); await page.getByRole('button', { name: `Sort by ${field}`, exact: true }).click();
      const result = await response; expect(result.status()).toBe(200); const body = await result.json();
      const query = new URL(result.url()).searchParams; expect(query.get('sort')).toBe(field); expect(query.get('skip')).toBe('0');
      const direction = query.get('direction'); const expected = [...created].sort((a, b) => {
        const left = a[field as keyof typeof a], right = b[field as keyof typeof b];
        const order = left < right ? -1 : left > right ? 1 : 0;
        return order === 0 ? (a.id < b.id ? -1 : 1) : direction === 'asc' ? order : -order;
      });
      expect(body.items.map((u: { id: string }) => u.id)).toEqual(expected.map(u => u.id));
      await expect(page.getByRole('columnheader').filter({ has: page.getByRole('button', { name: `Sort by ${field}`, exact: true }) })).toHaveAttribute('aria-sort', direction === 'asc' ? 'ascending' : 'descending');
    }
  }
  await page.getByLabel('Page size', { exact: true }).selectOption({ label: '1' }); const one = await apply(page); expect(one.limit).toBe(1); expect(one.items).toHaveLength(1); expect(one.total).toBe(2);
  await page.getByLabel(`Select ${one.items[0].username}`, { exact: true }).check();
  const next = page.waitForResponse(listResponse); await page.getByRole('button', { name: 'Next page', exact: true }).click();
  const second = await next; expect(second.status()).toBe(200); const two = await second.json(); expect(two.skip).toBe(1); expect(two.total).toBe(2); expect(two.items[0].id).not.toBe(one.items[0].id);
  await expect(page.getByText('0 selected on this page', { exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  const back = page.waitForResponse(listResponse); await page.goBack(); expect((await (await back).json()).items[0].id).toBe(one.items[0].id);
  await page.getByLabel('Search username or city', { exact: true }).fill(`${prefix}-missing`); const empty = await apply(page); expect(empty.total).toBe(0); expect(empty.items).toEqual([]); await expect(page.getByText('No accounts match these filters.', { exact: true })).toBeVisible();
  await page.getByLabel('Search username or city', { exact: true }).fill(prefix); await page.getByLabel('Activity', { exact: true }).selectOption('false'); expect((await apply(page)).total).toBe(0);
  await page.getByLabel('Activity', { exact: true }).selectOption('true'); expect((await apply(page)).total).toBe(2);
  const cleared = page.waitForResponse(listResponse); await page.getByRole('button', { name: 'Clear filters', exact: true }).click(); expect((await (await cleared).json()).limit).toBe(20); await expect(page.getByLabel('Search username or city', { exact: true })).toHaveValue('');
  // Cause an actual network outage, never a mocked API response, then prove explicit error and retry.
  await network.send('Network.emulateNetworkConditions', { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await expectedOfflineError({ path: '/api/users', method: 'GET', count: 1 }, async () => {
    const [failed] = await Promise.all([
      page.waitForEvent('requestfailed', request => new URL(request.url()).pathname === '/api/users' && request.method() === 'GET'),
      page.getByRole('button', { name: 'Sort by city', exact: true }).click()
    ]);
    await expect(page.getByRole('alert')).toContainText('Accounts are unavailable');
    await expect(page.getByRole('button', { name: 'Retry', exact: true })).toBeEnabled();
    await expect(page.getByRole('cell', { name: created[0].username, exact: true })).toHaveCount(0);
    return failed;
  });
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  const retry = page.waitForResponse(listResponse); await page.getByRole('button', { name: 'Retry', exact: true }).click(); expect((await retry).status()).toBe(200);
  await expect(page.getByRole('alert')).toHaveCount(0);
  // A fresh authenticated session re-reads the same persisted BSON identities and profiles.
  await page.getByRole('button', { name: 'Sign out', exact: true }).first().click(); await login(page);
  const revisit = page.waitForResponse(listResponse); await page.getByRole('link', { name: 'Users', exact: true }).click(); expect((await revisit).status()).toBe(200);
  await assertGridReady(page, await revisit);
  await page.getByLabel('Search username or city', { exact: true }).fill(prefix); const persisted = await apply(page); expect(persisted.items.map(u => u.id)).toEqual(created.map(u => u.id));
  expect(persisted.items.map(u => u.city)).toEqual(created.map(u => u.city)); expect(unsupported).toEqual([]); expect(browserErrors).toEqual([]);
  await network.detach();
});
