using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voidstrap.Utility
{
    public sealed class TolerantEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }

        private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                try
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        string? str = reader.GetString();
                        if (!string.IsNullOrEmpty(str) && Enum.TryParse<T>(str, true, out var parsed))
                            return parsed;
                        return default;
                    }

                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        if (reader.TryGetInt64(out long num))
                        {
                            object boxed = Enum.ToObject(typeof(T), num);
                            if (Enum.IsDefined(typeof(T), boxed))
                                return (T)boxed;
                            return (T)boxed;
                        }
                    }
                }
                catch
                {
                }

                try { reader.Skip(); } catch { }
                return default;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(Convert.ToInt64(value));
            }
        }
    }
}
