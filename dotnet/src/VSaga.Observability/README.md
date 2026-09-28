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

## Docs

[docs/observability.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/observability.md) —
traces, metrics, the persisted event log, and OTLP wiring.

## License

MIT
