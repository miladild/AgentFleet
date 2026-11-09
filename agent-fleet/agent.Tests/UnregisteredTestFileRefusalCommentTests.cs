namespace AgentFleet.Tests;

// The refusal for a whole-file write of a test file counts only registrations outside comments, and judges every file ending the
// test runners load, not only the first few.
public sealed class UnregisteredTestFileRefusalCommentTests
{
    private const string ScriptOnly = "const assert = require('node:assert');\nassert.equal(1 + 1, 2);\n";

    [Theory]
    [InlineData("// test('adds two numbers', () => {});\nconst x = 1;\n")]
    [InlineData("/* test('adds', () => {}); */\nconst x = 1;\n")]
    [InlineData("/*\n  describe('add', () => {\n    it('adds', () => {});\n  });\n*/\nassert.equal(1, 1);\n")]
    [InlineData("const x = 1; // it('adds', () => {})\n")]
    [InlineData("const url = 'http://example.test/a';\n// test('later', () => {});\n")]
    public void A_test_file_whose_only_registrations_are_commented_out_is_refused(string content)
    {
        string? refusal = PlanRunnerToolPolicy.UnregisteredTestFileRefusal("write_file", "test/add.test.ts", content, role: null);

        Assert.NotNull(refusal);
        Assert.Contains("registers no tests, so nothing was written", refusal);
    }

    [Theory]
    [InlineData("// the suite is below\ntest('adds', () => {});\n")]
    [InlineData("/* helper for the suite */\ndescribe('add', () => {\n  it('adds', () => {});\n});\n")]
    [InlineData("/* test('old') */ it('new', () => {});\n")]
    [InlineData("// test('old')\n/* it('older') */\ntest('current', () => {});\n")]
    [InlineData("const url = 'http://example.test/a';\ntest('fetches', () => {});\n")]
    [InlineData("const url = 'http://example.test/a'; test('same line', () => {});\n")]
    public void A_real_registration_after_a_comment_is_allowed(string content) =>
        Assert.Null(PlanRunnerToolPolicy.UnregisteredTestFileRefusal("write_file", "test/add.test.ts", content, role: null));

    [Theory]
    [InlineData("test/add.test.mts")]
    [InlineData("test/add.test.cjs")]
    [InlineData("test/add.test.cts")]
    [InlineData("src/components/Add.spec.tsx")]
    [InlineData("test/add.spec.mjs")]
    [InlineData("test/add.spec.mts")]
    [InlineData("test/add.spec.cjs")]
    public void The_test_file_endings_the_runners_load_are_judged(string path)
    {
        Assert.NotNull(PlanRunnerToolPolicy.UnregisteredTestFileRefusal("write_file", path, ScriptOnly, role: null));
        Assert.Null(PlanRunnerToolPolicy.UnregisteredTestFileRefusal("write_file", path, "test('adds', () => {});\n", role: null));
    }
}
