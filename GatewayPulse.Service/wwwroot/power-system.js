(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.PowerSystemView = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function present(value) {
    return value !== null && value !== undefined && value !== '';
  }

  function number(value, digits) {
    if (value === null || value === undefined || value === '') return null;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed.toFixed(digits) : null;
  }

  function remaining(minutes) {
    if (minutes === null || minutes === undefined || minutes === '') return null;
    const value = Number(minutes);
    if (!Number.isFinite(value) || value < 0) return null;
    if (value < 120) return `${Math.round(value)} min`;
    if (value < 2880) return `${Math.round(value / 60)} hr`;
    const days = Math.floor(value / 1440);
    const hours = Math.round((value - days * 1440) / 60);
    return hours ? `${days} d ${hours} hr` : `${days} d`;
  }

  function age(timestamp, now) {
    if (!timestamp) return null;
    const elapsed = Math.max(0, Math.round((now.getTime() - new Date(timestamp).getTime()) / 1000));
    if (!Number.isFinite(elapsed)) return null;
    if (elapsed < 60) return `${elapsed}s ago`;
    if (elapsed < 3600) return `${Math.round(elapsed / 60)}m ago`;
    return `${Math.round(elapsed / 3600)}h ago`;
  }

  function normalizeLegacy(power) {
    if (Number(power.schemaVersion || 1) >= 2 && power.system) return power;
    const type = String(power.provider || '').toLowerCase().indexOf('batteryprotect') >= 0
      ? 'BatteryProtect'
      : (power.device || 'Power device');
    return {
      schemaVersion: 1,
      system: {
        status: power.connected ? (power.alarm || power.outputEnabled === false ? 'Critical' : 'Healthy') : 'Critical',
        voltage: power.voltage,
        current: power.current,
        watts: power.watts,
        stateOfCharge: power.stateOfCharge,
        consumedAmpHours: power.consumedAmpHours,
        timeRemainingMinutes: power.timeRemainingMinutes,
        powerState: power.powerState,
        outputEnabled: power.outputEnabled,
        alarm: power.alarm,
        alarmReason: power.alarmReason
      },
      devices: [{
        type,
        connected: power.connected === true,
        stale: power.connected !== true && String(power.error || '').toLowerCase().indexOf('stale') >= 0,
        connectionState: power.connected ? 'Connected' : 'Disconnected',
        device: power.device,
        deviceId: power.deviceId,
        voltage: power.voltage,
        outputEnabled: power.outputEnabled,
        alarm: power.alarm,
        alarmReason: power.alarmReason,
        rssi: power.rssi,
        lastUpdate: power.lastUpdate,
        error: power.error
      }]
    };
  }

  function deviceView(device, now) {
    const stale = device.stale === true || String(device.connectionState || '').toLowerCase() === 'stale';
    const connected = device.connected === true;
    let state = 'Disconnected';
    let tone = 'bad';
    if (connected) {
      state = 'Connected';
      tone = device.alarm ? 'bad' : 'good';
    } else if (stale) {
      state = 'Telemetry stale';
      tone = 'warn';
    } else if (String(device.connectionState || '').toLowerCase() === 'misconfigured') {
      state = 'Configuration needed';
      tone = 'warn';
    }

    const details = [];
    if (device.type === 'BatteryProtect' && present(device.outputEnabled))
      details.push(device.outputEnabled ? 'Output on' : 'Output off');
    if (device.alarm === true)
      details.push(`Alarm: ${device.alarmReason || 'Active'}`);
    else if (device.type === 'BatteryProtect' && device.alarm === false)
      details.push('No alarm');
    if (present(device.rssi)) details.push(`${device.rssi} dBm`);
    const updated = age(device.lastUpdate, now);
    if (updated) details.push(updated);

    return {
      name: device.type || device.device || 'Power device',
      identity: device.device || device.model || device.deviceId || null,
      state,
      tone,
      detail: details.join(' · '),
      updatedAge: updated,
      error: device.error || null,
      _raw: {
        type: device.type || null,
        outputEnabled: device.outputEnabled,
        alarm: device.alarm,
        alarmReason: device.alarmReason || null,
        rssi: device.rssi
      }
    };
  }

  function buildPowerSystemView(input, nowValue) {
    const power = normalizeLegacy(input || {});
    const system = power.system || {};
    const now = nowValue instanceof Date ? nowValue : new Date(nowValue || Date.now());
    const metrics = [];
    const add = (key, label, value, tone) => {
      if (present(value)) metrics.push({ key, label, value, tone: tone || '' });
    };

    const voltage = number(system.voltage, 2);
    const current = number(system.current, 2);
    const watts = number(system.watts, 1);
    const stateOfCharge = number(system.stateOfCharge, 1);
    const consumed = number(system.consumedAmpHours, 1);
    add('stateOfCharge', 'State of Charge', stateOfCharge === null ? null : `${Number(stateOfCharge)}%`);
    add('voltage', 'Voltage', voltage === null ? null : `${voltage} V`, 'blue');
    add('current', 'Current', current === null ? null : `${current} A`);
    add('watts', 'Power', watts === null ? null : `${watts} W`);
    add('remaining', 'Estimated Runtime', remaining(system.timeRemainingMinutes));
    const powerState = present(system.powerState) && String(system.powerState) !== 'Unknown'
      ? system.powerState
      : null;
    add('powerState', 'State', powerState);
    add('consumedAmpHours', 'Consumed', consumed === null ? null : `${consumed} Ah`);
    add('output', 'Protect output', present(system.outputEnabled) ? (system.outputEnabled ? 'Enabled' : 'Disabled') : null, system.outputEnabled === false ? 'bad' : 'good');
    add('alarm', 'Alarm', present(system.alarm) ? (system.alarm ? (system.alarmReason || 'Active') : 'None') : null, system.alarm ? 'bad' : 'good');

    const status = system.status || ((power.devices || []).some(device => device.connected) ? 'Healthy' : 'Critical');
    return {
      status,
      statusTone: status === 'Critical' ? 'bad' : (status === 'Healthy' ? 'good' : 'warn'),
      metrics,
      stateOfCharge: stateOfCharge === null ? null : Math.max(0, Math.min(100, Number(stateOfCharge))),
      devices: (power.devices || []).map(device => deviceView(device, now))
    };
  }

  function isOverallHealthy(gatewayHealthy, power) {
    if (gatewayHealthy !== true || !power) return false;
    if (power.system && present(power.system.status)) return power.system.status === 'Healthy';
    return power.connected === true;
  }

  return { buildPowerSystemView, isOverallHealthy };
});
