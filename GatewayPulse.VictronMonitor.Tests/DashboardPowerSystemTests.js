const assert = require('assert');
const { buildPowerSystemView, isOverallHealthy } = require('../GatewayPulse.Service/wwwroot/power-system.js');

const now = new Date('2026-07-31T12:00:10Z');

function metric(view, key) {
  return view.metrics.find(item => item.key === key);
}

(function batteryProtectOnly() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Healthy', voltage: 13.57, outputEnabled: true, alarm: false },
    devices: [{ type: 'BatteryProtect', connected: true, connectionState: 'Connected', outputEnabled: true, alarm: false, rssi: -63, lastUpdate: '2026-07-31T12:00:08Z' }]
  }, now);
  assert.strictEqual(view.status, 'Healthy');
  assert.strictEqual(metric(view, 'voltage').value, '13.57 V');
  assert.strictEqual(metric(view, 'stateOfCharge'), undefined);
  assert.strictEqual(view.devices.length, 1);
  assert.strictEqual(view.devices[0].name, 'BatteryProtect');
  assert.match(view.devices[0].detail, /Output on/);
})();

(function smartShuntOnly() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Healthy', voltage: 13.57, current: -3.8, watts: -51.566, stateOfCharge: 96, consumedAmpHours: -8.4, timeRemainingMinutes: 2040, powerState: 'Discharging', alarm: false },
    devices: [{ type: 'SmartShunt', connected: true, connectionState: 'Connected', rssi: -61, lastUpdate: '2026-07-31T12:00:07Z' }]
  }, now);
  assert.strictEqual(metric(view, 'current').value, '-3.80 A');
  assert.strictEqual(metric(view, 'watts').value, '-51.6 W');
  assert.strictEqual(metric(view, 'powerState').value, 'Discharging');
  assert.strictEqual(metric(view, 'remaining').value, '34 hr');
  assert.strictEqual(view.stateOfCharge, 96);
  assert.strictEqual(metric(view, 'stateOfCharge').label, 'State of Charge');
  assert.deepStrictEqual(view.devices.map(device => device.name), ['SmartShunt']);
  assert.match(view.devices[0].detail, /-61 dBm/);
  assert.match(view.devices[0].detail, /3s ago/);
})();

(function hidesNullishAndUnknownMetrics() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Healthy', voltage: 13.42, powerState: 'Unknown', outputEnabled: true, alarm: false, timeRemainingMinutes: null, current: null, watts: null, stateOfCharge: null },
    devices: [{ type: 'BatteryProtect', connected: true, connectionState: 'Connected', outputEnabled: true, alarm: false, rssi: -58, lastUpdate: '2026-07-31T12:00:08Z' }]
  }, now);
  assert.strictEqual(metric(view, 'powerState'), undefined);
  assert.strictEqual(metric(view, 'remaining'), undefined);
  assert.strictEqual(metric(view, 'current'), undefined);
  assert.strictEqual(metric(view, 'stateOfCharge'), undefined);
  assert.strictEqual(metric(view, 'output').value, 'Enabled');
  assert.strictEqual(view.stateOfCharge, null);
})();

(function bothDevices() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Healthy', voltage: 13.57, current: 4.2, watts: 56.994, stateOfCharge: 80, powerState: 'Charging', outputEnabled: true, alarm: false },
    devices: [
      { type: 'SmartShunt', connected: true, connectionState: 'Connected', rssi: -61, lastUpdate: '2026-07-31T12:00:07Z' },
      { type: 'BatteryProtect', connected: true, connectionState: 'Connected', outputEnabled: true, alarm: false, rssi: -63, lastUpdate: '2026-07-31T12:00:08Z' }
    ]
  }, now);
  assert.strictEqual(metric(view, 'powerState').value, 'Charging');
  assert.deepStrictEqual(view.devices.map(device => device.name), ['SmartShunt', 'BatteryProtect']);
})();

(function staleAndDisconnectedAreDistinct() {
  const stale = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Warning' },
    devices: [{ type: 'SmartShunt', connected: false, stale: true, connectionState: 'Stale', lastUpdate: '2026-07-31T11:58:00Z' }]
  }, now);
  const disconnected = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Critical' },
    devices: [{ type: 'SmartShunt', connected: false, stale: false, connectionState: 'Disconnected' }]
  }, now);
  assert.strictEqual(stale.devices[0].state, 'Telemetry stale');
  assert.strictEqual(stale.devices[0].tone, 'warn');
  assert.strictEqual(disconnected.devices[0].state, 'Disconnected');
  assert.strictEqual(disconnected.devices[0].tone, 'bad');
})();

(function alarmIsCritical() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: { status: 'Critical', alarm: true, alarmReason: 'Low voltage' },
    devices: [{ type: 'SmartShunt', connected: true, connectionState: 'Connected', alarm: true, alarmReason: 'Low voltage' }]
  }, now);
  assert.strictEqual(view.status, 'Critical');
  assert.strictEqual(view.statusTone, 'bad');
  assert.strictEqual(metric(view, 'alarm').value, 'Low voltage');
  assert.strictEqual(metric(view, 'alarm').tone, 'bad');
})();

(function overallHealthIncludesPowerState() {
  assert.strictEqual(isOverallHealthy(true, { system: { status: 'Healthy' } }), true);
  assert.strictEqual(isOverallHealthy(true, { system: { status: 'Warning' } }), false);
  assert.strictEqual(isOverallHealthy(true, { system: { status: 'Critical' } }), false);
  assert.strictEqual(isOverallHealthy(true, { connected: false }), false);
  assert.strictEqual(isOverallHealthy(true, { connected: true }), true);
  assert.strictEqual(isOverallHealthy(false, { system: { status: 'Healthy' } }), false);
})();

console.log('Dashboard Power System tests passed: BatteryProtect-only, SmartShunt-only, both, stale/disconnected, and alarm.');
