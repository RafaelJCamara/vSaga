using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// A state that serialises as JSON <c>null</c> — through an ordinary <c>InsertAsync</c> and the default
/// serializer options clause 12 requires, since a type's own converter is part of the type, not the
/// options. It is how a case plants clause 11's null blob without reaching into any provider's storage:
/// reading it back, <c>System.Text.Json</c> returns null for a JSON null without consulting the converter,
/// which is exactly the value a store must refuse to hand back as "no such saga".
/// </summary>
[JsonConverter(typeof(NullBlobConverter))]
internal sealed class NullBlobSagaState : SagaState;

/// <summary>Writes every <see cref="NullBlobSagaState"/> as JSON null.</summary>
internal sealed class NullBlobConverter : JsonConverter<NullBlobSagaState>
{
    public override NullBlobSagaState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A NullBlobSagaState is only ever stored as JSON null, which never reaches a converter.");

    public override void Write(Utf8JsonWriter writer, NullBlobSagaState value, JsonSerializerOptions options) =>
        writer.WriteNullValue();
}
