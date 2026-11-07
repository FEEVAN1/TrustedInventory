using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrustedInventory.Application.Common;

/// <summary>Wire format (section 15): camelCase, string enums, unknown members rejected (additionalProperties: false).</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectNullableAnnotations = true;
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new OptionalJsonConverterFactory());
        return options;
    }

    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>Reads a merge-patch field: a present property (even null) becomes a set Optional.</summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (value.IsSet)
                JsonSerializer.Serialize(writer, value.Value, options);
            else
                writer.WriteNullValue();
        }
    }
}
