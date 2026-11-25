using System.Text.Json;

namespace AgentFleet;

/// <summary>
/// The compiler options of a project's tsconfig.json as command-line text, so the TypeScript compiler can check files the project's own
/// type check does not read (a tsconfig.json whose "include" lists only the source folder) the way the project would. The text goes into
/// a shell command that runs on a worker and the project file is not trusted, so nothing is copied that is not known: an option this
/// class does not list, a value with a character outside letters, digits and "_ . , @ / -", or a value of the wrong kind means the
/// options cannot be reproduced, and so does a file that builds on other files ("extends", "references"). The answer is then null.
/// </summary>
internal static class TypecheckFlags
{
    private enum OptionKind
    {
        /// <summary>A word: "--target ES2022".</summary>
        Text,

        /// <summary>A list of words joined by commas: "--lib ES2022,DOM". An empty list is left out.</summary>
        List,

        /// <summary>A switch: "--strict" when on, "--strict false" when off.</summary>
        Switch
    }

    private static readonly JsonDocumentOptions ProjectFileOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // The options that are reproduced, in the order they are written. Where the project file puts them does not matter.
    private static readonly (string Name, OptionKind Kind)[] Reproduced =
    [
        ("target", OptionKind.Text),
        ("lib", OptionKind.List),
        ("module", OptionKind.Text),
        ("moduleResolution", OptionKind.Text),
        ("jsx", OptionKind.Text),
        ("moduleDetection", OptionKind.Text),
        ("strict", OptionKind.Switch),
        ("esModuleInterop", OptionKind.Switch),
        ("skipLibCheck", OptionKind.Switch),
        ("allowSyntheticDefaultImports", OptionKind.Switch),
        ("resolveJsonModule", OptionKind.Switch),
        ("isolatedModules", OptionKind.Switch),
        ("verbatimModuleSyntax", OptionKind.Switch),
        ("noUncheckedIndexedAccess", OptionKind.Switch),
        ("noImplicitAny", OptionKind.Switch),
        ("strictNullChecks", OptionKind.Switch),
        ("noImplicitReturns", OptionKind.Switch),
        ("noFallthroughCasesInSwitch", OptionKind.Switch),
        ("noUnusedLocals", OptionKind.Switch),
        ("noUnusedParameters", OptionKind.Switch),
        ("exactOptionalPropertyTypes", OptionKind.Switch),
        ("allowJs", OptionKind.Switch),
        ("checkJs", OptionKind.Switch),
        ("forceConsistentCasingInFileNames", OptionKind.Switch),
        ("allowImportingTsExtensions", OptionKind.Switch),
        ("strictFunctionTypes", OptionKind.Switch),
        ("strictBindCallApply", OptionKind.Switch),
        ("strictPropertyInitialization", OptionKind.Switch),
        ("noImplicitThis", OptionKind.Switch),
        ("useUnknownInCatchVariables", OptionKind.Switch),
        ("alwaysStrict", OptionKind.Switch),
        ("noImplicitOverride", OptionKind.Switch),
        ("noPropertyAccessFromIndexSignature", OptionKind.Switch),
        ("erasableSyntaxOnly", OptionKind.Switch),
        ("experimentalDecorators", OptionKind.Switch),
        ("useDefineForClassFields", OptionKind.Switch),
        ("allowUnreachableCode", OptionKind.Switch),
        ("allowUnusedLabels", OptionKind.Switch),
        ("types", OptionKind.List)
    ];

    // Options that only matter when the compiler writes files. A check never writes any, so they are left out whatever they hold.
    private static readonly HashSet<string> EmitOnly = new(StringComparer.Ordinal)
    {
        "noEmit", "outDir", "rootDir", "declaration", "declarationMap", "sourceMap", "inlineSourceMap", "composite", "incremental",
        "tsBuildInfoFile", "removeComments", "importHelpers", "pretty", "newLine", "emitDeclarationOnly", "isolatedDeclarations"
    };

    /// <summary>
    /// The compiler options of a tsconfig.json as command-line text (empty when the file sets none that matter), or null when they cannot
    /// be reproduced safely: the text is blank or not JSON, "compilerOptions" is missing or not an object, the file has "extends" or
    /// "references", an option is not in the list above, or a value is not plain text of the right kind. Comments and trailing commas are
    /// allowed, as in the compiler's own reading of the file.
    /// </summary>
    internal static string? FromTsconfig(string? tsconfigText)
    {
        if (string.IsNullOrWhiteSpace(tsconfigText))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tsconfigText, ProjectFileOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement? compilerOptions = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "extends" or "references")
                {
                    return null;
                }

                if (property.Name == "compilerOptions")
                {
                    compilerOptions = property.Value;
                }
            }

            return compilerOptions is { ValueKind: JsonValueKind.Object } options ? Write(options) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string that is not valid text (a lone surrogate): either way there is nothing safe to copy.
            return null;
        }
    }

    private static string? Write(JsonElement compilerOptions)
    {
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty option in compilerOptions.EnumerateObject())
        {
            if (EmitOnly.Contains(option.Name))
            {
                continue;
            }

            if (!Reproduced.Any(known => known.Name == option.Name))
            {
                return null;
            }

            // An option written twice takes its last value, as the compiler's own reading of the file does.
            given[option.Name] = option.Value;
        }

        var flags = new List<string>();
        foreach ((string name, OptionKind kind) in Reproduced)
        {
            if (!given.TryGetValue(name, out JsonElement value))
            {
                continue;
            }

            switch (kind)
            {
                case OptionKind.Text:
                    if (value.ValueKind != JsonValueKind.String || !IsPlain(value.GetString()))
                    {
                        return null;
                    }

                    flags.Add($"--{name} {value.GetString()}");
                    break;

                case OptionKind.List:
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    var items = new List<string>();
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || !IsPlain(item.GetString()))
                        {
                            return null;
                        }

                        items.Add(item.GetString()!);
                    }

                    if (items.Count > 0)
                    {
                        flags.Add($"--{name} {string.Join(',', items)}");
                    }

                    break;

                default:
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return null;
                    }

                    flags.Add(value.ValueKind == JsonValueKind.True ? $"--{name}" : $"--{name} false");
                    break;
            }
        }

        return string.Join(' ', flags);
    }

    // Letters, digits and "_ . , @ / -": nothing a shell or the compiler's own argument reader treats as more than part of a word.
    private static bool IsPlain(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or ',' or '@' or '/' or '-');
}
