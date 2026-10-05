using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Hispania.Platform.Contracts.Identity;
using Hispania.Playback.Contracts;

namespace Hispania.Compatibility.Tests;

internal static class SelectedApi
{
    public static string Capture()
    {
        var names = File.ReadAllLines(BaselineCorpus.PathFor("api/0.7/selected.txt")).Where(line => line.StartsWith("TYPE ", StringComparison.Ordinal)).Select(line => line.Split(" | ")[0][5..]).ToHashSet(StringComparer.Ordinal);
        Assembly[] assemblies = [typeof(IIdentityQueryV1).Assembly, typeof(DirectPlayExecutionResult).Assembly];
        var types = assemblies.SelectMany(assembly => assembly.GetExportedTypes()).Where(type => names.Contains(type.FullName!)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();
        foreach (var type in types)
        {
            lines.Add($"TYPE {type.FullName} | base={type.BaseType} | interfaces={string.Join(",", type.GetInterfaces().Select(item => item.ToString()).Order(StringComparer.Ordinal))}");
            foreach (var argument in type.GetGenericArguments())
                lines.Add($"GENERIC {argument.Name} {argument.GenericParameterAttributes} constraints=[{string.Join(",", argument.GetGenericParameterConstraints().Select(item => item.ToString()).Order(StringComparer.Ordinal))}]");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(member => member.ToString(), StringComparer.Ordinal))
            {
                if (member is Type) continue;
                var detail = member switch
                {
                    MethodInfo method => $"static={method.IsStatic};abstract={method.IsAbstract};virtual={method.IsVirtual};return={NullableShape(nullability.Create(method.ReturnParameter))};params={Parameters(method.GetParameters(), nullability)}",
                    ConstructorInfo constructor => "params=" + Parameters(constructor.GetParameters(), nullability),
                    PropertyInfo property => $"null={NullableShape(nullability.Create(property))};get={property.GetMethod?.IsPublic};set={property.SetMethod?.IsPublic}",
                    FieldInfo field => $"null={NullableShape(nullability.Create(field))};value={(field.IsLiteral ? Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : "nonconstant")}",
                    _ => ""
                };
                lines.Add($"{member.MemberType} {member} | {detail}");
            }
        }
        return string.Join('\n', lines) + "\n";
    }
    private static string Parameters(ParameterInfo[] parameters, NullabilityInfoContext context) => string.Join(";", parameters.Select(parameter => $"{parameter.Name}:{parameter.ParameterType}:{NullableShape(context.Create(parameter))}:in={parameter.IsIn}:out={parameter.IsOut}:optional={parameter.IsOptional}:default={(parameter.HasDefaultValue ? parameter.DefaultValue is null ? "null" : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) : "none")}"));
    private static string NullableShape(NullabilityInfo value) => $"{value.ReadState}/{value.WriteState}[{string.Join(",", value.GenericTypeArguments.Select(NullableShape))}]{(value.ElementType is null ? "" : NullableShape(value.ElementType))}";
    public static string[] Diff(string baseline, string current)
    {
        static HashSet<string> Entries(string text)
        {
            var entries = new HashSet<string>(StringComparer.Ordinal);
            var type = "";
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("TYPE ", StringComparison.Ordinal)) type = line.Split(" | ")[0][5..];
                entries.Add(type + " :: " + line);
            }
            return entries;
        }
        var old = Entries(baseline); var now = Entries(current);
        return old.Except(now).Order(StringComparer.Ordinal).Select(line => "BREAKING: " + line)
            .Concat(now.Except(old).Order(StringComparer.Ordinal).Select(line => "ADDITIVE: " + line)).ToArray();
    }
}
