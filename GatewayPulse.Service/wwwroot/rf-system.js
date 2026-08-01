(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.RfSystemView = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function num(value) {
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }

  function fmtPower(watts) {
    const n = num(watts);
    if (n === null) return '—';
    if (Math.abs(n) >= 100) return n.toFixed(0) + ' W';
    if (Math.abs(n) >= 10) return n.toFixed(1) + ' W';
    return n.toFixed(2) + ' W';
  }

  function fmtSwr(swr) {
    const n = num(swr);
    if (n === null) return '—';
    return n.toFixed(2);
  }

  function fmtDb(value) {
    const n = num(value);
    if (n === null) return '—';
    return n.toFixed(1) + ' dB';
  }

  function fmtOhms(value) {
    const n = num(value);
    if (n === null) return '—';
    return n.toFixed(1) + ' Ω';
  }

  function fmtPhase(value) {
    const n = num(value);
    if (n === null) return '—';
    return n.toFixed(1) + '°';
  }

  function statusFromTelemetry(rf) {
    if (!rf || rf.connected === false) {
      if (rf && rf.stale) return { text: 'STALE', cls: 'warn' };
      return { text: 'OFFLINE', cls: 'bad' };
    }
    if (rf.transmitting) {
      const swr = num(rf.swr);
      if (swr !== null && swr >= 3) return { text: 'CRITICAL SWR', cls: 'bad' };
      if (swr !== null && swr >= 2) return { text: 'HIGH SWR', cls: 'warn' };
      return { text: 'TX', cls: 'ok' };
    }
    return { text: 'IDLE', cls: 'ok' };
  }

  function buildPrimaryMetrics(rf) {
    const peak = rf && rf.transmitting
      ? rf.peakForwardPowerWatts
      : (rf && rf.lastPeakForwardPowerWatts);
    return [
      { key: 'forward', label: 'Forward Power', value: fmtPower(rf && rf.forwardPowerWatts) },
      { key: 'reflected', label: 'Reflected Power', value: fmtPower(rf && rf.reflectedPowerWatts) },
      { key: 'swr', label: 'SWR', value: fmtSwr(rf && rf.swr) },
      { key: 'peak', label: 'Peak Power', value: fmtPower(peak), hint: rf && !rf.transmitting && rf.lastPeakForwardPowerWatts != null ? 'last TX' : null }
    ];
  }

  function buildSecondaryMetrics(rf, preferences) {
    const prefs = (preferences && preferences.rfTelemetry) || (preferences && preferences.RfTelemetry) || {};
    const show = (key, fallback) => {
      if (Object.prototype.hasOwnProperty.call(prefs, key)) return prefs[key] !== false;
      const pascal = key.charAt(0).toUpperCase() + key.slice(1);
      if (Object.prototype.hasOwnProperty.call(prefs, pascal)) return prefs[pascal] !== false;
      return fallback !== false;
    };

    const rows = [];
    const add = (enabled, label, value) => {
      if (!enabled) return;
      if (value === null || value === undefined || value === '') return;
      rows.push({ label, value });
    };

    add(show('txState', true), 'TX state', rf && rf.transmitting ? 'Transmitting' : 'Idle');
    add(show('returnLoss', true), 'Return loss', rf && rf.returnLossDb != null ? fmtDb(rf.returnLossDb) : null);
    add(show('dbm', true), 'dBm', rf && rf.dbm != null ? Number(rf.dbm).toFixed(1) + ' dBm' : null);
    add(show('impedance', true), 'Impedance |Z|', rf && rf.impedanceOhms != null ? fmtOhms(rf.impedanceOhms) : null);
    add(show('phase', true), 'Phase', rf && rf.phaseDegrees != null ? fmtPhase(rf.phaseDegrees) : null);
    add(show('resistance', true), 'Resistance R', rf && rf.resistanceOhms != null ? fmtOhms(rf.resistanceOhms) : null);
    add(show('reactance', true), 'Reactance X', rf && rf.reactanceOhms != null ? fmtOhms(rf.reactanceOhms) : null);
    add(show('powerRange', true), 'Power range', rf && rf.powerRange);
    add(show('meterMode', true), 'Meter mode', rf && rf.meterMode);
    add(show('connectionState', true), 'Connection', rf && (rf.connectionState || (rf.connected ? 'Connected' : 'Disconnected')));
    add(show('protocolStatus', true), 'Protocol', rf && rf.protocolStatus);
    add(show('lastUpdate', true), 'Last update', rf && rf.lastUpdate ? new Date(rf.lastUpdate).toLocaleString() : null);
    if (rf && rf.comPort) rows.push({ label: 'COM port', value: rf.comPort + (rf.baudRate ? ' @ ' + rf.baudRate : '') });
    if (rf && rf.error) rows.push({ label: 'Status detail', value: rf.error });
    return rows;
  }

  function fmtFreq(khz) {
    const n = num(khz);
    if (n === null) return 'Unknown';
    return n.toFixed(1) + ' kHz';
  }

  function fmtDuration(seconds) {
    const n = num(seconds);
    if (n === null) return '—';
    if (n < 60) return n.toFixed(1) + ' s';
    return Math.floor(n / 60) + 'm ' + Math.round(n % 60) + 's';
  }

  function buildTransmissionRow(tx) {
    const row = document.createElement('div');
    row.className = 'event';
    const changed = !!(tx.frequencyChangedDuringTx || tx.FrequencyChangedDuringTx);
    const inProgress = !!(tx.inProgress || tx.InProgress);
    const start = tx.startTime || tx.StartTime;
    const source = tx.frequencySource || tx.FrequencySource || 'Unknown';
    const confidence = tx.frequencyConfidence || tx.FrequencyConfidence || 'Unknown';
    const age = tx.frequencyAgeSecondsAtStart ?? tx.FrequencyAgeSecondsAtStart;
    const startFreq = tx.startFrequencyKhz ?? tx.StartFrequencyKhz;
    const endFreq = tx.endFrequencyKhz ?? tx.EndFrequencyKhz;
    const peak = tx.peakForwardPowerWatts ?? tx.PeakForwardPowerWatts;
    const maxSwr = tx.maxSwr ?? tx.MaxSwr;
    const avgSwr = tx.averageSwr ?? tx.AverageSwr;
    const maxRef = tx.maxReflectedPowerWatts ?? tx.MaxReflectedPowerWatts;
    const duration = tx.durationSeconds ?? tx.DurationSeconds;

    let freqLine = `Frequency: ${fmtFreq(startFreq)}`;
    if (changed && endFreq != null) freqLine += ` → ${fmtFreq(endFreq)} (changed during TX)`;
    freqLine += ` · Source: ${source}`;
    if (age != null) freqLine += ` · Age at TX start: ${Number(age).toFixed(0)} seconds`;
    freqLine += ` · Confidence: ${confidence}`;

    const when = start ? new Date(start).toLocaleString() : '—';
    const title = inProgress ? 'TX in progress' : 'RF transmission';
    row.innerHTML =
      `<div><b>${title}</b> · ${when}</div>` +
      `<div class="muted">${freqLine}</div>` +
      `<div>Peak ${fmtPower(peak)} · Max refl ${fmtPower(maxRef)} · Max SWR ${fmtSwr(maxSwr)} · Avg SWR ${fmtSwr(avgSwr)} · ${fmtDuration(duration)}</div>`;
    return row;
  }

  return {
    statusFromTelemetry,
    buildPrimaryMetrics,
    buildSecondaryMetrics,
    buildTransmissionRow,
    fmtPower,
    fmtSwr
  };
});
