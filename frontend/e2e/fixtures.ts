import { test as base, expect, Response, ConsoleMessage, Request } from '@playwright/test';

type ExpectedHttpError = (
  expected: { path: string; method: string; status: number },
  operation: () => Promise<Response>
) => Promise<void>;

type ExpectedOfflineError = (
  expected: { path: '/api/users' | '/api/dashboard'; method: 'GET'; count: 1 },
  operation: () => Promise<Request>
) => Promise<void>;

/** Retain every console message, including arguments, location and originating page. */
type BrowserConsoleEntry = {
  message: ConsoleMessage;
  type: string;
  text: string;
  location: ReturnType<ConsoleMessage['location']>;
  args: ReturnType<ConsoleMessage['args']>;
  page: ReturnType<ConsoleMessage['page']>;
};

/** Record all console metadata and every console/page, HTTP and request failure. */
export const test = base.extend<{
  browserErrors: string[];
  browserConsole: BrowserConsoleEntry[];
  expectedHttpError: ExpectedHttpError;
  expectedOfflineError: ExpectedOfflineError;
}>({
  browserConsole: async ({ page }, use) => {
    const entries: BrowserConsoleEntry[] = [];
    const capture = (message: ConsoleMessage): void => {
      entries.push({ message, type: message.type(), text: message.text(), location: message.location(), args: message.args(), page: message.page() });
    };
    page.on('console', capture);
    try { await use(entries); } finally { page.off('console', capture); }
  },
  browserErrors: [async ({ page, browserConsole }, use) => {
    const errors: string[] = [];
    const pageErrors: string[] = [];
    const runtime = (error: Error): void => { pageErrors.push(error.message); errors.push(error.message); };
    const consoleError = (message: ConsoleMessage): void => {
      if (message.type() === 'error') errors.push(message.text());
    };
    const httpError = (response: Response): void => {
      if (response.status() >= 400) errors.push(`HTTP ${response.status()} ${response.request().method()} ${response.url()}`);
    };
    const requestError = (request: Request): void => {
      errors.push(`Request failed ${request.method()} ${request.url()}: ${request.failure()?.errorText}`);
    };
    page.on('pageerror', runtime);
    page.on('console', consoleError);
    page.on('response', httpError);
    page.on('requestfailed', requestError);
    await use(errors);
    // Runtime exceptions are never classifiable, even by a consumer's resource handler.
    expect(pageErrors).toEqual([]);
    expect(errors).toEqual([]);
    page.off('pageerror', runtime);
    page.off('console', consoleError);
    page.off('response', httpError);
    page.off('requestfailed', requestError);
  }, { auto: true }],
  expectedHttpError: async ({ page, browserErrors }, use) => {
    await use(async (expected, operation) => {
      // Exactly one asserted response and one URL-matched Chromium resource error.
      // URL, method, status, request identity and the operation window must all agree.
      expect(browserErrors).toEqual([]);
      const requests: Request[] = [];
      const responses: { response: Response; index: number }[] = [];
      const consoles: { message: ConsoleMessage; index: number }[] = [];
      const request = (value: Request): void => {
        if (new URL(value.url()).pathname === expected.path && value.method() === expected.method) requests.push(value);
      };
      const response = (value: Response): void => {
        if (value.status() >= 400) responses.push({ response: value, index: browserErrors.length - 1 });
      };
      const consoleError = (message: ConsoleMessage): void => {
        if (message.type() === 'error') consoles.push({ message, index: browserErrors.length - 1 });
      };
      page.on('request', request);
      page.on('response', response);
      page.on('console', consoleError);
      try {
        const asserted = await operation();
        expect(new URL(asserted.url()).pathname).toBe(expected.path);
        expect(asserted.request().method()).toBe(expected.method);
        expect(asserted.status()).toBe(expected.status);
        expect(requests).toEqual([asserted.request()]);
        expect(await asserted.finished()).toBeNull();
        const matches = () => consoles.filter(({ message }) =>
          message.location().url === asserted.url() &&
          /^Failed to load resource: the server responded with a status of (\d+) \([^\n]+\)$/.exec(message.text())?.[1] === String(expected.status));
        await expect.poll(() => matches().length).toBe(1);
        expect(requests).toEqual([asserted.request()]);
        const http = responses.filter(entry => entry.response === asserted);
        expect(http).toHaveLength(1);
        // Never remove pageerrors, other URLs/statuses, extra requests or console exceptions.
        for (const index of [...http, ...matches()].map(entry => entry.index).sort((a, b) => b - a)) browserErrors.splice(index, 1);
        expect(browserErrors).toEqual([]);
      } finally {
        page.off('request', request);
        page.off('response', response);
        page.off('console', consoleError);
      }
    });
  },
  expectedOfflineError: async ({ page, browserErrors, browserConsole }, use) => {
    await use(async (expected, operation) => {
      expect(browserErrors).toEqual([]);
      expect(expected.method).toBe('GET');
      expect(expected.count).toBe(1);
      const consoleStart = browserConsole.length;
      const requests: Request[] = [];
      const failures: { request: Request; index: number }[] = [];
      const consoles: { message: ConsoleMessage; index: number }[] = [];
      const request = (value: Request): void => {
        // Include other methods on this endpoint so they cannot impersonate the GET.
        if (new URL(value.url()).pathname === expected.path) requests.push(value);
      };
      const failed = (value: Request): void => {
        failures.push({ request: value, index: browserErrors.length - 1 });
      };
      const consoleError = (message: ConsoleMessage): void => {
        if (message.type() === 'error') consoles.push({ message, index: browserErrors.length - 1 });
      };
      page.on('request', request);
      page.on('requestfailed', failed);
      page.on('console', consoleError);
      try {
        // The operation explicitly waits for requestfailed, never merely an alert.
        const asserted = await operation();
        expect(new URL(asserted.url()).pathname).toBe(expected.path);
        expect(asserted.method()).toBe(expected.method);
        expect(asserted.failure()?.errorText).toBe('net::ERR_INTERNET_DISCONNECTED');
        expect(await asserted.response()).toBeNull();
        expect(requests).toEqual([asserted]);
        const matches = () => consoles.filter(({ message }) =>
          message.location().url === asserted.url() &&
          message.text() === 'Failed to load resource: net::ERR_INTERNET_DISCONNECTED');
        await expect.poll(() => matches().length).toBe(expected.count);
        // Console messages have no method field: correlate their exact URL to the
        // sole observed request identity, whose method and failure were asserted.
        expect(requests).toEqual([asserted]);
        const http = failures.filter(entry => entry.request === asserted);
        expect(http).toHaveLength(expected.count);
        expect(browserConsole.slice(consoleStart).filter(entry =>
          entry.type === 'error' && entry.location.url === asserted.url() &&
          entry.text === 'Failed to load resource: net::ERR_INTERNET_DISCONNECTED')).toHaveLength(expected.count);
        // Remove only the correlated pair; keep unrelated failures and console
        // errors, all metadata, and pageerrors (also independently asserted).
        for (const index of [...http, ...matches()].map(entry => entry.index).sort((a, b) => b - a)) browserErrors.splice(index, 1);
        expect(browserErrors).toEqual([]);
      } finally {
        page.off('request', request);
        page.off('requestfailed', failed);
        page.off('console', consoleError);
      }
    });
  }
});
export { expect };
/** Require the explicitly provisioned test credential, never a hardcoded shipped secret. */
export function adminPassword(): string {
  const password = process.env['E2E_ADMIN_PASSWORD'];
  if (!password) throw new Error('Playwright config must provide E2E_ADMIN_PASSWORD.');
  return password;
}
