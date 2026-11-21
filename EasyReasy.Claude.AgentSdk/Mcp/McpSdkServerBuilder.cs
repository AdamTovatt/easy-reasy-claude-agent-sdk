using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyReasy.Claude.AgentSdk.Mcp;

/// <summary>
/// Builds an in-process ("sdk") MCP server configuration with strongly-typed tool registration.
/// </summary>
public sealed class McpSdkServerBuilder
{
    private readonly string _serverName;
    private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);

    internal McpSdkServerBuilder(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name must be non-empty.", nameof(serverName));
        _serverName = serverName;
    }

    /// <summary>
    /// Register a tool by delegate; input schema and argument binding are inferred from the delegate signature.
    /// </summary>
    /// <remarks>
    /// Supported signatures include:
    /// <list type="bullet">
    /// <item><description><c>(T1 a, T2 b, CancellationToken ct) => TResult</c></description></item>
    /// <item><description><c>(T1 a, T2 b) => Task&lt;TResult&gt;</c></description></item>
    /// <item><description><c>(TArgs args) => TResult</c> (single complex param binds from the whole JSON args object)</description></item>
    /// </list>
    /// </remarks>
    public McpSdkServerBuilder Tool(string name, Delegate handler, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name must be non-empty.", nameof(name));

        if (_tools.ContainsKey(name))
            throw new ArgumentException($"Tool '{name}' is already registered.", nameof(name));

        _tools[name] = ToolRegistration.Create(name, description, handler);
        return this;
    }

    internal McpSdkServerConfig Build()
    {
        McpServerHandlers handlers = new McpServerHandlers
        {
            ListTools = ct => Task.FromResult<IReadOnlyList<McpToolDefinition>>(
                _tools.Values
                    .Select(t => t.Definition)
                    .ToList()
            ),
            CallTool = async (toolName, args, ct) =>
            {
                if (!_tools.TryGetValue(toolName, out ToolRegistration? tool))
                    return McpToolResults.Text($"Unknown tool: '{toolName}'", isError: true);

                try
                {
                    return await tool.InvokeAsync(args, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return McpToolResults.Text(ex.Message, isError: true);
                }
            }
        };

        return new McpSdkServerConfig
        {
            Name = _serverName,
            Handlers = handlers
        };
    }

    /// <summary>
    /// The type a value of <paramref name="type"/> holds: <c>T</c> for <c>Nullable&lt;T&gt;</c>, otherwise the type itself.
    /// </summary>
    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private sealed class ToolRegistration
    {
        private static readonly JsonSerializerOptions ToolJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly Delegate _handler;
        private readonly BindingPlan _bindingPlan;

        public McpToolDefinition Definition { get; }

        private ToolRegistration(string name, string? description, JsonElement inputSchema, Delegate handler, BindingPlan bindingPlan)
        {
            _handler = handler;
            _bindingPlan = bindingPlan;
            Definition = new McpToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            };
        }

        public static ToolRegistration Create(string name, string? description, Delegate handler)
        {
            McpSchemaGenerator schemaGenerator = new McpSchemaGenerator();
            BindingPlan plan = BindingPlan.Create(handler.Method, schemaGenerator);
            JsonElement schema = plan.BuildInputSchema(schemaGenerator);
            return new ToolRegistration(name, description, schema, handler, plan);
        }

        public async Task<McpToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            object?[] invokeArgs = _bindingPlan.BindArguments(args, ct);
            object? result = _handler.DynamicInvoke(invokeArgs);
            object? value = await AwaitIfNeededAsync(result).ConfigureAwait(false);
            return ConvertToToolResult(value);
        }

        private static McpToolResult ConvertToToolResult(object? value)
        {
            switch (value)
            {
                case McpToolResult toolResult:
                    return toolResult;
                case McpContent content:
                    return new McpToolResult { Content = [content] };
                case IEnumerable<McpContent> contents:
                    return new McpToolResult { Content = contents.ToList() };
                case null:
                    return McpToolResults.Text("");
                case string s:
                    return McpToolResults.Text(s);
                case JsonElement je:
                    return McpToolResults.Text(je.GetRawText());
                default:
                    if (IsSimpleScalar(value.GetType()))
                        return McpToolResults.Text(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    return McpToolResults.Text(JsonSerializer.Serialize(value, ToolJsonOptions));
            }
        }

        private static bool IsSimpleScalar(Type type)
        {
            if (type.IsEnum) return true;
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => true,
                TypeCode.Byte => true,
                TypeCode.SByte => true,
                TypeCode.Int16 => true,
                TypeCode.UInt16 => true,
                TypeCode.Int32 => true,
                TypeCode.UInt32 => true,
                TypeCode.Int64 => true,
                TypeCode.UInt64 => true,
                TypeCode.Single => true,
                TypeCode.Double => true,
                TypeCode.Decimal => true,
                TypeCode.String => true,
                _ => false
            };
        }

        private static async Task<object?> AwaitIfNeededAsync(object? result)
        {
            if (result is null) return null;

            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                Type taskType = task.GetType();
                if (taskType.IsGenericType)
                    return taskType.GetProperty("Result")?.GetValue(task);
                return null;
            }

            Type type = result.GetType();
            if (type.FullName is { } fullName && fullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
            {
                MethodInfo? asTask = type.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
                if (asTask != null && asTask.Invoke(result, null) is Task vtTask)
                {
                    await vtTask.ConfigureAwait(false);
                    Type vtTaskType = vtTask.GetType();
                    if (vtTaskType.IsGenericType)
                        return vtTaskType.GetProperty("Result")?.GetValue(vtTask);
                    return null;
                }
            }

            return result;
        }

        private sealed class BindingPlan
        {
            private readonly List<BindingParameter> _parameters;
            private readonly bool _hasCancellationToken;
            private readonly bool _bindWholeObject;

            private BindingPlan(List<BindingParameter> parameters, bool hasCancellationToken, bool bindWholeObject)
            {
                _parameters = parameters;
                _hasCancellationToken = hasCancellationToken;
                _bindWholeObject = bindWholeObject;
            }

            public static BindingPlan Create(MethodInfo method, McpSchemaGenerator schemaGenerator)
            {
                ParameterInfo[] allParams = method.GetParameters();
                bool hasCt = allParams.Length > 0 && allParams[^1].ParameterType == typeof(CancellationToken);
                ParameterInfo[] logicalParams = hasCt ? allParams[..^1] : allParams;

                bool bindWhole = false;
                if (logicalParams.Length == 1 && IsComplexObject(logicalParams[0].ParameterType))
                    bindWhole = true;

                List<BindingParameter> parameters = logicalParams
                    .Select(p => new BindingParameter(p, schemaGenerator))
                    .ToList();

                return new BindingPlan(parameters, hasCt, bindWhole);
            }

            public JsonElement BuildInputSchema(McpSchemaGenerator schemaGenerator)
            {
                Dictionary<string, object?> schema = _bindWholeObject && _parameters.Count == 1
                    ? schemaGenerator.GenerateArgsObjectSchema(_parameters[0].ParameterType)
                    : schemaGenerator.GenerateParametersSchema(
                        _parameters.Select(p => (p.JsonName, p.ParameterType, p.IsRequired)).ToList());

                return JsonSerializer.SerializeToElement(schema, ToolJsonOptions);
            }

            public object?[] BindArguments(JsonElement args, CancellationToken ct)
            {
                object?[] values = new object?[_parameters.Count + (_hasCancellationToken ? 1 : 0)];

                if (_bindWholeObject && _parameters.Count == 1)
                {
                    values[0] = Deserialize(args, _parameters[0].ParameterType);
                }
                else
                {
                    foreach ((BindingParameter? p, int idx) in _parameters.Select((p, i) => (p, i)))
                    {
                        if (!TryGetProperty(args, p.JsonName, out JsonElement prop))
                        {
                            if (p.HasDefaultValue)
                            {
                                values[idx] = p.DefaultValue;
                                continue;
                            }

                            if (p.AllowsNull)
                            {
                                values[idx] = null;
                                continue;
                            }

                            throw new ArgumentException($"Missing required argument '{p.JsonName}'.");
                        }

                        values[idx] = ConvertElement(prop, p.ParameterType);
                    }
                }

                if (_hasCancellationToken)
                    values[^1] = ct;

                return values;
            }

            private static bool TryGetProperty(JsonElement args, string name, out JsonElement value)
            {
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value))
                    return true;

                if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in args.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            value = prop.Value;
                            return true;
                        }
                    }
                }

                value = default;
                return false;
            }

            private static object? ConvertElement(JsonElement element, Type targetType)
            {
                if (targetType == typeof(JsonElement))
                    return element.Clone();

                if (targetType == typeof(string))
                    return element.ValueKind == JsonValueKind.Null ? null : element.GetString();

                if (targetType == typeof(bool) || targetType == typeof(bool?))
                    return element.ValueKind == JsonValueKind.Null ? null : element.GetBoolean();

                if (targetType.IsEnum)
                {
                    if (element.ValueKind == JsonValueKind.String)
                        return Enum.Parse(targetType, element.GetString() ?? "", ignoreCase: true);
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int i))
                        return Enum.ToObject(targetType, i);
                }

                if (IsNumber(targetType))
                {
                    if (element.ValueKind == JsonValueKind.Null)
                        return null;

                    Type underlying = Unwrap(targetType);
                    if (underlying == typeof(decimal) && element.TryGetDecimal(out decimal dec))
                        return dec;

                    bool isIntegral = IsIntegralNumber(targetType);
                    if (isIntegral)
                    {
                        if (element.TryGetInt64(out long l))
                            return Convert.ChangeType(l, underlying, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        if (element.TryGetDouble(out double d))
                            return Convert.ChangeType(d, underlying, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                return Deserialize(element, targetType);
            }

            private static object? Deserialize(JsonElement element, Type targetType)
                => JsonSerializer.Deserialize(element, targetType, ToolJsonOptions);

            private static bool IsNumber(Type t)
            {
                t = Unwrap(t);
                return Type.GetTypeCode(t) switch
                {
                    TypeCode.Byte => true,
                    TypeCode.SByte => true,
                    TypeCode.Int16 => true,
                    TypeCode.UInt16 => true,
                    TypeCode.Int32 => true,
                    TypeCode.UInt32 => true,
                    TypeCode.Int64 => true,
                    TypeCode.UInt64 => true,
                    TypeCode.Single => true,
                    TypeCode.Double => true,
                    TypeCode.Decimal => true,
                    _ => false
                };
            }

            private static bool IsIntegralNumber(Type t)
            {
                t = Unwrap(t);
                return Type.GetTypeCode(t) switch
                {
                    TypeCode.Byte => true,
                    TypeCode.SByte => true,
                    TypeCode.Int16 => true,
                    TypeCode.UInt16 => true,
                    TypeCode.Int32 => true,
                    TypeCode.UInt32 => true,
                    TypeCode.Int64 => true,
                    TypeCode.UInt64 => true,
                    _ => false
                };
            }

            // Lists, string-keyed dictionaries and object also pass this check, so a single parameter of one of
            // those types binds from the whole arguments object and the schema's root is not an object.
            // (object args) relies on this to receive the raw arguments object.
            private static bool IsComplexObject(Type t)
            {
                t = Unwrap(t);
                if (t == typeof(string)) return false;
                if (t == typeof(JsonElement)) return false;
                if (t.IsPrimitive) return false;
                if (t.IsEnum) return false;
                return Type.GetTypeCode(t) == TypeCode.Object;
            }
        }

        private sealed class BindingParameter
        {
            public string JsonName { get; }
            public Type ParameterType { get; }
            public bool HasDefaultValue { get; }
            public object? DefaultValue { get; }
            public bool AllowsNull { get; }
            public bool IsRequired { get; }

            public BindingParameter(ParameterInfo p, McpSchemaGenerator schemaGenerator)
            {
                ParameterType = p.ParameterType;
                string paramName = p.Name ?? throw new ArgumentException("Delegate parameters must have names.");
                JsonName = JsonNamingPolicy.CamelCase.ConvertName(paramName);

                HasDefaultValue = p.HasDefaultValue;
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null;

                AllowsNull = schemaGenerator.AllowsNull(p);
                IsRequired = !HasDefaultValue && !AllowsNull;
            }
        }
    }

    /// <summary>
    /// Generates a tool's input schema and decides which of its values allow null. Create one per tool
    /// registration: the nullability reader it holds is not thread-safe, and sharing one between
    /// registrations that run concurrently corrupts it for every later registration in the process.
    /// The <c>$defs</c> it collects belong to the one input schema it generates.
    /// </summary>
    /// <remarks>
    /// An object type that lies on a cycle in the type graph (it embeds itself, directly, through other
    /// types, or through collections) can't be inlined, so it is written once under the root's
    /// <c>$defs</c> and every place it appears becomes a <c>$ref</c> to it. Every other type is inlined.
    /// </remarks>
    private sealed class McpSchemaGenerator
    {
        private const string DefinitionsKeyword = "$defs";

        private readonly NullabilityInfoContext _nullability = new NullabilityInfoContext();
        private readonly Dictionary<Type, bool> _onCycle = new Dictionary<Type, bool>();
        private readonly Dictionary<Type, string> _definitionKeys = new Dictionary<Type, string>();
        private readonly Dictionary<string, Dictionary<string, object?>?> _definitions = new(StringComparer.Ordinal);

        public bool AllowsNull(ParameterInfo parameter)
            => AllowsNull(_nullability.Create(parameter).WriteState, parameter.ParameterType);

        public bool AllowsNull(PropertyInfo property)
        {
            NullabilityInfo nullability = _nullability.Create(property);

            // A property with no setter has no write nullability (it reports Unknown), so its getter's is what it declares.
            NullabilityState declared = property.SetMethod != null ? nullability.WriteState : nullability.ReadState;
            return AllowsNull(declared, property.PropertyType);
        }

        private static bool AllowsNull(NullabilityState declared, Type type)
            => declared == NullabilityState.Nullable || Nullable.GetUnderlyingType(type) != null;

        /// <summary>
        /// The input schema of a tool whose single parameter binds from the whole arguments object.
        /// </summary>
        public Dictionary<string, object?> GenerateArgsObjectSchema(Type argsType)
        {
            Type type = Unwrap(argsType);

            // A reference can't stand in for the root of a tool input schema, so a cyclic object args type is
            // inlined there as well as defined for the references inside it. The copy keeps $defs from being
            // added to its own definition.
            Dictionary<string, object?> root = ShapeOf(type) is TypeShape.ObjectOf && IsOnCycle(type)
                ? new Dictionary<string, object?>(_definitions[Define(type)]!)
                : GenerateForType(type);

            return WithDefinitions(root);
        }

        /// <summary>
        /// The input schema of a tool whose parameters each bind from one property of the arguments object.
        /// </summary>
        public Dictionary<string, object?> GenerateParametersSchema(IReadOnlyList<(string JsonName, Type Type, bool IsRequired)> parameters)
        {
            Dictionary<string, object?> properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            List<string> required = new List<string>();

            foreach ((string jsonName, Type type, bool isRequired) in parameters)
            {
                properties[jsonName] = GenerateForType(type);
                if (isRequired)
                    required.Add(jsonName);
            }

            return WithDefinitions(ObjectSchema(properties, required));
        }

        private static Dictionary<string, object?> ObjectSchema(Dictionary<string, object?> properties, List<string> required)
        {
            Dictionary<string, object?> schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }

        private Dictionary<string, object?> WithDefinitions(Dictionary<string, object?> root)
        {
            if (_definitions.Count > 0)
                root[DefinitionsKeyword] = _definitions;

            return root;
        }

        private Dictionary<string, object?> GenerateForType(Type type)
        {
            type = Unwrap(type);

            return ShapeOf(type) switch
            {
                TypeShape.Fixed fixedShape => fixedShape.Schema,
                TypeShape.ArrayOf array => new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = GenerateForType(array.Items)
                },
                TypeShape.MapOf map => new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = GenerateForType(map.Values)
                },
                TypeShape.ObjectOf => IsOnCycle(type)
                    ? new Dictionary<string, object?> { ["$ref"] = $"#/{DefinitionsKeyword}/{Define(type)}" }
                    : GenerateObjectSchema(type),
                TypeShape other => throw UnhandledShape(other)
            };
        }

        /// <summary>
        /// Writes <paramref name="type"/>'s schema under <c>$defs</c> the first time it is asked for, and returns its key.
        /// </summary>
        private string Define(Type type)
        {
            if (_definitionKeys.TryGetValue(type, out string? key))
                return key;

            string baseKey = DefinitionKeyName(type);
            key = baseKey;
            for (int suffix = 2; _definitions.ContainsKey(key); suffix++)
                key = $"{baseKey}{suffix}";

            _definitionKeys[type] = key;

            // Reserve the slot before generating so a type is listed ahead of the cyclic types it reaches,
            // and so its own references inside the body find its key instead of defining it again.
            _definitions[key] = null;
            _definitions[key] = GenerateObjectSchema(type);
            return key;
        }

        /// <summary>
        /// A type's simple name, with a generic type's arguments written out (<c>Node&lt;int&gt;</c> is <c>NodeOfInt32</c>).
        /// </summary>
        private static string DefinitionKeyName(Type type)
        {
            if (type.IsArray)
                return $"ArrayOf{DefinitionKeyName(type.GetElementType()!)}";

            if (!type.IsGenericType)
                return type.Name;

            int arityMarker = type.Name.IndexOf('`');
            string name = arityMarker < 0 ? type.Name : type.Name[..arityMarker];
            return $"{name}Of{string.Join("And", type.GetGenericArguments().Select(DefinitionKeyName))}";
        }

        private bool IsOnCycle(Type type)
        {
            if (!_onCycle.TryGetValue(type, out bool onCycle))
            {
                onCycle = EmbedsBeneath(type, type, new HashSet<Type>());
                _onCycle[type] = onCycle;
            }

            return onCycle;
        }

        /// <summary>
        /// Whether <paramref name="target"/>'s schema is embedded anywhere beneath <paramref name="from"/>'s.
        /// </summary>
        private static bool EmbedsBeneath(Type from, Type target, HashSet<Type> visited)
        {
            foreach (Type embedded in EmbeddedTypes(from).Select(Unwrap))
            {
                if (embedded == target)
                    return true;

                if (visited.Add(embedded) && EmbedsBeneath(embedded, target, visited))
                    return true;
            }

            return false;
        }

        private static IEnumerable<Type> EmbeddedTypes(Type type) => ShapeOf(type) switch
        {
            TypeShape.Fixed => [],
            TypeShape.ArrayOf array => [array.Items],
            TypeShape.MapOf map => [map.Values],
            TypeShape.ObjectOf => SchemaProperties(type).Select(p => p.PropertyType),
            TypeShape other => throw UnhandledShape(other)
        };

        // The compiler can't see that TypeShape's cases are closed, so each walk over them ends with this.
        private static InvalidOperationException UnhandledShape(TypeShape shape)
            => new InvalidOperationException($"Schema generation has no case for {shape.GetType().Name}.");

        private static TypeShape ShapeOf(Type type)
        {
            if (type == typeof(JsonElement) || type == typeof(object))
                return new TypeShape.Fixed(new Dictionary<string, object?>());

            if (type.IsEnum)
                return new TypeShape.Fixed(new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = Enum.GetNames(type)
                });

            if (type == typeof(string))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "string" });

            if (type == typeof(bool))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "boolean" });

            if (type == typeof(Guid))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uuid" });

            if (type == typeof(Uri))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uri" });

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time" });

            if (IsIntegral(type))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "integer" });

            if (IsNumber(type))
                return new TypeShape.Fixed(new Dictionary<string, object?> { ["type"] = "number" });

            // Every IDictionary is also an IEnumerable of key/value pairs, and this check runs before the dictionary
            // one, so dictionaries take this branch and are described as arrays of {key, value} objects.
            if (TryGetEnumerableElementType(type, out Type? elementType))
                return new TypeShape.ArrayOf(elementType);

            if (TryGetStringDictionaryValueType(type, out Type? valueType))
                return new TypeShape.MapOf(valueType);

            return new TypeShape.ObjectOf();
        }

        // Every property with a public getter is listed, including a computed get-only one (int Count => Items.Count)
        // that deserialization never sets.
        private static PropertyInfo[] SchemaProperties(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetMethod != null && p.GetMethod.IsPublic)
                .Where(p => p.GetIndexParameters().Length == 0)
                .ToArray();

        private Dictionary<string, object?> GenerateObjectSchema(Type type)
        {
            Dictionary<string, object?> properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            List<string> required = new List<string>();

            foreach (PropertyInfo prop in SchemaProperties(type))
            {
                string jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                               ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);

                properties[jsonName] = GenerateForType(prop.PropertyType);

                bool requiredByAttr = prop.GetCustomAttribute<RequiredAttribute>() != null;
                bool requiredByNullability = !AllowsNull(prop);
                if (requiredByAttr || requiredByNullability)
                    required.Add(jsonName);
            }

            Dictionary<string, object?> schema = ObjectSchema(properties, required);

            string? typeDescription = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(typeDescription))
                schema["description"] = typeDescription;

            return schema;
        }

        private static bool IsIntegral(Type t) => Type.GetTypeCode(t) switch
        {
            TypeCode.Byte => true,
            TypeCode.SByte => true,
            TypeCode.Int16 => true,
            TypeCode.UInt16 => true,
            TypeCode.Int32 => true,
            TypeCode.UInt32 => true,
            TypeCode.Int64 => true,
            TypeCode.UInt64 => true,
            _ => false
        };

        private static bool IsNumber(Type t) => Type.GetTypeCode(t) switch
        {
            TypeCode.Single => true,
            TypeCode.Double => true,
            TypeCode.Decimal => true,
            _ => false
        };

        private static bool TryGetEnumerableElementType(Type type, out Type elementType)
        {
            if (type == typeof(string))
            {
                elementType = typeof(void);
                return false;
            }

            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            Type? enumerable = type.GetInterfaces()
                .Concat([type])
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (enumerable != null)
            {
                elementType = enumerable.GetGenericArguments()[0];
                return true;
            }

            elementType = typeof(void);
            return false;
        }

        private static bool TryGetStringDictionaryValueType(Type type, out Type valueType)
        {
            Type? dict = type.GetInterfaces()
                .Concat([type])
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            if (dict != null)
            {
                Type[] args = dict.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }
            }

            valueType = typeof(void);
            return false;
        }

        /// <summary>
        /// How a type is described in a schema. Generating a schema and finding cycles both walk a type
        /// through this, so the two always agree on which types a schema embeds.
        /// </summary>
        private abstract record TypeShape
        {
            public sealed record Fixed(Dictionary<string, object?> Schema) : TypeShape;
            public sealed record ArrayOf(Type Items) : TypeShape;
            public sealed record MapOf(Type Values) : TypeShape;
            public sealed record ObjectOf : TypeShape;
        }
    }
}
