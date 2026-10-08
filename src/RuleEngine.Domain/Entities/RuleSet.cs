using RuleEngine.Domain.Common;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Domain.Entities;

/// <summary>A named group of rules sharing one fact schema and one evaluation strategy.</summary>
public sealed class RuleSet
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    private RuleSet()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public EvaluationStrategy Strategy { get; private set; }

    /// <summary>Serialized <see cref="FactSchema"/> (nvarchar(max)).</summary>
    public string FactSchemaJson { get; private set; } = "{}";

    public bool IsActive { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token, regenerated on every change.</summary>
    public Guid ConcurrencyStamp { get; private set; }

    public List<Rule> Rules { get; private set; } = new();

    public static RuleSet Create(
        string name,
        string? description,
        EvaluationStrategy strategy,
        FactSchema schema,
        string createdBy,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new DomainException($"Rule set name is required and must be at most {NameMaxLength} characters.");
        }

        EnsureValidSchema(schema);
        return new RuleSet
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = Text.Truncate(description?.Trim(), DescriptionMaxLength),
            Strategy = strategy,
            FactSchemaJson = schema.ToJson(),
            IsActive = true,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            ConcurrencyStamp = Guid.NewGuid(),
        };
    }

    public FactSchema GetFactSchema() => FactSchema.FromJson(FactSchemaJson);

    public void UpdateFactSchema(FactSchema schema, DateTime nowUtc)
    {
        EnsureValidSchema(schema);
        FactSchemaJson = schema.ToJson();
        Touch(nowUtc);
    }

    public void UpdateDetails(string? description, EvaluationStrategy strategy, DateTime nowUtc)
    {
        Description = Text.Truncate(description?.Trim(), DescriptionMaxLength);
        Strategy = strategy;
        Touch(nowUtc);
    }

    public void Activate(DateTime nowUtc)
    {
        IsActive = true;
        Touch(nowUtc);
    }

    public void Deactivate(DateTime nowUtc)
    {
        IsActive = false;
        Touch(nowUtc);
    }

    private static void EnsureValidSchema(FactSchema schema)
    {
        var errors = schema.Validate();
        if (errors.Count > 0)
        {
            throw new DomainException("Invalid fact schema: " + string.Join(" ", errors));
        }
    }

    private void Touch(DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
