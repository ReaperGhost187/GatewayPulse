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

## Derived metrics (not native serial fields)

- Reflected power: `Pf * ((SWR-1)/(SWR+1))^2`
- Return loss (dB): `-20 * log10((SWR-1)/(SWR+1))` (capped at 60 dB)
- R / X: `|Z| * cos(phase)`, `|Z| * sin(phase)`
- TX active: forward power above a small threshold (field 5 is autorange, not TX)
- Peak power: software peak-hold during the current transmission; last peak retained when idle

## Not available from the `P` poll

- Operating frequency (requires external CAT / rigctld; not in this frame)
- Hardware coupler identity beyond High/Mid/Low autorange
- Numeric value of the meter’s **User** SWR alarm setpoint
