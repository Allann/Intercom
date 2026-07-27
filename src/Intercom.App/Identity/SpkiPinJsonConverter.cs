using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intercom.App.Identity;

/// <summary>
/// SpkiPin wraps a private byte[] with no public settable member, so default
/// reflection-based serialization would produce "{}". Serialize it the same
/// way a bare byte[] would be (base64), so on-disk shape doesn't change.
/// </summary>
sealed class SpkiPinJsonConverter : JsonConverter<SpkiPin>
{
    public override SpkiPin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetBytesFromBase64());

    public override void Write(Utf8JsonWriter writer, SpkiPin value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(value.Bytes);
}
