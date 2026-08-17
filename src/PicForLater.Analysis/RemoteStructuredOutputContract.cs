using System.Text.Json;
using System.Text.Json.Nodes;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

internal static class RemoteStructuredOutputContract
{
    public static JsonElement JsonSchema { get; } = CreateJsonSchema();

    public static string PromptInstruction(RemoteStructuredOutputMode outputMode) =>
        outputMode is RemoteStructuredOutputMode.JsonObject
            or RemoteStructuredOutputMode.PromptOnly
            ? JsonObjectPromptInstruction
            : "The request carries the exact JSON Schema through the provider's structured-output field.";

    private const string JsonObjectPromptInstruction =
        """
        This provider only guarantees a JSON object, so follow this exact contract:
        - Return exactly these eight root keys, with no additional keys:
          schemaVersion, title, summary, visualFacts, categoryIds, entities,
          detectedLanguages, warnings.
        - schemaVersion must be "picforlater.analysis.v1".
        - title is a string of at most 80 characters. summary is a string of at
          most 320 characters.
        - visualFacts is an array of at most 3 strings, each at most 120
          characters. categoryIds is an array of at most 4 strings, each at most
          64 characters. detectedLanguages is an array of at most 4 BCP-47
          strings, each at most 35 characters. warnings is an array of at most 4
          strings, each at most 120 characters.
        - entities is an array of objects. Each entity has exactly kind, rawText,
          normalizedValue, and evidence. kind is date, time, datetime, location,
          or address; normalizedValue is a string or null; the other fields are
          strings. Return at most 3 entities. kind is at most 32 characters,
          rawText at most 80, and normalizedValue and evidence at most 120 each.
        - All eight keys are required even when their value is an empty string or
          empty array, except title and summary: both must be non-empty and grounded
          in the supplied content. Never copy placeholder or skeleton values.
          Output raw JSON only, without Markdown fences or commentary.
        """;

    private static JsonElement CreateJsonSchema()
    {
        var root = JsonNode.Parse(QwenStructuredOutputParser.JsonSchema)?.AsObject()
            ?? throw new InvalidOperationException("The structured output schema is unavailable.");
        root.Remove("x-guidance");
        ReplaceConstWithEnum(root);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void ReplaceConstWithEnum(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            if (jsonObject["const"] is JsonNode constant)
            {
                jsonObject.Remove("const");
                jsonObject["enum"] = new JsonArray(constant.DeepClone());
            }

            foreach (var property in jsonObject.ToArray())
            {
                ReplaceConstWithEnum(property.Value);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                ReplaceConstWithEnum(item);
            }
        }
    }
}
