using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

// Namespace unchanged (docs/SOUND_LIBRARY_BOUNDARY.md, decision 6): the network's JSON and the model
// library's both write vectors with it.
namespace OpenFPS.Common.Networking;

/// <summary>A vector as {"X":..,"Y":..,"Z":..}: its numbers are fields, which the serializer does not write.</summary>
public class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float x = 0, y = 0, z = 0;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return new Vector3(x, y, z);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();

            string? propertyName = reader.GetString()?.ToLower();
            reader.Read();
            switch (propertyName)
            {
                case "x": x = reader.GetSingle(); break;
                case "y": y = reader.GetSingle(); break;
                case "z": z = reader.GetSingle(); break;
            }
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}
