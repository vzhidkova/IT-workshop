
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogHive.Models
{
    public class LogEventRequest
    {
        [Required]
        [JsonPropertyName("timestamp")]
        [JsonConverter(typeof(MattermostTimestampConverter))]
        public DateTimeOffset? Timestamp { get; set; }

        [Required]
        [JsonPropertyName("level")]
        [MaxLength(10)]
        public string Level { get; set; } = string.Empty;

        [Required]
        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("caller")]
        [MaxLength(500)]
        public string? Caller { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
    }

    public class MattermostTimestampConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Timestamp must be a string.");

            var value = reader.GetString();

            if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            {
                return timestamp;
            }

            throw new JsonException("Invalid timestamp format.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset? value,
            JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString("O"));
            else
                writer.WriteNullValue();
        }
    }
}
