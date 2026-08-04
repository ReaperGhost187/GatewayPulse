(function () {
  'use strict';

  const TRACE_DEFS = [
    { key: 'forward', label: 'Forward Power', unit: 'W', color: '#4fc3f7', defaultOn: true },
    { key: 'peak', label: 'Peak Power', unit: 'W', color: '#32d583', defaultOn: true },
    { key: 'reflected', label: 'Reflected (calc)', unit: 'W', color: '#ffb84d', defaultOn: true },
    { key: 'swr', label: 'SWR', unit: '', color: '#ff5d73', defaultOn: true, axis: 'swr' },
    { key: 'frequency', label: 'Frequency', unit: 'kHz', color: '#b39ddb', defaultOn: false, step: true, axis: 'freq' },
    { key: 'returnLoss', label: 'Return Loss', unit: 'dB', color: '#80cbc4', defaultOn: false, axis: 'db' },
    { key: 'batteryVoltage', label: 'Battery V', unit: 'V', color: '#90caf9', defaultOn: false, axis: 'batt' },
    { key: 'batteryCurrent', label: 'Battery A', unit: 'A', color: '#ce93d8', defaultOn: false, axis: 'batt' },
    { key: 'batterySoc', label: 'Battery SOC', unit: '%', color: '#a5d6a7', defaultOn: false, axis: 'soc' }
  ];

  const EVENT_COLORS = {
    tx_start: '#32d583',
    tx_end: '#8ca2b8',
    frequency_change: '#b39ddb',
    high_swr: '#ff5d73',
    high_reflected: '#ffb84d',
    battery_alarm: '#ff5d73',
    gateway_restart: '#4fc3f7',
    winlink_session_start: '#80cbc4',
    winlink_session_end: '#596b7e'
  };

  let data = null;
  let enabled = Object.fromEntries(TRACE_DEFS.map(t => [t.key, t.defaultOn]));
  let hoverT = null;
  let selectedEvent = null;
  let zoom = null; // { from, to } in ms

  function $(id) { return document.getElementById(id); }

  function num(v) {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }

  function fmtTime(iso) {
    if (!iso) return '—';
    return new Date(iso).toLocaleString();
  }

  function fmtVal(v, unit) {
    const n = num(v);
    if (n === null) return '—';
    if (unit === 'kHz') return n.toFixed(1) + ' kHz';
    if (unit === '%') return n.toFixed(0) + '%';
    if (unit === 'V') return n.toFixed(2) + ' V';
    if (unit === 'A') return n.toFixed(2) + ' A';
    if (unit === 'dB') return n.toFixed(1) + ' dB';
    if (unit === 'W') {
      if (Math.abs(n) >= 100) return n.toFixed(0) + ' W';
      if (Math.abs(n) >= 10) return n.toFixed(1) + ' W';
      return n.toFixed(2) + ' W';
    }
    if (!unit) return n.toFixed(2);
    return n.toFixed(2) + ' ' + unit;
  }

  function buildToggles() {
    const host = $('trace_toggles');
    host.innerHTML = '';
    TRACE_DEFS.forEach(t => {
      const label = document.createElement('label');
      label.className = 'trace-toggle';
      label.innerHTML = `<input type="checkbox" data-trace="${t.key}" ${enabled[t.key] ? 'checked' : ''}/>` +
        `<span class="swatch" style="background:${t.color}"></span>${t.label}`;
      label.querySelector('input').addEventListener('change', (e) => {
        enabled[t.key] = e.target.checked;
        draw();
      });
      host.appendChild(label);
    });
  }

  function currentRange() {
    return $('range_select').value || 'last';
  }

  async function load() {
    const range = currentRange();
    $('status_line').textContent = 'Loading…';
    try {
      let url = '/api/rf/analysis?range=' + encodeURIComponent(range);
      if (range === 'custom') {
        const from = $('custom_from').value;
        const to = $('custom_to').value;
        if (from) url += '&from=' + encodeURIComponent(new Date(from).toISOString());
        if (to) url += '&to=' + encodeURIComponent(new Date(to).toISOString());
      }
      const res = await fetch(url, { cache: 'no-store' });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      data = await res.json();
      zoom = null;
      selectedEvent = null;
      $('status_line').textContent =
        `${data.sampleCount || 0} samples · reflected power is calculated · ${fmtTime(data.from)} → ${fmtTime(data.to)}`;
      renderEvents();
      draw();
    } catch (err) {
      $('status_line').textContent = 'Failed to load analysis: ' + (err.message || err);
    }
  }

  function renderEvents() {
    const host = $('event_list');
    host.innerHTML = '';
    const events = (data && data.events) || [];
    if (!events.length) {
      host.innerHTML = '<div class="muted">No events in this range.</div>';
      return;
    }
    events.slice().reverse().forEach(ev => {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'event-row';
      const color = EVENT_COLORS[ev.type] || '#8ca2b8';
      row.innerHTML =
        `<span class="dot" style="background:${color}"></span>` +
        `<span><b>${escapeHtml(ev.type || '')}</b> · ${fmtTime(ev.timestamp || ev.Timestamp)}` +
        `<div class="muted">${escapeHtml(ev.detail || ev.Detail || '')}</div></span>`;
      row.addEventListener('click', () => {
        selectedEvent = ev;
        const t = new Date(ev.timestamp || ev.Timestamp).getTime();
        zoom = { from: t - 15000, to: t + 15000 };
        showTelemetryAt(t);
        draw();
      });
      host.appendChild(row);
    });
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  function seriesPoints(key) {
    const series = data && data.series && data.series[key];
    if (!Array.isArray(series)) return [];
    return series.map(p => ({ t: new Date(p.t).getTime(), v: num(p.v) })).filter(p => p.v !== null);
  }

  function windowBounds() {
    if (zoom) return zoom;
    const from = data && data.from ? new Date(data.from).getTime() : Date.now() - 3600000;
    const to = data && data.to ? new Date(data.to).getTime() : Date.now();
    return { from, to };
  }

  function showTelemetryAt(tMs) {
    const host = $('telemetry_at');
    const rows = [];
    TRACE_DEFS.forEach(def => {
      const pts = seriesPoints(def.key);
      if (!pts.length) return;
      let best = pts[0];
      let bestDist = Math.abs(pts[0].t - tMs);
      for (let i = 1; i < pts.length; i++) {
        const d = Math.abs(pts[i].t - tMs);
        if (d < bestDist) { best = pts[i]; bestDist = d; }
      }
      if (bestDist > 60000) return;
      let display = fmtVal(best.v, def.unit);
      if (def.key === 'swr' && best.v <= 1.00) display = '≤1.00';
      if (def.key === 'reflected') display += ' (calc)';
      rows.push(`<div><span class="muted">${def.label}</span> ${display}</div>`);
    });
    host.innerHTML = rows.length
      ? `<div class="label">At ${new Date(tMs).toLocaleString()}</div>${rows.join('')}`
      : '<div class="muted">No telemetry near this marker.</div>';
  }

  function draw() {
    const canvas = $('rf_chart');
    if (!canvas || !data) return;
    const ctx = canvas.getContext('2d');
    const cssW = canvas.clientWidth || 900;
    const cssH = canvas.clientHeight || 360;
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.floor(cssW * ratio));
    canvas.height = Math.max(1, Math.floor(cssH * ratio));
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);

    const pad = { l: 54, r: 18, t: 18, b: 36 };
    const plotW = cssW - pad.l - pad.r;
    const plotH = cssH - pad.t - pad.b;
    const bounds = windowBounds();
    const minT = bounds.from;
    const maxT = Math.max(bounds.to, minT + 1000);

    ctx.fillStyle = '#050a10';
    ctx.fillRect(0, 0, cssW, cssH);
    ctx.strokeStyle = '#20324a';
    ctx.strokeRect(pad.l, pad.t, plotW, plotH);

    // Collect y ranges per axis group
    const axes = {
      power: { min: 0, max: 1, keys: ['forward', 'peak', 'reflected'] },
      swr: { min: 1, max: 1.5, keys: ['swr'] },
      freq: { min: 0, max: 1, keys: ['frequency'] },
      db: { min: 0, max: 40, keys: ['returnLoss'] },
      batt: { min: 0, max: 15, keys: ['batteryVoltage', 'batteryCurrent'] },
      soc: { min: 0, max: 100, keys: ['batterySoc'] }
    };

    TRACE_DEFS.forEach(def => {
      if (!enabled[def.key]) return;
      const axisKey = def.axis || 'power';
      const axis = axes[axisKey];
      seriesPoints(def.key).forEach(p => {
        if (p.t < minT || p.t > maxT) return;
        axis.min = Math.min(axis.min, p.v);
        axis.max = Math.max(axis.max, p.v);
      });
    });

    Object.values(axes).forEach(axis => {
      if (axis.max <= axis.min) axis.max = axis.min + 1;
      const padY = (axis.max - axis.min) * 0.08;
      axis.min -= padY;
      axis.max += padY;
    });

    const xOf = t => pad.l + ((t - minT) / (maxT - minT)) * plotW;
    const yOf = (v, axisKey) => {
      const axis = axes[axisKey] || axes.power;
      return pad.t + plotH - ((v - axis.min) / (axis.max - axis.min)) * plotH;
    };

    // Grid
    ctx.strokeStyle = 'rgba(32,50,74,.55)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = pad.t + (plotH * i) / 4;
      ctx.beginPath();
      ctx.moveTo(pad.l, y);
      ctx.lineTo(pad.l + plotW, y);
      ctx.stroke();
    }

    // Traces
    TRACE_DEFS.forEach(def => {
      if (!enabled[def.key]) return;
      const pts = seriesPoints(def.key).filter(p => p.t >= minT && p.t <= maxT);
      if (!pts.length) return;
      const axisKey = def.axis || 'power';
      ctx.strokeStyle = def.color;
      ctx.lineWidth = 1.8;
      ctx.beginPath();
      pts.forEach((p, i) => {
        const x = xOf(p.t);
        const y = yOf(p.v, axisKey);
        if (def.step && i > 0) {
          ctx.lineTo(x, yOf(pts[i - 1].v, axisKey));
          ctx.lineTo(x, y);
        } else if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      });
      ctx.stroke();
    });

    // Event markers
    ((data && data.events) || []).forEach(ev => {
      const t = new Date(ev.timestamp || ev.Timestamp).getTime();
      if (t < minT || t > maxT) return;
      const x = xOf(t);
      const color = EVENT_COLORS[ev.type] || '#8ca2b8';
      ctx.strokeStyle = color;
      ctx.globalAlpha = 0.85;
      ctx.beginPath();
      ctx.moveTo(x, pad.t);
      ctx.lineTo(x, pad.t + plotH);
      ctx.stroke();
      ctx.globalAlpha = 1;
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(x, pad.t + 6, 3.5, 0, Math.PI * 2);
      ctx.fill();
    });

    // Hover
    if (hoverT != null && hoverT >= minT && hoverT <= maxT) {
      const x = xOf(hoverT);
      ctx.strokeStyle = 'rgba(238,244,251,.55)';
      ctx.setLineDash([4, 4]);
      ctx.beginPath();
      ctx.moveTo(x, pad.t);
      ctx.lineTo(x, pad.t + plotH);
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // Axis labels
    ctx.fillStyle = '#8ca2b8';
    ctx.font = '12px Segoe UI, system-ui, sans-serif';
    ctx.fillText(new Date(minT).toLocaleTimeString(), pad.l, cssH - 10);
    const endLabel = new Date(maxT).toLocaleTimeString();
    ctx.fillText(endLabel, pad.l + plotW - ctx.measureText(endLabel).width, cssH - 10);
    ctx.fillText(axes.power.max.toFixed(0) + ' W', 6, pad.t + 10);
    ctx.fillText(axes.power.min.toFixed(0) + ' W', 6, pad.t + plotH);

    canvas._plot = { pad, plotW, plotH, minT, maxT, xOf };
  }

  function onPointer(ev) {
    const canvas = $('rf_chart');
    const plot = canvas && canvas._plot;
    if (!plot || !data) return;
    const rect = canvas.getBoundingClientRect();
    const x = ev.clientX - rect.left;
    if (x < plot.pad.l || x > plot.pad.l + plot.plotW) {
      hoverT = null;
      draw();
      return;
    }
    hoverT = plot.minT + ((x - plot.pad.l) / plot.plotW) * (plot.maxT - plot.minT);
    showTelemetryAt(hoverT);
    draw();
  }

  // --- Historical SWR by Frequency (not a time-series) ---
  let swrData = null;
  let swrHoverIndex = -1;

  function fmtFreqHz(hz) {
    const n = num(hz);
    if (n === null) return '—';
    if (n >= 1e6) return (n / 1e6).toFixed(n % 1000 === 0 ? 3 : 4) + ' MHz';
    return (n / 1000).toFixed(n % 100 === 0 ? 1 : 2) + ' kHz';
  }

  function fmtSwrVal(v, atFloor) {
    const n = num(v);
    if (n === null) return '—';
    if (atFloor || n <= 1.00) return '≤1.00';
    return n.toFixed(2);
  }

  function fmtDur(seconds) {
    const n = num(seconds);
    if (n === null) return '—';
    if (n < 60) return n.toFixed(1) + ' s';
    return Math.floor(n / 60) + 'm ' + Math.round(n % 60) + 's';
  }

  async function loadSwrByFrequency() {
    const status = $('swr_status');
    if (!status) return;
    status.textContent = 'Loading…';
    try {
      const range = $('swr_range').value || '30d';
      const params = new URLSearchParams();
      params.set('range', range);
      params.set('metric', $('swr_metric').value || 'max');
      params.set('confidence', $('swr_confidence').value || 'all');
      params.set('source', $('swr_source').value || 'all');
      const minFwd = num($('swr_min_fwd').value);
      if (minFwd !== null) params.set('minForwardWatts', String(minFwd));
      const minKhz = num($('swr_min_khz').value);
      const maxKhz = num($('swr_max_khz').value);
      if (minKhz !== null) params.set('minFrequencyHz', String(Math.round(minKhz * 1000)));
      if (maxKhz !== null) params.set('maxFrequencyHz', String(Math.round(maxKhz * 1000)));
      if ($('swr_aggregate').checked) params.set('aggregate', 'true');
      const compare = $('swr_compare').value;
      if (compare) params.set('compare', compare);
      if (range === 'custom') {
        const from = $('swr_from').value;
        const to = $('swr_to').value;
        if (from) params.set('from', new Date(from).toISOString());
        if (to) params.set('to', new Date(to).toISOString());
      }

      const res = await fetch('/api/rf/swr-by-frequency?' + params.toString(), { cache: 'no-store' });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      swrData = await res.json();
      swrHoverIndex = -1;
      populateSwrSources(swrData.sources || []);
      status.textContent =
        `${swrData.observationCount || 0} observations · metric ${swrData.metric || 'max'}` +
        (swrData.aggregate ? ' · aggregated by frequency' : ' · scatter') +
        ` · ${fmtTime(swrData.from)} → ${fmtTime(swrData.to)}`;
      renderSwrCompare(swrData.comparison);
      drawSwrByFrequency();
      $('swr_hover').textContent = 'Hover or click a point for session details.';
    } catch (err) {
      status.textContent = 'Failed to load SWR-by-frequency: ' + (err.message || err);
    }
  }

  function populateSwrSources(sources) {
    const sel = $('swr_source');
    if (!sel) return;
    const current = sel.value || 'all';
    const keep = new Set(['all', ...sources]);
    sel.innerHTML = '<option value="all">All sources</option>';
    sources.forEach(s => {
      const opt = document.createElement('option');
      opt.value = s;
      opt.textContent = s;
      sel.appendChild(opt);
    });
    sel.value = keep.has(current) ? current : 'all';
  }

  function renderSwrCompare(comparison) {
    const box = $('swr_compare_box');
    if (!box) return;
    if (!comparison || !comparison.buckets || !comparison.buckets.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }
    const rows = comparison.buckets
      .filter(b => (b.currentSampleCount || 0) + (b.previousSampleCount || 0) > 0)
      .slice(0, 40)
      .map(b => {
        const worse = b.gettingWorse ? ' class="worse"' : '';
        const delta = b.deltaAverageSwr == null ? '—' : ((b.deltaAverageSwr > 0 ? '+' : '') + Number(b.deltaAverageSwr).toFixed(2));
        return `<tr${worse}>` +
          `<td>${fmtFreqHz(b.frequencyHz)}</td>` +
          `<td>${b.currentAverageSwr != null ? Number(b.currentAverageSwr).toFixed(2) : '—'} (n=${b.currentSampleCount || 0})</td>` +
          `<td>${b.previousAverageSwr != null ? Number(b.previousAverageSwr).toFixed(2) : '—'} (n=${b.previousSampleCount || 0})</td>` +
          `<td>${delta}</td></tr>`;
      }).join('');
    box.hidden = false;
    box.innerHTML =
      `<div><b>Comparison</b> · current ${comparison.mode} vs previous ${comparison.mode}</div>` +
      `<table><thead><tr><th>Frequency</th><th>Current avg SWR</th><th>Previous avg SWR</th><th>Δ</th></tr></thead>` +
      `<tbody>${rows || '<tr><td colspan="4">No overlapping frequency data.</td></tr>'}</tbody></table>`;
  }

  function drawSwrByFrequency() {
    const canvas = $('swr_freq_chart');
    if (!canvas || !swrData) return;
    const ctx = canvas.getContext('2d');
    const cssW = canvas.clientWidth || 900;
    const cssH = canvas.clientHeight || 360;
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.floor(cssW * ratio));
    canvas.height = Math.max(1, Math.floor(cssH * ratio));
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);

    const pad = { l: 48, r: 16, t: 16, b: 40 };
    const plotW = cssW - pad.l - pad.r;
    const plotH = cssH - pad.t - pad.b;
    ctx.fillStyle = '#050a10';
    ctx.fillRect(0, 0, cssW, cssH);
    ctx.strokeStyle = '#20324a';
    ctx.strokeRect(pad.l, pad.t, plotW, plotH);

    const aggregate = !!swrData.aggregate && Array.isArray(swrData.aggregates) && swrData.aggregates.length;
    let xs = [];
    let ys = [];
    let meta = [];

    if (aggregate) {
      swrData.aggregates.forEach(a => {
        const x = num(a.frequencyHz);
        const y = ($('swr_metric').value === 'average')
          ? (num(a.averageSwr) ?? num(a.medianSwr))
          : (num(a.worstSwr) ?? num(a.medianSwr));
        if (x === null || y === null) return;
        xs.push(x); ys.push(y);
        meta.push(a);
      });
    } else {
      (swrData.points || []).forEach(p => {
        const x = num(p.frequencyHz);
        const y = num(p.swr);
        if (x === null || y === null) return;
        xs.push(x); ys.push(y);
        meta.push(p);
      });
    }

    if (!xs.length) {
      ctx.fillStyle = '#8ca2b8';
      ctx.font = '14px Segoe UI, system-ui, sans-serif';
      ctx.fillText('No valid SWR/frequency observations in this range.', pad.l + 12, pad.t + 28);
      canvas._swrPlot = null;
      return;
    }

    let minX = Math.min(...xs);
    let maxX = Math.max(...xs);
    if (maxX <= minX) { minX -= 1000; maxX += 1000; }
    const padX = (maxX - minX) * 0.05;
    minX -= padX; maxX += padX;

    let minY = 1.0;
    let maxY = Math.max(1.5, Math.max(...ys));
    const padY = (maxY - minY) * 0.12;
    maxY += padY;

    const xOf = hz => pad.l + ((hz - minX) / (maxX - minX)) * plotW;
    const yOf = swr => pad.t + plotH - ((swr - minY) / (maxY - minY)) * plotH;

    // Grid
    ctx.strokeStyle = 'rgba(32,50,74,.55)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = pad.t + (plotH * i) / 4;
      ctx.beginPath();
      ctx.moveTo(pad.l, y);
      ctx.lineTo(pad.l + plotW, y);
      ctx.stroke();
      const swrLabel = (maxY - ((maxY - minY) * i) / 4).toFixed(2);
      ctx.fillStyle = '#8ca2b8';
      ctx.font = '11px Segoe UI, system-ui, sans-serif';
      ctx.fillText(swrLabel, 6, y + 3);
    }

    if (aggregate) {
      // Line ordered by frequency (already sorted server-side)
      ctx.strokeStyle = '#4fc3f7';
      ctx.lineWidth = 1.8;
      ctx.beginPath();
      xs.forEach((x, i) => {
        const px = xOf(x);
        const py = yOf(ys[i]);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.stroke();

      xs.forEach((x, i) => {
        const px = xOf(x);
        const py = yOf(ys[i]);
        const n = meta[i].sampleCount || 1;
        const r = Math.min(10, 3 + Math.log10(n + 1) * 3);
        ctx.fillStyle = i === swrHoverIndex ? '#ffb84d' : '#32d583';
        ctx.beginPath();
        ctx.arc(px, py, r, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = '#eef4fb';
        ctx.font = '10px Segoe UI, system-ui, sans-serif';
        ctx.fillText('n=' + n, px + r + 2, py + 3);
      });
    } else {
      // Scatter — do NOT connect chronologically
      xs.forEach((x, i) => {
        const px = xOf(x);
        const py = yOf(ys[i]);
        ctx.fillStyle = i === swrHoverIndex ? '#ffb84d' : '#4fc3f7';
        ctx.beginPath();
        ctx.arc(px, py, i === swrHoverIndex ? 5.5 : 3.5, 0, Math.PI * 2);
        ctx.fill();
      });
    }

    ctx.fillStyle = '#8ca2b8';
    ctx.font = '12px Segoe UI, system-ui, sans-serif';
    ctx.fillText(fmtFreqHz(minX + padX), pad.l, cssH - 12);
    const right = fmtFreqHz(maxX - padX);
    ctx.fillText(right, pad.l + plotW - ctx.measureText(right).width, cssH - 12);
    ctx.fillText('SWR', 8, pad.t - 2);

    canvas._swrPlot = { pad, plotW, plotH, minX, maxX, minY, maxY, xOf, yOf, xs, ys, meta, aggregate };
  }

  function showSwrDetail(index) {
    const host = $('swr_hover');
    if (!host || !swrData || index < 0) return;
    const plot = $('swr_freq_chart')._swrPlot;
    if (!plot || !plot.meta[index]) return;
    const m = plot.meta[index];
    if (plot.aggregate) {
      host.innerHTML =
        `<b>${fmtFreqHz(m.frequencyHz)}</b> · n=${m.sampleCount || 0}<br>` +
        `Median ${fmtSwrVal(m.medianSwr)} · Average ${fmtSwrVal(m.averageSwr)} · Worst ${fmtSwrVal(m.worstSwr)}` +
        (m.peakForwardWatts != null ? ` · Peak fwd ${fmtVal(m.peakForwardWatts, 'W')}` : '');
      return;
    }
    host.innerHTML =
      `<b>${fmtTime(m.t)}</b> · ${fmtFreqHz(m.frequencyHz)}<br>` +
      `SWR ${fmtSwrVal(m.swr, m.swrAtResolutionFloor)}` +
      (m.maxSwr != null ? ` · Max ${fmtSwrVal(m.maxSwr, m.swrAtResolutionFloor)}` : '') +
      (m.averageSwr != null ? ` · Avg ${fmtSwrVal(m.averageSwr)}` : '') +
      `<br>Peak ${fmtVal(m.peakForwardPowerWatts, 'W')}` +
      ` · Refl ${fmtVal(m.maxReflectedPowerWatts, 'W')} (${m.reflectedPowerSource || 'calculated'})` +
      ` · ${fmtDur(m.durationSeconds)} · ${m.burstCount || 1} burst(s)` +
      `<br>Source ${escapeHtml(m.frequencySource || 'Unknown')} · Confidence ${escapeHtml(m.frequencyConfidence || 'Unknown')}` +
      (m.frequencyAgeSecondsAtStart != null ? ` · Age ${Number(m.frequencyAgeSecondsAtStart).toFixed(0)}s` : '');
  }

  function onSwrPointer(ev) {
    const canvas = $('swr_freq_chart');
    const plot = canvas && canvas._swrPlot;
    if (!plot) return;
    const rect = canvas.getBoundingClientRect();
    const mx = ev.clientX - rect.left;
    const my = ev.clientY - rect.top;
    let best = -1;
    let bestDist = 18;
    plot.xs.forEach((x, i) => {
      const px = plot.xOf(x);
      const py = plot.yOf(plot.ys[i]);
      const d = Math.hypot(px - mx, py - my);
      if (d < bestDist) { bestDist = d; best = i; }
    });
    swrHoverIndex = best;
    if (best >= 0) showSwrDetail(best);
    drawSwrByFrequency();
  }

  function wire() {
    buildToggles();
    $('range_select').addEventListener('change', () => {
      const custom = currentRange() === 'custom';
      $('custom_range').hidden = !custom;
      load();
    });
    $('btn_reload').addEventListener('click', load);
    $('btn_reset_zoom').addEventListener('click', () => { zoom = null; draw(); });
    $('custom_apply').addEventListener('click', load);
    const canvas = $('rf_chart');
    canvas.addEventListener('mousemove', onPointer);
    canvas.addEventListener('mouseleave', () => { hoverT = null; draw(); });
    canvas.addEventListener('click', onPointer);

    ['swr_range', 'swr_metric', 'swr_source', 'swr_confidence', 'swr_compare'].forEach(id => {
      const el = $(id);
      if (el) el.addEventListener('change', () => {
        if (id === 'swr_range') $('swr_custom_range').hidden = $('swr_range').value !== 'custom';
        loadSwrByFrequency();
      });
    });
    $('swr_aggregate').addEventListener('change', loadSwrByFrequency);
    $('swr_reload').addEventListener('click', loadSwrByFrequency);
    $('swr_custom_apply').addEventListener('click', loadSwrByFrequency);
    ['swr_min_fwd', 'swr_min_khz', 'swr_max_khz'].forEach(id => {
      $(id).addEventListener('change', loadSwrByFrequency);
    });
    const swrCanvas = $('swr_freq_chart');
    swrCanvas.addEventListener('mousemove', onSwrPointer);
    swrCanvas.addEventListener('click', onSwrPointer);
    swrCanvas.addEventListener('mouseleave', () => {
      swrHoverIndex = -1;
      drawSwrByFrequency();
    });

    window.addEventListener('resize', () => { draw(); drawSwrByFrequency(); });
    load();
    loadSwrByFrequency();
    setInterval(() => {
      if (document.visibilityState === 'visible') {
        load();
        loadSwrByFrequency();
      }
    }, 15000);
  }

  document.addEventListener('DOMContentLoaded', wire);
})();
