using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// The state type every snapshot case writes: the engine-owned <see cref="SagaState"/> fields plus
/// two business fields. Deliberately a sealed leaf with a string and a decimal, so the golden-blob case
/// exercises what a mis-configured serializer would get wrong — a naming policy (every name is
/// PascalCase), a string-enum converter (<c>Kind</c>/<c>Status</c> are numbers), null handling (the
/// parent pointer and business key default to null) and character escaping.
/// </summary>
public sealed class ConformanceSagaState : SagaState
{
    public string? OrderId { get; set; }

    public decimal Amount { get; set; }
}
