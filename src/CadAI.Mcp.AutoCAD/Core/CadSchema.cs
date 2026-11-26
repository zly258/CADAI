using System.Collections.Generic;

namespace CadAI.Mcp.AutoCAD.Core
{
    internal static class CadSchema
    {
        public static Dictionary<string, object> Function(string name, string description, Dictionary<string, object> parameters)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters ?? Obj()
                }
            };
        }

        public static Dictionary<string, object> Obj(params PropertySpec[] specs)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            if (specs != null)
            {
                foreach (var spec in specs)
                {
                    if (spec == null) continue;
                    var property = new Dictionary<string, object>
                    {
                        ["type"] = spec.Type,
                        ["description"] = spec.Description
                    };
                    if (spec.ItemsType != null)
                        property["items"] = new Dictionary<string, object> { ["type"] = spec.ItemsType };
                    properties[spec.Name] = property;
                    if (spec.Required) required.Add(spec.Name);
                }
            }

            var result = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };
            if (required.Count > 0) result["required"] = required.ToArray();
            return result;
        }

        public static PropertySpec Str(string n, string d, bool r = false) => new PropertySpec(n, "string", d, r);
        public static PropertySpec Int(string n, string d, bool r = false) => new PropertySpec(n, "integer", d, r);
        public static PropertySpec Num(string n, string d, bool r = false) => new PropertySpec(n, "number", d, r);
        public static PropertySpec Bool(string n, string d, bool r = false) => new PropertySpec(n, "boolean", d, r);
        public static PropertySpec Arr(string n, string d, string itemType = "string", bool r = false) => new PropertySpec(n, "array", d, r, itemType);

        public sealed class PropertySpec
        {
            public PropertySpec(string n, string t, string d, bool r, string items = null)
            {
                Name = n;
                Type = t;
                Description = d;
                Required = r;
                ItemsType = items;
            }

            public string Name { get; }
            public string Type { get; }
            public string Description { get; }
            public bool Required { get; }
            public string ItemsType { get; }
        }
    }
}
