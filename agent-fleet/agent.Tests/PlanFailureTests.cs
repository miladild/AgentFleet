namespace AgentFleet.Tests;

public sealed class PlanFailureTests
{
    [Fact]
    public void Repeated_failing_tests_have_the_same_signature_despite_paths_and_timings()
    {
        FailureFingerprint first = PlanFailure.FailureSignature("Failed Sample.Tests.OpenSession [42 ms]\nerror CS1002: ; expected");
        FailureFingerprint second = PlanFailure.FailureSignature("Failed Sample.Tests.OpenSession [108 ms]\nerror CS1002: ; expected");

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(2, first.Size);
    }

    [Fact]
    public void A_repeated_failure_set_without_changes_is_a_stall()
    {
        const string failure = "Failed Sample.Tests.OpenSession [42 ms]\nFailed Sample.Tests.CloseSession [51 ms]";
        FailureFingerprint signature = PlanFailure.FailureSignature(failure);

        Assert.Equal(FailureClass.Stall, PlanFailure.Classify(null, failure,
            [new FailureRound(signature.Value, signature.Size, FilesChanged: 0, EditToolCalled: false)]));
    }

    [Fact]
    public void A_smaller_failing_set_is_code_progress()
    {
        const string before = "Failed Sample.Tests.OpenSession [42 ms]\nFailed Sample.Tests.CloseSession [51 ms]";
        const string after = "Failed Sample.Tests.OpenSession [17 ms]";
        FailureFingerprint previous = PlanFailure.FailureSignature(before);

        Assert.Equal(FailureClass.CodeProgress, PlanFailure.Classify(null, after,
            [new FailureRound(previous.Value, previous.Size, FilesChanged: 1, EditToolCalled: true)]));
    }

    [Fact]
    public void A_powershell_parser_error_is_a_check_defect()
    {
        Assert.Equal(FailureClass.CheckDefect, PlanFailure.Classify(null,
            "ParserError: Unexpected token '&&' in expression or statement.", []));
    }

    [Fact]
    public void A_missing_ollama_server_binary_is_infrastructure()
    {
        Assert.Equal(FailureClass.Infra, PlanFailure.Classify(null,
            "error starting llama-server: llama-server binary not found", []));
    }

    [Fact]
    public void A_first_failing_round_without_an_edit_is_a_no_op()
    {
        Assert.Equal(FailureClass.NoOp, PlanFailure.Classify(null,
            "Failed Sample.Tests.OpenSession [42 ms]", []));
    }
}
