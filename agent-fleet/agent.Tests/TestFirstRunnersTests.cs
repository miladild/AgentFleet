namespace AgentFleet.Tests;

// Excerpts of real pytest 9.1, jest and vitest output (see the runner samples the tests were checked against). The paths are synthetic.
public sealed class TestFirstRunnersTests
{
    private const string PytestPass = "============================= test session starts =============================\n" +
        "collected 2 items\n\ntest_pass.py ..                                                          [100%]\n\n" +
        "============================== 2 passed in 0.05s ==============================";

    private const string PytestFail = "collected 5 items\n\ntest_fail.py .F.Fs                                                       [100%]\n\n" +
        "=================================== FAILURES ===================================\n" +
        "_______________________________ test_subtracts ________________________________\n\n" +
        ">       assert add(1, 2) == -1\nE       assert 3 == -1\n\ntest_fail.py:5: AssertionError\n" +
        "______________________________ test_table[2-2-5] ________________________________\n\n" +
        "test_fail.py:9: AssertionError\n" +
        "=========================== short test summary info ===========================\n" +
        "FAILED test_fail.py::test_subtracts - assert 3 == -1\n" +
        "FAILED test_fail.py::test_table[2-2-5] - assert 4 == 5\n" +
        "=================== 2 failed, 2 passed, 1 skipped in 0.08s ====================";

    private const string PytestFailQuiet = ".F.Fs                                                                    [100%]\n" +
        "================================== FAILURES ===================================\n" +
        "_______________________________ test_subtracts ________________________________\n\n" +
        "test_fail.py:5: AssertionError\n" +
        "=========================== short test summary info ===========================\n" +
        "FAILED test_fail.py::test_subtracts - assert 3 == -1\n" +
        "FAILED test_fail.py::test_table[2-2-5] - assert 4 == 5\n" +
        "2 failed, 2 passed, 1 skipped in 0.06s";

    private const string PytestPassQuiet = "..                                                                       [100%]\n2 passed in 0.01s";

    private const string PytestFailVerbose = "collecting ... collected 5 items\n\n" +
        "test_fail.py::test_adds PASSED                                           [ 20%]\n" +
        "test_fail.py::test_subtracts FAILED                                      [ 40%]\n" +
        "test_fail.py::test_table[1-1-2] PASSED                                   [ 60%]\n" +
        "test_fail.py::test_table[2-2-5] FAILED                                   [ 80%]\n" +
        "test_fail.py::test_skipped SKIPPED (later)                               [100%]\n" +
        "=========================== short test summary info ===========================\n" +
        "FAILED test_fail.py::test_subtracts - assert 3 == -1\n" +
        "FAILED test_fail.py::test_table[2-2-5] - assert 4 == 5\n" +
        "=================== 2 failed, 2 passed, 1 skipped in 0.08s ====================";

    private const string PytestImportError = "collected 0 items / 1 error\n\n" +
        "=================================== ERRORS ====================================\n" +
        "______________________ ERROR collecting test_missing.py _______________________\n" +
        "E   ImportError: cannot import name 'multiply' from 'calc' (/work/project/calc.py)\n" +
        "=========================== short test summary info ===========================\n" +
        "ERROR test_missing.py\n" +
        "!!!!!!!!!!!!!!!!!!! Interrupted: 1 error during collection !!!!!!!!!!!!!!!!!!!!\n" +
        "============================== 1 error in 0.15s ==============================";

    private const string PytestSyntaxError = "collected 0 items / 1 error\n\n" +
        "______________________ ERROR collecting test_syntax.py _______________________\n" +
        "E     File \"/work/project/test_syntax.py\", line 1\nE       def test_x(:\nE   SyntaxError: invalid syntax\n" +
        "=========================== short test summary info ===========================\n" +
        "ERROR test_syntax.py\n" +
        "!!!!!!!!!!!!!!!!!!! Interrupted: 1 error during collection !!!!!!!!!!!!!!!!!!!!\n" +
        "============================== 1 error in 0.20s ==============================";

    private const string PytestPartialCollectionError = "collected 3 items / 1 error\n\n" +
        "______________________ ERROR collecting test_missing.py _______________________\n" +
        "E   ImportError: cannot import name 'multiply' from 'calc' (/work/project/calc.py)\n" +
        "=========================== short test summary info ===========================\n" +
        "ERROR test_missing.py\n" +
        "FAILED test_fail.py::test_subtracts - assert 3 == -1\n" +
        "=================== 1 failed, 2 passed, 1 error in 0.12s ====================";

    private const string PytestNoTests = "collected 0 items\n\n============================ no tests ran in 0.02s ============================";

    private const string PytestAllSkipped = "s                                                                        [100%]\n1 skipped, 4 deselected in 0.01s";

    private const string JestPass = "npm notice run jest pass.test.js\nTest Suites: 1 passed, 1 total\n" +
        "Tests:       2 passed, 2 total\nSnapshots:   0 total\nTime:        0.835 s\nRan all test suites matching pass.test.js.";

    private const string JestFail = "FAIL ./fail.test.js\n  ● math › subtracts\n\n    expect(received).toBe(expected) // Object.is equality\n\n" +
        "    Expected: -1\n    Received: 3\n\n      at Object.toBe (fail.test.js:4:47)\n\n" +
        "  ● math › table 2 2\n\n    expect(received).toBe(expected) // Object.is equality\n\n" +
        "    Expected: 5\n    Received: 4\n\n      at toBe (fail.test.js:6:80)\n\n" +
        "Test Suites: 1 failed, 1 total\nTests:       2 failed, 1 skipped, 2 passed, 5 total\nSnapshots:   0 total\n" +
        "Time:        0.57 s\nRan all test suites matching fail.test.js.";

    private const string JestMissingFunction = "FAIL ./missing.test.js\n  ● mul\n\n    TypeError: multiply is not a function\n\n" +
        "      at Object.multiply (missing.test.js:2:28)\n\n" +
        "Test Suites: 1 failed, 1 total\nTests:       1 failed, 1 total\nSnapshots:   0 total\nTime:        0.4 s";

    private const string JestSyntax = "FAIL ./syntax.test.js\n  ● Test suite failed to run\n\n    Jest encountered an unexpected token\n\n" +
        "    Details:\n\n    SyntaxError: /work/project/syntax.test.js: Unexpected token (1:28)\n\n" +
        "Test Suites: 1 failed, 1 total\nTests:       0 total\nSnapshots:   0 total\nTime:        0.438 s";

    private const string JestNoTestsInFile = "FAIL ./none.test.js\n  ● Test suite failed to run\n\n" +
        "    Your test suite must contain at least one test.\n\n" +
        "Test Suites: 1 failed, 1 total\nTests:       0 total\nSnapshots:   0 total\nTime:        0.406 s";

    private const string JestNoMatch = "No tests found, exiting with code 1\nRun with `--passWithNoTests` to exit with code 0\n" +
        "In /work/project\n  8 files checked.\n  testMatch: **/__tests__/**/*.?([mc])[jt]s?(x), **/?(*.)+(spec|test).?([mc])[jt]s?(x) - 5 matches\n" +
        "Pattern: nomatch.test.js - 0 matches";

    private const string JestSuiteFailedBesideTests = "FAIL ./mixed.test.js\n  ● Test suite failed to run\n\n" +
        "    Jest encountered an unexpected token\n\n  ● math › adds\n\n    Expected: 3\n    Received: 4\n\n" +
        "Test Suites: 2 failed, 2 total\nTests:       1 failed, 1 total";

    private const string VitestPass = " Test Files  1 passed (1)\n      Tests  1 passed (1)\n   Start at  11:51:58\n   Duration  198ms";

    private const string VitestFail = " ❯ vfail.test.mjs (3 tests | 1 failed | 1 skipped) 9ms\n   ❯ math (3)\n     × subtracts 6ms\n\n" +
        "⎯⎯⎯⎯⎯⎯⎯ Failed Tests 1 ⎯⎯⎯⎯⎯⎯⎯\n\n FAIL  vfail.test.mjs > math > subtracts\nAssertionError: expected 3 to be -1 // Object.is equality\n\n" +
        " ❯ vfail.test.mjs:6:47\n\n Test Files  1 failed (1)\n      Tests  1 failed | 1 passed | 1 skipped (3)";

    private const string VitestImportError = " ❯ vmissing.test.mjs (0 test)\n\n⎯⎯⎯⎯⎯⎯ Failed Suites 1 ⎯⎯⎯⎯⎯⎯\n\n" +
        " FAIL  vmissing.test.mjs [ vmissing.test.mjs ]\nError: Cannot find module './nothing.js' imported from /work/project/vmissing.test.mjs\n\n" +
        " Test Files  1 failed (1)\n      Tests  no tests";

    private const string VitestNoTests = " FAIL  vnone.test.mjs [ vnone.test.mjs ]\nError: No test suite found in file /work/project/vnone.test.mjs\n\n" +
        " Test Files  1 failed (1)\n      Tests  no tests";

    private const string VitestNoMatch = "No test files found, exiting with code 1\n\nfilter: nomatch\ninclude: **/*.{test,spec}.?(c|m)[jt]s?(x)";

    public static IEnumerable<object[]> Samples() =>
    [
        [PytestPass, 0, "tests/test_pass.py", "tests-pass", 2, 0],
        [PytestFail, 1, "tests/test_fail.py", "accepted", 4, 2],
        [PytestFailQuiet, 1, "tests/test_fail.py", "accepted", 4, 2],
        [PytestPassQuiet, 0, "tests/test_pass.py", "tests-pass", 2, 0],
        [PytestFailVerbose, 1, "tests/test_fail.py", "accepted", 4, 2],
        [PytestImportError, 2, "tests/test_missing.py", "no-test-ran", 0, 0],
        [PytestSyntaxError, 2, "tests/test_syntax.py", "build-error", 0, 0],
        [PytestNoTests, 5, "tests/test_none.py", "no-test-ran", 0, 0],
        [PytestAllSkipped, 0, "tests/test_skip.py", "no-test-ran", 0, 0],
        [JestPass, 0, "tests/pass.test.js", "tests-pass", 2, 0],
        [JestFail, 1, "tests/fail.test.js", "accepted", 4, 2],
        [JestMissingFunction, 1, "tests/missing.test.js", "accepted", 1, 1],
        [JestSyntax, 1, "tests/syntax.test.js", "build-error", 0, 0],
        [JestNoTestsInFile, 1, "tests/none.test.js", "no-test-ran", 0, 0],
        [JestNoMatch, 1, "tests/nomatch.test.js", "no-test-ran", 0, 0],
        [VitestPass, 0, "tests/vpass.test.mjs", "tests-pass", 1, 0],
        [VitestFail, 1, "tests/vfail.test.mjs", "accepted", 2, 1],
        [VitestImportError, 1, "tests/vmissing.test.mjs", "no-test-ran", 0, 0],
        [VitestNoTests, 1, "tests/vnone.test.mjs", "no-test-ran", 0, 0],
        [VitestNoMatch, 1, "tests/nomatch.test.mjs", "no-test-ran", 0, 0],
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Real_runner_output_gets_the_expected_outcome(string sample, int exitCode, string testFile, string outcome, int executed, int failing)
    {
        TestFirstDecision decision = TestFirst.Evaluate($"Exit code: {exitCode}\n{sample}", [testFile]);

        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(executed, decision.Executed);
        Assert.Equal(failing, decision.Failing);
    }

    [Fact]
    public void A_dotnet_summary_and_a_pytest_summary_in_one_output_add_up()
    {
        const string output = "Exit code: 1\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1\n" +
            "FAILED test_a.py::test_b - assert 1 == 2\n=== 1 failed, 2 passed in 0.10s ===";

        TestFirstDecision decision = TestFirst.Evaluate(output, ["tests/LimitsTests.cs"]);

        Assert.Equal("accepted", decision.Outcome);
        Assert.Equal(4, decision.Executed);
        Assert.Equal(1, decision.Failing);
    }

    [Fact]
    public void A_pytest_collection_error_beside_a_failing_test_is_not_accepted()
    {
        TestFirstDecision decision = TestFirst.Evaluate("Exit code: 2\n" + PytestPartialCollectionError, ["tests/test_fail.py"]);

        Assert.Equal("build-error", decision.Outcome);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void A_jest_suite_that_failed_to_run_is_not_a_failing_test()
    {
        TestFirstDecision decision = TestFirst.Evaluate("Exit code: 1\n" + JestSuiteFailedBesideTests, ["tests/mixed.test.js"]);

        Assert.Equal("build-error", decision.Outcome);
        Assert.Equal(1, decision.Failing);
    }

    [Fact]
    public void Jest_todo_tests_are_not_run_and_do_not_count_as_executed()
    {
        const string output = "Exit code: 1\n  ● math › subtracts\n\nTests:       1 failed, 1 todo, 2 total";

        TestFirstDecision decision = TestFirst.Evaluate(output, ["tests/fail.test.js"]);

        Assert.Equal(1, decision.Executed);
        Assert.Equal("accepted", decision.Outcome);
    }

    [Fact]
    public void A_jest_console_block_is_not_a_failing_test_when_every_test_passed()
    {
        // A coverage threshold can fail the run after every test passed; the console block heading is not a test that failed.
        const string output = "Exit code: 1\nPASS ./coverage.test.js\n  ● Console\n\n    console.log\n      debug line\n\n" +
            "      at Object.log (coverage.test.js:2:11)\n\n" +
            "Jest: \"global\" coverage threshold for branches (90%) not met: 50%\n\n" +
            "Test Suites: 1 passed, 1 total\nTests:       2 passed, 2 total\nSnapshots:   0 total\nTime:        0.9 s";

        TestFirstDecision decision = TestFirst.Evaluate(output, ["tests/coverage.test.js"]);

        Assert.Equal(0, decision.Failing);
        Assert.Equal("unreadable", decision.Outcome);
    }
}
