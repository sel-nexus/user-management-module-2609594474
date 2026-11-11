// Load tier-local public development configuration and proxy the approved same-origin API.
require('dotenv').config({ path: require('node:path').join(__dirname, '.env'), quiet: true });
if (!process.env.API_PROXY_TARGET) throw new Error('Configure API_PROXY_TARGET in frontend/.env or environment.');
module.exports = { '/api': { target: process.env.API_PROXY_TARGET, secure: false, changeOrigin: true } };
