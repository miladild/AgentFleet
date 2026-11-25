namespace AgentFleet.Tests;

/// <summary>
/// A project's own compiler options as command-line text, so files its type check does not read can be checked the way the project
/// would check them. The text goes into a shell command and the project file is not trusted, so only options this list knows, with
/// plain values of the right kind, are reproduced. Anything else gives no flags at all.
/// </summary>
public sealed class TypecheckFlagsTests
{
    private static string? Flags(string compilerOptions) =>
        TypecheckFlags.FromTsconfig("{ \"compilerOptions\": { " + compilerOptions + " } }");

    [Fact]
    public void A_project_file_gives_its_options_in_the_order_of_the_list_not_the_order_of_the_file()
    {
        const string text = """
            {
              "compilerOptions": {
                "types": ["node"],
                "noEmit": true,
                "skipLibCheck": true,
                "esModuleInterop": true,
                "strict": true,
                "moduleResolution": "bundler",
                "module": "ESNext",
                "lib": ["ES2022"],
                "target": "ES2022"
              },
              "include": ["src/server"]
            }
            """;

        Assert.Equal("--target ES2022 --lib ES2022 --module ESNext --moduleResolution bundler --strict --esModuleInterop --skipLibCheck --types node",
            TypecheckFlags.FromTsconfig(text));
    }

    [Fact]
    public void Every_known_option_keeps_one_fixed_place()
    {
        string[] switches =
        [
            "strict", "esModuleInterop", "skipLibCheck", "allowSyntheticDefaultImports", "resolveJsonModule", "isolatedModules",
            "verbatimModuleSyntax", "noUncheckedIndexedAccess", "noImplicitAny", "strictNullChecks", "noImplicitReturns",
            "noFallthroughCasesInSwitch", "noUnusedLocals", "noUnusedParameters", "exactOptionalPropertyTypes", "allowJs", "checkJs",
            "forceConsistentCasingInFileNames", "allowImportingTsExtensions", "strictFunctionTypes", "strictBindCallApply",
            "strictPropertyInitialization", "noImplicitThis", "useUnknownInCatchVariables", "alwaysStrict", "noImplicitOverride",
            "noPropertyAccessFromIndexSignature", "erasableSyntaxOnly", "experimentalDecorators", "useDefineForClassFields",
            "allowUnreachableCode", "allowUnusedLabels"
        ];
        string reversed = string.Join(", ", switches.Reverse().Select(name => $"\"{name}\": true")) +
            ", \"types\": [\"node\", \"jest\"], \"moduleDetection\": \"force\", \"jsx\": \"react-jsx\", \"moduleResolution\": \"bundler\", " +
            "\"module\": \"ESNext\", \"lib\": [\"ES2022\", \"DOM\"], \"target\": \"ES2022\"";

        Assert.Equal(
            "--target ES2022 --lib ES2022,DOM --module ESNext --moduleResolution bundler --jsx react-jsx --moduleDetection force " +
            string.Join(' ', switches.Select(name => $"--{name}")) + " --types node,jest",
            Flags(reversed));
    }

    [Fact]
    public void Comments_and_trailing_commas_in_the_project_file_are_read()
    {
        const string text = "{\n  // the project's options\n  \"compilerOptions\": {\n    /* checks */ \"strict\": true,\n    \"target\": \"ES2022\", // newest\n  },\n}";

        Assert.Equal("--target ES2022 --strict", TypecheckFlags.FromTsconfig(text));
    }

    [Fact]
    public void A_switch_that_is_off_is_written_with_false_so_the_compiler_does_not_turn_it_on()
    {
        Assert.Equal("--strict false --noImplicitAny false", Flags("\"noImplicitAny\": false, \"strict\": false"));
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("esModuleInterop")]
    [InlineData("skipLibCheck")]
    [InlineData("allowSyntheticDefaultImports")]
    [InlineData("resolveJsonModule")]
    [InlineData("isolatedModules")]
    [InlineData("verbatimModuleSyntax")]
    [InlineData("noUncheckedIndexedAccess")]
    [InlineData("noImplicitAny")]
    [InlineData("strictNullChecks")]
    [InlineData("noImplicitReturns")]
    [InlineData("noFallthroughCasesInSwitch")]
    [InlineData("noUnusedLocals")]
    [InlineData("noUnusedParameters")]
    [InlineData("exactOptionalPropertyTypes")]
    [InlineData("allowJs")]
    [InlineData("checkJs")]
    [InlineData("forceConsistentCasingInFileNames")]
    [InlineData("allowImportingTsExtensions")]
    [InlineData("strictFunctionTypes")]
    [InlineData("strictBindCallApply")]
    [InlineData("strictPropertyInitialization")]
    [InlineData("noImplicitThis")]
    [InlineData("useUnknownInCatchVariables")]
    [InlineData("alwaysStrict")]
    [InlineData("noImplicitOverride")]
    [InlineData("noPropertyAccessFromIndexSignature")]
    [InlineData("erasableSyntaxOnly")]
    [InlineData("experimentalDecorators")]
    [InlineData("useDefineForClassFields")]
    [InlineData("allowUnreachableCode")]
    [InlineData("allowUnusedLabels")]
    public void A_known_switch_is_written_on_or_off(string name)
    {
        Assert.Equal($"--{name}", Flags($"\"{name}\": true"));
        Assert.Equal($"--{name} false", Flags($"\"{name}\": false"));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("module")]
    [InlineData("moduleResolution")]
    [InlineData("jsx")]
    [InlineData("moduleDetection")]
    public void A_known_text_option_is_written_with_its_value(string name) =>
        Assert.Equal($"--{name} Some-Value_1.x/y@z", Flags($"\"{name}\": \"Some-Value_1.x/y@z\""));

    [Fact]
    public void Options_that_only_matter_for_emitting_are_dropped_whatever_they_hold()
    {
        const string emit = "\"noEmit\": true, \"outDir\": \"dist\", \"rootDir\": \"src\", \"declaration\": true, \"declarationMap\": true, " +
            "\"sourceMap\": true, \"inlineSourceMap\": false, \"composite\": true, \"incremental\": true, \"tsBuildInfoFile\": \".cache/build.tsbuildinfo\", " +
            "\"removeComments\": true, \"importHelpers\": true, \"pretty\": false, \"newLine\": \"lf\", \"emitDeclarationOnly\": true, \"isolatedDeclarations\": true";

        Assert.Equal(string.Empty, Flags(emit));
        Assert.Equal("--strict", Flags(emit + ", \"strict\": true"));
    }

    [Fact]
    public void A_project_file_with_no_options_gives_an_empty_text_not_null()
    {
        Assert.Equal(string.Empty, Flags(string.Empty));
        Assert.Equal(string.Empty, TypecheckFlags.FromTsconfig("{ \"compilerOptions\": {}, \"include\": [\"src\"], \"exclude\": [\"dist\"] }"));
    }

    [Fact]
    public void An_empty_list_is_left_out()
    {
        Assert.Equal("--strict", Flags("\"lib\": [], \"types\": [], \"strict\": true"));
    }

    [Fact]
    public void An_option_written_twice_takes_the_last_value_as_the_compiler_does()
    {
        Assert.Equal("--strict false", Flags("\"strict\": true, \"strict\": false"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("not json")]
    [InlineData("{ \"compilerOptions\": { \"strict\": true ")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"include\": [\"src\"] }")]
    [InlineData("{ \"compilerOptions\": [] }")]
    [InlineData("{ \"compilerOptions\": \"strict\" }")]
    [InlineData("{ \"compilerOptions\": null }")]
    [InlineData("{ \"extends\": \"./tsconfig.base.json\", \"compilerOptions\": { \"strict\": true } }")]
    [InlineData("{ \"compilerOptions\": { \"strict\": true }, \"extends\": [\"./a.json\", \"./b.json\"] }")]
    [InlineData("{ \"references\": [{ \"path\": \"./lib\" }], \"compilerOptions\": { \"strict\": true } }")]
    public void A_project_file_that_cannot_be_read_or_builds_on_other_files_gives_no_flags(string? text) =>
        Assert.Null(TypecheckFlags.FromTsconfig(text));

    [Theory]
    [InlineData("\"paths\": { \"@/*\": [\"src/*\"] }")]
    [InlineData("\"baseUrl\": \".\"")]
    [InlineData("\"rootDirs\": [\"src\", \"generated\"]")]
    [InlineData("\"typeRoots\": [\"./types\"]")]
    [InlineData("\"plugins\": [{ \"name\": \"a-plugin\" }]")]
    [InlineData("\"customConditions\": [\"development\"]")]
    [InlineData("\"jsxImportSource\": \"preact\"")]
    [InlineData("\"strict\": true, \"somethingNew\": true")]
    public void An_option_that_is_not_in_the_list_means_the_options_cannot_be_reproduced(string options) =>
        Assert.Null(Flags(options));

    [Theory]
    [InlineData("\"target\": \"es5; echo x\"")]
    [InlineData("\"target\": \"es5 && echo x\"")]
    [InlineData("\"target\": \"es5 | more\"")]
    [InlineData("\"module\": \"a b\"")]
    [InlineData("\"moduleResolution\": \"node'x\"")]
    [InlineData("\"jsx\": \"react\\\"x\"")]
    [InlineData("\"moduleDetection\": \"force`x\"")]
    [InlineData("\"target\": \"ES2022\\n\"")]
    [InlineData("\"target\": \"ES2022\\r\\necho x\"")]
    [InlineData("\"target\": \"$(whoami)\"")]
    [InlineData("\"target\": \"\"")]
    [InlineData("\"target\": \"\\ud800\"")]
    [InlineData("\"lib\": [\"ES2022\", \"DOM;x\"]")]
    [InlineData("\"lib\": [\"ES2022\", \"\"]")]
    [InlineData("\"types\": [\"node\", \"$(whoami)\"]")]
    [InlineData("\"types\": [\"node\\n\"]")]
    public void A_value_with_a_character_a_shell_could_read_means_the_options_cannot_be_reproduced(string options) =>
        Assert.Null(Flags(options));

    [Theory]
    [InlineData("\"strict\": \"true\"")]
    [InlineData("\"strict\": 1")]
    [InlineData("\"strict\": null")]
    [InlineData("\"target\": 5")]
    [InlineData("\"target\": null")]
    [InlineData("\"target\": [\"ES2022\"]")]
    [InlineData("\"lib\": \"ES2022\"")]
    [InlineData("\"lib\": [1]")]
    [InlineData("\"types\": [[\"node\"]]")]
    public void A_value_of_the_wrong_kind_means_the_options_cannot_be_reproduced(string options) =>
        Assert.Null(Flags(options));
}
