# VSaga.Chaos

Fault-injection package for vSaga: chaos-engineering middleware that drops, delays, and duplicates saga
messages in test and staging environments, to exercise a saga's own timeout/compensation paths and
duplicate-delivery handling against real transport-level failures.

## Install

```bash
dotnet add package VSaga.Chaos
```

## Usage

```csharp
services.AddVSagaChaos(o =>
{
    o.Drop.Enabled = true;
    o.Drop.Probability = 0.05;
});
```

## Docs

[docs/chaos.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/chaos.md) for the full fault model
(Delay/Drop/Duplicate) and running it against the reference sample.

## License

MIT
