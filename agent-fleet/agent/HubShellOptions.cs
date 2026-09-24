using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AgentFleet;

internal sealed class HubShellOptions
{
    private HubShellOptions(TimeSpan executionTimeout)
    {
        ExecutionTimeout = executionTimeout;
    }

    public TimeSpan ExecutionTimeout { get; }

    public static HubShellOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? rawValue = configuration["SHELL_EXECUTION_TIMEOUT_SECONDS"];
        int seconds = 120;
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            if (!int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                seconds is < 1 or > 600)
            {
                throw new InvalidOperationException(
                    "SHELL_EXECUTION_TIMEOUT_SECONDS must be an integer between 1 and 600.");
            }
        }

        return new HubShellOptions(TimeSpan.FromSeconds(seconds));
    }
}
