/** Capture representative accessible structure/styles/HTML without mocking product data. */
const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const { homedir } = require('node:os');
const fs = require('node:fs/promises');
const path = require('node:path');

async function main() {
  const backend = process.env.E2E_BACKEND_URL || 'http://127.0.0.1:5080';
  const frontend = process.env.E2E_FRONTEND_URL || 'http://127.0.0.1:4200';
  const password = randomBytes(24).toString('base64url');
  const database = `umm_preflight_${randomBytes(12).toString('hex')}`;
  const env = { ...process.env, TMPDIR: '/tmp', E2E_ADMIN_PASSWORD: password, E2E_MONGO_DATABASE: database, E2E_REUSE: 'true', ASPNETCORE_ENVIRONMENT: 'Testing', ASPNETCORE_URLS: backend, DOTNET_ROOT: `${homedir()}/.dotnet`, Mongo__Uri: 'mongodb://127.0.0.1:27017', Mongo__Database: database, Jwt__Secret: randomBytes(64).toString('base64'), Jwt__Issuer: 'umm-e2e', Jwt__Audience: 'umm-e2e-browser', Jwt__ExpiryMinutes: '30', Bootstrap__Enabled: 'true', Bootstrap__Username: 'e2e-admin', Bootstrap__Password: password, API_PROXY_TARGET: backend };
  const api = spawn(`${homedir()}/.dotnet/dotnet`, ['run', '--project', '../backend/UserManagement.Api.csproj', '--no-launch-profile'], { env, stdio: 'ignore' });
  const web = spawn(process.execPath, ['node_modules/@angular/cli/bin/ng.js', 'serve', '--host', '127.0.0.1', '--port', new URL(frontend).port], { env, stdio: 'ignore' });
  let browser;
  try {
    for (const url of [`${backend}/api/health`, frontend]) {
      let ready = false;
      for (let i = 0; i < 120; i++) {
        try { if ((await fetch(url)).ok) { ready = true; break; } } catch {}
        await new Promise(r => setTimeout(r, 1000));
      }
      if (!ready) throw new Error(`Preview did not become ready: ${url}`);
    }
    await fs.mkdir('evidence', { recursive: true });
    browser = await chromium.launch({ args: ['--no-sandbox'] });
    const page = await browser.newPage();
    const records = [];
    async function capture(name) {
      await page.evaluate(() => document.fonts.ready);
      const cdp = await page.context().newCDPSession(page);
      const accessibility = await cdp.send('Accessibility.getFullAXTree');
      await cdp.detach();
      records.push({ name, url: page.url(), accessibility: accessibility.nodes.map(x => ({ role: x.role?.value, name: x.name?.value, ignored: x.ignored })), styles: await page.evaluate(() => ({ font: getComputedStyle(document.body).fontFamily, fontSize: getComputedStyle(document.body).fontSize, mainPadding: getComputedStyle(document.querySelector('main')).padding, width: innerWidth, scrollWidth: document.documentElement.scrollWidth, background: getComputedStyle(document.documentElement).backgroundColor })), html: await page.content() });
      await page.screenshot({ path: `evidence/${name}.png`, fullPage: true });
    }
    await page.goto(frontend);
    await capture('entry-desktop');
    await page.getByRole('link', { name: 'Sign in', exact: true }).first().click();
    await page.getByLabel('Username', { exact: true }).fill('e2e-admin');
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.getByRole('link', { name: 'Dashboard', exact: true }).click();
    await page.getByTestId('total').waitFor();
    await capture('dashboard-desktop');
    await page.getByRole('link', { name: 'Users', exact: true }).click();
    await page.getByRole('link', { name: 'e2e-admin', exact: true }).waitFor();
    await capture('grid-desktop');
    await page.getByLabel('Search username or city', { exact: true }).fill('no-matching-preflight-account');
    await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
    await page.getByText('No accounts match these filters.', { exact: true }).waitFor();
    await capture('grid-empty');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('link', { name: 'Profile', exact: true }).click();
    await page.getByRole('heading', { name: /profile/i }).first().waitFor();
    await capture('profile-mobile');
    await fs.writeFile('evidence/preflight.txt', JSON.stringify(records, null, 2));
    console.log('Preflight captured desktop entry/dashboard/grid, filtered-empty and mobile profile; screenshots saved.');
  } finally {
    if (browser) await browser.close();
    api.kill(); web.kill();
  }
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
