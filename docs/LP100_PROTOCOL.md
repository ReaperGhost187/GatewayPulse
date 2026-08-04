# TelePost LP-100A serial protocol (Gateway Pulse)

Source of truth: TelePost LP-100A Operating Manual, Software / serial section
(also cross-checked with trusted open-source parsers such as gsa700/lp100a-monitor).

## Serial settings (firmware ≥ 1.2.0.0)

| Setting | Value |
|--------|--------|
| Baud | **115200** |
| Data | 8 |
| Parity | None |
| Stop | 1 |
| Flow control | None |
| Cable | Straight-through DB9 M-F (not null-modem) |

Older firmware: 38400 (pre-1.2.0.0) or 19200 (pre-1.0.3); those builds may omit dBm/SWR.

## Commands used by Gateway Pulse

| Command | Purpose | Used? |
|---------|---------|-------|
| `P` | Poll for one telemetry frame | **Yes — required to request data** |
| `A` | Cycle SWR alarm setpoint | **No** (would alter meter config / PTT protect) |
| `M` | Cycle display mode | **No** (would leave Watts screen) |
| `F` | Cycle Avg/Peak/Tune | **No** (would alter meter mode) |

Gateway Pulse is **read-only**: it only sends `P`.

## Display snapshots (not RF-envelope samples)

Each `P` response is a **display snapshot** of what the LP-100A front panel is showing at poll time — not a high-rate RF-envelope sample stream.

For bursty modes such as **PACTOR**:

1. Set the LP-100A meter mode to **Peak** (Peak Hold) on the front panel (same guidance as TelePost VCP).
2. Gateway Pulse **does not** send `F`/`A`/`M` to force Peak Hold — the operator sets it.
3. Poll while transmitting at ~**50–80 ms** (`IntervalMs`, default **80**).
4. Coalesce overs with `SessionCoalesceMs` (default **6000** ms) so quiet gaps between PACTOR bursts become **one** Transmission History session.

Telemetry surfaces `meterMode` and a `meterModeHint` when the meter is not already in Peak/Tune.

## Response format

Send ASCII `P`. The meter replies with one frame starting with `;` and **no CR/LF**:

```text
;1457.00,49.3,005.0,2,N8LP ,0,2,61.6,1.02
```

Fields (comma-separated):

0. Forward power (W)
1. |Z| (ohms)
2. Phase (degrees)
3. SWR alarm index (0=off, 1=1.5, 2=2.0, 3=2.5, 4=3.0, 5=User)
4. Callsign (6 chars, space-padded)
5. Power range autorange (0=High, 1=Mid, 2=Low) — **not** a TX flag
6. Meter mode (0=Average, 1=Peak, 2=Tune)
7. dBm
8. SWR

Keep the LP-100A on the **Watts / Power** screen or power/vector values will not be live.

Raw frame bodies are exposed on telemetry as `lastRawFrameBody` / `recentRawFrameBodies` for front-panel compare.

## Derived metrics (not native serial fields)

- Reflected power: `Pf * ((SWR-1)/(SWR+1))^2` — labeled `reflectedPowerSource: "calculated"` / `reflectedPowerWattsCalculated`
- Return loss (dB): `-20 * log10((SWR-1)/(SWR+1))` (capped at 60 dB)
- R / X: `|Z| * cos(phase)`, `|Z| * sin(phase)`
- TX active: forward power above a small threshold (field 5 is autorange, not TX)
- Peak power: software peak-hold during the current transmission; last peak retained when idle
- SWR exactly **1.00** is the meter’s resolution floor — UI shows **≤1.00**, not “perfect match”

### SWR acceptance floor (`SwrMinForwardWatts`)

Default **0.5 W**. Below this forward power the LP-100A may still report SWR, but coupler noise makes session max/avg SWR unreliable. Only samples with forward ≥ `SwrMinForwardWatts` update session max/average SWR and max reflected.

## Session coalescing (PACTOR)

`SessionCoalesceMs` (default **6000**, legacy alias `TxEndDebounceMs`) is the quiet-gap timeout:

- Forward power above `TxThresholdWatts` starts a session (`BurstCount = 1`).
- Power dropping to ~0 does **not** end the session immediately.
- If power returns before the coalesce timeout, `BurstCount` increments and peaks/SWR continue accumulating.
- After `SessionCoalesceMs` of continuous below-threshold power, one completed RF session is written to Transmission History.

## Historical SWR by Frequency

Completed coalesced sessions with valid frequency + valid SWR are also written to
`C:\PWM\RfSwrByFrequency.json` for the RF Analysis **Historical SWR by Frequency** chart
(X = frequency, Y = SWR — not a time-series). API: `GET /api/rf/swr-by-frequency`.

## Raw capture mode

For offline PACTOR analysis, enable Settings → **Capture raw P frames** or run the collector with `--capture`.

Writes bounded timestamped logs under `C:\PWM\logs\lp100-raw-capture_YYYYMMDD_HHMMSS.log` (~8 MiB max).

## Not available from the `P` poll

- Operating frequency (requires external CAT / rigctld; not in this frame)
- Hardware coupler identity beyond High/Mid/Low autorange
- Numeric value of the meter’s **User** SWR alarm setpoint
- Direct reflected-power measurement (always calculated)
