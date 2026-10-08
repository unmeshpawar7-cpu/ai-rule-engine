using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Generation;

/// <summary>
/// Deterministic, offline rule generator: a small hand-written grammar (not an LLM) that understands
/// sentences such as
/// <list type="bullet">
/// <item>"Documents with quantity greater than 10 should require manager approval."</item>
/// <item>"If amount is at least 50000 and department is IT, require director approval and tag as high-value."</item>
/// <item>"Reject purchase orders where supplier risk rating is one of High, Critical because supplier risk is too high."</item>
/// </list>
/// Field phrases are mapped to the rule set's fact schema through paths, aliases and field names
/// (<see cref="FieldResolver"/>); anything it cannot map is reported as an error rather than guessed.
/// Every interpretation that is not literal is listed in <see cref="GeneratedRuleProposal.Assumptions"/>.
/// </summary>
public sealed class PatternRuleGenerator : IRuleGenerator
{
    public const string GeneratorName = "pattern";
    public const string GrammarVersion = "pattern-grammar/v1";
    public const int MaxTextLength = 1000;

    private const int MaxSeparators = 8;
    private const double BaseConfidence = 0.95;
    private const double PenaltyPerAssumption = 0.1;
    private const double MinConfidence = 0.4;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Comparator[] Comparators = BuildComparators();

    private static readonly Regex Whitespace = R(@"\s+");
    private static readonly Regex Symbols = R(@"\s*(>=|<=|!=|<>|==|=|>|<)\s*");
    private static readonly Regex IfThenStart = R(@"^(?:if|when|whenever)\s+(?<body>.+)$");
    private static readonly Regex ThenSeparator = R(@"\s*,?\s+then\s+|\s*,\s*");
    private static readonly Regex SubjectForm = R(@"^(?<subject>.*?)\s*\b(?:with|where|whose|having|that\s+have|that\s+has|which\s+have|which\s+has|if|when)\s+(?<rest>.+)$");
    private static readonly Regex ModalSeparator = R(@"\s+(?:should|must|shall|will|need\s+to|needs\s+to|have\s+to|has\s+to|are\s+to|is\s+to)\s+");
    private static readonly Regex ConditionKeyword = R(@"\s+(?:where|when|whenever|if|with|whose|having)\s+");
    private static readonly Regex Connector = R(@"\s*,?\s+(and|or)\s+");
    private static readonly Regex ActionSeparator = R(@"\s*,\s*(?:and\s+|then\s+)?|\s+and\s+(?:then\s+)?|\s+then\s+|\s*;\s*");
    private static readonly Regex LeadingModal = R(@"^(?:(?:then|and|also|it|they|we|you|automatically|always|please)\s+)*(?:(?:should|must|shall|will|can|needs?\s+to|has\s+to|have\s+to)\s+)?(?:(?:automatically|always)\s+)?(?:be\s+)?");
    private static readonly Regex TrailingSubject = R(@"\s+(?:for|on|to)\s+(?:all\s+|any\s+|every\s+|each\s+)?[a-z][a-z \-]{0,40}$");
    private static readonly Regex OrMore = R(@"\s+or\s+(?:more|above|higher|greater|later|after)$");
    private static readonly Regex OrLess = R(@"\s+or\s+(?:less|below|lower|fewer|earlier|before)$");
    private static readonly Regex NumberValue = R(@"^(?:[$€£₹]|usd|eur|gbp|inr|rs\.?)?\s*(?<num>-?\d{1,3}(?:,\d{3})+(?:\.\d+)?|-?\d+(?:\.\d+)?)\s*(?<suffix>k|m|thousand|million)?(?<rest>.*)$");
    private static readonly Regex UnitWords = R(@"^\s*(?:[a-z]+(?:\s+[a-z]+)?)?\s*$");
    private static readonly Regex ListSeparator = R(@"\s*,\s*(?:or\s+|and\s+)?|\s+or\s+|\s+and\s+|\s*/\s*");
    private static readonly Regex BecauseTail = R(@"\s+(?:because|since|due\s+to)\s+.*$");
    private static readonly Regex Quoted = R("^[\"'“‘](?<inner>.*)[\"'”’]$");

    private const string ReasonPart = @"(?:\s+(?:because|since|due\s+to|with\s+(?:the\s+)?reason)\s*:?\s+(?<reason>.+))?";
    private const string RolePart = @"(?<role>[a-z][a-z \-]*?)";
    private const string NamePart = @"['""]?(?<name>[a-z0-9][a-z0-9 _.:\-]*?)['""]?";

    private static readonly Regex[] ApprovalPatterns =
    {
        R(@"^(?:sent|routed|escalated|forwarded)\s+(?:to\s+)?(?:the\s+|a\s+|an\s+)?" + RolePart + @"\s+for\s+(?:approval|sign[- ]?off)" + ReasonPart + "$"),
        R(@"^(?:requires?|needs?|gets?)\s+(?:approval|sign[- ]?off)\s+(?:from|by)\s+(?:the\s+|a\s+|an\s+)?" + RolePart + ReasonPart + "$"),
        R(@"^(?:requires?|needs?|gets?)\s+(?:an?\s+|the\s+)?" + RolePart + @"(?:'s)?\s+(?:approval|sign[- ]?off)" + ReasonPart + "$"),
        R(@"^approved\s+by\s+(?:the\s+|a\s+|an\s+)?" + RolePart + ReasonPart + "$"),
        R(@"^(?:requires?|needs?)\s+(?:an?\s+)?(?:approval|sign[- ]?off)" + ReasonPart + "$"),
    };

    private static readonly Regex RejectPattern = R(@"^(?:rejected|reject(?:\s+(?:it|them))?|declined|decline(?:\s+(?:it|them))?|blocked|block(?:\s+(?:it|them))?|denied|deny(?:\s+(?:it|them))?)" + ReasonPart + "$");

    private static readonly Regex[] TagPatterns =
    {
        R(@"^(?:tagged|tag(?:\s+\w+)?|flagged|flag(?:\s+\w+)?|marked|mark(?:\s+\w+)?|labell?ed|label(?:\s+\w+)?)\s+(?:as|with)\s+(?:the\s+tag\s+|a\s+tag\s+|tag\s+)?" + NamePart + "$"),
        R(@"^(?:tagged|flagged|labell?ed)\s+" + NamePart + "$"),
        R(@"^add\s+(?:the\s+|a\s+)?tag\s+" + NamePart + "$"),
    };

    private static readonly Regex NotifyPattern = R(@"^(?:notify|alert|inform|send\s+(?:a\s+)?notification\s+to)\s+(?:the\s+)?(?<channel>[a-z0-9][a-z0-9 _\-]*?)(?:\s+(?:team|channel))?(?:\s+(?:using|with|via)\s+(?:the\s+)?(?:template\s+)?['""]?(?<template>[a-z0-9][a-z0-9_.:\-]*)['""]?(?:\s+template)?)?$");

    private static readonly Regex[] WorkflowPatterns =
    {
        R(@"^(?:trigger|start|launch|kick\s+off|run|initiate)\s+(?:the\s+|a\s+)?workflow\s+" + NamePart + "$"),
        R(@"^(?:trigger|start|launch|kick\s+off|run|initiate)\s+(?:the\s+|a\s+)?" + NamePart + @"\s+workflow$"),
    };

    private static readonly Regex SetValuePattern = R(@"^set\s+(?:the\s+)?(?<field>.+?)\s+to\s+(?<value>.+)$");

    public Task<RuleGenerationResult> GenerateAsync(RuleGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Generate(request));

    public RuleGenerationResult Generate(RuleGenerationRequest request)
    {
        try
        {
            return GenerateCore(request);
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("The rule text is too complex for the deterministic grammar; simplify it or split it into several rules.");
        }
    }

    private RuleGenerationResult GenerateCore(RuleGenerationRequest request)
    {
        var text = NormalizeText(request.Text ?? string.Empty);
        if (text.Length == 0)
        {
            return Fail("Rule text is empty.");
        }

        if (text.Length > MaxTextLength)
        {
            return Fail($"Rule text must be at most {MaxTextLength} characters.");
        }

        var frames = Frames(text).ToList();
        foreach (var frame in frames)
        {
            var condition = ParseCondition(frame.ConditionText, request.Schema);
            if (condition is null)
            {
                continue;
            }

            var actions = ParseActions(frame.ActionText, condition.Node, request.Schema, frame.StripSubject);
            if (actions is null)
            {
                continue;
            }

            var notes = new List<string>();
            notes.AddRange(condition.Notes);
            notes.AddRange(actions.Notes);
            if (!string.IsNullOrWhiteSpace(frame.Subject))
            {
                notes.Add($"'{frame.Subject}' was read as the scope of the rule and is not turned into a condition.");
            }

            var penalty = condition.Penalty + actions.Penalty;
            var proposal = new GeneratedRuleProposal
            {
                Name = Text.Truncate(BuildName(condition.Node, actions.Actions), 200)!,
                Condition = condition.Node,
                Actions = actions.Actions,
                Explanation = Explain(condition.Node, actions.Actions),
                Confidence = Math.Round(Math.Max(MinConfidence, BaseConfidence - (penalty * PenaltyPerAssumption)), 2),
                Assumptions = notes.Distinct(StringComparer.Ordinal).ToList(),
            };

            return new RuleGenerationResult
            {
                Proposal = proposal,
                Generator = GeneratorName,
                PromptVersion = GrammarVersion,
                Model = null,
                Attempts = 1,
                LatencyMs = 0,
            };
        }

        return Fail(Diagnose(text, frames, request.Schema).ToArray());
    }

    // ---------------------------------------------------------------- sentence frames

    private sealed record Frame(string ConditionText, string ActionText, string? Subject, bool StripSubject);

    private static IEnumerable<Frame> Frames(string text)
    {
        var ifThen = IfThenStart.Match(text);
        if (ifThen.Success)
        {
            var body = ifThen.Groups["body"].Value;
            var separators = ThenSeparator.Matches(body)
                .OrderBy(m => m.Value.Contains("then", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Index)
                .Take(MaxSeparators * 2);
            foreach (var separator in separators)
            {
                yield return new Frame(body[..separator.Index], body[(separator.Index + separator.Length)..], null, false);
            }

            yield break;
        }

        var subjectForm = SubjectForm.Match(text);
        if (subjectForm.Success)
        {
            var rest = subjectForm.Groups["rest"].Value;
            foreach (Match modal in ModalSeparator.Matches(rest))
            {
                yield return new Frame(
                    rest[..modal.Index],
                    rest[(modal.Index + modal.Length)..],
                    subjectForm.Groups["subject"].Value.Trim(),
                    false);
            }
        }

        foreach (Match keyword in ConditionKeyword.Matches(text))
        {
            // "<action> where <condition> because <reason>": the reason belongs to the action.
            var conditionText = text[(keyword.Index + keyword.Length)..];
            var actionText = text[..keyword.Index];
            var because = BecauseTail.Match(conditionText);
            if (because.Success)
            {
                yield return new Frame(conditionText[..because.Index], actionText + because.Value, null, true);
            }

            yield return new Frame(conditionText, actionText, null, true);
        }
    }

    private static IEnumerable<string> Diagnose(string text, IReadOnlyList<Frame> frames, FactSchema schema)
    {
        var errors = new List<string>
        {
            "The deterministic grammar could not interpret this text. Supported shapes: " +
            "'If <condition> then <action>', '<subject> with <condition> should <action>', '<action> when <condition>'.",
        };

        if (frames.Count == 0)
        {
            errors.Add("No condition was found; introduce it with 'if', 'when', 'where' or 'with'.");
            return errors;
        }

        foreach (var frame in frames.Take(3))
        {
            var condition = ParseCondition(frame.ConditionText, schema);
            if (condition is null)
            {
                foreach (var part in Connector.Split(frame.ConditionText).Where(p => !IsConnectorWord(p)))
                {
                    var clauseErrors = new List<string>();
                    if (ParseClause(part, null, schema, clauseErrors) is null)
                    {
                        errors.AddRange(clauseErrors);
                    }
                }
            }
            else if (ParseActions(frame.ActionText, condition.Node, schema, frame.StripSubject) is null)
            {
                errors.Add(
                    $"Could not understand the action '{frame.ActionText.Trim()}'. Supported actions: require <role> approval, " +
                    "reject [because <reason>], tag as <tag>, notify <channel> [using <template>], trigger workflow <name>, set <field> to <value>.");
            }
        }

        return errors.Distinct(StringComparer.Ordinal).Take(6).ToList();
    }

    private static bool IsConnectorWord(string part) =>
        string.Equals(part, "and", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "or", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- conditions

    private sealed record Comparator(string Phrase, ConditionOperator Operator, bool Negate, Regex Pattern);

    private sealed record ClauseResult(ConditionNode Node, string FieldPhrase, string ComparatorPhrase, IReadOnlyList<string> Notes, int Penalty);

    private sealed record ConditionResult(ConditionNode Node, IReadOnlyList<string> Notes, int Penalty);

    private static ConditionResult? ParseCondition(string text, FactSchema schema)
    {
        var cleaned = text.Trim().Trim(',', ';').Trim();
        if (cleaned.Length == 0 || Connector.Matches(cleaned).Count > MaxSeparators)
        {
            return null;
        }

        var chain = ParseClauseChain(cleaned, null, schema);
        if (chain is null)
        {
            return null;
        }

        var notes = chain.SelectMany(c => c.Clause.Notes).ToList();
        var penalty = chain.Sum(c => c.Clause.Penalty);

        // Group with the usual precedence: 'and' binds tighter than 'or'.
        var groups = new List<List<ConditionNode>> { new() { chain[0].Clause.Node } };
        var hasAnd = false;
        var hasOr = false;
        for (var i = 1; i < chain.Count; i++)
        {
            if (string.Equals(chain[i].Connector, "or", StringComparison.OrdinalIgnoreCase))
            {
                hasOr = true;
                groups.Add(new List<ConditionNode> { chain[i].Clause.Node });
            }
            else
            {
                hasAnd = true;
                groups[^1].Add(chain[i].Clause.Node);
            }
        }

        if (hasAnd && hasOr)
        {
            notes.Add("Mixed 'and'/'or' were grouped with 'and' binding tighter than 'or'.");
            penalty++;
        }

        var terms = groups.Select(g => g.Count == 1 ? g[0] : ConditionNode.AllOf(g.ToArray())).ToList();
        var node = terms.Count == 1 ? terms[0] : ConditionNode.AnyOf(terms.ToArray());
        return new ConditionResult(node, notes, penalty);
    }

    /// <summary>
    /// Splits "a and b or c" into clauses. A split is accepted only if both sides parse, so that
    /// "between 10 and 20" or "one of IT, HR or Finance" are not torn apart.
    /// </summary>
    private static List<(ClauseResult Clause, string? Connector)>? ParseClauseChain(string text, ClauseResult? previous, FactSchema schema)
    {
        foreach (Match connector in Connector.Matches(text))
        {
            var left = ParseClause(text[..connector.Index], previous, schema, null);
            if (left is null)
            {
                continue;
            }

            var rest = ParseClauseChain(text[(connector.Index + connector.Length)..], left, schema);
            if (rest is null)
            {
                continue;
            }

            rest[0] = (rest[0].Clause, connector.Groups[1].Value.ToLowerInvariant());
            rest.Insert(0, (left, null));
            return rest;
        }

        var single = ParseClause(text, previous, schema, null);
        return single is null ? null : new List<(ClauseResult Clause, string? Connector)> { (single, null) };
    }

    private static ClauseResult? ParseClause(string rawText, ClauseResult? previous, FactSchema schema, List<string>? errors)
    {
        var text = rawText.Trim().Trim(',', ';').Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var result = TryComparators(text, previous?.FieldPhrase, schema, errors);
        if (result is null && previous is not null)
        {
            // "department is HR or IT": a bare value continues the previous comparison.
            result = TryComparators($"{previous.FieldPhrase} {previous.ComparatorPhrase} {text}", null, schema, null);
        }

        return result;
    }

    private static ClauseResult? TryComparators(string text, string? inheritedFieldPhrase, FactSchema schema, List<string>? errors)
    {
        string? firstError = null;
        foreach (var comparator in Comparators)
        {
            foreach (Match match in comparator.Pattern.Matches(text))
            {
                var fieldPhrase = text[..match.Index].Trim();
                var valueText = text[(match.Index + match.Length)..].Trim();
                if (fieldPhrase.Length == 0)
                {
                    if (inheritedFieldPhrase is null)
                    {
                        continue;
                    }

                    fieldPhrase = inheritedFieldPhrase;
                }

                var resolution = FieldResolver.Resolve(fieldPhrase, schema);
                if (resolution.Field is null)
                {
                    firstError ??= resolution.Error;
                    continue;
                }

                var notes = new List<string>();
                var node = BuildLeaf(resolution.Field, comparator, valueText, notes, out var error);
                if (node is null)
                {
                    firstError ??= error;
                    continue;
                }

                if (resolution.MatchKind != "path")
                {
                    notes.Add($"'{fieldPhrase}' was mapped to field '{resolution.Field.Path}' (by {resolution.MatchKind}).");
                }

                var penalty = notes.Count(n => !n.StartsWith('\'') && !n.StartsWith("Text comparisons", StringComparison.Ordinal));
                return new ClauseResult(node, fieldPhrase, comparator.Phrase, notes, penalty);
            }
        }

        errors?.Add(firstError ?? $"Could not find a comparison (such as 'is', 'greater than', 'one of') in '{text}'.");
        return null;
    }

    private static ConditionNode? BuildLeaf(FactField field, Comparator comparator, string valueText, List<string> notes, out string? error)
    {
        error = null;
        var op = comparator.Operator;
        var negate = comparator.Negate;
        var value = valueText;

        if (op is ConditionOperator.Equals or ConditionOperator.NotEquals && field.Type is FactType.Number or FactType.Date)
        {
            if (OrMore.IsMatch(value) && op == ConditionOperator.Equals)
            {
                op = ConditionOperator.GreaterThanOrEqual;
                value = OrMore.Replace(value, string.Empty);
            }
            else if (OrLess.IsMatch(value) && op == ConditionOperator.Equals)
            {
                op = ConditionOperator.LessThanOrEqual;
                value = OrLess.Replace(value, string.Empty);
            }
        }

        if (field.Type == FactType.StringList && op is ConditionOperator.Equals or ConditionOperator.NotEquals)
        {
            negate = negate ^ (op == ConditionOperator.NotEquals);
            op = ConditionOperator.Contains;
            notes.Add($"'{comparator.Phrase}' on list field '{field.Path}' was read as 'contains'.");
        }

        if (!ConditionOperators.Supports(op, field.Type))
        {
            error = $"'{comparator.Phrase}' cannot be applied to {field.Type} field '{field.Path}'.";
            return null;
        }

        ConditionNode leaf;
        if (!ConditionOperators.RequiresValue(op))
        {
            if (value.Length > 0)
            {
                error = $"'{comparator.Phrase}' does not take a value, but '{value}' followed it.";
                return null;
            }

            leaf = ConditionNode.Leaf(field.Path, op);
        }
        else
        {
            if (value.Length == 0)
            {
                error = $"A value is missing after '{comparator.Phrase}' for field '{field.Path}'.";
                return null;
            }

            var parsed = ParseOperand(op, field, value, notes, out error);
            if (parsed is null)
            {
                return null;
            }

            var textual = field.Type is FactType.String or FactType.StringList;
            if (textual && op != ConditionOperator.Matches)
            {
                notes.Add("Text comparisons are case-insensitive.");
            }

            leaf = ConditionNode.Leaf(field.Path, op, parsed, textual && op != ConditionOperator.Matches ? (bool?)true : null);
        }

        return negate ? ConditionNode.Negate(leaf) : leaf;
    }

    private static JsonNode? ParseOperand(ConditionOperator op, FactField field, string text, List<string> notes, out string? error)
    {
        error = null;
        switch (op)
        {
            case ConditionOperator.In:
            case ConditionOperator.NotIn:
            {
                var inner = text.Trim().TrimStart('(', '[').TrimEnd(')', ']');
                var items = ListSeparator.Split(inner).Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
                if (items.Count == 0)
                {
                    error = $"An empty list was given for field '{field.Path}'.";
                    return null;
                }

                var array = new JsonArray();
                foreach (var item in items)
                {
                    var scalar = ParseScalar(field, item, notes, out error);
                    if (scalar is null)
                    {
                        return null;
                    }

                    array.Add(scalar.Node);
                }

                return array;
            }

            case ConditionOperator.Between:
            {
                foreach (var separator in new[] { @"\s+and\s+", @"\s+to\s+", @"\s*[-–]\s*" })
                {
                    var parts = Regex.Split(text, separator, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var lowNotes = new List<string>();
                    var low = ParseScalar(field, parts[0], lowNotes, out _);
                    var high = ParseScalar(field, parts[1], lowNotes, out _);
                    if (low is null || high is null)
                    {
                        continue;
                    }

                    notes.AddRange(lowNotes);
                    if (low.CompareTo(high) > 0)
                    {
                        (low, high) = (high, low);
                        notes.Add("The 'between' bounds were given high-to-low and were swapped.");
                    }

                    return new JsonArray(low.Node, high.Node);
                }

                error = $"'between' needs two {field.Type} values such as '10 and 20' for field '{field.Path}'.";
                return null;
            }

            case ConditionOperator.Contains:
            case ConditionOperator.StartsWith:
            case ConditionOperator.EndsWith:
            case ConditionOperator.Matches:
                return JsonValue.Create(Unquote(text));

            default:
                return ParseScalar(field, text, notes, out error)?.Node;
        }
    }

    private sealed class Scalar
    {
        public required JsonNode Node { get; init; }

        public decimal? Number { get; init; }

        public DateTime? Date { get; init; }

        public int CompareTo(Scalar other)
        {
            if (Number is not null && other.Number is not null)
            {
                return Number.Value.CompareTo(other.Number.Value);
            }

            if (Date is not null && other.Date is not null)
            {
                return Date.Value.CompareTo(other.Date.Value);
            }

            return 0;
        }
    }

    private static Scalar? ParseScalar(FactField field, string rawText, List<string> notes, out string? error)
    {
        error = null;
        var text = Unquote(rawText.Trim());
        switch (field.Type)
        {
            case FactType.Number:
            {
                var match = NumberValue.Match(text);
                if (!match.Success || !decimal.TryParse(match.Groups["num"].Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"'{text}' is not a number (field '{field.Path}').";
                    return null;
                }

                var suffix = match.Groups["suffix"].Value.ToLowerInvariant();
                if (suffix is "k" or "thousand")
                {
                    number *= 1_000m;
                }
                else if (suffix is "m" or "million")
                {
                    number *= 1_000_000m;
                }

                var rest = match.Groups["rest"].Value.Trim();
                if (rest.Length > 0)
                {
                    if (!UnitWords.IsMatch(rest))
                    {
                        error = $"'{text}' is not a number (field '{field.Path}').";
                        return null;
                    }

                    notes.Add($"The unit '{rest}' after {match.Groups["num"].Value} was ignored.");
                }

                number = decimal.Parse(number.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
                return new Scalar { Node = JsonValue.Create(number), Number = number };
            }

            case FactType.Date:
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    error = $"'{text}' is not a date; use ISO format such as 2026-01-31 (field '{field.Path}').";
                    return null;
                }

                var utc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                var formatted = utc.TimeOfDay == TimeSpan.Zero
                    ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                return new Scalar { Node = JsonValue.Create(formatted)!, Date = utc };
            }

            case FactType.Boolean:
            {
                var lowered = text.ToLowerInvariant();
                bool? flag = lowered switch
                {
                    "true" or "yes" or "y" or "set" or "on" => true,
                    "false" or "no" or "n" or "off" => false,
                    _ => null,
                };
                if (flag is null)
                {
                    error = $"'{text}' is not a boolean (use true/false or yes/no) for field '{field.Path}'.";
                    return null;
                }

                return new Scalar { Node = JsonValue.Create(flag.Value) };
            }

            default:
            {
                if (text.Length == 0 || text.Length > 500)
                {
                    error = $"A text value for field '{field.Path}' must be 1-500 characters.";
                    return null;
                }

                return new Scalar { Node = JsonValue.Create(text)! };
            }
        }
    }

    // ---------------------------------------------------------------- actions

    private sealed record ActionResult(RuleActionDefinition Action, IReadOnlyList<string> Notes, int Penalty);

    private sealed record ActionsResult(List<RuleActionDefinition> Actions, IReadOnlyList<string> Notes, int Penalty);

    private static ActionsResult? ParseActions(string text, ConditionNode condition, FactSchema schema, bool stripSubject)
    {
        var defaultReason = Text.Truncate("Rule condition met: " + Describe(condition), 500)!;
        var cleaned = text.Trim().TrimEnd('.', '!', ';', ',').Trim();
        var chain = ParseActionChain(cleaned, defaultReason, schema);
        if (chain is not null)
        {
            return Combine(chain, Array.Empty<string>());
        }

        if (!stripSubject)
        {
            return null;
        }

        // "<action> <subject> where ...": drop the object noun phrase ("documents", "for purchase orders"),
        // keeping a trailing "because <reason>" attached to the action.
        var reasonTail = BecauseTail.Match(cleaned);
        var head = reasonTail.Success ? cleaned[..reasonTail.Index] : cleaned;
        var tail = reasonTail.Success ? reasonTail.Value : string.Empty;
        var trailing = TrailingSubject.Match(head);
        if (trailing.Success)
        {
            chain = ParseActionChain(head[..trailing.Index] + tail, defaultReason, schema);
            if (chain is not null)
            {
                return Combine(chain, new[] { $"'{trailing.Value.Trim()}' was read as the scope of the rule and is not turned into a condition." });
            }
        }

        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var drop = 1; drop <= Math.Min(3, words.Length - 1); drop++)
        {
            var candidate = string.Join(' ', words.Take(words.Length - drop)) + tail;
            chain = ParseActionChain(candidate, defaultReason, schema);
            if (chain is not null)
            {
                var removed = string.Join(' ', words.Skip(words.Length - drop));
                return Combine(chain, new[] { $"'{removed}' was read as the scope of the rule and is not turned into a condition." });
            }
        }

        return null;
    }

    private static ActionsResult Combine(List<ActionResult> chain, IEnumerable<string> extraNotes) =>
        new(
            chain.Select(c => c.Action).ToList(),
            chain.SelectMany(c => c.Notes).Concat(extraNotes).ToList(),
            chain.Sum(c => c.Penalty));

    private static List<ActionResult>? ParseActionChain(string text, string defaultReason, FactSchema schema)
    {
        var separators = ActionSeparator.Matches(text);
        if (separators.Count <= MaxSeparators)
        {
            foreach (Match separator in separators)
            {
                var first = ParseAction(text[..separator.Index], defaultReason, schema);
                if (first is null)
                {
                    continue;
                }

                var rest = ParseActionChain(text[(separator.Index + separator.Length)..], defaultReason, schema);
                if (rest is null)
                {
                    continue;
                }

                rest.Insert(0, first);
                return rest;
            }
        }

        var single = ParseAction(text, defaultReason, schema);
        return single is null ? null : new List<ActionResult> { single };
    }

    private static ActionResult? ParseAction(string rawText, string defaultReason, FactSchema schema)
    {
        var text = LeadingModal.Replace(rawText.Trim().TrimEnd('.', '!', ';', ','), string.Empty, 1).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var notes = new List<string>();
        var penalty = 0;

        foreach (var pattern in ApprovalPatterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            string role;
            if (match.Groups["role"].Success && match.Groups["role"].Value.Trim().Length > 0)
            {
                role = ToPascal(match.Groups["role"].Value);
            }
            else
            {
                role = "Approver";
                notes.Add("No approver role was named; the generic role 'Approver' was used.");
                penalty++;
            }

            var reason = match.Groups["reason"].Success ? CleanFreeText(match.Groups["reason"].Value) : null;
            return new ActionResult(RuleActionDefinition.RequireApproval(role, reason), notes, penalty);
        }

        var reject = RejectPattern.Match(text);
        if (reject.Success)
        {
            var reason = reject.Groups["reason"].Success ? CleanFreeText(reject.Groups["reason"].Value) : null;
            if (string.IsNullOrEmpty(reason))
            {
                reason = defaultReason;
                notes.Add("No rejection reason was given; a reason was derived from the condition.");
                penalty++;
            }

            return new ActionResult(RuleActionDefinition.Reject(reason), notes, penalty);
        }

        foreach (var pattern in TagPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                var tag = Slug(match.Groups["name"].Value);
                return tag.Length == 0 ? null : new ActionResult(RuleActionDefinition.AddTag(tag), notes, penalty);
            }
        }

        var notify = NotifyPattern.Match(text);
        if (notify.Success)
        {
            var channel = Slug(notify.Groups["channel"].Value);
            if (channel.Length == 0)
            {
                return null;
            }

            var template = notify.Groups["template"].Success ? notify.Groups["template"].Value : string.Empty;
            if (template.Length == 0)
            {
                template = "rule-matched";
                notes.Add("No notification template was named; the default template 'rule-matched' was used.");
                penalty++;
            }

            return new ActionResult(RuleActionDefinition.Notify(channel, template), notes, penalty);
        }

        foreach (var pattern in WorkflowPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                var workflow = Slug(match.Groups["name"].Value);
                return workflow.Length == 0 ? null : new ActionResult(RuleActionDefinition.TriggerWorkflow(workflow), notes, penalty);
            }
        }

        var set = SetValuePattern.Match(text);
        if (set.Success)
        {
            var resolution = FieldResolver.Resolve(set.Groups["field"].Value, schema);
            if (resolution.Field is null)
            {
                return null;
            }

            var scalar = ParseScalar(resolution.Field, set.Groups["value"].Value, notes, out _);
            if (scalar is null)
            {
                return null;
            }

            return new ActionResult(RuleActionDefinition.SetValue(resolution.Field.Path, scalar.Node), notes, penalty + notes.Count);
        }

        return null;
    }

    // ---------------------------------------------------------------- descriptions

    public static string Describe(ConditionNode node) => node.Kind switch
    {
        ConditionNodeKind.All => "(" + string.Join(" AND ", node.GetChildren().Select(Describe)) + ")",
        ConditionNodeKind.Any => "(" + string.Join(" OR ", node.GetChildren().Select(Describe)) + ")",
        ConditionNodeKind.Not => "NOT " + Describe(node.Not!),
        ConditionNodeKind.Leaf => DescribeLeaf(node),
        _ => "?",
    };

    private static string DescribeLeaf(ConditionNode leaf)
    {
        var symbol = leaf.Operator switch
        {
            nameof(ConditionOperator.Equals) => "=",
            nameof(ConditionOperator.NotEquals) => "!=",
            nameof(ConditionOperator.GreaterThan) => ">",
            nameof(ConditionOperator.GreaterThanOrEqual) => ">=",
            nameof(ConditionOperator.LessThan) => "<",
            nameof(ConditionOperator.LessThanOrEqual) => "<=",
            _ => leaf.Operator ?? "?",
        };
        return leaf.Value is null ? $"{leaf.Field} {symbol}" : $"{leaf.Field} {symbol} {leaf.Value.ToJsonString()}";
    }

    private static string DescribeAction(RuleActionDefinition action) => action.ActionType switch
    {
        RuleActionType.RequireApproval => $"require approval from role '{action.GetString(ActionParameters.Role)}'",
        RuleActionType.Reject => $"reject ({action.GetString(ActionParameters.Reason)})",
        RuleActionType.AddTag => $"add tag '{action.GetString(ActionParameters.Tag)}'",
        RuleActionType.Notify => $"notify channel '{action.GetString(ActionParameters.Channel)}' with template '{action.GetString(ActionParameters.Template)}'",
        RuleActionType.TriggerWorkflow => $"trigger workflow '{action.GetString(ActionParameters.Workflow)}'",
        RuleActionType.SetValue => $"set {action.GetString(ActionParameters.Field)} to {(action.TryGetParameter(ActionParameters.Value, out var v) ? v.GetRawText() : "null")}",
        _ => action.Type,
    };

    private static string Explain(ConditionNode condition, IReadOnlyList<RuleActionDefinition> actions) =>
        $"When {Describe(condition)}, {string.Join(", then ", actions.Select(DescribeAction))}.";

    private static string BuildName(ConditionNode condition, IReadOnlyList<RuleActionDefinition> actions) =>
        $"{Describe(condition)} -> {string.Join(" + ", actions.Select(a => a.Type))}";

    // ---------------------------------------------------------------- helpers

    private static RuleGenerationResult Fail(params string[] errors) =>
        RuleGenerationResult.Failure(GeneratorName, GrammarVersion, errors);

    private static string NormalizeText(string text)
    {
        var collapsed = Whitespace.Replace(text.Trim(), " ");
        collapsed = Symbols.Replace(collapsed, " $1 ").Trim();
        return collapsed.TrimEnd('.', '!', ';').Trim();
    }

    private static string Unquote(string text)
    {
        var match = Quoted.Match(text.Trim());
        return match.Success ? match.Groups["inner"].Value.Trim() : text.Trim();
    }

    private static string? CleanFreeText(string text)
    {
        var cleaned = Unquote(text.Trim().TrimEnd('.', '!', ';', ','));
        return cleaned.Length == 0 ? null : Text.Truncate(cleaned, 500);
    }

    private static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in Unquote(text).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or ':')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string ToPascal(string text)
    {
        var words = text.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (word.Length <= 4 && word.All(char.IsUpper))
            {
                builder.Append(word);
            }
            else
            {
                builder.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static Comparator[] BuildComparators()
    {
        var definitions = new (string Phrase, ConditionOperator Operator, bool Negate)[]
        {
            ("is greater than or equal to", ConditionOperator.GreaterThanOrEqual, false),
            ("greater than or equal to", ConditionOperator.GreaterThanOrEqual, false),
            ("is at least", ConditionOperator.GreaterThanOrEqual, false),
            ("at least", ConditionOperator.GreaterThanOrEqual, false),
            ("is no less than", ConditionOperator.GreaterThanOrEqual, false),
            ("is not less than", ConditionOperator.GreaterThanOrEqual, false),
            ("no less than", ConditionOperator.GreaterThanOrEqual, false),
            ("is on or after", ConditionOperator.GreaterThanOrEqual, false),
            (">=", ConditionOperator.GreaterThanOrEqual, false),
            ("is less than or equal to", ConditionOperator.LessThanOrEqual, false),
            ("less than or equal to", ConditionOperator.LessThanOrEqual, false),
            ("is at most", ConditionOperator.LessThanOrEqual, false),
            ("at most", ConditionOperator.LessThanOrEqual, false),
            ("is no more than", ConditionOperator.LessThanOrEqual, false),
            ("is not more than", ConditionOperator.LessThanOrEqual, false),
            ("no more than", ConditionOperator.LessThanOrEqual, false),
            ("does not exceed", ConditionOperator.LessThanOrEqual, false),
            ("doesn't exceed", ConditionOperator.LessThanOrEqual, false),
            ("is on or before", ConditionOperator.LessThanOrEqual, false),
            ("<=", ConditionOperator.LessThanOrEqual, false),
            ("is greater than", ConditionOperator.GreaterThan, false),
            ("greater than", ConditionOperator.GreaterThan, false),
            ("is more than", ConditionOperator.GreaterThan, false),
            ("more than", ConditionOperator.GreaterThan, false),
            ("is higher than", ConditionOperator.GreaterThan, false),
            ("higher than", ConditionOperator.GreaterThan, false),
            ("is above", ConditionOperator.GreaterThan, false),
            ("above", ConditionOperator.GreaterThan, false),
            ("is after", ConditionOperator.GreaterThan, false),
            ("exceeds", ConditionOperator.GreaterThan, false),
            ("exceed", ConditionOperator.GreaterThan, false),
            ("is over", ConditionOperator.GreaterThan, false),
            ("over", ConditionOperator.GreaterThan, false),
            (">", ConditionOperator.GreaterThan, false),
            ("is less than", ConditionOperator.LessThan, false),
            ("less than", ConditionOperator.LessThan, false),
            ("fewer than", ConditionOperator.LessThan, false),
            ("is lower than", ConditionOperator.LessThan, false),
            ("lower than", ConditionOperator.LessThan, false),
            ("is below", ConditionOperator.LessThan, false),
            ("below", ConditionOperator.LessThan, false),
            ("is before", ConditionOperator.LessThan, false),
            ("is under", ConditionOperator.LessThan, false),
            ("under", ConditionOperator.LessThan, false),
            ("<", ConditionOperator.LessThan, false),
            ("is between", ConditionOperator.Between, false),
            ("between", ConditionOperator.Between, false),
            ("is not one of", ConditionOperator.NotIn, false),
            ("is none of", ConditionOperator.NotIn, false),
            ("is not any of", ConditionOperator.NotIn, false),
            ("not one of", ConditionOperator.NotIn, false),
            ("is not in", ConditionOperator.NotIn, false),
            ("not in", ConditionOperator.NotIn, false),
            ("is one of", ConditionOperator.In, false),
            ("is any of", ConditionOperator.In, false),
            ("one of", ConditionOperator.In, false),
            ("is in", ConditionOperator.In, false),
            ("in", ConditionOperator.In, false),
            ("does not contain", ConditionOperator.Contains, true),
            ("doesn't contain", ConditionOperator.Contains, true),
            ("does not include", ConditionOperator.Contains, true),
            ("doesn't include", ConditionOperator.Contains, true),
            ("contains", ConditionOperator.Contains, false),
            ("contain", ConditionOperator.Contains, false),
            ("includes", ConditionOperator.Contains, false),
            ("include", ConditionOperator.Contains, false),
            ("starts with", ConditionOperator.StartsWith, false),
            ("start with", ConditionOperator.StartsWith, false),
            ("begins with", ConditionOperator.StartsWith, false),
            ("begin with", ConditionOperator.StartsWith, false),
            ("ends with", ConditionOperator.EndsWith, false),
            ("end with", ConditionOperator.EndsWith, false),
            ("matches pattern", ConditionOperator.Matches, false),
            ("matches", ConditionOperator.Matches, false),
            ("is not provided", ConditionOperator.IsNull, false),
            ("is not set", ConditionOperator.IsNull, false),
            ("is missing", ConditionOperator.IsNull, false),
            ("is null", ConditionOperator.IsNull, false),
            ("is empty", ConditionOperator.IsNull, false),
            ("is blank", ConditionOperator.IsNull, false),
            ("is not null", ConditionOperator.IsNotNull, false),
            ("is not empty", ConditionOperator.IsNotNull, false),
            ("is not blank", ConditionOperator.IsNotNull, false),
            ("is present", ConditionOperator.IsNotNull, false),
            ("is provided", ConditionOperator.IsNotNull, false),
            ("is set", ConditionOperator.IsNotNull, false),
            ("exists", ConditionOperator.IsNotNull, false),
            ("is not equal to", ConditionOperator.NotEquals, false),
            ("not equal to", ConditionOperator.NotEquals, false),
            ("does not equal", ConditionOperator.NotEquals, false),
            ("doesn't equal", ConditionOperator.NotEquals, false),
            ("is not", ConditionOperator.NotEquals, false),
            ("isn't", ConditionOperator.NotEquals, false),
            ("!=", ConditionOperator.NotEquals, false),
            ("<>", ConditionOperator.NotEquals, false),
            ("is equal to", ConditionOperator.Equals, false),
            ("equal to", ConditionOperator.Equals, false),
            ("equals", ConditionOperator.Equals, false),
            ("is", ConditionOperator.Equals, false),
            ("==", ConditionOperator.Equals, false),
            ("=", ConditionOperator.Equals, false),
        };

        return definitions
            .Select(d => new Comparator(
                d.Phrase,
                d.Operator,
                d.Negate,
                R(@"(?<!\S)" + Regex.Escape(d.Phrase).Replace("\\ ", "\\s+", StringComparison.Ordinal) + @"(?!\S)")))
            .OrderByDescending(c => c.Phrase.Length)
            .ToArray();
    }
}
