namespace AgentFleet;

/// <summary>An edit check gate is ambient only while a worker's model is making its edits, like the worker scope.</summary>
internal static class EditCheckContext
{
    private static readonly AsyncLocal<EditCheckGate?> CurrentGate = new();
    public static EditCheckGate? Current => CurrentGate.Value;

    public static IDisposable Push(EditCheckGate gate)
    {
        EditCheckGate? previous = CurrentGate.Value;
        CurrentGate.Value = gate;
        return new Restore(previous);
    }

    private sealed class Restore(EditCheckGate? previous) : IDisposable
    {
        public void Dispose() => CurrentGate.Value = previous;
    }
}
