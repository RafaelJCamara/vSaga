using System.Text.Json.Serialization;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// A state whose serialisation runs a one-shot hook. Clause 1 has every snapshot store bump the live
/// object's version before serialising it and write only afterwards, so serialisation is the one moment
/// every provider passes through inside that window — and the hook is how a case lands a rival write
/// there, deterministically, without reaching into any provider.
/// </summary>
internal sealed class RaceProbeSagaState : SagaState
{
    /// <summary>Runs, then clears itself, the next time this object is serialised.</summary>
    [JsonIgnore]
    public Action? OnNextSerialize { get; set; }

    /// <summary>Serialised like any field; reading it is what fires <see cref="OnNextSerialize"/>.</summary>
    public string Probe
    {
        get
        {
            var hook = OnNextSerialize;
            OnNextSerialize = null;
            hook?.Invoke();
            return "probe";
        }
    }
}
