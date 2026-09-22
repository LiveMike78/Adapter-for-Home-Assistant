# AVEVA Adapter for Home Assistant

An [AVEVA Adapter Framework](https://docs.aveva.com/bundle/adapter-framework)-based adapter that
connects to a [Home Assistant](https://www.home-assistant.io/) instance, subscribes to live state
and attribute changes, and streams them into an AVEVA OMF-compatible destination (PI Server via PI
Web API, AVEVA Data Hub/CONNECT, or Edge Data Store).

## What it does

- **Live collection** via Home Assistant's WebSocket API (`subscribe_events` on `state_changed`) —
  not polling. A single subscription covers both entity state and every entity attribute, since HA
  sends both together on every change.
- **Full discovery**: one call to `GET /api/states` enumerates every entity, its state, and its
  current attributes, so data selection can be built and auto-selected immediately — no MQTT-style
  "watch for N minutes" passive discovery needed.
- **One selectable point per attribute** (plus one for entity state), e.g.
  `sensor.kitchen_temperature`, `sensor.kitchen_temperature.battery_level`.
- **History backfill** on startup and after any reconnect gap, via `GET /api/history/period`, so
  outages don't leave holes in the archive.
- Configurable include/exclude filtering by domain, entity ID (glob), and attribute name.
- Targets **Windows x64, Linux x64, Linux ARM64, and Docker (linux/amd64 + linux/arm64)**.

## ⚠️ Verify before building

This repository was scaffolded from the [AVEVA Adapter Framework documentation](https://docs.aveva.com/bundle/adapter-framework)
and the [WeatherGovAdapter sample](https://github.com/AVEVA/adapter-framework/tree/main/Sample/WeatherGovAdapter),
without the ability to restore the actual `AdapterFramework.Data.Framework.*` NuGet packages or
inspect their compiled source directly (some framework docs pages are JS-rendered and some package
internals could not be confirmed at research time). Before your first build:

1. **Restore the NuGet packages** referenced in
   `src/AdapterFramework.Data.Adapter.HomeAssistant/AdapterFramework.Data.Adapter.HomeAssistant.csproj`
   and confirm the pinned version (`2.1.0` placeholder) is still current.
2. **Reconcile `HomeAssistantAdapter.cs`** with the actual base class/interface `AdapterCommon`
   exposes for a push/event-driven collector (this adapter is event-driven, unlike the sample's
   poll-based `HttpClient` pattern) — see the class-level comment in that file.
3. **Wire `AdapterFramework.Data.Framework.Host`** into `AdapterFramework.Data.System.Host/Program.cs`
   in place of the minimal bootstrap there now, so the adapter gets the framework's real
   `/api/v1/configuration` REST surface (port 5590), component registration, health reporting,
   `PersistentQueue`-backed OMF egress, and Windows Service/systemd lifecycle — follow the sample's
   own `Program.cs` once you can see it alongside the restored packages.
4. **Confirm the `[Protected]` attribute's actual namespace** (commented out in
   `DataSourceConfiguration.cs`) from `AdapterFramework.Data.Framework.DataProtector` or
   `.Abstractions`, and re-enable it on `AccessToken`.
5. **Confirm `DataSelection`/`Discoveries` JSON field names** against the framework's actual schema
   for this version — the shapes here follow the pattern used by AVEVA's MQTT/DNP3/RDBMS adapters
   but weren't verified byte-for-byte against this framework release.
6. Check whether `AdapterCommon`'s "history recovery" feature is a hook you should implement instead
   of (or in addition to) the bespoke `HistoryBackfillService`/`FileCheckpointStore` included here.

None of the above should require rewriting the Home Assistant-specific logic (REST/WebSocket
clients, discovery, filtering, value conversion, backfill) — only the outer shell that plugs it
into the framework's hosting/ingestion model.

## Repository layout

```
src/
  AdapterFramework.Data.Adapter.HomeAssistant/   Core adapter logic (HA-specific)
  AdapterFramework.Data.System.Host/             Local/executable host
tests/
  AdapterFramework.Data.Adapter.HomeAssistant.UnitTests/
ConfigurationExamples/                            Example DataSource/DataSelection/Schedules/Discoveries JSON
Edge-Module/                                       Default container config
docker/Dockerfile                                  Multi-arch (amd64/arm64) container build
.github/workflows/                                 CI (build+test) and Release (binaries + Docker) pipelines
```

## Configuration

See `ConfigurationExamples/HomeAssistant.DataSource.json` for the full settings list. At minimum you need:

- `BaseUrl` — your Home Assistant URL, e.g. `https://homeassistant.local:8123`
- `AccessToken` — a [Long-Lived Access Token](https://www.home-assistant.io/docs/authentication/#your-account-profile)
  generated from your HA profile page

## Local development

```bash
cd src/AdapterFramework.Data.System.Host
dotnet run -- discover                 # run discovery and print found points
dotnet run                             # start live collection (once DataSelection loading is wired in)
```

## Docker

```bash
docker buildx build --platform linux/amd64,linux/arm64 -f docker/Dockerfile -t aveva-adapter-for-home-assistant .
```

## Publishing self-contained binaries

```bash
dotnet publish src/AdapterFramework.Data.System.Host -c Release -r win-x64      --self-contained true -p:PublishSingleFile=true
dotnet publish src/AdapterFramework.Data.System.Host -c Release -r linux-x64    --self-contained true -p:PublishSingleFile=true
dotnet publish src/AdapterFramework.Data.System.Host -c Release -r linux-arm64  --self-contained true -p:PublishSingleFile=true
```

## License

Apache-2.0 — see [LICENSE](LICENSE), consistent with the AVEVA Adapter Framework itself.

## Background / design notes

The full research and decision log behind this repository (AVEVA Adapter Framework package
reference, Home Assistant REST/WebSocket API notes, data-modeling rationale, and open items to
verify) is preserved in `docs/PREP-NOTES.md` for anyone picking this project up later.
