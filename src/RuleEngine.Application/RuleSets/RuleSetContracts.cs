using FluentValidation;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.RuleSets;

/// <summary>Request to create a rule set.</summary>
public sealed class CreateRuleSetRequest
{
    /// <summary>Unique rule set name.</summary>
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>FirstMatch or AllMatches.</summary>
    public EvaluationStrategy Strategy { get; init; } = EvaluationStrategy.AllMatches;

    /// <summary>Fields the rules may reference and the execution input must satisfy.</summary>
    public FactSchema? FactSchema { get; init; }
}

/// <summary>Request to replace a rule set's fact schema.</summary>
public sealed class UpdateFactSchemaRequest
{
    public FactSchema? FactSchema { get; init; }

    /// <summary>Optional optimistic-concurrency check; 409 when the rule set changed since it was read.</summary>
    public Guid? ExpectedConcurrencyStamp { get; init; }
}

/// <summary>Request to update a rule set's description and evaluation strategy.</summary>
public sealed class UpdateRuleSetRequest
{
    public string? Description { get; init; }

    public EvaluationStrategy Strategy { get; init; } = EvaluationStrategy.AllMatches;

    /// <summary>Optional optimistic-concurrency check; 409 when the rule set changed since it was read.</summary>
    public Guid? ExpectedConcurrencyStamp { get; init; }
}

public sealed class RuleSetListQuery : PageQuery
{
    public bool? IsActive { get; set; }

    /// <summary>Case-sensitive substring match on the name (provider collation applies).</summary>
    public string? Search { get; set; }
}

public sealed record RuleSetDto(
    Guid Id,
    string Name,
    string? Description,
    EvaluationStrategy Strategy,
    FactSchema FactSchema,
    bool IsActive,
    int RuleCount,
    int ActiveRuleCount,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid ConcurrencyStamp);

public sealed class CreateRuleSetRequestValidator : AbstractValidator<CreateRuleSetRequest>
{
    public CreateRuleSetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(RuleSet.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(RuleSet.DescriptionMaxLength);
        RuleFor(x => x.Strategy).IsInEnum();
        RuleFor(x => x.FactSchema).NotNull();
        RuleFor(x => x.FactSchema!.Fields).NotEmpty().When(x => x.FactSchema is not null)
            .WithMessage("The fact schema must declare at least one field.");
    }
}

public sealed class UpdateRuleSetRequestValidator : AbstractValidator<UpdateRuleSetRequest>
{
    public UpdateRuleSetRequestValidator()
    {
        RuleFor(x => x.Description).MaximumLength(RuleSet.DescriptionMaxLength);
        RuleFor(x => x.Strategy).IsInEnum();
    }
}

public sealed class UpdateFactSchemaRequestValidator : AbstractValidator<UpdateFactSchemaRequest>
{
    public UpdateFactSchemaRequestValidator()
    {
        RuleFor(x => x.FactSchema).NotNull();
        RuleFor(x => x.FactSchema!.Fields).NotEmpty().When(x => x.FactSchema is not null)
            .WithMessage("The fact schema must declare at least one field.");
    }
}
