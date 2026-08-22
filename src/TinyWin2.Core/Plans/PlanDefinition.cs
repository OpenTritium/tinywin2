using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Plans;

public enum PlanParameterType {
    Enum,
    Int,
    Bool,
    String
}

public sealed record PlanParameterOption(string Value, string Label, string? RiskLevel = null);

public sealed record PlanParameter(
    string Name,
    PlanParameterType Type,
    string Label,
    JsonNode? Default,
    IReadOnlyList<PlanParameterOption> Options) {
    public JsonObject ToJson() {
        var result = new JsonObject {
            ["name"] = Name,
            ["type"] = Type.ToString().ToLowerInvariant(),
            ["label"] = Label
        };
        if (Default is not null) {
            result["default"] = Default.DeepClone();
        }

        if (Options.Count > 0) {
            result["options"] = new JsonArray([.. Options.Select(ToJson)]);
        }

        return result;
    }

    private static JsonObject ToJson(PlanParameterOption option) {
        var result = new JsonObject {
            ["value"] = option.Value,
            ["label"] = option.Label
        };
        if (option.RiskLevel is not null) {
            result["riskLevel"] = option.RiskLevel;
        }

        return result;
    }
}

/// <summary>One atomic operation owned by a plan and executed by one resource executor.</summary>
public sealed record PlanOperation(string Resource, OperationAction Action, JsonObject Spec);

/// <summary>A leaf plan definition loaded from <c>plans/*.json</c>.</summary>
public sealed partial record PlanDefinition {
    private const int CurrentSchemaVersion = 3;

    private static readonly string[] RiskLevels = ["Low", "Medium", "High"];

    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public string RiskLevel { get; private init; } = "Medium";
    public IReadOnlyList<string> Requires { get; private init; } = [];
    public IReadOnlyList<string> Conflicts { get; private init; } = [];
    public IReadOnlyList<PlanParameter> Parameters { get; private init; } = [];
    public required PlanOperation Operation { get; init; }
    public string? Hash { get; init; }

    public static PlanDefinition FromJson(JsonObject obj, string? sourceFile = null) {
        var errors = new List<string>();
        RejectUnknownProperties(obj, "plan", [
            "schemaVersion", "id", "version", "title", "description", "category", "riskLevel",
            "requires", "conflicts", "parameters", "operation"
        ], errors);
        var schemaVersion = ReadInt(obj, "schemaVersion", errors);
        if (schemaVersion is not null && schemaVersion != CurrentSchemaVersion) {
            errors.Add($"schemaVersion must be {CurrentSchemaVersion}; older plan schemas are not supported.");
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
        var category = ReadString(obj, "category", errors);
        var riskLevel = ReadString(obj, "riskLevel", errors) ?? "Medium";
        if (!RiskLevels.Contains(riskLevel)) {
            errors.Add($"riskLevel '{riskLevel}' invalid (Low|Medium|High).");
        }

        var requires = ReadStringArray(obj, "requires", errors);
        var conflicts = ReadStringArray(obj, "conflicts", errors);
        var parameters = ReadParameters(obj, errors);
        var operation = ReadOperation(obj, errors);
        if (errors.Count > 0) {
            throw new PlanValidationException(sourceFile ?? "<memory>", errors);
        }

        return new() {
            Id = id!,
            Version = version!,
            Title = title!,
            Description = description!,
            Category = category!,
            RiskLevel = riskLevel,
            Requires = requires,
            Conflicts = conflicts,
            Parameters = parameters,
            Operation = operation!
        };
    }

    private static string? ReadString(JsonObject obj, string name, List<string> errors, bool required = true) {
        if (!obj.TryGetPropertyValue(name, out var node)) {
            if (required) {
                errors.Add($"'{name}' is required.");
            }

            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text)) {
            if (!string.IsNullOrWhiteSpace(text) || !required) {
                return text;
            }

            errors.Add($"'{name}' must not be empty.");
            return null;
        }

        errors.Add($"'{name}' must be a string.");
        return null;
    }

    private static int? ReadInt(JsonObject obj, string name, List<string> errors) {
        if (!obj.TryGetPropertyValue(name, out var node)) {
            errors.Add($"'{name}' is required.");
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var number)) {
            return number;
        }

        errors.Add($"'{name}' must be an integer.");
        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject obj, string name, List<string> errors) {
        if (!obj.TryGetPropertyValue(name, out var node)) {
            return [];
        }

        if (node is not JsonArray array) {
            errors.Add($"'{name}' must be an array of strings.");
            return [];
        }

        var values = new List<string>();
        foreach (var item in array) {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text)) {
                values.Add(text);
            }
            else {
                errors.Add($"'{name}' must contain non-empty strings.");
            }
        }

        if (values.Count != values.Distinct(StringComparer.Ordinal).Count()) {
            errors.Add($"'{name}' must not contain duplicates.");
        }

        return values;
    }

    private static IReadOnlyList<PlanParameter> ReadParameters(JsonObject obj, List<string> errors) {
        if (!obj.TryGetPropertyValue("parameters", out var node)) {
            return [];
        }

        if (node is not JsonArray array) {
            errors.Add("'parameters' must be an array.");
            return [];
        }

        var result = new List<PlanParameter>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++) {
            var item = array[index];
            if (item is not JsonObject parameterObj) {
                errors.Add($"parameters[{index}]: entry must be an object.");
                continue;
            }

            RejectUnknownProperties(parameterObj, $"parameters[{index}]", [
                "name", "type", "label", "default", "options"
            ], errors);
            var inner = new List<string>();
            var name = ReadString(parameterObj, "name", inner);
            var typeText = ReadString(parameterObj, "type", inner);
            var label = ReadString(parameterObj, "label", inner, false) ?? name;
            PlanParameterType? type = null;
            if (Enum.TryParse<PlanParameterType>(typeText, true, out var parsedType)
                && Enum.IsDefined(parsedType)) {
                type = parsedType;
            }
            else {
                inner.Add($"parameter type '{typeText}' invalid (enum|int|bool|string).");
            }

            var options = new List<PlanParameterOption>();
            if (parameterObj["options"] is JsonArray optionArray) {
                for (var optionIndex = 0; optionIndex < optionArray.Count; optionIndex++) {
                    var optionNode = optionArray[optionIndex];
                    if (optionNode is not JsonObject optionObj) {
                        inner.Add($"parameters[{index}].options[{optionIndex}]: entry must be an object.");
                        continue;
                    }

                    RejectUnknownProperties(optionObj, $"parameters[{index}].options[{optionIndex}]", [
                        "value", "label", "riskLevel"
                    ], inner);
                    var optionInner = new List<string>();
                    var value = ReadString(optionObj, "value", optionInner);
                    var optionLabel = ReadString(optionObj, "label", optionInner, false) ?? value;
                    var optionRisk = ReadString(optionObj, "riskLevel", optionInner, false);
                    if (optionRisk is not null && !RiskLevels.Contains(optionRisk)) {
                        optionInner.Add($"option riskLevel '{optionRisk}' invalid (Low|Medium|High).");
                    }

                    if (optionInner.Count > 0) {
                        inner.AddRange(optionInner);
                        continue;
                    }

                    options.Add(new(value!, optionLabel!, optionRisk));
                }
            }

            if (type == PlanParameterType.Enum && options.Count == 0) {
                inner.Add("enum parameter requires at least one option.");
            }

            if (type is not null && type != PlanParameterType.Enum && options.Count > 0) {
                inner.Add("only enum parameters may declare options.");
            }

            if (options.GroupBy(o => o.Value, StringComparer.Ordinal).Any(g => g.Count() > 1)) {
                inner.Add("parameter option values must be unique.");
            }

            if (name is not null && !seenNames.Add(name)) {
                inner.Add($"duplicate parameter name '{name}'.");
            }

            if (inner.Count > 0) {
                errors.AddRange(inner.Select(e => e.StartsWith("parameters[", StringComparison.Ordinal)
                    ? e
                    : $"parameters[{index}]: {e}"));
                continue;
            }

            var defaultNode = parameterObj["default"]?.DeepClone();
            switch (type) {
                case PlanParameterType.Enum: {
                    var defaultText = defaultNode is JsonValue dv && dv.TryGetValue<string>(out var s) ? s : null;
                    if (defaultText is not null &&
                        options.All(o => !string.Equals(o.Value, defaultText, StringComparison.Ordinal))) {
                        errors.Add($"parameters[{index}]: default '{defaultText}' is not one of the declared options.");

                        continue;
                    }

                    defaultNode ??= options[0].Value;
                    break;
                }
                case PlanParameterType.Bool:
                    defaultNode ??= false;
                    break;
                case PlanParameterType.Int:
                case PlanParameterType.String:
                case null:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            result.Add(new(name!, type!.Value, label!, defaultNode, options));
        }

        return result;
    }

    private static PlanOperation? ReadOperation(JsonObject obj, List<string> errors) {
        if (obj["operation"] is not JsonObject operationObj) {
            errors.Add("'operation' must be an object.");
            return null;
        }

        RejectUnknownProperties(operationObj, "operation", ["resource", "action", "spec"], errors);
        var inner = new List<string>();
        var resource = ReadString(operationObj, "resource", inner);
        if (resource is not null && !ResourcePattern().IsMatch(resource)) {
            inner.Add($"resource '{resource}' must look like 'domain.name'.");
        }

        var actionText = ReadString(operationObj, "action", inner);
        if (!Enum.TryParse<OperationAction>(actionText, true, out var action)
            || !Enum.IsDefined(action)) {
            inner.Add($"action '{actionText}' invalid (configure|set|remove|cleanup|copy).");
        }

        var spec = operationObj["spec"] as JsonObject;
        if (spec is null) {
            inner.Add("'spec' must be an object.");
        }

        if (inner.Count > 0) {
            errors.AddRange(inner.Select(e => $"operation: {e}"));
            return null;
        }

        return new(resource!, action, spec!);
    }

    private static void RejectUnknownProperties(
        JsonObject obj,
        string path,
        IReadOnlyList<string> allowed,
        List<string> errors) {
        errors.AddRange(obj.Select(property => property.Key)
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
            .Select(property => $"{path} contains unknown field '{property}'."));
    }

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:\.[a-z0-9-]+)+$")]
    private static partial Regex ResourcePattern();
}

public sealed class PlanValidationException(string source, IReadOnlyList<string> errors)
    : Exception($"Invalid plan '{source}':{Environment.NewLine}{string.Join(Environment.NewLine + "  - ", errors)}");
