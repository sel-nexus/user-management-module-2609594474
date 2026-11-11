// Run native Angular TestBed tests in Chromium without requiring writable bin links.
module.exports = config => config.set({
  frameworks: ['jasmine'],
  plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-jasmine-html-reporter')],
  reporters: ['progress'], browsers: ['ChromeHeadlessNoSandbox'], singleRun: true,
  customLaunchers: { ChromeHeadlessNoSandbox: { base: 'ChromeHeadless', flags: ['--no-sandbox', '--disable-dev-shm-usage'] } },
  client: { clearContext: false }, browserNoActivityTimeout: 60000
});
