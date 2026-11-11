import { randomBytes } from 'node:crypto';
import { test, expect, adminPassword } from './fixtures';
/** Prove UI navigation, real failed/successful login, backend content, logout and recovery non-operation. */
test('entry → failure → recovery → successful login → logout stays honest on desktop and mobile', async ({ page, browserErrors, expectedHttpError }, testInfo) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: /A clear register/ })).toBeVisible();
  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
  await page.getByLabel('Password', { exact: true }).fill('Wrong-password-123!');
  await expectedHttpError({ path: '/api/login', method: 'POST', status: 401 }, async () => {
    const failed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/login' && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    const denied = await failed;
    expect(denied.status()).toBe(401);
    expect((await denied.json()).code).toBe('invalid_credentials');
    await expect(page.getByRole('alert')).toContainText('Username or password is incorrect.');
    return denied;
  });
  const recoveryRequests: string[] = [];
  const capture = (request: import('@playwright/test').Request): void => { if (request.url().includes('/api/')) recoveryRequests.push(request.url()); };
  page.on('request', capture);
  await page.getByRole('link', { name: 'Forgot password?' }).click();
  await expect(page.getByRole('heading', { name: 'Password recovery is not available' })).toBeVisible();
  await expect(page.getByText(/No recovery request has been sent/)).toBeVisible();
  await page.getByRole('link', { name: 'Back to sign in' }).click();
  expect(recoveryRequests).toEqual([]); page.off('request', capture);
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
  await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const successful = page.waitForResponse(response => response.url().endsWith('/api/login') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const response = await successful; expect(response.status()).toBe(200);
  const body = await response.json(); expect(body.token_type).toBe('Bearer'); expect(body.user.role).toBe('Admin'); expect(body.user.id).toMatch(/^[0-9a-f]{24}$/); expect(JSON.stringify(body)).not.toContain('password');
  await expect(page.getByRole('heading', { name: `Signed in as ${body.user.username}` })).toBeVisible();
  await expect(page.getByText(body.user.role, { exact: true })).toBeVisible();
  expect(await page.evaluate(() => ({ local: Object.keys(localStorage), session: Object.keys(sessionStorage) }))).toEqual({ local: [], session: [] });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('session.png'), fullPage: true });
  await page.getByRole('button', { name: 'Sign out' }).first().click();
  await expect(page.getByRole('heading', { name: 'Sign in to the register' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Session', exact: true })).toHaveCount(0);
  expect(browserErrors).toEqual([]);
});

// Each case has a fresh unauthenticated browser context and only one initial goto.
for (const route of ['/profile', '/users', '/audit', '/dashboard']) {
  test(`unauthenticated initial entry ${route} redirects to login without protected requests or data`, async ({ page, browserErrors }) => {
    const apiRequests: string[] = [];
    page.on('request', request => {
      if (new URL(request.url()).pathname.startsWith('/api/')) apiRequests.push(request.url());
    });
    const entry = await page.goto(route);
    expect(entry).not.toBeNull();
    expect(entry!.status()).toBe(200);
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('heading', { name: 'Sign in to the register', exact: true })).toBeVisible();
    await expect(page.getByRole('table')).toHaveCount(0);
    await expect(page.getByTestId('profile-username')).toHaveCount(0);
    await expect(page.getByTestId('total')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /Signed in as|My profile|Users|Audit log|Dashboard/ })).toHaveCount(0);
    for (const name of ['Users', 'Audit', 'Dashboard', 'Profile', 'Session']) {
      await expect(page.getByRole('link', { name, exact: true })).toHaveCount(0);
    }
    expect(apiRequests).toEqual([]);
    expect(browserErrors).toEqual([]);
  });
}

test('Admin provisions a User through UI; User can read own profile but has no Admin data or navigation', async ({ page, browserErrors }) => {
  const username = `boundary-${randomBytes(8).toString('hex')}`;
  const password = randomBytes(24).toString('base64url');
  await page.goto('/');
  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
  await page.getByLabel('Password', { exact: true }).fill(adminPassword());
  const adminLogin = page.waitForResponse(r => new URL(r.url()).pathname === '/api/login' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const adminResponse = await adminLogin;
  expect(adminResponse.status()).toBe(200);
  expect((await adminResponse.json()).user.role).toBe('Admin');
  for (const name of ['Users', 'Audit', 'Dashboard']) await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Provision a new account', exact: true }).click();
  await page.getByLabel('Username (required)', { exact: true }).fill(username);
  await page.getByLabel('Password (required)', { exact: true }).fill(password);
  const create = page.waitForResponse(r => new URL(r.url()).pathname === '/api/users' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Create account', exact: true }).click();
  const created = await create;
  expect(created.status()).toBe(201);
  const account = await created.json();
  expect(account.username).toBe(username);
  expect(account.role).toBe('User');
  expect(account.is_active).toBe(true);
  await expect(page.getByRole('status')).toContainText(account.id);
  await page.getByRole('button', { name: 'Sign out', exact: true }).first().click();
  const adminRequests: string[] = [];
  page.on('request', request => {
    if (/^\/api\/(users|logs|dashboard)(\/|$)/.test(new URL(request.url()).pathname)) adminRequests.push(request.url());
  });
  await page.getByLabel('Username', { exact: true }).fill(username);
  await page.getByLabel('Password', { exact: true }).fill(password);
  const login = page.waitForResponse(r => new URL(r.url()).pathname === '/api/login' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const userResponse = await login;
  expect(userResponse.status()).toBe(200);
  const session = await userResponse.json();
  expect(session.user.id).toBe(account.id);
  expect(session.user.role).toBe('User');
  await expect(page.getByRole('heading', { name: `Signed in as ${username}`, exact: true })).toBeVisible();
  const profile = page.waitForResponse(r => new URL(r.url()).pathname === '/api/profile' && r.request().method() === 'GET');
  await page.getByRole('link', { name: 'Manage my profile', exact: true }).click();
  const own = await profile;
  expect(own.status()).toBe(200);
  expect(await own.json()).toEqual(account);
  await expect(page.getByTestId('profile-username')).toHaveText(username);
  await page.getByRole('link', { name: 'Back to session', exact: true }).click();
  for (const name of ['Users', 'Audit', 'Dashboard', 'Create account', 'Provision a new account', 'Browse accounts', 'Review audit records', 'View dashboard']) {
    await expect(page.getByRole('link', { name, exact: true })).toHaveCount(0);
  }
  await expect(page.getByRole('table')).toHaveCount(0);
  await expect(page.getByTestId('total')).toHaveCount(0);
  expect(adminRequests).toEqual([]);
  await page.getByRole('button', { name: 'Sign out', exact: true }).first().click();
  await expect(page).toHaveURL(/\/login$/);
  expect(browserErrors).toEqual([]);
});
