(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.DashboardPreferences = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const CARD_KEYS = [
    'gatewayStatus',
    'configuredFrequency',
    'activity',
    'powerSystem',
    'powerEvents',
    'rfPower',
    'winlinkActivityToday',
    'scanChannels',
    'stationConnectionCounts',
    'uptimeSinceLastStart',
    'last50Connections',
    'recentWinlinkActivity',
    'advancedDiagnostics'
  ];

  const RF_TELEMETRY_KEYS = [
    'returnLoss',
    'dbm',
    'resistance',
    'reactance',
    'impedance',
    'phase',
    'powerRange',
    'meterMode',
    'txState',
    'connectionState',
    'lastUpdate',
    'protocolStatus'
  ];

  const TELEMETRY_KEYS = [
    'stateOfCharge',
    'voltage',
    'current',
    'power',
    'consumedAmpHours',
    'estimatedRuntime',
    'chargingDischargingState',
    'batteryProtectOutput',
    'alarm',
    'rssi',
    'deviceNameModel'
  ];

  const METRIC_PREF_MAP = {
    stateOfCharge: 'stateOfCharge',
    voltage: 'voltage',
    current: 'current',
    watts: 'power',
    consumedAmpHours: 'consumedAmpHours',
    remaining: 'estimatedRuntime',
    powerState: 'chargingDischargingState',
    output: 'batteryProtectOutput',
    alarm: 'alarm'
  };

  function defaults() {
    const cards = {};
    for (const key of CARD_KEYS) cards[key] = true;
    const powerTelemetry = {};
    for (const key of TELEMETRY_KEYS) powerTelemetry[key] = true;
    const rfTelemetry = {};
    for (const key of RF_TELEMETRY_KEYS) rfTelemetry[key] = true;
    return { cards, powerTelemetry, rfTelemetry };
  }

  function asBool(value, fallback) {
    if (typeof value === 'boolean') return value;
    if (value === 'true') return true;
    if (value === 'false') return false;
    return fallback;
  }

  function normalize(input) {
    const baseline = defaults();
    const source = input || {};
    const cardsSource = source.cards || source.Cards || {};
    const telemetrySource = source.powerTelemetry || source.PowerTelemetry || {};
    const rfSource = source.rfTelemetry || source.RfTelemetry || {};
    const cards = {};
    for (const key of CARD_KEYS) {
      const pascal = key.charAt(0).toUpperCase() + key.slice(1);
      cards[key] = asBool(
        Object.prototype.hasOwnProperty.call(cardsSource, key)
          ? cardsSource[key]
          : cardsSource[pascal],
        true);
    }
    const powerTelemetry = {};
    for (const key of TELEMETRY_KEYS) {
      const pascal = key.charAt(0).toUpperCase() + key.slice(1);
      powerTelemetry[key] = asBool(
        Object.prototype.hasOwnProperty.call(telemetrySource, key)
          ? telemetrySource[key]
          : telemetrySource[pascal],
        true);
    }
    const rfTelemetry = {};
    for (const key of RF_TELEMETRY_KEYS) {
      const pascalKey = key.charAt(0).toUpperCase() + key.slice(1);
      rfTelemetry[key] = asBool(
        Object.prototype.hasOwnProperty.call(rfSource, key)
          ? rfSource[key]
          : rfSource[pascalKey],
        true);
    }
    return { cards, powerTelemetry, rfTelemetry };
  }

  function isCardVisible(preferences, cardKey) {
    const prefs = normalize(preferences);
    return prefs.cards[cardKey] !== false;
  }

  function isTelemetryVisible(preferences, telemetryKey) {
    const prefs = normalize(preferences);
    return prefs.powerTelemetry[telemetryKey] !== false;
  }

  function filterPowerMetrics(metrics, preferences) {
    const prefs = normalize(preferences);
    return (metrics || []).filter(metric => {
      const prefKey = METRIC_PREF_MAP[metric.key];
      if (!prefKey) return true;
      return prefs.powerTelemetry[prefKey] !== false;
    });
  }

  function applyPowerTelemetryPreferences(view, preferences) {
    const prefs = normalize(preferences);
    const next = {
      status: view.status,
      statusTone: view.statusTone,
      metrics: filterPowerMetrics(view.metrics, prefs),
      stateOfCharge: prefs.powerTelemetry.stateOfCharge ? view.stateOfCharge : null,
      devices: (view.devices || []).map(device => ({
        name: device.name,
        identity: prefs.powerTelemetry.deviceNameModel ? device.identity : null,
        state: device.state,
        tone: device.tone,
        detail: filterDeviceDetail(device, prefs),
        error: device.error || null,
        _raw: device._raw || null
      }))
    };
    return next;
  }

  function filterDeviceDetail(device, preferences) {
    const parts = [];
    const raw = device._raw || {};
    if (preferences.powerTelemetry.batteryProtectOutput &&
        raw.type === 'BatteryProtect' &&
        raw.outputEnabled !== null &&
        raw.outputEnabled !== undefined &&
        raw.outputEnabled !== '') {
      parts.push(raw.outputEnabled ? 'Output on' : 'Output off');
    }
    if (raw.alarm === true) {
      // Active alarms always remain in device detail so they cannot disappear from a visible card.
      parts.push(`Alarm: ${raw.alarmReason || 'Active'}`);
    } else if (preferences.powerTelemetry.alarm &&
               raw.type === 'BatteryProtect' &&
               raw.alarm === false) {
      parts.push('No alarm');
    }
    if (preferences.powerTelemetry.rssi &&
        raw.rssi !== null &&
        raw.rssi !== undefined &&
        raw.rssi !== '') {
      parts.push(`${raw.rssi} dBm`);
    }
    if (device.updatedAge) parts.push(device.updatedAge);
    return parts.join(' · ');
  }

  function extractPowerAlert(power) {
    const system = (power && power.system) || {};
    const status = system.status || '';
    const alarm = system.alarm === true ||
      ((power && power.devices) || []).some(device => device && device.alarm === true);
    const critical = status === 'Critical' || status === 'Warning' || alarm;
    if (!critical) return null;

    let detail = system.alarmReason || '';
    if (!detail) {
      const deviceAlarm = ((power && power.devices) || []).find(device => device && device.alarm === true);
      detail = (deviceAlarm && (deviceAlarm.alarmReason || deviceAlarm.error)) || '';
    }
    if (!detail && status) detail = `Power system ${status.toLowerCase()}`;
    if (!detail) detail = 'Power attention required';
    return {
      status: status || (alarm ? 'Critical' : 'Warning'),
      detail,
      alarm
    };
  }

  function buildOverallBanner(gatewayHealthy, power, preferences, powerSystemViewApi) {
    const prefs = normalize(preferences);
    const healthy = powerSystemViewApi.isOverallHealthy(gatewayHealthy, power);
    const alert = extractPowerAlert(power);
    const powerCardHidden = prefs.cards.powerSystem === false;
    const alarmFieldHidden = prefs.powerTelemetry.alarm === false;

    // Keep critical power warnings visible when the Power System card (or alarm field) is hidden.
    if (alert && (powerCardHidden || (alert.alarm && alarmFieldHidden))) {
      const label = alert.status === 'Warning' ? 'WARNING' : 'ALARM';
      return {
        text: `🔴 ${label}: ${alert.detail}`,
        bad: true,
        surfacedAlarm: true
      };
    }

    if (healthy) {
      return { text: '🟢 HEALTHY', bad: false, surfacedAlarm: false };
    }
    return { text: '🔴 ATTENTION REQUIRED', bad: true, surfacedAlarm: false };
  }

  function applyCardVisibility(preferences, root) {
    const prefs = normalize(preferences);
    const doc = root || (typeof document !== 'undefined' ? document : null);
    if (!doc) return prefs;

    const cardNodes = doc.querySelectorAll('[data-card]');
    for (const node of cardNodes) {
      const key = node.getAttribute('data-card');
      const visible = prefs.cards[key] !== false;
      node.classList.toggle('pref-hidden', !visible);
      if (visible) node.removeAttribute('hidden');
      else node.setAttribute('hidden', '');
    }

    const sections = doc.querySelectorAll('[data-section]');
    for (const section of sections) {
      const keys = (section.getAttribute('data-section-cards') || '')
        .split(',')
        .map(value => value.trim())
        .filter(Boolean);
      const anyVisible = keys.length === 0
        ? true
        : keys.some(key => prefs.cards[key] !== false);
      section.classList.toggle('pref-hidden', !anyVisible);
      if (anyVisible) section.removeAttribute('hidden');
      else section.setAttribute('hidden', '');
    }

    return prefs;
  }

  return {
    CARD_KEYS,
    TELEMETRY_KEYS,
    METRIC_PREF_MAP,
    defaults,
    normalize,
    isCardVisible,
    isTelemetryVisible,
    filterPowerMetrics,
    applyPowerTelemetryPreferences,
    extractPowerAlert,
    buildOverallBanner,
    applyCardVisibility
  };
});
