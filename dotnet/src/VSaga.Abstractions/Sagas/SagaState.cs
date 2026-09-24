namespace VSaga.Abstractions.Sagas;

/// <summary>
/// Base type for a saga instance's persisted data. Concrete sagas derive from this to add
/// their own business-specific fields (order id, amount, etc).
/// </summary>
public abstract class SagaState
{
    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public SagaKind Kind { get; set; } = SagaKind.Orchestrated;

    public string CurrentState { get; set; } = string.Empty;

    public SagaStatus Status { get; set; } = SagaStatus.Running;

    /// <summary>Optimistic concurrency token, incremented on every persisted transition.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The saga type that started this instance via <c>ISagaContext.StartChildAsync</c>, or null if
    /// nothing did. Together with <see cref="ParentCorrelationId"/> this is the whole of a child's
    /// link to its parent: both null means a root saga.
    /// <para>
    /// Set once, when the orchestrator creates the instance, from headers the parent stamped on the
    /// child's initiating message — never on a later step, since an instance's parent cannot change.
    /// A child has its own correlation id, so the pair identifies a different instance than this one.
    /// </para>
    /// </summary>
    public string? ParentSagaType { get; set; }

    /// <summary>The correlation id of the instance that started this one — see <see cref="ParentSagaType"/>.</summary>
    public Guid? ParentCorrelationId { get; set; }

    /// <summary>
    /// This saga type's declared business key (via <c>CorrelateOn</c>), or null if the saga
    /// type hasn't declared one. Engine-owned, set once at creation -- same precedent as
    /// <see cref="ParentSagaType"/> above. Promoted to a real column in every persistence provider (not
    /// left inside the serialized state blob) so it can be looked up directly; see
    /// <c>ISagaSnapshotStore{TState}.FindByBusinessKeyAsync</c>.
    /// </summary>
    public string? BusinessKey { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
