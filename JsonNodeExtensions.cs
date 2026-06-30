using System.Text.Json.Nodes;

internal static class JsonNodeExtensions
{
    internal static JsonObject CreateMessage(int message)
    {
        return new JsonObject
        {
            ["message"] = message,
            ["args"] = new JsonObject(),
        };
    }

    internal static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Expected a JSON object.");
    }

    internal static JsonObject Args(this JsonObject json)
    {
        return json["args"]?.AsObject()
            ?? throw new InvalidOperationException("Message has no args object.");
    }

    internal static int Message(this JsonObject json)
    {
        return json["message"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Message has no message code.");
    }

    internal static int Int32(this JsonObject json, string name)
    {
        return json[name]?.GetValue<int>()
            ?? throw new InvalidOperationException($"Missing integer property '{name}'.");
    }

    internal static bool Boolean(this JsonObject json, string name)
    {
        return json[name]?.GetValue<bool>()
            ?? throw new InvalidOperationException($"Missing boolean property '{name}'.");
    }

    internal static string String(this JsonObject json, string name)
    {
        return json[name]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Missing string property '{name}'.");
    }
}
