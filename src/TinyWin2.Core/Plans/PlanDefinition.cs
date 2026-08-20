using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Plans;

public enum PlanArgumentType {
    Enum,
    Int,
    Bool,
    String,
}

/// <summary>One selectable option of an enum argument, carrying its own risk level.</summary>
public sealed record PlanArgumentOption(string Value, string Label, string? Risk = null);

/// <summary>A user-tunable argument declared by a plan (e.g. service start mode).</summary>
public sealed record PlanArgument(
    string Name,
    PlanArgumentType Type,
    string Label,
    JsonNode? Default,
    IReadOnlyList<PlanArgumentOption> Options) {
    public JsonObject ToJson() => new() {
        ["name"] = Name,
        ["type"] = Type.ToString().ToLowerInvariant(),
        ["label"] = Label,
        ["default"] = Default?.DeepClone(),
        ["options"] = Options.Count == 0
            ? null
            : new JsonArray(Options.Select(o => (JsonNode)new JsonObject {
                ["value"] = o.Value,
                ["label"] = o.Label,
                ["risk"] = o.Risk,
            }).ToArray()),
    };
}

/// <summary>One declared exec inside a plan; <see cref="With"/> may reference plan arguments via $arg/$map.</summary>
public sealed record PlanExec(string Resource, Ensure Ensure, JsonObject With);

/// <summary>A leaf plan definition loaded from <c>plans/*.json</c>.</summary>
public sealed partial record PlanDefinition {
    public const int CurrentSchemaVersion = 2;

    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>Display/layer grouping key (maps v1 category).</summary>
    public required string Group { get; init; }
    public string Risk { get; init; } = "Medium";
    public string Tier { get; init; } = "Standard";
    public IReadOnlyList<string> Requires { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public IReadOnlyList<PlanArgument> Arguments { get; init; } = [];
    public IReadOnlyList<PlanExec> Execs { get; init; } = [];
    public string? Sha256 { get; init; }

    public static PlanDefinition FromJson(JsonObject obj, string? sourceFile = null) {
        var errors = new List<string>();
        var schemaVersion = ReadInt(obj, "schemaVersion", errors, required: true);
        if (schemaVersion is not null && schemaVersion != CurrentSchemaVersion) {
            errors.Add($"schemaVersion must be {CurrentSchemaVersion}.");
        }
        var id = ReadString(obj, "id", errors);
        if (id is not null && !IdPattern().IsMatch(id)) {
            errors.Add($"id '{id}' does not match required pattern.");
        }
        var version = ReadString(obj, "version", errors);
        if (version is not null && !VersionPattern().IsMatch(version)) {
            errors.Add($"version '{version}' is not semver (x.y.z).");
        }
        var title = ReadString(obj, "title", errors);
        var description = ReadString(obj, "description", errors);
        var group = ReadString(obj, "group", errors);
        var risk = ReadString(obj, "risk", errors) ?? "Medium";
        var tier = ReadString(obj, "tier", errors) ?? "Standard";
        if (risk is not null && !RiskLevels.Contains(risk)) {
            errors.Add($"risk '{risk}' invalid (Low|Medium|High).");
        }
        if (tier is not null && !Tiers.Contains(tier)) {
            errors.Add($"tier '{tier}' invalid (Standard|Expert|Experimental).");
        }
        var requires = ReadStringArray(obj, "requires", errors);
        var conflicts = ReadStringArray(obj, "conflicts", errors);
        var arguments = ReadArguments(obj, errors);
        var execs = ReadExecs(obj, errors);
        if (errors.Count > 0) {
            throw new PlanValidationException(sourceFile ?? "<memory>", errors);
        }
        return new PlanDefinition {
            SchemaVersion = CurrentSchemaVersion,
            Id = id!,
            Version = version!,
            Title = title!,
            Description = description!,
            Group = group!,
            Risk = risk!,
            Tier = tier!,
            Requires = requires,
            Conflicts = conflicts,
            Arguments = arguments,
            Execs = execs,
        };
    }

    internal static readonly string[] RiskLevels = ["Low", "Medium", "High"];
    internal static readonly string[] Tiers = ["Standard", "Expert", "Experimental"];

    internal static IReadOnlyList<string> ReadStringArray(JsonObject obj, string name, List<string> errors, bool required = false) {
        if (obj[name] is not { } node) {
            if (required) {
                errors.Add($"'{name}' is required.");
            }
            return [];
        }
        if (node is not JsonArray array) {
            errors.Add($"'{name}' must be an array of strings.");
            return [];
        }
        var values = new List<string>();
        foreach (var item in array) {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) {
                values.Add(text);
            }
            else {
                errors.Add($"'{name}' must contain non-empty strings.");
            }
        }
        return values;
    }

    internal static string? ReadString(JsonObject obj, string name, List<string> errors, bool required = true) {
        if (obj[name] is not { } node) {
            if (required) {
                errors.Add($"'{name}' is required.");
            }
            return null;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) {
            if (string.IsNullOrWhiteSpace(text) && required) {
                errors.Add($"'{name}' must not be empty.");
                return null;
            }
            return text;
        }
        errors.Add($"'{name}' must be a string.");
        return null;
    }

    internal static int? ReadInt(JsonObject obj, string name, List<string> errors, bool required = true) {
        if (obj[name] is not { } node) {
            if (required) {
                errors.Add($"'{name}' is required.");
            }
            return null;
        }
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) {
            return number;
        }
        errors.Add($"'{name}' must be an integer.");
        return null;
    }

    private static IReadOnlyList<PlanArgument> ReadArguments(JsonObject obj, List<string> errors) {
        if (obj["arguments"] is not { } node) {
            return [];
        }
        if (node is not JsonArray array) {
            errors.Add("'arguments' must be an array.");
            return [];
        }
        var result = new List<PlanArgument>();
        var seenNames = new HashSet<string>();
        foreach (var item in array) {
            if (item is not JsonObject argumentObj) {
                errors.Add("'arguments' entries must be objects.");
                continue;
            }
            var inner = new List<string>();
            var name = ReadString(argumentObj, "name", inner);
            var typeText = ReadString(argumentObj, "type", inner);
            var label = ReadString(argumentObj, "label", inner, required: false) ?? name;
            if (!Enum.TryParse<PlanArgumentType>(typeText, ignoreCase: true, out var type)) {
                inner.Add($"argument type '{typeText}' invalid (enum|int|bool|string).");
            }
            var options = new List<PlanArgumentOption>();
            if (argumentObj["options"] is JsonArray optionArray) {
                foreach (var optionNode in optionArray) {
                    if (optionNode is not JsonObject optionObj) {
                        inner.Add("'arguments[].options' entries must be objects.");
                        continue;
                    }
                    var optionInner = new List<string>();
                    var value = ReadString(optionObj, "value", optionInner);
                    var optionLabel = ReadString(optionObj, "label", optionInner, required: false) ?? value;
                    var optionRisk = ReadString(optionObj, "risk", optionInner, required: false);
                    if (optionInner.Count > 0) {
                        inner.AddRange(optionInner);
                        continue;
                    }
                    options.Add(new PlanArgumentOption(value!, optionLabel!, optionRisk));
                }
            }
            if (type == PlanArgumentType.Enum && options.Count == 0) {
                inner.Add("enum argument requires at least one option.");
            }
            if (options.GroupBy(o => o.Value, StringComparer.Ordinal).Any(g => g.Count() > 1)) {
                inner.Add("enum option values must be unique.");
            }
            if (name is not null && !seenNames.Add(name)) {
                inner.Add($"duplicate argument name '{name}'.");
            }
            if (inner.Count > 0) {
                errors.AddRange(inner.Select(e => $"arguments[{result.Count}]: {e}"));
                continue;
            }
            var defaultNode = argumentObj["default"]?.DeepClone();
            if (type == PlanArgumentType.Enum) {
                var defaultText = defaultNode is JsonValue dv && dv.TryGetValue<string>(out var s) ? s : null;
                if (defaultText is not null && options.All(o => !string.Equals(o.Value, defaultText, StringComparison.Ordinal))) {
                    errors.Add($"arguments[{result.Count}]: default '{defaultText}' is not one of the declared options.");
                    continue;
                }
                if (defaultText is null) {
                    defaultNode = options[0].Value;
                }
            }
            else if (type == PlanArgumentType.Bool && defaultNode is null) {
                defaultNode = false;
            }
            result.Add(new PlanArgument(name!, type, label!, defaultNode, options));
        }
        return result;
    }

    private static IReadOnlyList<PlanExec> ReadExecs(JsonObject obj, List<string> errors) {
        if (obj["execs"] is not { } node) {
            errors.Add("'execs' is required.");
            return [];
        }
        if (node is not JsonArray array) {
            errors.Add("'execs' must be an array.");
            return [];
        }
        var result = new List<PlanExec>();
        foreach (var item in array) {
            if (item is not JsonObject execObj) {
                errors.Add("'execs' entries must be objects.");
                continue;
            }
            var inner = new List<string>();
            var resource = ReadString(execObj, "resource", inner);
            if (resource is not null && !ResourcePattern().IsMatch(resource)) {
                inner.Add($"resource '{resource}' must look like 'domain.name'.");
            }
            var ensureText = ReadString(execObj, "ensure", inner, required: false) ?? "present";
            if (!Enum.TryParse<Ensure>(ensureText, ignoreCase: true, out var ensure)) {
                inner.Add($"ensure '{ensureText}' invalid (present|absent).");
            }
            var withObj = execObj["with"] as JsonObject;
            if (withObj is null) {
                inner.Add("'with' must be an object.");
            }
            if (inner.Count > 0) {
                errors.AddRange(inner.Select(e => $"execs[{result.Count}]: {e}"));
                continue;
            }
            result.Add(new PlanExec(resource!, ensure, withObj!));
        }
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex VersionPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]+(?:\.[a-z0-9-]+)+$")]
    private static partial System.Text.RegularExpressions.Regex ResourcePattern();
}

public sealed class PlanValidationException(string source, IReadOnlyList<string> errors)
    : Exception($"Invalid plan '{source}':{Environment.NewLine}{string.Join(Environment.NewLine + "  - ", errors)}") {
    public IReadOnlyList<string> Errors { get; } = errors;
}
