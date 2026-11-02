using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Ekko.Brain.Tools;

/// <summary>
/// A tool's args record is its contract: the JSON schema the model sees is generated from it,
/// and the model's output is validated by deserializing into it. One source of truth, no hand-written schemas.
/// </summary>
public static class ToolJson
{
    /// <summary>Strict on purpose: unknown properties, missing required ones, nulls where not nullable, and unknown enum names all fail.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static JsonNode SchemaFor<T>() => Options.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
    {
        TreatNullObliviousAsNonNullable = true,
        // The exporter doesn't map these attributes itself.
        TransformSchemaNode = (context, node) =>
        {
            if (context.PropertyInfo?.AttributeProvider is not { } attributes || node is not JsonObject property)
                return node;
            if (attributes.GetCustomAttributes(typeof(DescriptionAttribute), true).FirstOrDefault() is DescriptionAttribute d)
                property["description"] = d.Description;
            if (attributes.GetCustomAttributes(typeof(RangeAttribute), true).FirstOrDefault() is RangeAttribute r)
            {
                property["minimum"] = Convert.ToDouble(r.Minimum);
                property["maximum"] = Convert.ToDouble(r.Maximum);
            }
            return node;
        },
    });

    /// <summary>Deserialize, then check DataAnnotations ([Range] etc.). The error is phrased so it can go back to the model on a retry.</summary>
    public static bool TryParse<T>(JsonElement json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error) where T : class
    {
        value = null;
        try
        {
            value = json.Deserialize<T>(Options);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        if (value is null)
        {
            error = "arguments must be a JSON object";
            return false;
        }

        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), problems, validateAllProperties: true))
        {
            error = string.Join("; ", problems.Select(p => p.ErrorMessage));
            value = null;
            return false;
        }
        error = null;
        return true;
    }
}
