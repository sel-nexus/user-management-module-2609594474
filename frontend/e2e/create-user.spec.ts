import { randomBytes } from 'node:crypto';
import { test, expect, adminPassword } from './fixtures';

/** Prove real UI provisioning, persisted login and duplicate failure without mocks or direct route jumps. */
test('Admin login → create → duplicate error → newly created User login', async ({ page, browserErrors }) => {
  const username = `created-${randomBytes(8).toString('hex')}`;
  const password = randomBytes(24).toString('base64url');
  await page.goto('/');
  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
  await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const login = page.waitForResponse(response => response.url().endsWith('/api/login') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const loggedIn = await login; expect(loggedIn.status()).toBe(200); expect((await loggedIn.json()).user.role).toBe('Admin');
  await page.getByRole('link', { name: 'Provision a new account' }).click();
  await expect(page.getByRole('heading', { name: 'Create account', exact: true })).toBeVisible();
  await page.getByLabel('Username (required)', { exact: true }).fill(` ${username.toUpperCase()} `);
  await page.getByLabel('Password (required)', { exact: true }).fill(password);
  await page.getByLabel('Address', { exact: true }).fill('Example address');
  await page.getByLabel('City', { exact: true }).fill('Example city');
  await page.getByLabel('Country', { exact: true }).fill('Example country');
  await page.getByLabel('Education', { exact: true }).fill('Example education');
  const create = page.waitForResponse(response => response.url().endsWith('/api/users') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Create account', exact: true }).click();
  const created = await create; expect(created.status()).toBe(201); const body = await created.json();
  expect(body.username).toBe(username); expect(body.id).toMatch(/^[0-9a-f]{24}$/); expect(body.role).toBe('User'); expect(body.is_active).toBe(true);
  expect(body.address).toBe('Example address'); expect(body.city).toBe('Example city'); expect(body.country).toBe('Example country'); expect(body.education).toBe('Example education');
  expect(Object.keys(body).sort()).toEqual(['address', 'city', 'country', 'created_at', 'education', 'id', 'is_active', 'role', 'updated_at', 'username']);
  expect(created.headers()['location']).toBe(`/api/users/${body.id}`);
  expect(body.created_at).toBe(body.updated_at); expect(body.created_at).toMatch(/Z$/);
  await expect(page.getByRole('status')).toContainText(`Account created for ${body.username}`);
  await expect(page.getByRole('status')).toContainText(body.id);
  await expect(page.getByLabel('Password (required)', { exact: true })).toHaveValue('');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

  await page.getByLabel('Username (required)', { exact: true }).fill(username.toUpperCase());
  await page.getByLabel('Password (required)', { exact: true }).fill(password);
  const duplicate = page.waitForResponse(response => response.url().endsWith('/api/users') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Create account', exact: true }).click();
  const rejected = await duplicate; expect(rejected.status()).toBe(409); const problem = await rejected.json();
  expect(problem.code).toBe('duplicate_username'); expect(problem.traceId).toBeTruthy(); expect(JSON.stringify(problem)).not.toContain(password);
  await expect(page.getByRole('alert')).toContainText('Username is already in use');
  await expect(page.getByRole('status')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Create account', exact: true })).toBeEnabled();
  // The shared fixture records resource errors. Classify only this proven 409, not application exceptions.
  const resource409 = /^Failed to load resource: the server responded with a status of 409 \(Conflict\)$/;
  await expect.poll(() => browserErrors.filter(error => resource409.test(error)).length).toBe(1);
  browserErrors.splice(browserErrors.findIndex(error => resource409.test(error)), 1);
  expect(browserErrors).toEqual([]);

  await page.getByRole('link', { name: 'Back to session' }).click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).first().click();
  await page.getByLabel('Username', { exact: true }).fill(username);
  await page.getByLabel('Password', { exact: true }).fill(password);
  const newLogin = page.waitForResponse(response => response.url().endsWith('/api/login') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const persisted = await newLogin; expect(persisted.status()).toBe(200); const session = await persisted.json();
  expect(session.user.id).toBe(body.id); expect(session.user.city).toBe(body.city); expect(session.user.role).toBe('User');
  await expect(page.getByRole('heading', { name: `Signed in as ${session.user.username}` })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Provision a new account' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Create account', exact: true })).toHaveCount(0);
  expect(browserErrors).toEqual([]);
});
