namespace VSaga.Core.Dsl;

public enum UnhandledEventPolicy
{
    /// <summary>Record an UnexpectedEvent timeline entry and otherwise ignore the message (default) — the safe choice for out-of-order/duplicate deliveries.</summary>
    LogAndIgnore,

    /// <summary>Throw, routing the message down the same path an ordinary step failure takes: the orchestrator marks the saga Failed and <b>acks</b> the message — it is never nacked or redelivered. Use only when out-of-order delivery genuinely indicates a bug, and note the cost is a silent one-shot Failed, not a retry.</summary>
    Throw,
}
