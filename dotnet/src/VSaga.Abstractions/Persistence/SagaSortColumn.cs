using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

public enum SagaSortColumn
{
    UpdatedAt,

    /// <summary>
    /// Sorts by <see cref="SagaStatus"/>'s declared order — its underlying numeric value — not by its
    /// name. The dashboard depends on it: it repositions live-pushed rows by that same declared order, so
    /// a provider sorting by name would disagree with its own client.
    /// </summary>
    Status,
}
