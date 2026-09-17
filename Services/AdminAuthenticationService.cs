using System.Security.Cryptography;
using System.Text;

namespace JBZUniveresalLunix.Services;

public static class AdminAuthenticationService
{
    private const string ProbeMaintenancePassword = "admin";

    public static bool VerifyProbeMaintenance(string? suppliedPassword) =>
        Verify(ProbeMaintenancePassword, suppliedPassword);

    public static bool VerifyBoardMaintenance(string? suppliedPassword) =>
        Verify(ProbeMaintenancePassword, suppliedPassword);

    public static bool Verify(string? configuredPassword, string? suppliedPassword)
    {
        if (string.IsNullOrEmpty(configuredPassword))
            return false;

        byte[] expected = Encoding.UTF8.GetBytes(configuredPassword);
        byte[] supplied = Encoding.UTF8.GetBytes(suppliedPassword ?? string.Empty);
        try
        {
            return expected.Length == supplied.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(supplied);
        }
    }
}
