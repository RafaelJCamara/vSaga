using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// A state whose serialised blob is not JSON at all — a document cut off mid-way, the shape a corrupt row
/// takes. Like <see cref="NullBlobSagaState"/> it goes in through an ordinary <c>InsertAsync</c> and the
/// default serializer options clause 12 requires, since a type's own converter is part of the type, not
/// the options; the converter writes the broken text with input validation off
/// (<see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/>), which is how a case plants clause 11's
/// corrupt blob without reaching into any provider's storage.
/// </summary>
[JsonConverter(typeof(MalformedBlobConverter))]
internal sealed class MalformedBlobSagaState : SagaState;

/// <summary>
/// Writes every <see cref="MalformedBlobSagaState"/> as a JSON object cut off after its first member's
/// name. Reading consumes the value the way any converter must, so only the text being malformed can make
/// a read throw: a store that handed this row back would hand back a state, and the case would fail.
/// </summary>
internal sealed class MalformedBlobConverter : JsonConverter<MalformedBlobSagaState>
{
    private const string CutOffDocument = "{\"CorrelationId\":";

    public override MalformedBlobSagaState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return new MalformedBlobSagaState();
    }

    public override void Write(Utf8JsonWriter writer, MalformedBlobSagaState value, JsonSerializerOptions options) =>
        writer.WriteRawValue(CutOffDocument, skipInputValidation: true);
}
