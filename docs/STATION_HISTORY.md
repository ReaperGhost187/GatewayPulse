# Station Contact History

Gateway Pulse reports RMS Relay station contacts to the mobile app and dashboard.

## What counts as a contact

One contact is one unique RMS Relay line `HF client connection from CALLSIGN`, identified by
its log timestamp (gateway local time) and callsign. An SSID suffix is kept, so `KX7ABC-5`
and `KX7ABC` are separate stations. The same line can appear in more than one
Relay log file; it is counted once. Trimode ARQ sessions (`SessionsToday`) are counted
separately and are not contacts.

`/api/status` fields follow the same rule:

- `stationCounts`, `recentStationConnections`, and Relay `hourlyActivity` count each contact once.
- `lastStation` / `lastRelayEvent` are the newest contact/event, not the last line parsed.
- These fields still cover only the newest 200 Relay log files, and `recentStationConnections`
  is capped at 50 rows. Use `/api/stations` for history.

## Endpoints

All are `GET`, under `/api/stations`, and require the mobile bearer token for remote requests
(same rule as `/api/status`). Requests that arrive through Cloudflare Tunnel or another proxy
are remote even though the tunnel connects over loopback; see "Local vs remote requests".

| Endpoint | Returns |
| --- | --- |
| `/api/stations?days=30` | Totals, per-station counts (ranked), daily counts for the last `days` (1-366, zero-filled), hour-of-day counts, latest contact, coverage |
| `/api/stations/contacts?station=&cursor=&limit=50` | Newest-first page of contacts (limit 1-200). Pass `nextCursor` back as `cursor` for the next page. `station` filters by callsign. |
| `/api/stations/{callsign}?days=90` | One station: contacts, rank, total stations, first/last seen, daily and hourly counts. `404` if the station has no contacts. |

Timestamps are ISO 8601 with the gateway's UTC offset.

## Coverage and archive

History is only as old as the Relay logs that still exist. A background collector indexes every
`*.log` file in `GatewayPulse:RelayLogs` (including subfolders) at startup and then every
`StationHistory:RefreshMinutes` (default 5, range 1-60). API requests also refresh, at most every
15 seconds. Only files whose size or modified time changed are re-read. Contacts are merged into
an archive:

- Default path: `StationContactHistory.json` next to the RF telemetry file.
- Override with `StationHistory:ArchivePath` in `appsettings.json`.
- Capped at the newest 100,000 contacts.

Once archived, contacts survive RMS Relay pruning its logs. Contacts pruned before the archive
first ran cannot be recovered. The `coverage` object reports `oldestContact` (archive plus logs),
`oldestLogContact`, `logFilesScanned`, `logFolderAvailable`, `archivedContacts`, and
`archiveStartedAt` so clients can state exactly what history exists.

## Local vs remote requests

A request is local (no token needed; settings and test routes allowed) only when its socket is
loopback **and** it carries none of these proxy headers: `CF-Connecting-IP`, `CF-Ray`,
`True-Client-IP`, `X-Forwarded-For`, `X-Forwarded-Host`, `X-Real-IP`, `Forwarded`.

Cloudflare Tunnel runs on the gateway PC and forwards internet requests to Kestrel over loopback.
Before this rule, those requests were treated as local, so `/api/status` and other protected
routes were readable without a token through the public hostname. Now they need the mobile
bearer token, and settings/test/radio-CAT routes return 403 through the tunnel.

The browser dashboard does not send a token, so it only works from the gateway PC itself
(`http://127.0.0.1:8080`) or with a token-aware client. Opened through the tunnel hostname,
its data requests return 401.
