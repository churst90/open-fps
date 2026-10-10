using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Common.Networking;

public class QuaternionConverter : JsonConverter<Quaternion>
{
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float x = 0, y = 0, z = 0, w = 0; // W defaults to 1 when absent (wSet)
        bool wSet = false;

        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) 
            {
                var q = new Quaternion(x, y, z, wSet ? w : 1.0f);
                // Never an all-zero quaternion.
                if (q.LengthSquared() < 0.001f) return Quaternion.Identity;
                return q;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();

            string? propertyName = reader.GetString()?.ToLower();
            reader.Read();
            switch (propertyName)
            {
                case "x": x = reader.GetSingle(); break;
                case "y": y = reader.GetSingle(); break;
                case "z": z = reader.GetSingle(); break;
                case "w": w = reader.GetSingle(); wSet = true; break;
            }
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteNumber("W", value.W);
        writer.WriteEndObject();
    }
}
