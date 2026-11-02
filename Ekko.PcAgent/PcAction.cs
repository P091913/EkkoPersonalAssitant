using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ekko.PcAgent;

/// <summary>One configured action: press keys, launch a program, or lock the PC. Exactly one of the three.</summary>
public sealed class PcAction
{
    public string? Keys { get; init; }
    public int Repeat { get; init; } = 1;
    public string? Launch { get; init; }
    public bool Lock { get; init; }

    /// <summary>Checked at startup, so a typo in config stops the agent instead of failing on first use.</summary>
    public string? Problem()
    {
        if ((Keys is null ? 0 : 1) + (Launch is null ? 0 : 1) + (Lock ? 1 : 0) != 1)
            return "set exactly one of Keys, Launch or Lock";
        if (Repeat is < 1 or > 10)
            return "Repeat must be 1-10";
        try
        {
            if (Keys is not null)
                Keyboard.Parse(Keys);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Returns what was done; throws with a readable message on failure.</summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        if (Lock)
            return LockWorkStation() ? "locked" : throw new InvalidOperationException("Windows refused to lock");

        if (Launch is not null)
        {
            var path = Environment.ExpandEnvironmentVariables(Launch);
            if (!File.Exists(path))
                throw new FileNotFoundException($"{path} isn't installed there");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return "launched " + Path.GetFileName(path);
        }

        var keys = Keyboard.Parse(Keys!);
        for (var i = 0; i < Repeat; i++)
        {
            if (i > 0)
                await Task.Delay(30, ct);
            Keyboard.Press(keys);
        }
        return Repeat == 1 ? $"pressed {Keys}" : $"pressed {Keys} x{Repeat}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
}
