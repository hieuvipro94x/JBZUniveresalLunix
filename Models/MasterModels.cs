namespace JBZUniveresalLunix.Models;

/// <summary>
/// Chuỗi xác nhận master hoàn toàn tự động. Production chỉ được mở khi state=Completed.
/// </summary>
public enum MasterSequenceState
{
    Disabled = 0,
    WaitingGoodMaster = 1,
    TestingGoodMaster = 2,
    EjectingGoodMaster = 3,
    WaitingBadMaster = 4,
    TestingBadMaster = 5,
    EjectingBadMaster = 6,
    Completed = 7
}

/// <summary>
/// Định danh ổn định cho một điểm lỗi master. Cùng một fault xuất hiện ở hàng trăm frame
/// vẫn tạo đúng một key. V12.10.2 chuẩn hóa theo cạnh điện vật lý để A-B và B-A là một lỗi.
/// </summary>
public readonly record struct MasterFaultKey(
    ProductFaultType FaultType,
    int SourceIo,
    int TargetIo,
    int ExpectedSourceIo,
    int ExpectedTargetIo)
{
    /// <summary>
    /// V12.10.2: key Master phải đại diện cho LỖI ĐIỆN VẬT LÝ, không đại diện
    /// cho hướng frame firmware. Cùng một cầu nối IO1-IO7 có thể xuất hiện
    /// IO1->IO7 rồi IO7->IO1; hai frame đó chỉ được tính là một lỗi Master.
    /// </summary>
    public static MasterFaultKey From(FaultDetail fault)
    {
        int expectedSource = fault.ExpectedSourceIo ?? 0;
        int expectedTarget = fault.ExpectedTargetIo ?? 0;
        int actualSource = fault.ActualSourceIo ?? 0;
        int actualTarget = fault.ActualTargetIo ?? 0;

        int[] related = fault.RelatedIos
            .Where(io => io > 0)
            .Distinct()
            .Take(2)
            .ToArray();

        if (fault.Type == ProductFaultType.OpenCircuit)
        {
            // OPEN ưu tiên cặp mong đợi của THT. Nếu một phía thiếu, fallback
            // sang RelatedIos để các open một chân vẫn có key khác nhau.
            int a = expectedSource > 0
                ? expectedSource
                : actualSource > 0
                    ? actualSource
                    : related.ElementAtOrDefault(0);
            int b = expectedTarget > 0
                ? expectedTarget
                : actualTarget > 0
                    ? actualTarget
                    : related.ElementAtOrDefault(1);

            NormalizePair(ref a, ref b);
            return new MasterFaultKey(ProductFaultType.OpenCircuit, a, b, a, b);
        }

        if (fault.Type is ProductFaultType.WrongWiring or ProductFaultType.ShortCircuit)
        {
            // WrongWiring và Short có thể là hai cách phân loại của CÙNG một cạnh
            // điện khi firmware trả cả hai hướng. Master gate chỉ đếm cạnh vật lý
            // duy nhất; UI vẫn giữ FaultDetail đầu tiên để hiển thị loại lỗi cụ thể.
            int a = actualSource > 0
                ? actualSource
                : related.ElementAtOrDefault(0);
            int b = actualTarget > 0
                ? actualTarget
                : related.ElementAtOrDefault(1);

            if (a <= 0 && expectedSource > 0)
                a = expectedSource;
            if (b <= 0 && expectedTarget > 0)
                b = expectedTarget;

            NormalizePair(ref a, ref b);

            // Dùng một FaultType canonical để WrongWiring<->Short cùng cặp IO
            // không thể làm MasterDetectedFaultCount tăng hai lần.
            return new MasterFaultKey(ProductFaultType.WrongWiring, a, b, 0, 0);
        }

        return new MasterFaultKey(
            fault.Type,
            actualSource,
            actualTarget,
            expectedSource,
            expectedTarget);
    }

    private static void NormalizePair(ref int a, ref int b)
    {
        // 0 biểu diễn phía chưa biết; luôn để phía hợp lệ đứng trước để IOx-0
        // và 0-IOx vẫn là cùng một key.
        if (a <= 0 && b > 0)
        {
            (a, b) = (b, a);
            return;
        }

        if (a > 0 && b > 0 && a > b)
            (a, b) = (b, a);
    }
}

/// <summary>Dòng đỏ dùng riêng cho danh sách lỗi Master NG trên TestView.</summary>
public sealed class MasterFaultDisplayRow
{
    public int Number { get; init; }
    public int RequiredCount { get; init; }
    public string ProgressText => $"LỖI MASTER {Number}/{RequiredCount}";
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public string Actual { get; init; } = string.Empty;
}
