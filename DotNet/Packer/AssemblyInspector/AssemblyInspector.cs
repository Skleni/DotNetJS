﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using static Packer.TextUtilities;
using static Packer.TypeUtilities;

namespace Packer;

internal class AssemblyInspector : IDisposable
{
    public List<Assembly> Assemblies { get; } = new();
    public List<Method> Methods { get; } = new();
    public List<Type> Types { get; } = new();

    private readonly List<string> warnings = new();
    private readonly List<MetadataLoadContext> contexts = new();
    private readonly TypeConverter typeConverter;

    public AssemblyInspector (NamespaceBuilder namespaceBuilder)
    {
        typeConverter = new TypeConverter(namespaceBuilder);
    }

    public void InspectInDirectory (string directory)
    {
        var assemblyPaths = Directory.GetFiles(directory, "*.dll");
        var context = CreateLoadContext(assemblyPaths);
        foreach (var assemblyPath in assemblyPaths)
            try { InspectAssembly(assemblyPath, context); }
            catch (Exception e) { AddSkippedAssemblyWarning(assemblyPath, e); }
        Types.AddRange(typeConverter.GetObjectTypes());
        contexts.Add(context);
    }

    public void Report (TaskLoggingHelper logger)
    {
        logger.LogMessage(MessageImportance.Normal, "DotNetJS assembly inspection result:");
        logger.LogMessage(MessageImportance.Normal, JoinLines($"Discovered {Assemblies.Count} assemblies:",
            JoinLines(Assemblies.Select(a => a.Name))));
        logger.LogMessage(MessageImportance.Normal, JoinLines($"Discovered {Methods.Count} JS methods:",
            JoinLines(Methods.Select(m => m.ToString()))));

        foreach (var warning in warnings)
            logger.LogWarning(warning);
    }

    public void Dispose ()
    {
        foreach (var context in contexts)
            context.Dispose();
        contexts.Clear();
    }

    private MetadataLoadContext CreateLoadContext (IEnumerable<string> assemblyPaths)
    {
        var resolver = new PathAssemblyResolver(assemblyPaths);
        return new MetadataLoadContext(resolver);
    }

    private void InspectAssembly (string assemblyPath, MetadataLoadContext context)
    {
        var name = Path.GetFileName(assemblyPath);
        var base64 = ReadBase64(assemblyPath);
        Assemblies.Add(new Assembly(name, base64));
        if (!ShouldIgnoreAssembly(assemblyPath))
            InspectMethods(context.LoadFromAssemblyPath(assemblyPath));
    }

    private void AddSkippedAssemblyWarning (string assemblyPath, Exception exception)
    {
        var assemblyName = Path.GetFileName(assemblyPath);
        var message = $"Failed to inspect '{assemblyName}' assembly; " +
                      $"affected methods won't be available in JavaScript. Error: {exception.Message}";
        warnings.Add(message);
    }

    private void InspectMethods (System.Reflection.Assembly assembly)
    {
        foreach (var method in GetStaticMethods(assembly))
        foreach (var attribute in method.CustomAttributes)
            if (attribute.AttributeType.Name == Attributes.Invokable)
                Methods.Add(CreateMethod(method, MethodType.Invokable));
            else if (attribute.AttributeType.Name == Attributes.Function)
                Methods.Add(CreateMethod(method, MethodType.Function));
    }

    private Method CreateMethod (MethodInfo info, MethodType type) => new() {
        Name = info.Name,
        Assembly = GetAssemblyName(info.DeclaringType),
        Arguments = info.GetParameters().Select(CreateArgument).ToArray(),
        ReturnType = typeConverter.ToTypeScript(info.ReturnType),
        Async = IsAwaitable(info.ReturnType),
        Type = type
    };

    private Argument CreateArgument (ParameterInfo info) => new() {
        Name = info.Name == "function" ? "fn" : info.Name,
        Type = typeConverter.ToTypeScript(info.ParameterType)
    };

    private static string ReadBase64 (string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return Convert.ToBase64String(bytes);
    }

    private static IEnumerable<MethodInfo> GetStaticMethods (System.Reflection.Assembly assembly)
    {
        var exported = assembly.GetExportedTypes();
        return exported.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static));
    }
}
