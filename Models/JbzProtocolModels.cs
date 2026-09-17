namespace JBZUniveresalLunix.Models;

public enum JbzEventFamily
{
    Empty, Idn, ModelName, Start, Measure, Clear, Open, Short, Other, Sequence, NewTest,
    ClearConnector, TestPin, Pin, Circuit, Input, Output, Resistor, Ampare, Voltage, Error,
    Pass, Pen, Removal, Unconnect, Stop, Boot, Ack, Ok, Raw
}

public sealed record JbzBoardEvent(
    JbzEventFamily Family,
    string Raw,
    IReadOnlyList<int>? Numbers = null,
    IReadOnlyList<string>? Values = null);

public sealed record JbzCommandExpectation(string Value, bool Prefix, TimeSpan Timeout)
{
    public bool Matches(string line) => Prefix
        ? line.StartsWith(Value, StringComparison.Ordinal)
        : string.Equals(line, Value, StringComparison.Ordinal);
}

public sealed record JbzProtocolCommand(
    string Text,
    JbzCommandExpectation Expectation,
    string Source = "compiled-model");

public sealed record JbzModelUploadProgress(int Percent, int Completed, int Total, string Command);

public sealed record JbzCompiledModel(
    string ModelName,
    ProductModel Product,
    IReadOnlyList<JbzProtocolCommand> Commands,
    int PinRows,
    int SourceRecords,
    int TargetItems,
    int ConnectorCount);
