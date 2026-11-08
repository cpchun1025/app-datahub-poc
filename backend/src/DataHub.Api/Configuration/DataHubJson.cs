using System.Text.Json;
using System.Text.Json.Serialization;
using DataHub.Api.Serialization;

namespace DataHub.Api.Configuration;

/// <summary>API 同 checkpoint 檔案共用嘅 JSON 設定：camelCase、enum 用字串、時間用 UTC ISO 8601。</summary>
public static class DataHubJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };
        Apply(options);
        return options;
    }

    public static void Apply(JsonSerializerOptions options)
    {
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
