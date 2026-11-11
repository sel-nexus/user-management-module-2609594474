import { randomBytes } from 'node:crypto';
import { Response } from '@playwright/test';
import { test, expect, adminPassword } from './fixtures';
import { AuditPage, AuditRecord } from '../src/app/shared/api.models';

/** Real operation-to-review journey, inherited desktop/mobile projects and browser-error fixture. */
test('real login, account and profile operations match audit rows without events from reviewing or pagination', async ({ page, browserErrors }, testInfo) => {
  const username = `audit-${randomBytes(8).toString('hex')}`;
  const password = randomBytes(24).toString('base64url');
  const city = `Audit city ${randomBytes(4).toString('hex')}`;
  const mutations: string[] = [];
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/') && r.method() !== 'GET') mutations.push(`${r.method()} ${new URL(r.url()).pathname}`); });
  const response = (path: string, method = 'GET'): Promise<Response> => page.waitForResponse(r => new URL(r.url()).pathname === path && r.request().method() === method);
  await page.goto('/');
  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin'); await page.getByLabel('Password', { exact: true }).fill('Wrong-password-123!');
  const deniedLogin = response('/api/login', 'POST'); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); expect((await deniedLogin).status()).toBe(401);
  await expect(page.getByRole('alert')).toContainText('Username or password is incorrect');
  await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const login = response('/api/login', 'POST'); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); const signedIn = await login; expect(signedIn.status()).toBe(200); const admin = (await signedIn.json()).user; expect(admin.role).toBe('Admin');
  await page.getByRole('link', { name: 'Provision a new account' }).click();
  await page.getByLabel('Username (required)', { exact: true }).fill(username); await page.getByLabel('Password (required)', { exact: true }).fill(password);
  const create = response('/api/users', 'POST'); await page.getByRole('button', { name: 'Create account', exact: true }).click(); const created = await create; expect(created.status()).toBe(201); const account = await created.json(); expect(account.username).toBe(username);
  const detail = response(`/api/users/${account.id}`); await page.getByRole('link', { name: 'View created account' }).click(); expect(await (await detail).json()).toEqual(account);
  await page.getByLabel('City', { exact: true }).fill(city);
  const edit = response(`/api/users/${account.id}`, 'PUT'); await page.getByRole('button', { name: 'Save changes', exact: true }).click(); const edited = await edit; expect(edited.status()).toBe(200); expect((await edited.json()).city).toBe(city);
  const own = response('/api/profile'); await page.getByRole('link', { name: 'Profile', exact: true }).click(); expect((await own).status()).toBe(200);
  await page.getByLabel('Education', { exact: true }).fill(`Audit degree ${randomBytes(4).toString('hex')}`);
  const updateProfile = response('/api/profile', 'PUT'); await page.getByRole('button', { name: 'Save profile', exact: true }).click(); const profile = await updateProfile; expect(profile.status()).toBe(200); expect((await profile.json()).id).toBe(admin.id);
  const beforeReview = [...mutations];
  // Real network latency exposes loading without intercepting or fabricating responses.
  const network = await page.context().newCDPSession(page); await network.send('Network.enable'); await network.send('Network.emulateNetworkConditions', { offline: false, latency: 500, downloadThroughput: -1, uploadThroughput: -1 });
  const read = response('/api/logs'); await page.getByRole('link', { name: 'Audit', exact: true }).click(); await expect(page.getByRole('status').filter({ hasText: 'Loading audit records' })).toBeVisible();
  const logsResponse = await read; expect(logsResponse.status()).toBe(200); const logs: AuditPage = await logsResponse.json(); expect(logs.limit).toBe(20); expect(logs.skip).toBe(0); expect(logs.items.length).toBeLessThanOrEqual(20);
  expect(logsResponse.request().headers()['authorization']).toMatch(/^Bearer /);
  const find = (action: string, status: string, identity: string): AuditRecord => {
    const record = logs.items.find(x => x.action === action && x.status === status && x.user_id === identity); expect(record).toBeDefined(); return record!;
  };
  for (const record of [find('login', 'failure', admin.id), find('login', 'success', admin.id), find('create_user', 'success', account.id), find('update_user', 'success', account.id), find('update_profile', 'success', admin.id)]) {
    const row = page.locator(`tr[data-record-id="${record.id}"]`); await expect(row).toContainText(record.action); await expect(row).toContainText(record.status); await expect(row).toContainText(record.user_id!); await expect(row.locator('time')).toHaveAttribute('datetime', record.timestamp);
  }
  for (const item of logs.items) {
    expect(Object.keys(item).sort()).toEqual(item.user_id ? ['action', 'id', 'status', 'timestamp', 'user_id'] : ['action', 'id', 'status', 'timestamp']); expect(item.id).toMatch(/^[0-9a-f]{24}$/);
  }
  for (let i = 1; i < logs.items.length; i++) { const a = logs.items[i - 1], b = logs.items[i]; expect(Date.parse(a.timestamp) > Date.parse(b.timestamp) || (a.timestamp === b.timestamp && a.id > b.id)).toBe(true); }
  const text = await logsResponse.text(); for (const secret of [password, adminPassword(), username, city, 'password_hash', 'access_token']) expect(text).not.toContain(secret);
  await expect(page.getByRole('heading', { name: 'Audit log', exact: true })).toBeVisible(); await expect(page.getByRole('link', { name: 'Audit', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('columnheader')).toHaveCount(5); await expect(page.getByRole('region', { name: 'Audit records' })).toHaveAttribute('tabindex', '0');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await page.getByLabel('Page size', { exact: true }).selectOption({ label: '1' });
  const one = response('/api/logs'); await page.getByRole('button', { name: 'Apply page size' }).click(); const first: AuditPage = await (await one).json(); expect(first.limit).toBe(1); expect(first.skip).toBe(0); expect(first.total).toBe(logs.total); expect(first.items).toEqual(logs.items.slice(0, 1)); await expect(page).toHaveURL(/limit=1&skip=0/);
  const next = response('/api/logs'); await page.getByRole('button', { name: 'Next page' }).click(); const second: AuditPage = await (await next).json(); expect(second.skip).toBe(1); expect(second.total).toBe(logs.total); expect(second.items).toEqual(logs.items.slice(1, 2)); await expect(page).toHaveURL(/limit=1&skip=1/); await expect(page.locator(`tr[data-record-id="${second.items[0].id}"]`)).toBeVisible();
  const previous = response('/api/logs'); await page.getByRole('button', { name: 'Previous page' }).click(); expect(await (await previous).json()).toEqual(first);
  const reset = response('/api/logs'); await page.getByRole('button', { name: 'Reset pagination' }).click(); expect(await (await reset).json()).toEqual(logs);
  await page.getByRole('link', { name: 'Back to session', exact: true }).click();
  const revisit = response('/api/logs'); await page.getByRole('link', { name: 'Review audit records' }).click(); expect(await (await revisit).json()).toEqual(logs);
  expect(mutations).toEqual(beforeReview);
  await page.getByRole('region', { name: 'Audit records' }).focus(); await page.keyboard.press('ArrowRight');
  await page.screenshot({ path: testInfo.outputPath(`audit-${testInfo.project.name}.png`), fullPage: true });
  expect(browserErrors).toEqual([]); await network.detach();
});
