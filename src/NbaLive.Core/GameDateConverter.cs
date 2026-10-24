using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NbaLive.Core;

public sealed class GameDateConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) =>
        DateOnly.TryParseExact(reader.GetString(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw new JsonException("Invalid game date.");
    public override void Write(Utf8JsonWriter writer,DateOnly value,JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
}
