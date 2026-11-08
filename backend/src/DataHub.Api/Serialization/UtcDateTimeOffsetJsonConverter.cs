using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataHub.Api.Serialization;

/// <summary>
/// DateTimeOffset 一律以 UTC 輸出（後綴 Z），例如 2026-01-01T08:30:15.123Z；保留完整 tick 精度以便 checkpoint round trip。
/// 讀入時接受任何 ISO 8601 offset，然後轉成 UTC。
/// </summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            throw new JsonException($"'{text}' 唔係有效嘅 ISO 8601 時間。");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(FormatUtc(value));

    public static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
}
