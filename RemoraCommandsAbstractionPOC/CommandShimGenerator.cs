﻿using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RemoraCommandsAbstractionPOC;

[Generator]
public class CommandShimGenerator : ISourceGenerator
{
    private const string CommandGroupName = ": CommandGroup";

    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var semanticModel = context.Compilation.GetSemanticModel(tree);

            var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            var commandGroup = context.Compilation.GetTypeByMetadataName("Remora.Commands.Groups.CommandGroup");

            if (classDeclaration is null)
            {
                continue; // No class to check.
            }

            if (!semanticModel.GetDeclaredSymbol(classDeclaration).BaseType.Equals(commandGroup))
            {
                continue; // Not a command group.
            }
            
            var builder = new CodeWriter();

            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("using System;");
            builder.AppendLine("using Remora.Results;");
            builder.AppendLine("using Remora.Commands;");
            builder.AppendLine("using System.Collections.Generic;");

            var emittedNamespace = false;

            using var classWriter = builder.CreateChildWriter();

            foreach (var node in root.DescendantNodes())
            {
                if (node is ClassDeclarationSyntax cds)
                {
                    if (!cds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                        continue;

                    if (!cds.BaseList?.Types.Any() ?? true)
                        break; // Class doesn't inherit anything.

                    var symbol = semanticModel.GetDeclaredSymbol(cds);
                    
                    if (!emittedNamespace && symbol.ContainingNamespace.IsGlobalNamespace)
                    {
                        classWriter.AppendLine($"namespace {symbol.ContainingNamespace.Name}");
                        classWriter.AppendLine("{");
                        emittedNamespace = true;
                    }

                    // We're in a command, but this class in particular isn't a command group.
                    if (!symbol.BaseType.Equals(symbol))
                        break;

                    classWriter.AppendLine($"class {cds.Identifier}");
                    classWriter.AppendLine("{");
                }

                if (node is MethodDeclarationSyntax mds)
                {
                    var symbol = context.Compilation.GetSemanticModel(tree).GetDeclaredSymbol(mds);

                    if (symbol.DeclaredAccessibility.HasFlag(Accessibility.Public))
                        continue;

                    // Check if the return type is a task of sorts
                    if (symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is not ("global::System.Threading.Tasks.Task" or "global::System.Threading.Tasks.ValueTask"))
                        continue;

                    // Check if the method has a command attribute
                    var attributes = symbol.GetAttributes();

                    var commandAttribute = context.Compilation.GetTypeByMetadataName("Remora.Commands.Attributes.CommandAttribute")!;

                    if (!attributes.Any(a => a.AttributeClass.Equals(commandAttribute)))
                        continue;

                    using var methodWriter = classWriter.CreateChildWriter();

                    RipAttributes(methodWriter, attributes);
                    ShimMethod(methodWriter, symbol);
                }

                classWriter.AppendLine('}');
            }

            context.AddSource("RemoraCommandsAbstractionPOC.Generated", builder.ToString());

        }
    }

    private static void ShimMethod(CodeWriter methodWriter, IMethodSymbol symbol)
    {
        methodWriter.Append("public async Task<Result> ");
        methodWriter.Append(symbol.Name + "_Shim");

        if (!symbol.Parameters.Any() || symbol.Parameters.All(p => !p.GetAttributes().Any()))
        {
            methodWriter.Append("()");
        }
        else
        {
            methodWriter.AppendLine('(');

            using var parameterWriter = methodWriter.CreateChildWriter();

            for (var i = 0; i < symbol.Parameters.Length; i++)
            {
                var parameter = symbol.Parameters[i];
                RipAttributes(parameterWriter, parameter.GetAttributes());

                if (parameter.IsParams)
                {
                    parameterWriter.Append("params ");
                }

                parameterWriter.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

                if (parameter.Type.TypeKind is TypeKind.Array)
                {
                    parameterWriter.Append("[]");
                }

                parameterWriter.Append(parameter.Name);

                if (parameter.IsOptional)
                {
                    parameterWriter.Append(" = ");
                    parameterWriter.Append(parameter.ExplicitDefaultValue.ToString());
                }

                if (symbol.Parameters.Length > 1 && i < symbol.Parameters.Length - 1)
                    parameterWriter.Append(',');

                parameterWriter.AppendLine(' ');
            }

            methodWriter.AppendLine(')');

            methodWriter.AppendLine('{');

            using var bodyWriter = methodWriter.CreateChildWriter();

            bodyWriter.AppendLine($"await {symbol.Name}({string.Join(", ", symbol.Parameters.Select(p => p.Name))}");
            bodyWriter.AppendLine(");");

            bodyWriter.AppendLine("return Result.Success();");

            methodWriter.AppendLine('}');
        }
    }

    private static void RipAttributes(CodeWriter writer, ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            writer.Append('[');

            var attributeName = attribute.AttributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            writer.Append(attributeName);

            if (attribute.ConstructorArguments.Any())
            {
                var stringifiedArguments = new List<string>();

                foreach (var argument in attribute.ConstructorArguments)
                {
                    // Check if the argument is an enum
                    if (argument.Kind is TypedConstantKind.Enum)
                    {
                        var enumName = argument.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var enumValue = argument.Value.ToString();
                        stringifiedArguments.Add($"{enumName}.{enumValue}");
                    }
                    else if (argument.Kind is TypedConstantKind.Array)
                    {
                       stringifiedArguments.Add(string.Join(", ", argument.Values));
                    }
                    else
                    {
                        stringifiedArguments.Add(argument.Value.ToString());
                    }
                }

                foreach (var argument in attribute.NamedArguments)
                {
                    // Check if the argument is an enum
                    if (argument.Value.Kind is TypedConstantKind.Enum)
                    {
                        var enumName = argument.Value.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var enumValue = argument.Value.Value.ToString();
                        stringifiedArguments.Add($"{argument.Key} = {enumName}.{enumValue}");
                    }
                    else
                    {
                        stringifiedArguments.Add($"{argument.Key} = {argument.Value.ToString()}");
                    }
                }

                writer.Append('(');
                writer.Append($"{attributeName}({string.Join(", ", stringifiedArguments)})");
                writer.Append(')');
            }

            writer.AppendLine(']');
        }
    }
}