using System.Globalization;
using System.Xml.Linq;
using Gamehook.Domain.Property;

namespace Gamehook.Infrastructure;

internal sealed class XmlCondition
{
    private readonly List<(ConditionExpression? Expression, List<(Property Target, object? Value)> Assignments)> branches = [];
    public List<Property> Targets { get; } = [];

    public static XmlCondition Compile(XElement[] elements, ref int index, string? path)
    {
        var condition = new XmlCondition();
        var targets = new Dictionary<string, Property>(StringComparer.Ordinal);
        var sawElse = false;
        do
        {
            var branch = elements[index];
            var name = branch.Name.LocalName;
            if (sawElse) throw new InvalidDataException("An <else> must be the last branch in a condition.");
            sawElse = name == "else";
            if (branch.Attributes().Any(a => a.Name != "expression") || (sawElse && branch.Attribute("expression") is not null))
                throw new InvalidDataException($"Invalid attributes on <{name}>.");
            var expression = sawElse ? null : new ConditionExpression(
                (string?)branch.Attribute("expression") ?? throw new InvalidDataException($"<{name}> requires an expression."));
            var assignments = new List<(Property, object?)>();
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in branch.Elements())
            {
                if (element.Name.LocalName != "property" || element.HasElements ||
                    element.Attributes().Any(a => a.Name.LocalName is not ("name" or "type" or "value") || a.Name.Namespace != XNamespace.None))
                    throw new InvalidDataException("Conditional branches support only literal <property name=\"...\" type=\"...\" value=\"...\" /> assignments.");
                var localName = (string?)element.Attribute("name");
                if (string.IsNullOrWhiteSpace(localName)) throw new InvalidDataException("Conditional property requires a name.");
                var propertyPath = string.IsNullOrEmpty(path) ? localName : $"{path}.{localName}";
                if (!assigned.Add(propertyPath)) throw new InvalidDataException($"Duplicate conditional assignment '{propertyPath}'.");
                var type = (string?)element.Attribute("type") ?? "int";
                var value = ParseValue(type, (string?)element.Attribute("value"), propertyPath);
                if (!targets.TryGetValue(propertyPath, out var target))
                {
                    var config = new PropertyConfig(propertyPath, type, null, 1, null, null, null);
                    target = Property.Create(config);
                    targets.Add(propertyPath, target);
                    condition.Targets.Add(target);
                }
                else if (target.Type != type) throw new InvalidDataException($"Conditional property '{propertyPath}' has conflicting types.");
                assignments.Add((target, value));
            }
            if (assignments.Count == 0) throw new InvalidDataException($"<{name}> requires at least one property assignment.");
            condition.branches.Add((expression, assignments));
            if (index + 1 >= elements.Length || elements[index + 1].Name.LocalName is not ("elif" or "else")) break;
            index++;
        } while (true);
        return condition;
    }

    private static object? ParseValue(string type, string? value, string path)
    {
        if (type is not ("string" or "bool" or "int")) throw new InvalidDataException($"Conditional property '{path}' has unsupported type '{type}'.");
        if (value is null) return null;
        if (type == "string") return value;
        if (type == "bool" && bool.TryParse(value, out var boolean)) return boolean;
        if (type == "int" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        throw new InvalidDataException($"Conditional property '{path}' has invalid {type} value '{value}'.");
    }

    public void Validate(IReadOnlyDictionary<string, Property> properties)
    {
        foreach (var branch in branches) branch.Expression?.Validate(properties);
    }

    public void Evaluate(IReadOnlyDictionary<string, Property> properties)
    {
        foreach (var branch in branches)
        {
            if (branch.Expression is not null && !branch.Expression.Evaluate(properties)) continue;
            foreach (var (target, value) in branch.Assignments) target.SetValueOverride(value);
            break;
        }
    }
}
