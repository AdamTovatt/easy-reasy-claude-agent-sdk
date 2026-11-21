using EasyReasy.Claude.AgentSdk.Mcp;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Tests that tools can be registered on separate builders from many threads at once. Tool
/// registration reads nullability annotations, and the reader that does it caches what it has read
/// in a plain dictionary. Shared between threads, that cache is corrupted by concurrent writes and
/// then fails every later registration in the process.
/// </summary>
public sealed class McpSdkServerBuilderConcurrencyTests
{
    // A reader shared between registrations failed both tests on every run at this count, in 1 to 7
    // seconds each. Lowering it without showing that a shared reader still fails can leave the tests
    // unable to fail.
    private const int ToolCount = 4000;

    [Fact]
    public async Task Tool_RegisteredOnManyBuildersInParallel_WithAnnotatedParameters_ProducesCorrectSchemas()
    {
        // Each handler is declared on its own annotated type, so each registration reads a member
        // the nullability reader has not cached yet, and every one of them writes to its cache.
        AnnotatedHandler[] handlers = AnnotatedTypeFactory.CreateParameterHandlers(ToolCount);

        await AssertRegisteredInParallelWithCorrectSchemasAsync(handlers);
    }

    [Fact]
    public async Task Tool_RegisteredOnManyBuildersInParallel_WithAnnotatedArgsObjects_ProducesCorrectSchemas()
    {
        // Each handler takes its own annotated args type, and the handlers' declaring types carry no
        // annotation, so the cache writes happen while the input schema is generated from the args
        // type's properties.
        AnnotatedHandler[] handlers = AnnotatedTypeFactory.CreateArgsObjectHandlers(ToolCount);

        await AssertRegisteredInParallelWithCorrectSchemasAsync(handlers);
    }

    /// <summary>
    /// Registers every handler on its own builder in parallel, then checks that each schema requires
    /// "value" exactly when its type was annotated not-null. The annotations alternate, so a reader
    /// that hands back another member's answer gets it wrong either way.
    /// </summary>
    private static async Task AssertRegisteredInParallelWithCorrectSchemasAsync(AnnotatedHandler[] handlers)
    {
        McpServerRegistry[] registries = new McpServerRegistry[handlers.Length];
        ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) };

        Parallel.For(0, handlers.Length, options, i =>
            registries[i] = McpServers.Sdk($"server{i}", s => s.Tool("tool", handlers[i].Handler)));

        for (int i = 0; i < handlers.Length; i++)
        {
            McpSdkServerConfig config = Assert.IsType<McpSdkServerConfig>(registries[i][$"server{i}"]);
            IReadOnlyList<McpToolDefinition> tools = await config.Handlers!.ListTools!(CancellationToken.None);
            JsonElement schema = tools[0].InputSchema!.Value;

            if (handlers[i].ValueIsNullable)
                Assert.False(schema.TryGetProperty("required", out _), $"Tool {i} is annotated nullable but its schema requires a value.");
            else
                Assert.Equal(["value"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        }
    }

    private sealed record AnnotatedHandler(Delegate Handler, bool ValueIsNullable);

    /// <summary>
    /// Emits fresh types annotated the way the C# compiler annotates nullable-enabled code, so each
    /// test gets members that no nullability reader in the process has seen before.
    /// </summary>
    private static class AnnotatedTypeFactory
    {
        private const byte NotNullAnnotation = 1;
        private const byte NullableAnnotation = 2;

        public static AnnotatedHandler[] CreateParameterHandlers(int count)
        {
            ModuleBuilder module = CreateModule();
            ConstructorInfo annotationConstructor = DefineNullableContextAttribute(module);

            AnnotatedHandler[] handlers = new AnnotatedHandler[count];
            for (int i = 0; i < count; i++)
            {
                bool nullable = i % 2 == 1;
                TypeBuilder holder = module.DefineType($"ParameterHolder{i}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                holder.SetCustomAttribute(Annotation(annotationConstructor, nullable));
                DefineReturnNullMethod(holder, typeof(string));

                MethodInfo method = holder.CreateType().GetMethod("ReturnNull")!;
                handlers[i] = new AnnotatedHandler(method.CreateDelegate(typeof(Func<string, string>)), nullable);
            }

            return handlers;
        }

        public static AnnotatedHandler[] CreateArgsObjectHandlers(int count)
        {
            ModuleBuilder module = CreateModule();
            ConstructorInfo annotationConstructor = DefineNullableContextAttribute(module);

            AnnotatedHandler[] handlers = new AnnotatedHandler[count];
            for (int i = 0; i < count; i++)
            {
                bool nullable = i % 2 == 1;
                TypeBuilder args = module.DefineType($"Args{i}", TypeAttributes.Public | TypeAttributes.Sealed);
                args.SetCustomAttribute(Annotation(annotationConstructor, nullable));
                args.DefineDefaultConstructor(MethodAttributes.Public);
                DefineStatelessProperty(args, "Value", typeof(string));
                Type argsType = args.CreateType();

                TypeBuilder holder = module.DefineType($"ArgsHolder{i}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                DefineReturnNullMethod(holder, argsType);

                MethodInfo method = holder.CreateType().GetMethod("ReturnNull")!;
                handlers[i] = new AnnotatedHandler(method.CreateDelegate(Expression.GetFuncType(argsType, typeof(string))), nullable);
            }

            return handlers;
        }

        private static ModuleBuilder CreateModule()
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"AnnotatedTypes{Guid.NewGuid():N}"),
                AssemblyBuilderAccess.Run);
            return assembly.DefineDynamicModule("AnnotatedTypes");
        }

        private static CustomAttributeBuilder Annotation(ConstructorInfo annotationConstructor, bool nullable)
            => new CustomAttributeBuilder(annotationConstructor, [nullable ? NullableAnnotation : NotNullAnnotation]);

        /// <summary>
        /// The compiler emits its own copy of NullableContextAttribute into each assembly, and the
        /// nullability reader recognises it by name, so the dynamic module gets its own copy too.
        /// </summary>
        private static ConstructorInfo DefineNullableContextAttribute(ModuleBuilder module)
        {
            TypeBuilder attribute = module.DefineType(
                "System.Runtime.CompilerServices.NullableContextAttribute",
                TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(Attribute));

            ConstructorBuilder constructor = attribute.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                [typeof(byte)]);
            constructor.DefineParameter(1, ParameterAttributes.None, "flag");
            ILGenerator il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
            il.Emit(OpCodes.Ret);

            return attribute.CreateType().GetConstructor([typeof(byte)])!;
        }

        private static void DefineReturnNullMethod(TypeBuilder holder, Type parameterType)
        {
            MethodBuilder method = holder.DefineMethod(
                "ReturnNull",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(string),
                [parameterType]);
            method.DefineParameter(1, ParameterAttributes.None, "value");
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Defines a property whose getter returns null and whose setter discards the value. It needs
        /// the setter because the schema reads a property's write nullability, which a getter-only
        /// property does not have.
        /// </summary>
        private static void DefineStatelessProperty(TypeBuilder type, string name, Type propertyType)
        {
            const MethodAttributes accessorAttributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

            MethodBuilder getter = type.DefineMethod($"get_{name}", accessorAttributes, propertyType, Type.EmptyTypes);
            ILGenerator getterIl = getter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldnull);
            getterIl.Emit(OpCodes.Ret);

            MethodBuilder setter = type.DefineMethod($"set_{name}", accessorAttributes, typeof(void), [propertyType]);
            setter.GetILGenerator().Emit(OpCodes.Ret);

            PropertyBuilder property = type.DefineProperty(name, PropertyAttributes.None, propertyType, null);
            property.SetGetMethod(getter);
            property.SetSetMethod(setter);
        }
    }
}
