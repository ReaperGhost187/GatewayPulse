const assert = require('assert');
const {
  defaults,
  normalize,
  buildMapUrl,
  DEFAULT_MAP_URL
} = require('../GatewayPulse.Service/wwwroot/network-map.js');

(function defaultOptions() {
  const options = defaults();
  assert.strictEqual(options.mapUrl, DEFAULT_MAP_URL);
  assert.strictEqual(options.rememberServiceCode, true);
  assert.strictEqual(options.autoRefresh, true);
  assert.strictEqual(options.autoRefreshMinutes, 15);
})();

(function normalizeUppercasesAndClamps() {
  const options = normalize({
    ServiceCode: ' shares ',
    AutoRefreshMinutes: 0,
    RememberServiceCode: true,
    AutoRefresh: false
  });
  assert.strictEqual(options.serviceCode, 'SHARES');
  assert.strictEqual(options.autoRefreshMinutes, 1);
  assert.strictEqual(options.autoRefresh, false);
})();

(function normalizeRewritesLegacyDrupalUrl() {
  const options = normalize({ mapUrl: 'https://winlink.org/RMSChannels' });
  assert.strictEqual(options.mapUrl, DEFAULT_MAP_URL);
})();

(function buildMapUrlAppliesLowercaseServicecodes() {
  const url = buildMapUrl('https://winlink.org/RMSChannels', 'SHARES');
  assert.ok(url.includes('servicecodes=SHARES'));
  assert.ok(url.startsWith('https://cms.winlink.org:444/maps/WinlinkGateways.aspx'));
  assert.ok(!url.includes('serviceCodes='));
})();

(function buildMapUrlClearsPreviousCode() {
  const url = buildMapUrl(
    'https://cms.winlink.org:444/maps/WinlinkGateways.aspx?servicecodes=OLD&ServiceCodes=OLD',
    'PUBLIC');
  assert.ok(url.includes('servicecodes=PUBLIC'));
  assert.ok(!url.includes('OLD'));
})();

console.log('Network Map tests passed: defaults, normalize, and service-code URL application.');
