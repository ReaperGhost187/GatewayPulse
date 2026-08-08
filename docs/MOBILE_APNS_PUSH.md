# GatewayPulse Native APNs Push (Developer Guide)

Development-only additive path. **Pushover is unchanged.** Apple Push is fail-closed (`ApplePush:Enabled = false` by default).

## Architecture

```
existing alert decision → Pushover (unchanged) → publish MobileAlertEvent → MobileAlertRouter
  → per-device preferences → APNs HTTP/2 → iPhone
```

Emission points (parallel, after existing decisions):

| Source | When |
|---|---|
| `GatewayPulseService.EvaluateAlerts` | Relay / Trimode / ScannerStopped / Recovery transitions |
| `GatewayPulseService.EvaluateStationAlert` | Station connected (after first-observation suppress) |
| `RfAlertMonitor.MaybeSendAsync` | Same RF condition transitions / cooldowns as today |
| `PowerAlertMonitor` | Power status / alarm / BP output / device connect-stale transitions |

No alerts for TX start, scan hops, or CI-V frequency changes.

## Configuration

`appsettings.json`:

```json
"ApplePush": {
  "Enabled": false,
  "TeamId": "",
  "KeyId": "",
  "PrivateKeyPath": "C:\\PWM\\keys\\AuthKey_XXXXXXXXXX.p8",
  "BundleId": "com.yourorg.gatewaypulse",
  "DeviceRegistryPath": "C:\\PWM\\MobilePushDevices.json",
  "HistoryPath": "C:\\PWM\\MobilePushHistory.json",
  "HistoryCapacity": 200
}
```

Also requires `MobileApi:ApiToken` for remote device registration (same bearer as mobile telemetry).

## Device registration flow

1. iOS app obtains APNs device token (sandbox or production).
2. App generates a stable `deviceId` UUID (app install identity).
3. `POST /api/mobile/devices/register` with bearer (remote) or loopback:

```json
{
  "deviceId": "…",
  "apnsToken": "…",
  "environment": "sandbox",
  "appVersion": "1.0",
  "deviceName": "Tad iPhone"
}
```

4. Response returns device metadata **without** the APNs token (`hasToken: true/false` only).
5. Token rotations: same `deviceId`, new `apnsToken`.
6. Unregister: `DELETE /api/mobile/devices/{deviceId}`.

Registry file: `C:\PWM\MobilePushDevices.json` (atomic writes, restart-safe). Missing/empty is OK.

## Preferences

`GET/PUT /api/mobile/devices/{deviceId}/preferences`

- `masterEnabled` — global mute
- Category: `gateway`, `rf`, `power`
- Per-type toggles for each stable alert type
- **`powerWarning` defaults OFF** on phones
- Preferences affect **delivery only** — they never change gateway/RF/power engines

## Sandbox vs production

Per-device `environment`:

| Value | APNs host |
|---|---|
| `sandbox` (default) | `api.sandbox.push.apple.com` |
| `production` | `api.push.apple.com` |

Debug builds typically use sandbox tokens; TestFlight/App Store use production.

## iOS app flow (Mac / Xcode)

1. Enable Push Notifications capability + App ID with push.
2. Create APNs Auth Key (.p8) in Apple Developer → Keys. Note Team ID + Key ID.
3. Place `.p8` under `C:\PWM\keys\` on the gateway PC (never commit).
4. Set `ApplePush` in gateway `appsettings.json`, then `Enabled: true`.
5. App registers token after launch / token refresh against Cloudflare-tunneled or LAN mobile API.
6. Use `POST /api/mobile/devices/{deviceId}/test-notification` to verify.
7. `GET /api/mobile/push/status` shows enabled/configured/device counts — **no secrets**.

## Auth / security

- Remote `/api/mobile/*` **all methods** require MobileApi bearer.
- Other telemetry paths remain **GET + bearer** (unchanged).
- `/api/settings`, `/api/testalert`, `/api/radiocat`, `/api/rf/test-connection` stay **loopback-only**.
- Never log `.p8`, JWT, or APNs device tokens.
- APNs `410 Unregistered` / `BadDeviceToken` → clear token + mark stale; device row kept for prefs.

## Safe testing

1. Keep `ApplePush:Enabled = false` until credentials exist (fail-closed).
2. Unit tests use mocks / temp ECDSA keys — no real APNs.
3. With a real sandbox device: enable ApplePush, register, call test-notification.
4. Confirm Pushover still works independently when enabled.

## Canonical alert types

Gateway: `gateway.relay-offline`, `gateway.trimode-offline`, `gateway.scanner-stopped`, `gateway.recovery`, `gateway.station-connected`  
RF: `rf.disconnected`, `rf.stale`, `rf.recovery`, `rf.swr-warning`, `rf.swr-critical`, `rf.reflected`, `rf.high-power`, `rf.cleared`  
Power: `power.warning`, `power.critical`, `power.alarm`, `power.device-disconnected`, `power.device-stale`, `power.output-off`, `power.recovery`
