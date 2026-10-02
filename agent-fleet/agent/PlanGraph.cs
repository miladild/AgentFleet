namespace AgentFleet;

/// <summary>
/// Which step waits for which. A plan is a graph of steps, not a queue: a step that cannot be finished holds back only
/// the steps that depend on it, and the rest of the plan goes on. The default, for a plan that does not say, is the
/// straight chain it always was: each step depends on the one before it, steps of a parallel group on what came before
/// the group, and the last step (the final validation) on every other step.
/// </summary>
internal static class PlanGraph
{
    /// <summary>The ids this step waits for: its own list when it has one, else the default; the last step always waits for all.</summary>
    public static IReadOnlyList<int> DependenciesOf(IReadOnlyList<PlanStep> steps, PlanStep step)
    {
        int index = IndexOf(steps, step.Id);
        if (index < 0)
        {
            return [];
        }

        var dependencies = new SortedSet<int>();
        if (step.DependsOn is { } declared)
        {
            foreach (int id in declared)
            {
                if (id != step.Id && steps.Any(candidate => candidate.Id == id))
                {
                    dependencies.Add(id);
                }
            }
        }
        else if (index > 0)
        {
            // A parallel group's members share what came before the group.
            int previous = index - 1;
            while (step.ParallelGroup is not null && previous >= 0 && steps[previous].ParallelGroup == step.ParallelGroup)
            {
                previous--;
            }

            if (previous >= 0)
            {
                dependencies.Add(steps[previous].Id);
            }
        }

        if (steps.Count > 1 && index == steps.Count - 1)
        {
            foreach (PlanStep other in steps.Take(index))
            {
                dependencies.Add(other.Id);
            }
        }

        return [.. dependencies];
    }

    /// <summary>The plan with every step's dependencies written out, so a saved plan that never had any reads as a chain.</summary>
    public static PlanRecord Normalize(PlanRecord plan)
    {
        if (plan.Steps.Count == 0)
        {
            return plan;
        }

        IReadOnlyList<PlanStep> steps = plan.Steps;
        List<PlanStep> normalized = steps.Select(step =>
        {
            IReadOnlyList<int> dependencies = DependenciesOf(steps, step);
            return step.DependsOn is { } current && current.SequenceEqual(dependencies) ? step : step with { DependsOn = dependencies };
        }).ToList();
        return normalized.Zip(steps).All(pair => ReferenceEquals(pair.First, pair.Second)) ? plan : plan with { Steps = normalized };
    }

    /// <summary>The steps this one still waits for: dependencies that are not done.</summary>
    public static IReadOnlyList<PlanStep> Unmet(PlanRecord plan, PlanStep step) =>
        DependenciesOf(plan.Steps, step)
            .Select(id => plan.Steps.FirstOrDefault(candidate => candidate.Id == id))
            .Where(dependency => dependency is not null && dependency.Status != StepStatus.Done)
            .Select(dependency => dependency!)
            .ToList();

    public static bool IsReady(PlanRecord plan, PlanStep step) => Unmet(plan, step).Count == 0;

    /// <summary>
    /// What is wrong with the dependencies a plan was proposed with: an unknown step, a step that waits for itself or for
    /// one listed after it (so a cycle can never form: list the steps in the order they can run), a parallel group whose
    /// members wait for each other. Empty for a plan that does not say, which is the chain.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<PlanStepInput> steps)
    {
        var problems = new List<string>();
        for (int index = 0; index < steps.Count; index++)
        {
            int[]? declared = steps[index].DependsOn;
            if (declared is null)
            {
                continue;
            }

            int number = index + 1;
            string title = steps[index].Title;
            foreach (int id in declared.Distinct())
            {
                if (id < 1 || id > steps.Count)
                {
                    problems.Add($"Step {number} ({title}) depends on step {id}, which is not in the plan (steps are numbered 1 to {steps.Count}).");
                }
                else if (id == number)
                {
                    problems.Add($"Step {number} ({title}) depends on itself.");
                }
                else if (id > number)
                {
                    problems.Add(DependsOnLater(steps, number, id));
                }
                else if (steps[index].ParallelGroup is not null &&
                         string.Equals(steps[id - 1].ParallelGroup, steps[index].ParallelGroup, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Step {number} ({title}) depends on step {id}, but both are in the parallel group \"{steps[index].ParallelGroup}\": steps that run together cannot wait for each other.");
                }
            }
        }

        return problems;
    }

    // A step that waits for a later one is either a cycle (that one waits for this one, through other steps or directly) or
    // an order the runner would have to turn around; both are the planner's to fix.
    private static string DependsOnLater(IReadOnlyList<PlanStepInput> steps, int number, int later)
    {
        string name = $"Step {number} ({steps[number - 1].Title}) depends on step {later}, which comes after it";
        return Reaches(steps, later, number)
            ? $"{name} and step {later} depends on step {number} in turn: that is a cycle of dependencies."
            : $"{name}. List the steps in an order where each one comes after the steps it depends on.";
    }

    private static bool Reaches(IReadOnlyList<PlanStepInput> steps, int from, int target)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>([from]);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (current == target)
            {
                return true;
            }

            if (current < 1 || current > steps.Count || !seen.Add(current))
            {
                continue;
            }

            foreach (int next in steps[current - 1].DependsOn ?? [])
            {
                pending.Push(next);
            }
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<PlanStep> steps, int id)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }
}
