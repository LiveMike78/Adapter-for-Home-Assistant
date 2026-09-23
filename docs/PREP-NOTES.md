# AVEVA Adapter for Home Assistant — Prep Doc Part 1/5: Goal & Adapter Framework Reference

_Part of a 5-document set. See Part 5 for the full index and reference URLs._

## 1. Goal

Build an open-source **AVEVA Adapter for Home Assistant**, built on the AVEVA Adapter Framework, that:

1. Connects to a Home Assistant instance and **subscribes to live state-change events** (not polling).
2. Collects **entity state** and **entity attribute values** as individually selectable time-series points.
3. Supports **discovery** of all available states/attributes across the HA instance, with **auto-select**, driven by HA's own REST API (no MQTT-style "watch for N minutes" fallback needed — see §4).
4. **Backfills history** on startup and after any connectivity gap, using HA's history API, then continues live via WebSocket.
5. Ships as a normal AVEVA adapter: Windows x64, Linux x64, Linux ARM64, and Docker (multi-arch: amd64/arm64), consistent with other AVEVA adapters (MQTT, DNP3, RDBMS, OPC UA, etc.).
6. Is destination-agnostic for OMF egress — supports PI Server (PI Web API), AVEVA Data Hub/CONNECT, and Edge Data Store, since the framework handles egress generically. No destination-specific code needed; just document all three.

---

## 2. AVEVA Adapter Framework — what it is and how it's consumed

- Cross-platform, modular **.NET framework** for building OMF-egress data-collection apps ("adapters" and Edge Data Store apps). Open-sourced by AVEVA.
- **Target framework: .NET 10** (per official package docs — all `AdapterFramework.Data.*` packages target .NET 10). Verify this hasn't moved to a newer LTS at build time.
- Distributed as **composable NuGet packages** (not "clone and copy the source" — reference only what you need):

  | Package | Purpose |
  |---|---|
  | `AdapterFramework.Data.DataModel` | Core data model/types shared across the framework |
  | `AdapterFramework.Data.Framework.Abstractions` | Shared interfaces/contracts: configuration, security, events, health, message processing |
  | `AdapterFramework.Data.Framework.Common` | Shared utilities, HTTP constants, security helpers, health models |
  | `AdapterFramework.Data.Framework.AdapterCommon` | Common adapter base classes, **history recovery**, scheduling, **discovery** |
  | `AdapterFramework.Data.Framework.Host` | Executable host app — composes and runs the full adapter system |
  | `AdapterFramework.Data.Framework.ConfigurationProvider` | Config management/persistence, ASP.NET Core integration, command handling (this is what exposes the `/api/v1/configuration/...` REST surface) |
  | `AdapterFramework.Data.Framework.ComponentIdProvider` | Component identity service |
  | `AdapterFramework.Data.Framework.Registry` | Component registry — registration, discovery, config mapping |
  | `AdapterFramework.Data.Framework.DataProtector` | Encrypts/decrypts sensitive config (backs the `[Protected]` attribute) |
  | `AdapterFramework.Data.Framework.PersistentQueue` | Durable buffering/compression/persistent queueing for reliable egress |
  | (additional capability packages exist for failover/high-availability, serialization/messaging, diagnostics — reference as needed) |

  Typical minimum reference set for a new adapter:
  ```
  dotnet add package AdapterFramework.Data.Framework.Abstractions
  dotnet add package AdapterFramework.Data.Framework.AdapterCommon
  dotnet add package AdapterFramework.Data.Framework.Host
  ```
  Add `ConfigurationProvider`, `Registry`, `ComponentIdProvider`, `DataProtector`, `PersistentQueue` etc. per the sample's actual references (verify against the sample `.csproj` at build time — the exact combination wasn't fully visible during this research pass).

- **Reference implementation: `WeatherGovAdapter`**, in `Sample/` of `AVEVA/adapter-framework` on GitHub (also mirrored under the docs site). Structure to mirror:
  ```
  Sample/WeatherGovAdapter/
    WeatherGovAdapter.sln
    AdapterFramework.Data.Adapter.WeatherGovAdapter/     <- main adapter project
    AdapterFramework.Data.System.Host/                   <- local host used to run the adapter
      appsettings.json
    Tests/AdapterFramework.Data.Adapter.WeatherGovAdapter.UnitTests/
    ConfigurationExamples/
      WeatherGov.DataSource.json
      WeatherGov.DataSelection.json
      WeatherGov.Schedules.json
      WeatherGov.Discoveries.json
    Edge-Module/
      WeatherGovAdapter_defaultConfig.json                <- default edge-style config
  ```
  Build/run pattern from the docs:
  ```
  cd Sample/WeatherGovAdapter
  dotnet build AdapterFramework.Data.Adapter.WeatherGovAdapter\AdapterFramework.Data.Adapter.WeatherGovAdapter.csproj -c Debug --ignore-failed-sources
  dotnet run --project AdapterFramework.Data.System.Host\AdapterFramework.Data.System.Host.csproj --configuration Debug
  ```

- **Implementing an adapter** (from "Implement your adapter" guidance):
  - Add connection properties (host, port, credentials, tokens, etc.) to a `DataSourceConfiguration.cs`.
    - Implement `Equals()` and `Validate()` on it.
    - Mark sensitive properties (tokens, connection strings, passwords) with **`[Protected]`** so the framework encrypts them at rest via `DataProtector`.
  - Define the data-selection model (properties needed to identify/select a data item — for us: entity ID + optional attribute name, see §5).
  - Implement the actual collection logic (for us: WebSocket subscriber + REST history backfill, see §4/§6) and discovery logic (see §5).

- **Configuration REST API** (framework-provided, same across all AVEVA adapters): default port **5590**, base path `http://localhost:5590/api/v1/configuration/<componentId>/...`, sub-resources include `DataSource`, `DataSelection`, `Schedules`, `Discoveries`. Discovery is triggered via:
  ```
  POST http://localhost:5590/api/v1/configuration/<componentId>/Discoveries
  { "id": "<optional>", "query": "<adapter-specific query string>", "autoSelect": true|false }
  ```
  Discovery supports: retrieving results, polling status, cancel/delete, merging results into data selection, and diffing current discovery vs. previous discovery vs. current data selection. Only one discovery at a time is typical across other adapters — follow that convention unless the framework docs say otherwise.
  - Other adapters (RDBMS, DNP3, Structured Data Files) encode discovery scoping into a single `query` string of `key=value;key2=value2` pairs. We'll follow the same convention for consistency (see §5.3).

- **Licensing:** Adapter Framework itself is Apache-2.0 (per its GitHub repo). Use a compatible license (MIT or Apache-2.0) for this project; Apache-2.0 recommended for consistency with the framework and other AVEVA adapters.

### Known gaps to verify at build time (docs are JS-rendered; not everything was extractable during this research pass)
- Exact interface/base-class names to implement for a pollable vs. push/streaming data source (e.g. whether there's a dedicated "push" collector interface vs. the sample's poll-based `HttpClient` pattern — the WeatherGov sample polls; our HA source is push/event-driven via WebSocket, so check `AdapterCommon` for an event-driven collector base or confirm we run our own background WebSocket listener and call the framework's ingestion API directly).
- Exact `DataSelection` JSON schema field names (`selectValue`, `indexColumn` etc. are confirmed patterns from other adapters — RDBMS/DNP3 — but the WeatherGov sample's exact `WeatherGov.DataSelection.json` field names weren't retrievable).
- Exact `Discoveries` result schema fields for this framework version (confirmed pattern via the DNP3 adapter: `id`, `query`, `startTime`, `endTime`, `progress`, `itemsFound`, `newItems`, `resultUri`, `autoSelect`, `status`, `errors` — very likely consistent across adapters built on the same framework, but confirm against `AdapterCommon`).
- Whether `AdapterFramework.Data.Framework.AdapterCommon`'s "history recovery" feature is a plug-in hook we implement (e.g. an `IHistoryRecoverable` interface) or something we build entirely ourselves calling the standard ingestion API with backdated timestamps. Given HA's `/api/history/period` returns arbitrary past data, plan to implement history backfill ourselves (see §6) and adopt the framework's hook only if one cleanly exists.
- Confirm current package version (2.1.0 was seen in docs "Last Updated Aug 2026" — check for newer at build time) and pin it in the `.csproj`/`Directory.Packages.props`.

---

# AVEVA Adapter for Home Assistant — Prep Doc Part 2/5: Home Assistant API Reference

_Part of a 5-document set._

## 3. Home Assistant — API surfaces we'll use

Two different APIs are needed; **the REST API alone cannot stream events** (its `/api/events` endpoint only lists event types + listener counts, it does not push anything). Correcting the original assumption: event subscription requires HA's **WebSocket API**.

### 3.1 Authentication
- **Long-Lived Access Token**, generated by the user from their HA profile page (`http://<ha>:8123/profile`).
- Used as `Authorization: Bearer <token>` on REST calls, and as the `access_token` in the WebSocket `auth` message.
- Store as `[Protected]` in `DataSourceConfiguration.cs`.

### 3.2 REST API (`http://<host>:<port>/api/...`, default port 8123, or 80 on HAOS)
Used for: initial/on-demand discovery, full state snapshot, and history backfill. Key endpoints:

- `GET /api/` — health check (`{"message": "API running."}`)
- `GET /api/config` — instance config (time zone, unit system, version, etc.) — useful for adapter metadata/health.
- `GET /api/states` — **full dump of every entity's current state + attributes.** This is the backbone of discovery (see §5).
  ```json
  [
    {
      "entity_id": "sensor.kitchen_temperature",
      "state": "21.4",
      "last_changed": "...",
      "last_updated": "...",
      "attributes": { "unit_of_measurement": "°C", "friendly_name": "Kitchen Temperature", "device_class": "temperature" }
    }
  ]
  ```
- `GET /api/states/<entity_id>` — single entity snapshot.
- `GET /api/history/period/<timestamp>?filter_entity_id=<id1,id2>&end_time=<ts>&minimal_response&no_attributes&significant_changes_only` — historical state changes for backfill. `<timestamp>` defaults to 1 day before "now" if omitted. Returns an array-of-arrays (one inner array per requested entity).
- `GET /api/events` — list of event types + listener counts (NOT a stream — informational only, could be used to sanity-check that `state_changed` has listeners, not to receive data).
- `GET /api/error_log` — plaintext error log, potentially useful for adapter diagnostics/health reporting.

All endpoints return/accept JSON; auth header required on every call (`🔒` in HA docs = requires the Bearer token).

### 3.3 WebSocket API (`ws://<host>:<port>/api/websocket`)
Used for: **live event subscription.**

**Handshake:**
1. Client connects.
2. Server sends `{"type": "auth_required", "ha_version": "..."}`.
3. Client sends `{"type": "auth", "access_token": "<token>"}`.
4. Server replies `{"type": "auth_ok", ...}` or `{"type": "auth_invalid", "message": "..."}` (then disconnects).

**Subscribe to state changes** (this is the core of live collection):
```json
{ "id": 1, "type": "subscribe_events", "event_type": "state_changed" }
```
Server acks with a `result` message, then for every state change sends:
```json
{
  "id": 1,
  "type": "event",
  "event": {
    "event_type": "state_changed",
    "time_fired": "2016-11-26T01:37:24.265429+00:00",
    "origin": "LOCAL",
    "data": {
      "entity_id": "light.bed_light",
      "old_state": { "entity_id": "...", "state": "off", "attributes": {...}, "last_changed": "...", "last_updated": "...", "context": {...} },
      "new_state": { "entity_id": "...", "state": "on",  "attributes": {...}, "last_changed": "...", "last_updated": "...", "context": {...} }
    }
  }
}
```
**Important:** a single `state_changed` event carries both the new **state** and **every current attribute** — no separate "attribute changed" event type exists or is needed. One subscription covers both data selection categories from §1.2 of the original request.

Other useful WS commands: `get_states` (state dump over WS instead of REST — equivalent to `GET /api/states`), `ping`/`pong` (heartbeat — use to detect a dead connection and trigger reconnect + gap backfill), `unsubscribe_events`.

**Reconnection design implication:** HA restarts, network blips, etc. will drop the WebSocket. On reconnect: re-auth, re-subscribe, then immediately run a history backfill (§6) covering the outage window to close the gap before resuming live streaming.

---

## 4. Why full discovery (not time-boxed passive discovery) is viable here

Unlike MQTT — where the adapter has no prior knowledge of topics and must passively watch traffic for a configured duration to infer what's available — **Home Assistant's REST API already exposes a complete, queryable inventory of every entity, its current state, and every current attribute** via a single `GET /api/states` call. So:

- Discovery = call `GET /api/states` once, enumerate entities (optionally filtered — see §5.3), enumerate each entity's scalar attributes, emit one discovered item per state and per qualifying attribute (§5), return them via the framework's standard discovery result shape, support `autoSelect`.
- No polling window, no "listen for N minutes" fallback needed. This should be flagged as a **selling point** in the adapter's README relative to the MQTT adapter's discovery model.
- Optional enhancement: also support a "live-observed" discovery mode (subscribe briefly and note which entities actually change) purely as a way to help users find *active* points vs. static ones — but this is a nice-to-have filter, not a requirement, since full discovery already works.

---

# AVEVA Adapter for Home Assistant — Prep Doc Part 3/5: Data Modeling & Configuration Schema

_Part of a 5-document set._

## 5. Data modeling (decisions made)

### 5.1 Granularity: one selectable point per attribute (decided)
- Entity **state** is one selectable point: e.g. `sensor.kitchen_temperature` → its `state` value (numeric/string as reported by HA — most `sensor.*` are numeric strings, `binary_sensor.*`/`switch.*`/etc. are on/off style strings — see §5.4 for typing).
- Each **scalar attribute** is its own additional selectable point: e.g. `sensor.kitchen_temperature.battery_level`, `climate.living_room.current_temperature`, `light.bed_light.brightness`.
- Suggested selection-item naming convention (mirrors the entity_id + attribute pattern, PI-tag friendly):
  - State point ID: `<entity_id>` (e.g. `sensor.kitchen_temperature`) or `<entity_id>.state` if a clean namespace separator is preferred — pick one and apply consistently across discovery + OMF stream naming. Recommend `<entity_id>` for the state (matches how HA users already think of the entity) and `<entity_id>.<attribute_name>` for attributes.
  - OMF container/stream ID: same string, sanitized for OMF's allowed character set (check OMF 1.2/2.0 spec for stream ID constraints — likely alphanumeric + `.`/`_`/`-`; HA entity IDs (`domain.object_id`) and attribute names (snake_case) are already compatible with typical OMF id rules, but confirm at build time).

### 5.2 Attribute filtering (scalar-only, decided by default — confirm no objection when building)
HA attributes vary widely: numbers, strings, booleans, but also lists (e.g. `entity_id` lists on group/zone entities), nested dicts (e.g. some `climate`/`weather` attributes), and large arrays (e.g. RGB color arrays, forecast lists). Recommended default:
- **Include:** `number`, `string`, `bool` attribute values as individual points.
- **Include (flattened, optional/configurable):** short fixed-length arrays of primitives that represent a single concept, e.g. `rgb_color: [254, 208, 0]` → `light.bed_light.rgb_color_r/_g/_b`, if a "flatten simple arrays" option is enabled. Keep this off by default for MVP and document as a future enhancement — MVP just skips non-scalar attributes.
- **Exclude by default:** nested objects/dicts, long/variable-length arrays (forecast lists, entity_id member lists), `null`/`unknown`/`unavailable` values at discovery time (still collect them live if they later become populated — don't permanently exclude the *point*, just don't emit a value for an unavailable reading).
- Always exclude framework/UI-only attributes that add no time-series value if desired (e.g. `icon`, `friendly_name`, `supported_features`, `editable`) — make this an **excluded-attribute-name list**, configurable, with a sensible built-in default (`friendly_name`, `icon`, `entity_picture`, `supported_features`, `attribution`, `context`, `assumed_state` as starting excludes).

### 5.3 Data source / discovery scoping (entity filtering)
Mirror the MQTT/RDBMS adapters' `include`/`exclude` query convention:
- `DataSourceConfiguration` should support include/exclude glob or regex patterns on:
  - **domain** (e.g. include only `sensor,binary_sensor,climate,light,switch`, exclude `automation,script,update,person,zone` by default — these tend to be control/metadata entities of low historian value).
  - **entity_id** (explicit allow/deny list or wildcard, e.g. `sensor.kitchen_*`).
  - **excluded attribute names** (see §5.2).
- Discovery query string convention (matches DNP3/RDBMS/Structured-Data-Files pattern), e.g.:
  ```
  IncludeDomains=sensor,binary_sensor,climate;ExcludeEntities=sensor.debug_*;IncludeAttributes=true
  ```
  Exact key names are ours to define since this is a new adapter — keep consistent with the `Key=Value;Key2=Value2` string convention used elsewhere in the framework.

### 5.4 Typing / value conversion
- HA `state` values are always strings over the wire, even for numeric sensors (e.g. `"21.4"`). The adapter must attempt numeric parsing (`double.TryParse`) for points whose HA `device_class`/`unit_of_measurement`/discovered-history values look numeric, falling back to string type for genuinely categorical states (`"on"/"off"`, `"home"/"not_home"`, weather condition strings, etc.).
- Recommend inferring the OMF/PI type **per point at discovery time** (sample a few values via `/api/states` and/or recent `/api/history/period`) and locking it in the data selection item, consistent with how PI historians expect a stable type per tag. Flag type-change-at-runtime as a warning/skip-value condition rather than a crash.
- `unit_of_measurement` attribute (when present) should be surfaced as the point's engineering units/UOM metadata if the framework's data selection model supports a UOM field (RDBMS adapter's discovery query shows `UoM=` support — check if `DataSelection` items generally support a UOM property).

### 5.5 History backfill (decided: yes)
- On **adapter startup**, and on **reconnect after any WebSocket gap**, call `GET /api/history/period/<gap_start>?filter_entity_id=<comma-separated selected entities>&end_time=<gap_end>` for all currently-selected entities and replay the returned state changes into the framework's ingestion pipeline with their original timestamps, before resuming live streaming.
- Batch entity IDs sensibly (HA's endpoint accepts a comma-separated list — chunk to a safe batch size, e.g. 50–100 entities per call, to avoid oversized requests on large installs).
- Track "last successfully ingested timestamp per point" (or at least per adapter run) so backfill windows are the actual outage duration, not always "1 day" (HA's default). Persist this checkpoint so a full adapter restart (not just a WS reconnect) also only backfills the real gap, not redundant history. This checkpoint persistence should use the framework's existing state/config persistence mechanisms if available (check `ConfigurationProvider`/`Registry` for a suitable durable key-value store) rather than inventing a bespoke file.
- Attribute history: HA's `/api/history/period` returns full `state` + `attributes` per historical row (same shape as `/api/states`), so attribute backfill comes along "for free" from the same call — no separate attribute-history endpoint needed.
- Respect `minimal_response`/`no_attributes` query flags where attribute history isn't needed for a given point (performance optimization, optional).

---

## 6. Adapter configuration schema (draft — to refine into `DataSourceConfiguration.cs` at build time)

```jsonc
{
  "baseUrl": "http://homeassistant.local:8123",   // HA REST base URL
  "webSocketUrl": "ws://homeassistant.local:8123/api/websocket", // derive from baseUrl if omitted
  "accessToken": "<Protected>",                    // Long-Lived Access Token
  "allowInsecureBaseUrl": false,                    // require https/wss unless explicitly overridden (mirrors WeatherGov's allowInsecureBaseUrl pattern)
  "requestTimeoutMs": 10000,
  "maxRetries": 3,
  "reconnectBackoffMs": { "initial": 1000, "max": 60000 },
  "historyBackfillEnabled": true,
  "historyBackfillMaxLookbackHours": 24,            // safety cap if no checkpoint exists yet
  "historyBatchSize": 100,                          // entities per /api/history/period call
  "includeDomains": ["sensor","binary_sensor","climate","light","switch","cover","fan","media_player","lock","water_heater","weather","person","device_tracker"],
  "excludeDomains": ["automation","script","update","zone","persistent_notification"],
  "includeEntities": [],                            // optional explicit allow-list / glob
  "excludeEntities": [],                            // optional explicit deny-list / glob
  "excludeAttributes": ["friendly_name","icon","entity_picture","supported_features","attribution","context","assumed_state"],
  "flattenSimpleArrayAttributes": false              // MVP default off, see §5.2
}
```

---

# AVEVA Adapter for Home Assistant — Prep Doc Part 4/5: Repo Structure & Build/CI Plan

_Part of a 5-document set._

## 7. Repo structure plan

```
aveva-adapter-for-home-assistant/
  AdapterForHomeAssistant.sln
  src/
    AdapterFramework.Data.Adapter.HomeAssistant/     <- main adapter project (mirrors WeatherGov naming)
      DataSourceConfiguration.cs
      DataSelectionItem.cs
      HomeAssistantRestClient.cs
      HomeAssistantWebSocketClient.cs
      DiscoveryService.cs
      HistoryBackfillService.cs
      StateChangeIngestor.cs
      AdapterForHomeAssistant.csproj
    AdapterFramework.Data.System.Host/                <- local host, per sample convention
      appsettings.json
      AdapterFramework.Data.System.Host.csproj
  tests/
    AdapterFramework.Data.Adapter.HomeAssistant.UnitTests/
  ConfigurationExamples/
    HomeAssistant.DataSource.json
    HomeAssistant.DataSelection.json
    HomeAssistant.Schedules.json
    HomeAssistant.Discoveries.json
  Edge-Module/
    HomeAssistantAdapter_defaultConfig.json
  docker/
    Dockerfile                # multi-stage, multi-arch (linux-x64 + linux-arm64)
  .github/
    workflows/
      build-test.yml          # PR/CI: build + unit tests, all RIDs
      release.yml              # tag-triggered: publish self-contained win-x64/linux-x64/linux-arm64 zips + push multi-arch Docker image to GHCR
  README.md
  LICENSE                      # Apache-2.0 (recommended, matches framework)
  CONTRIBUTING.md
```

---

## 8. Cross-platform build & packaging plan

- **.NET self-contained publish**, per target RID:
  ```
  dotnet publish src/.../AdapterForHomeAssistant.csproj -c Release -r win-x64   --self-contained true -p:PublishSingleFile=true
  dotnet publish src/.../AdapterForHomeAssistant.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
  dotnet publish src/.../AdapterForHomeAssistant.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true
  ```
- **Docker**: multi-stage `Dockerfile` (SDK image to build, `mcr.microsoft.com/dotnet/runtime:10.0` or `aspnet:10.0` — confirm which the framework's own host needs, since `ConfigurationProvider` mentions ASP.NET Core integration — as the runtime base), built for `linux/amd64` and `linux/arm64` via `docker buildx`, pushed to GitHub Container Registry (`ghcr.io/<org>/aveva-adapter-for-home-assistant`).
- **Windows service** vs **Linux systemd unit**: other AVEVA adapters install as a Windows Service / systemd service — document install steps for both, consistent with the ecosystem (Mike has prior experience containerizing the AVEVA ACU with a `policy-rc.d` systemd workaround — same general packaging pattern likely applies here for a Debian-based Docker image if the adapter host tries to register a system service inside the container; the container should instead run the host directly in the foreground, no systemd needed, since Docker doesn't need a service manager).
- **GitHub Actions**:
  - CI on PR: `dotnet build` + `dotnet test` on ubuntu-latest (and optionally windows-latest for the win-x64 leg).
  - Release on tag (`v*`): matrix-build the three self-contained RIDs, zip each, attach to a GitHub Release; build+push the multi-arch Docker image with matching version tags + `latest`.

---

## 9. Open questions / things to confirm once inside the actual build (not blocking, but flag in the build prompt)

1. Exact base classes/interfaces in `AdapterFramework.Data.Framework.AdapterCommon` for: a push/event-driven collector (vs. the sample's poll-based pattern), and whether "history recovery" has a first-class hook to implement rather than building it fully ourselves.
2. Exact `DataSelection`/`Discoveries` JSON field names for this framework version (patterns are well-established from sibling adapters but should be verified against actual framework source/NuGet package contents, not just docs prose).
3. Current framework/package version to pin (2.1.0 observed; check for newer).
4. Whether `DataSelection` items support a UOM (units of measurement) field to carry HA's `unit_of_measurement` attribute.
5. OMF stream/container ID character constraints, to finalize the naming convention from §5.1.
6. Confirm minimum HA version required (WebSocket API and `/api/history/period` are long-stable, so this should be a low bar — just needs stating in the README, e.g. "HA Core 2023.x+").

---

# AVEVA Adapter for Home Assistant — Prep Doc Part 5/5: Open Questions, References, Decisions Log

_Part of a 5-document set. This part also serves as the index:_
1. Part1-Goal-and-Framework-Reference.md — goal + AVEVA Adapter Framework reference
2. Part2-HomeAssistant-API-Reference.md — HA REST + WebSocket API reference
3. Part3-Data-Modeling-and-Config.md — discovery rationale, data modeling decisions, config schema
4. Part4-Repo-Structure-and-Build-Plan.md — repo layout, build/packaging, CI/CD plan
5. Part5-OpenQuestions-References-Decisions.md — this file

## 9. Open questions / things to confirm once inside the actual build (not blocking, but flag in the build prompt)

1. Exact base classes/interfaces in `AdapterFramework.Data.Framework.AdapterCommon` for: a push/event-driven collector (vs. the sample's poll-based pattern), and whether "history recovery" has a first-class hook to implement rather than building it fully ourselves.
2. Exact `DataSelection`/`Discoveries` JSON field names for this framework version (patterns are well-established from sibling adapters but should be verified against actual framework source/NuGet package contents, not just docs prose).
3. Current framework/package version to pin (2.1.0 observed; check for newer).
4. Whether `DataSelection` items support a UOM (units of measurement) field to carry HA's `unit_of_measurement` attribute.
5. OMF stream/container ID character constraints, to finalize the naming convention from §5.1.
6. Confirm minimum HA version required (WebSocket API and `/api/history/period` are long-stable, so this should be a low bar — just needs stating in the README, e.g. "HA Core 2023.x+").

---

## 10. Key reference URLs

- AVEVA Adapter Framework docs home: https://docs.aveva.com/bundle/adapter-framework
- Develop your own adapter: https://docs.aveva.com/bundle/adapter-framework/page/1626256.html
- Implement your adapter: https://docs.aveva.com/bundle/adapter-framework/page/1627055.html
- Precompiled NuGet packages: https://docs.aveva.com/bundle/adapter-framework/page/1626205.html
- Sample adapter overview: https://docs.aveva.com/bundle/adapter-framework/page/1626497.html
- Weather.gov sample adapter detail: https://docs.aveva.com/bundle/adapter-framework/page/1627092.html
- Sample config/build/run pages: https://docs.aveva.com/bundle/adapter-framework/page/1627097.html , 1627100.html , 1627099.html , 1626275.html
- GitHub repo: https://github.com/AVEVA/adapter-framework (Sample under `Sample/WeatherGovAdapter/`)
- NuGet packages: https://www.nuget.org/packages?q=adapterframework.data
- AVEVA Adapter for MQTT discovery (pattern reference): https://docs.aveva.com/bundle/adapter-for-mqtt/page/1564131.html
- AVEVA Adapter for DNP3 discovery (pattern reference): https://docs.aveva.com/bundle/adapter-for-dnp3/page/1231161.html
- AVEVA Adapter for RDBMS discovery (pattern reference, incl. UoM in query): https://docs.aveva.com/bundle/adapter-for-rdbms/page/1231441.html
- Home Assistant REST API: https://developers.home-assistant.io/docs/api/rest
- Home Assistant WebSocket API: https://developers.home-assistant.io/docs/api/websocket

---

## 11. Decisions log (from clarifying questions)

- **Attribute granularity:** one selectable point per attribute (not bundled JSON).
- **History backfill:** yes — on startup and after reconnect gaps, via `/api/history/period`.
- **OMF destination:** kept generic — document PI Web API (on-prem), CONNECT/Data Hub (cloud), and Edge Data Store, no default destination baked in.

---

## 12. Addendum: DataSelection standard fields (correction)

An earlier pass of this repo's `HomeAssistantDataSelectionItem` model and `HomeAssistant.DataSelection.json`
example omitted four fields that are standard across AVEVA adapter DataSelection resources and are
**not actually optional** (they're always present, just carry defaults):

- `selected` (bool, default `false`) — whether the point is actively collected. Discovery now sets
  this from the discovery request's `autoSelect` flag; `HistoryBackfillService` and
  `StateChangeIngestor` both filter on it before collecting.
- `name` (string, default `null`) — display name. This adapter populates it from HA's
  `friendly_name` attribute when available.
- `streamId` (string, default `null`) — target OMF stream id; `null` means "derive from the point
  id" (see `HomeAssistantDataSelectionItem.GetEffectiveStreamId()`). Set explicitly only to
  override, e.g. to line up with a pre-existing PI tag/AF attribute name.
- `dataFilterId` (string, default `null`) — id of a configured data filter/transform; `null` means
  no filter applied.

Fixed in `HomeAssistantModels.cs`, `DiscoveryService.cs`, `HistoryBackfillService.cs`,
`StateChangeIngestor.cs`, and `ConfigurationExamples/HomeAssistant.DataSelection.json`. Still to
verify at build time: the exact JSON key casing/naming the framework itself uses for these four
fields (this repo uses PascalCase throughout for consistency with the rest of the model; sibling
adapters may use camelCase — check against the restored NuGet packages and adjust with
`[JsonPropertyName]` attributes if needed).
