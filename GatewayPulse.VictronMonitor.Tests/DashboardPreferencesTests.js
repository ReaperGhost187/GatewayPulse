const assert = require('assert');
const {
  defaults,
  normalize,
  isCardVisible,
  filterPowerMetrics,
  applyPowerTelemetryPreferences,
  applyCardVisibility,
  buildOverallBanner,
  extractPowerAlert
} = require('../GatewayPulse.Service/wwwroot/dashboard-preferences.js');
const { buildPowerSystemView, isOverallHealthy } = require('../GatewayPulse.Service/wwwroot/power-system.js');

(function defaultsShowFullDashboard() {
  const prefs = defaults();
  assert.strictEqual(prefs.cards.powerSystem, true);
  assert.strictEqual(prefs.cards.advancedDiagnostics, true);
  assert.strictEqual(prefs.powerTelemetry.stateOfCharge, true);
  assert.strictEqual(prefs.powerTelemetry.deviceNameModel, true);
})();

(function normalizeFillsMissingKeys() {
  const prefs = normalize({
    Cards: { PowerSystem: false },
    PowerTelemetry: { Voltage: false }
  });
  assert.strictEqual(prefs.cards.powerSystem, false);
  assert.strictEqual(prefs.cards.gatewayStatus, true);
  assert.strictEqual(prefs.powerTelemetry.voltage, false);
  assert.strictEqual(prefs.powerTelemetry.current, true);
})();

(function hiddenCardBehavior() {
  const prefs = normalize({ cards: { activity: false, powerEvents: false } });
  assert.strictEqual(isCardVisible(prefs, 'activity'), false);
  assert.strictEqual(isCardVisible(prefs, 'powerSystem'), true);

  const fakeDoc = {
    nodes: [
      { getAttribute: () => 'activity', classList: { toggle() {} }, setAttribute() {}, removeAttribute() {}, _hidden: false },
      { getAttribute: () => 'powerSystem', classList: { toggle() {} }, setAttribute() {}, removeAttribute() {}, _hidden: false }
    ],
    querySelectorAll(selector) {
      if (selector === '[data-card]') return this.nodes;
      return [];
    }
  };
  fakeDoc.nodes.forEach(node => {
    node.classList.toggle = (name, on) => { node._prefHidden = on; };
    node.setAttribute = () => { node._hidden = true; };
    node.removeAttribute = () => { node._hidden = false; };
  });
  applyCardVisibility(prefs, fakeDoc);
  assert.strictEqual(fakeDoc.nodes[0]._prefHidden, true);
  assert.strictEqual(fakeDoc.nodes[1]._prefHidden, false);
})();

(function hiddenTelemetryFieldBehavior() {
  const view = buildPowerSystemView({
    schemaVersion: 2,
    system: {
      status: 'Healthy',
      voltage: 13.5,
      current: 2.1,
      watts: 28.3,
      stateOfCharge: 88,
      consumedAmpHours: -4.2,
      timeRemainingMinutes: 600,
      powerState: 'Charging',
      outputEnabled: true,
      alarm: false
    },
    devices: [{
      type: 'SmartShunt',
      connected: true,
      connectionState: 'Connected',
      device: 'Victron SmartShunt 300A',
      model: 'SmartShunt 300A',
      rssi: -60,
      lastUpdate: '2026-07-31T12:00:00Z'
    }]
  }, new Date('2026-07-31T12:00:05Z'));

  const filtered = applyPowerTelemetryPreferences(view, {
    powerTelemetry: {
      voltage: false,
      current: true,
      power: false,
      stateOfCharge: false,
      deviceNameModel: false,
      rssi: false
    }
  });

  assert.ok(!filtered.metrics.some(metric => metric.key === 'voltage'));
  assert.ok(filtered.metrics.some(metric => metric.key === 'current'));
  assert.ok(!filtered.metrics.some(metric => metric.key === 'watts'));
  assert.strictEqual(filtered.stateOfCharge, null);
  assert.strictEqual(filtered.devices[0].identity, null);
  assert.ok(!String(filtered.devices[0].detail).includes('dBm'));
})();

(function resetToDefaults() {
  const customized = normalize({
    cards: { powerSystem: false, activity: false },
    powerTelemetry: { voltage: false, alarm: false }
  });
  assert.strictEqual(customized.cards.powerSystem, false);
  const reset = defaults();
  assert.strictEqual(reset.cards.powerSystem, true);
  assert.strictEqual(reset.powerTelemetry.voltage, true);
  assert.strictEqual(reset.powerTelemetry.alarm, true);
})();

(function alarmsSurfaceWhenPowerCardHidden() {
  const power = {
    schemaVersion: 2,
    connected: true,
    system: { status: 'Critical', alarm: true, alarmReason: 'Low voltage', voltage: 11.0 },
    devices: [{ type: 'BatteryProtect', connected: true, alarm: true, alarmReason: 'Low voltage' }]
  };
  const alert = extractPowerAlert(power);
  assert.ok(alert);
  assert.match(alert.detail, /Low voltage/i);

  const banner = buildOverallBanner(
    true,
    power,
    { cards: { powerSystem: false } },
    { isOverallHealthy });
  assert.strictEqual(banner.bad, true);
  assert.strictEqual(banner.surfacedAlarm, true);
  assert.match(banner.text, /ALARM/);
  assert.match(banner.text, /Low voltage/i);
})();

(function activeAlarmSurfacesWhenAlarmFieldHidden() {
  const power = {
    schemaVersion: 2,
    system: { status: 'Critical', alarm: true, alarmReason: 'Overload' },
    devices: []
  };
  const banner = buildOverallBanner(
    true,
    power,
    { cards: { powerSystem: true }, powerTelemetry: { alarm: false } },
    { isOverallHealthy });
  assert.strictEqual(banner.surfacedAlarm, true);
  assert.match(banner.text, /Overload/i);
})();

(function filterPowerMetricsHonorsPreferences() {
  const metrics = [
    { key: 'voltage', label: 'Voltage', value: '13.2 V' },
    { key: 'watts', label: 'Power', value: '10 W' },
    { key: 'remaining', label: 'Estimated Runtime', value: '2 hr' }
  ];
  const filtered = filterPowerMetrics(metrics, {
    powerTelemetry: { voltage: true, power: false, estimatedRuntime: false }
  });
  assert.deepStrictEqual(filtered.map(metric => metric.key), ['voltage']);
})();

console.log('Dashboard Preferences tests passed: cards, telemetry, persistence defaults, reset, and alarm surfacing.');
