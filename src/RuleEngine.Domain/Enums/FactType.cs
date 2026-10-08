namespace RuleEngine.Domain.Enums;

/// <summary>Declared type of a fact field; drives typed comparison semantics.</summary>
public enum FactType
{
    String,
    Number,
    Boolean,
    Date,
    StringList,
}
