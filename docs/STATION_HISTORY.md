# Station Contact History

Gateway Pulse reports RMS Relay and RMS Trimode station connections to the mobile app.

## What counts as a contact

One connection is either an RMS Relay `HF client connection from CALLSIGN` line or an
RMS Trimode ADIF record with a callsign, `QSO_DATE`, and `TIME_ON`. Trimode health,
channel, and SFI event lines are not station connections and are not indexed. ADIF UTC times are
converted to the gateway's local clock before comparison with Relay logs. An SSID suffix is
kept, so `KX7ABC-5` and `KX7ABC` are separate stations; alphabetic suffixes such as
`-R` and portable `/P` identifiers are also preserved. Duplicate copies of the
same record count once. When Relay and Trimode timestamps for the same callsign
are within two seconds, they represent one connection and the Relay record wins.
Every contact page entry carries a `source` (`Relay` or `Trimode`).

The existing `/api/status` station fields remain a recent Relay-only snapshot:

- `stationCounts`, `recentStationConnections`, and Relay `hourlyActivity` count each Relay connection once.
- `lastStation` / `lastRelayEvent` are the newest contact/event, not the last line parsed.
- These fields still cover only the newest 200 Relay log files, and `recentStationConnections`
  is capped at 50 rows. Use `/api/stations` for combined, archived history.

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

History is only as old as the retained Relay logs, Trimode ADIF files, and archive.
A background collector indexes every `*.log` file in `GatewayPulse:RelayLogs` and every
`*.adi`/`*.adif` file in `GatewayPulse:TrimodeLogs` (including subfolders) at startup and then every
`StationHistory:RefreshMinutes` (default 5, range 1-60). API requests also refresh, at most every
15 seconds. Only files whose size or modified time changed are re-read. Contacts are merged into
an archive:

- Default path: `StationContactHistory.json` next to the RF telemetry file.
- Override with `StationHistory:ArchivePath` in `appsettings.json`.
- No contact-count cutoff; the archive retains every distinct record it observes.
- `coverage.archiveHealthy` is false if the archive cannot be saved or a damaged archive was
  recovered. `archivedContacts` counts contacts confirmed in the saved file, not just memory.
  Failed writes are retried on every collector pass. The previous good archive is kept as
  `.bak`; a damaged primary is preserved as `.corrupt` for manual review. Do not remove that
  file until its contacts have been checked against the recovered archive.

Once archived, contacts survive source-log pruning. Contacts deleted before the archive
first ran can only be recovered from a remaining log, ADIF file, or backup. The `coverage`
object reports `oldestContact` (archive plus logs), `oldestLogContact`,
`oldestTrimodeContact`, `logFilesScanned`, `trimodeFilesScanned`, `logFolderAvailable`,
`trimodeFolderAvailable`, `archivedContacts`, and
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
