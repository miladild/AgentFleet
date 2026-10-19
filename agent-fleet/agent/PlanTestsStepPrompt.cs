namespace AgentFleet;

internal static class PlanTestsStepPrompt
{
    internal const string PlanningRules = """


        Tests first overrides the instruction to write a piece of code and its test in the same step.
        For each piece of new behaviour, first create a skeleton if the code under test does not exist:
        an ordinary step with names, signatures and bodies that throw "not implemented", checked by a build or syntax check.
        Then use a step with kind `tests`, only test files, and a verify command that runs just those tests.
        Those tests must FAIL until the behaviour exists. Follow it with an ordinary implementation step whose verify
        command runs those tests. The last step still runs the whole project's checks and is never a tests step.
        """;

    internal const string Addendum = """
        This is a tests step. These instructions override the ordinary requirement to make the check pass or stop when it passes.
        Write only tests. Do not create or change implementation files. The tests must fail now because the behaviour does not
        exist yet, and pass once it does. Each test checks one stated behaviour with a plain name. Cover the edge cases the task
        names. Run just those tests with the approved check. A real failing test is the required outcome; a build error or an
        import error is not. If tests cannot import the code under test, say so with report_blocker.
        """;

    internal static string Append(string? brief) => string.IsNullOrEmpty(brief) ? Addendum : brief + "\n\n" + Addendum;
}
