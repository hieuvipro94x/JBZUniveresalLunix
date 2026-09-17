namespace JBZUniveresalLunix.Models;

/// <summary>Production runtime supports only Universal Tester New over UART.</summary>
public enum BoardMode
{
    JbzSerial = 2
}

public static class BoardModeCatalog
{
    public static string DisplayName(BoardMode mode) => "JBZ UART";
}
