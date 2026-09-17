using JBZUniveresalLunix.Services;

namespace JBZUniveresalLunix.HardwareVerification;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? preferred = args.Length > 0 ? args[0] : null;
        if (preferred is not null && !System.Text.RegularExpressions.Regex.IsMatch(
                preferred, "^COM[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            Console.Error.WriteLine("Usage: HardwareVerification [COM<number>]");
            return 2;
        }
        try
        {
            (string port, string idn, string? model) =
                await JbzSerialBoardTransport.DiscoverAsync(preferred);
            Console.WriteLine($"BOARD COM: {port}");
            Console.WriteLine($"IDN: {idn}");
            Console.WriteLine($"MODEL: {model ?? "(no response)"}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UART verification failed: {ex.Message}");
            return 1;
        }
    }
}
