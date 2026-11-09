# VSaga.Observability

OpenTelemetry instrumentation for vSaga: traces and metrics for saga steps and message dispatch. The
persisted event log the dashboard reads is written by the engine itself and needs none of this wiring.

## Install

```bash
dotnet add package VSaga.Observability
```

## Usage

```csharp
services.AddVSagaOpenTelemetry();
```

Opt in to the store-backed `vsaga.saga.running` gauge (sagas in status `Running`, by saga type) on one process per
store, after the persistence registration. It reads the store once per `RefreshInterval` (30 seconds by default):

```csharp
services.AddVSagaRunningSagasGauge(opt => opt.RefreshInterval = TimeSpan.FromMinutes(1));
```

## Docs

[docs/observability.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/observability.md) —
traces, metrics, the persisted event log, and OTLP wiring.

## License

MIT
