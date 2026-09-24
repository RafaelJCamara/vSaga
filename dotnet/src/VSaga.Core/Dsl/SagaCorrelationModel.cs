using System.Globalization;
using System.Reflection;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>
/// Correlation registration table shared by <see cref="SagaDefinitionModel{TState}"/> and
/// <see cref="ChoreographySagaModel{TState}"/> rather than duplicated into each — <c>EventBuilder</c>'s
/// and <c>ChoreographyEventBuilder</c>'s <c>CorrelateBy</c> were already byte-identical twins, and a
/// third copy of this logic would only repeat that drift. Holds the single <c>CorrelateOn</c>-declared
/// business-key property (if any) plus the <c>(messageType → extractor)</c> map <c>CorrelateBy</c>
/// registers into when it targets that same property.
/// </summary>
internal sealed class SagaCorrelationModel<TState> where TState : SagaState, new()
{
    private readonly Dictionary<Type, Func<object, string?>> _extractors = new();

    public PropertyInfo? BusinessKeyProperty { get; private set; }

    /// <summary>Called once by <c>CorrelateOn(...)</c>. A saga that never calls it leaves this model inert.</summary>
    public void DeclareBusinessKey(PropertyInfo property)
    {
        if (BusinessKeyProperty is not null)
        {
            throw new SagaDefinitionException(
                $"Saga '{typeof(TState).Name}' already declared CorrelateOn('{BusinessKeyProperty.Name}').");
        }

        BusinessKeyProperty = property;
    }

    /// <summary>
    /// Called by every <c>CorrelateBy(messageKey, stateKey)</c>. For a saga that never called
    /// <c>CorrelateOn</c> this is a no-op and <c>CorrelateBy</c>'s original behaviour (assign onto state,
    /// nothing else) is unchanged. Once <c>CorrelateOn</c> has been declared it becomes exclusive:
    /// <paramref name="stateProperty"/> must be that same property, and only one extractor may be
    /// registered per message type. Both violations throw <see cref="SagaDefinitionException"/> from the
    /// saga definition's constructor rather than failing later at correlation time.
    /// </summary>
    public void RegisterExtractor<TMessage, TKey>(Type messageType, PropertyInfo stateProperty, Func<TMessage, TKey> messageKey)
    {
        if (BusinessKeyProperty is null)
            return;

        if (stateProperty != BusinessKeyProperty)
        {
            throw new SagaDefinitionException(
                $"Saga '{typeof(TState).Name}' declared CorrelateOn('{BusinessKeyProperty.Name}'), so CorrelateBy for " +
                $"{messageType.Name} must target the same property, not '{stateProperty.Name}'.");
        }

        if (_extractors.ContainsKey(messageType))
        {
            throw new SagaDefinitionException(
                $"Saga '{typeof(TState).Name}' already registered a correlation extractor for {messageType.Name}.");
        }

        _extractors[messageType] = message => Convert.ToString(messageKey((TMessage)message), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Null means this message carries no business key: either the saga never called <c>CorrelateOn</c>,
    /// or no <c>CorrelateBy</c> extractor is registered for this message's CLR type — including the
    /// synthetic <see cref="TimeoutSignal"/>, which resolves to null automatically with no special-casing.
    /// </summary>
    public string? TryExtract(object message) =>
        _extractors.TryGetValue(message.GetType(), out var extractor) ? extractor(message) : null;
}
