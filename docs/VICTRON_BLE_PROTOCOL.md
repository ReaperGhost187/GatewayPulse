# Victron BLE Instant Readout protocol notes

## Verified sources

- Victron Energy, *Extra manufacturer Data* (2022-12-14): <https://communityarchive.victronenergy.com/questions/187303/victron-bluetooth-advertising-protocol.html>
- Official protocol PDF: <https://communityarchive.victronenergy.com/storage/attachments/extra-manufacturer-data-2022-12-14.pdf>
- Home Assistant Victron BLE integration: <https://www.home-assistant.io/integrations/victron_ble/>
- `victron-ble` reference implementation and fixtures: <https://github.com/keshavdv/victron-ble>
- Python reference inspected during this implementation: `victron-ble` 0.9.3, especially `devices/base.py`, `devices/battery_monitor.py`, `devices/smart_battery_protect.py`, and `tests/test_battery_monitor.py`.

## Shared packet container

The Windows API removes Victron's company ID `0x02E1` and supplies the remaining manufacturer bytes:

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 1 | `0x10` Instant Readout/product advertisement |
| 1 | 1 | product-advertisement header byte |
| 2 | 2 | product ID, little-endian |
| 4 | 1 | device record type: `0x02` battery monitor/SmartShunt; `0x09` Smart BatteryProtect |
| 5 | 2 | little-endian IV/counter |
| 7 | 1 | first key byte used as a quick key check |
| 8 | 15 | AES-CTR encrypted record payload |

Encryption is AES-128 CTR. The two-byte IV initializes the low-order bytes of a zero-filled 128-bit little-endian counter. Every device uses its own 16-byte Instant Readout key. A wrong key-check byte or implausible decoded values rejects only that device's advertisement.

Diagnostic scan JSON redacts the cleartext key-check byte to `00` in manufacturer-data, advertisement-section, and reconstructed raw-byte fields before writing them. Decoding still receives the original in-memory advertisement. Production multi-device mode does not write per-advertisement scan logs.

Known model IDs include BatteryProtect `0xA3B0` through `0xA3B3`, SmartShunt 500A/50mV `0xA389`, and SmartShunt 300A/50mV `0xC038`.

## SmartShunt / battery-monitor payload (`0x02`)

Fields are packed least-significant-bit first across byte boundaries:

| Bits | Field | Conversion / unavailable value |
|---:|---|---|
| 16 | time remaining | minutes; `0xFFFF` = null |
| 16 signed | battery voltage | value / 100 V; `0x7FFF` = null |
| 16 | alarm reason flags | zero = no alarm |
| 16 | auxiliary value | interpreted by auxiliary type; `0xFFFF` = null |
| 2 | auxiliary type | 0 starter voltage, 1 midpoint voltage, 2 temperature, 3 disabled |
| 22 signed | battery current | value / 1000 A; all-one sentinel = null |
| 20 | consumed amp-hours magnitude | exposed as negative value / 10 Ah; all-one sentinel = null |
| 10 | state of charge | value / 10 percent; `0x3FF` = null |

Auxiliary voltage values use 0.01 V. Temperature uses 0.01 kelvin and is converted to Celsius. The protocol has one auxiliary field, so only the selected auxiliary mode is populated; unsupported alternatives remain null. Signed current is preserved. Signed watts are calculated only when both voltage and current are valid.

The advertisement does not provide a separate serial number or firmware field. Device address, advertised name, RSSI, connection state, and update timestamp come from the Windows advertisement event/runtime. Unknown product IDs are retained as hexadecimal model identifiers rather than fabricated model names.

## BatteryProtect payload (`0x09`)

| Byte offset | Size | Field |
|---:|---:|---|
| 0 | 1 | device state |
| 1 | 1 | output state: 1 on, 0 shutdown, 4 off, `0xFF` unavailable |
| 2 | 1 | error code |
| 3 | 2 | alarm flags |
| 5 | 2 | warning flags |
| 7 | 2 signed | input voltage / 100 V; `0x7FFF` unavailable |
| 9 | 2 | output voltage / 100 V; `0xFFFF` unavailable |
| 11 | 4 | off-reason flags |

BatteryProtect has no current, SOC, consumed Ah, temperature, or runtime in this record. Those fields remain null until SmartShunt data is available.

## Key acquisition

For each physical device in VictronConnect:

1. Pair with the device.
2. Open **Settings → Product Info**.
3. Enable **Instant Readout via Bluetooth**.
4. Select **Show** beside **Instant Readout Details**.
5. record the Bluetooth/MAC address and 32-hex-character advertisement key.

Changing the Victron Bluetooth PIN changes the advertisement key. Replace only the relevant protected key file and restart Gateway Pulse. Never place a real key in source control, appsettings, command lines, screenshots, logs, or support bundles.

## Security limitation

Instant Readout AES-CTR frames do not include an authentication tag or monotonic anti-replay proof. Address filtering and the one-byte key check are not cryptographic authentication. Treat decoded data as read-only monitoring information, never as authorization or a safety interlock.
