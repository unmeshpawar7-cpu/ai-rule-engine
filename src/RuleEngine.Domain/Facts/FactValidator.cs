using System.Text.Json.Nodes;

namespace RuleEngine.Domain.Facts;

public sealed record FactValidationError(string Field, string Message);

/// <summary>Validates an input fact document against a rule set's <see cref="FactSchema"/>.</summary>
public static class FactValidator
{
    public static IReadOnlyList<FactValidationError> Validate(FactSchema schema, JsonObject facts)
    {
        var errors = new List<FactValidationError>();
        foreach (var field in schema.Fields)
        {
            var found = FactPath.TryGet(facts, field.Path, out var node);
            var element = FactValueConverter.ToElement(node);
            if (!found || FactValueConverter.IsNull(element))
            {
                if (field.Required)
                {
                    errors.Add(new FactValidationError(field.Path, "Required fact is missing or null."));
                }

                continue;
            }

            if (!FactValueConverter.IsCompatible(field.Type, element!.Value))
            {
                errors.Add(new FactValidationError(field.Path, $"Expected a value of type {field.Type}."));
            }
        }

        return errors;
    }
}
