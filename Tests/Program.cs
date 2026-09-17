using System.IO.Compression;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Converters;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.ViewModels;
using JBZUniveresalLunix.Views;
using Microsoft.Data.Sqlite;

namespace JBZUniveresalLunix.SelfTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        int benchmarkIndex = Array.FindIndex(
            args,
            value => value.Equals("--database-benchmark", StringComparison.OrdinalIgnoreCase));
        if (benchmarkIndex >= 0)
        {
            int rows = benchmarkIndex + 1 < args.Length && int.TryParse(args[benchmarkIndex + 1], out int parsed)
                ? parsed
                : 100_000;
            return DatabaseBenchmark.Run(Math.Clamp(rows, 100_000, 1_000_000));
        }

        (string Name, Action Run)[] tests =
        [
            ("JBZ UART streaming protocol parser", TestJbzProtocolParser),
            ("JBZ model ACK fake-board contract", TestJbzModelAckContract),
            ("JBZ .model compiler golden trace", TestJbzModelCompilerGolden),
            ("JBZ realtime OPEN/SHORT topology projection", TestJbzRealtimeTopologyProjection),
            ("JBZ TESTPIN multi-contact trace and independent OPEN", TestJbzLiveProbeTrace),
            ("JBZ firmware HEX and traced packet layout", TestJbzFirmwareProtocol),
            ("Production scan accepts first frame after decoder sequence reset", TestProductionScanFirstFrameAfterSequenceReset),
            ("Scan watchdog intentional pause and staged recovery", TestScanWatchdogRecovery),
            ("New version inherits station production data without overwrite", TestProductionDataUpgrade),
            ("Startup connected-IO safety interlock", TestStartupIoInterlock),
            ("Inline probe does not clear wiring faults", TestInlineProbeDoesNotClearWiringFaults),
            ("500-cycle scan/probe/fault stress", TestFiveHundredCycleScanProbeFaultStress),
            ("Continuity/open/wrong/splice engine", TestEngineVectors),
            ("Pending continuity presentation and CLIP branch visibility", TestPendingContinuityPresentation),
            ("Final Htdrv TestWindow presentation lifecycle", TestFinalHtdrvTestWindowPresentation),
            ("Learned diagnostic topology normalization and persistence", TestLearnedTopology),
            ("Relay PASS/FAIL safe ordering", TestRelayOrdering),
            ("History SQLite/search/CSV/XLSX native types", TestHistory),
            ("History exactly-once transaction crash recovery", TestHistoryTransactionAtomicity),
            ("Legacy SQLite without SchemaInfo initializes safely", TestLegacyDatabaseWithoutSchemaInfo),
            ("SQLite schema v7 chronological History migration and query plans", TestDatabaseSchemaV5),
            ("History initialization waits for an active SQLite writer", TestHistoryInitializationWaitsForWriter),
            ("Production SQLite writer retries a transient lock", TestProductionPersistenceRetriesTransientLock),
            ("SQLite interrupted transaction reopens without deleting database", TestHistoryInterruptedTransactionRecovery),
            ("Canonical runtime paths and SQLite PartCnt authority", TestCanonicalRuntimePersistence),
            ("System log master switch preserves History", TestSystemLogMasterSwitch),
            ("ALL6 label data order", TestLabel),
            ("PASS label snapshot/idempotency/traceability", TestLabelPrintingSafety),
            ("Part-number auto resolver and paired setup", TestPartNumberResolver),
            ("Fault display localization and detail", TestFaultDisplayFormatter),
            ("UI brush cache and engine change filter", TestUiPerformanceGuards),
            ("Authoritative production state and stale UI snapshot gate", TestAuthoritativeProductionState),
            ("Model-aware product evidence rejects raw single IO", TestModelAwareProductEvidence),
            ("Duplicate CLIP fault rows do not lock hardware", TestDuplicateClipFaultRows),
            ("Leak connector mapping and PASS/FAIL presentation", TestWaterProofConfigurationAndPresentation),
            ("Final TestView status/master/device fault guards", TestFinalTestStatusGuards),
            ("Direct manual relay controls and production interlock", TestManualModeInterlock),
            ("START only arms and background scan survives cycle cancel", TestProductionScanTokenSurvivesCycleCancel),
            ("Production fault debounce and jig contact state", TestProductionFaultConfirmation),
            ("Incomplete product full release resets normal and CLIP cycle", TestIncompleteProductFullReleaseResetsClipCycle),
            ("Original PartCnt per-part counter compatibility", TestPartCounterStore),
            ("Original PHT20 PASS/ERR history compatibility", TestLegacyPhtHistory),
            ("Per-model production/probe maintenance counters", TestProductionCounters)
        ];

        int failed = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL: {name}\n  {ex}");
            }
        }

        Console.WriteLine($"SELF-TEST SUMMARY: {tests.Length - failed}/{tests.Length} PASS");
        return failed == 0 ? 0 : 1;
    }

    private static void TestJbzProtocolParser()
    {
        var parser = new JbzProtocolParser();
        Assert(parser.Push(Encoding.ASCII.GetBytes(":OPEN,10,10,")).Count == 0,
            "Partial UART line must be buffered");
        IReadOnlyList<JbzBoardEvent> events = parser.Push(
            Encoding.ASCII.GetBytes("11\r:OTHER,7,8\n:CIRCUIT,0\r\n:TESTPIN,42,OFF\r"));
        Assert(events.Count == 4, "CR, LF and CRLF framing must all be accepted");
        Assert(events[0].Family == JbzEventFamily.Open && events[0].Numbers!.SequenceEqual([10, 10, 11]), "OPEN payload");
        Assert(events[1].Family == JbzEventFamily.Other && events[1].Numbers!.SequenceEqual([7, 8]), "OTHER payload");
        Assert(events[2].Family == JbzEventFamily.Circuit && events[2].Numbers![0] == 0, "CIRCUIT PASS semantics");
        Assert(events[3].Family == JbzEventFamily.TestPin && events[3].Numbers![0] == 42 && events[3].Values![0] == "OFF", "TESTPIN state");
        JbzBoardEvent shortEvent = JbzProtocolParser.ParseLine(":SHORT,193,194");
        Assert(shortEvent.Family == JbzEventFamily.Short && shortEvent.Numbers!.SequenceEqual([193, 194]),
            "SHORT must remain a first-class Rev 1.42 wiring event");
        Assert(JbzProtocolParser.ParseLine(":NEWTEST").Family == JbzEventFamily.NewTest,
            "NEWTEST event family");
        Assert(JbzProtocolParser.ParseLine(":SEQ,1,2").Family == JbzEventFamily.Sequence,
            "SEQ event family");
        Assert(JbzProtocolParser.ParseLine(":CLEARCONNECTOR").Family == JbzEventFamily.ClearConnector,
            "CLEARCONNECTOR event family");
        Assert(JbzProtocolParser.ParseLine(":RESISTOR,3961").Values![0] == "3961",
            "Raw resistor ADC must not be converted to ohms");
    }

    private static void TestJbzModelAckContract()
    {
        var fakeBoard = Channel.CreateUnbounded<JbzBoardEvent>();
        static JbzProtocolCommand Cmd(string tx, string ack, int timeoutMs = 100) =>
            new(tx, new(ack, false, TimeSpan.FromMilliseconds(timeoutMs)));
        static void Push(Channel<JbzBoardEvent> board, string line) =>
            board.Writer.TryWrite(JbzProtocolParser.ParseLine(line));

        Push(fakeBoard, ":OK,MODEL");
        Assert(JbzModelAckWaiter.WaitAsync(Cmd(":MODEL,WH322110", ":OK,MODEL"),
            fakeBoard.Reader, CancellationToken.None).GetAwaiter().GetResult().Raw == ":OK,MODEL",
            "MODEL exact ACK succeeds");

        Push(fakeBoard, ":OK,PINDATA,12");
        Assert(JbzModelAckWaiter.WaitAsync(Cmd(":PINDATA,12,1,0,1,0,0", ":OK,PINDATA,12"),
            fakeBoard.Reader, CancellationToken.None).GetAwaiter().GetResult().Raw == ":OK,PINDATA,12",
            "PINDATA index ACK succeeds");

        Push(fakeBoard, ":OK,PINDATA,11");
        AssertThrows<InvalidDataException>(() => JbzModelAckWaiter.WaitAsync(
            Cmd(":PINDATA,12,1,0,1,0,0", ":OK,PINDATA,12"), fakeBoard.Reader,
            CancellationToken.None).GetAwaiter().GetResult(), "Wrong PINDATA index must fail");

        Push(fakeBoard, ":OK,MODEL"); // stale ACK from a previous family
        Push(fakeBoard, ":OK,PINDATA,12");
        Assert(JbzModelAckWaiter.WaitAsync(Cmd(":PINDATA,12,1,0,1,0,0", ":OK,PINDATA,12"),
            fakeBoard.Reader, CancellationToken.None).GetAwaiter().GetResult().Raw == ":OK,PINDATA,12",
            "Stale unrelated ACK cannot complete the current command");

        AssertThrows<TimeoutException>(() => JbzModelAckWaiter.WaitAsync(
            Cmd(":PINCOUNT,102", ":OK,PINCOUNT", 15), fakeBoard.Reader,
            CancellationToken.None).GetAwaiter().GetResult(), "No ACK times out");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertThrows<OperationCanceledException>(() => JbzModelAckWaiter.WaitAsync(
            Cmd(":PINCOUNT,102", ":OK,PINCOUNT"), fakeBoard.Reader,
            cancelled.Token).GetAwaiter().GetResult(), "Cancellation stops ACK wait");

        Push(fakeBoard, ":ERROR,DISCONNECTED");
        AssertThrows<IOException>(() => JbzModelAckWaiter.WaitAsync(
            Cmd(":PINCOUNT,102", ":OK,PINCOUNT"), fakeBoard.Reader,
            CancellationToken.None).GetAwaiter().GetResult(), "Board disconnect/error stops ACK wait");
    }

    private static void TestJbzModelCompilerGolden()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "JBZ_Windows", "tests", "fixtures");
        JbzCompiledModel model = JbzModelCompiler.Compile(Path.Combine(root, "WH322110.model"));
        string[] golden = File.ReadAllLines(Path.Combine(root, "WH322110_trace_commands.txt"));
        Assert(model.Commands.Select(command => command.Text).SequenceEqual(golden),
            "Compiled commands must match the proven Python golden trace exactly");
        Assert(model.Commands.Count == 115 && model.PinRows == 200 && model.SourceRecords == 102 &&
               model.TargetItems == 98 && model.ConnectorCount == 14,
            "WH322110 compiler summary must match the reference");
        Assert(model.Product.PartNumber == "P737561000" && model.Product.ProductName == "VOLT SNSG" &&
               model.Product.VehicleType == "MX5 HEV" && model.Product.CustomerCode == "P7375/61000",
            "WH322110 display fields match the reference [Common] metadata");

        JbzCompiledModel special = JbzModelCompiler.Compile(Path.Combine(root, "WH321798.model"));
        string[] specialGolden = File.ReadAllLines(Path.Combine(root, "WH321798_trace_commands.txt"));
        Assert(special.Commands.Select(command => command.Text).SequenceEqual(specialGolden),
            "WH321798 including Special=A flag and 201-pin CON tail must match Rev 1.42 trace exactly");
        Assert(special.Commands.Count == 116 && special.PinRows == 201 && special.SourceRecords == 103 &&
               special.TargetItems == 98 && special.ConnectorCount == 14,
            "WH321798 compiler summary must match the captured Rev 1.42 upload");
        Assert(special.Commands.Any(command => command.Text == ":PINDATA,97,196,98,0,0,16"),
            "Special=A source must encode firmware flag 16");

        // Regression from production log 2026-09-16: 321085.model contained
        // [Common]/Model=DL3, but firmware identity must still be 321085.
        string mismatchRoot = Path.Combine(Path.GetTempPath(), "JBZModelIdentity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mismatchRoot);
        try
        {
            string mismatchPath = Path.Combine(mismatchRoot, "321085.model");
            string mismatchText = File.ReadAllText(Path.Combine(root, "WH322110.model"))
                .Replace("Model=WH322110", "Model=DL3", StringComparison.Ordinal);
            File.WriteAllText(mismatchPath, mismatchText);
            JbzCompiledModel mismatch = JbzModelCompiler.Compile(mismatchPath);
            Assert(mismatch.ModelName == "321085" &&
                   mismatch.Product.ModelName == "321085" &&
                   mismatch.Commands[0].Text == ":MODEL,321085",
                ".model file stem is the canonical firmware/model identity even when [Common]/Model differs");
        }
        finally
        {
            try { Directory.Delete(mismatchRoot, recursive: true); } catch { }
        }
    }

    private static void TestJbzRealtimeTopologyProjection()
    {
        string path = Path.Combine(Environment.CurrentDirectory, "JBZ_Windows", "tests", "sample.model");
        JbzCompiledModel compiled = JbzModelCompiler.Compile(path);
        var adapter = new JbzBoardTransportAdapter(string.Empty, new ProductionSettings());
        adapter.ConfigureModel(compiled.Product);

        var frames = new List<ScanFrame>();
        adapter.FrameReceived += (_, frame) => frames.Add(frame);
        MethodInfo onEvent = typeof(JbzBoardTransportAdapter).GetMethod(
            "OnEvent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnEvent not found");

        void Push(string line) => onEvent.Invoke(adapter, [null, JbzProtocolParser.ParseLine(line)]);

        Push(":CLEAR");
        Assert(frames.Count == 1 && !frames[^1].Complete && !frames[^1].TerminatorKnown &&
               frames[^1].Connections.TryGetValue(1, out IReadOnlySet<int>? clearTargets) &&
               clearTargets.Count == 0,
            "CLEAR publishes presentation-only all-open continuity preview");

        Push(":OPEN,1");
        Assert(!frames[^1].Complete && frames[^1].Connections[1].SetEquals([2]),
            "OPEN with source only hides the completed Test-screen network through preview");

        Push(":OPEN,1,1,2");
        Assert(!frames[^1].Complete && frames[^1].Connections[1].Count == 0,
            "OPEN source+missing targets restores the Test-screen row immediately without deciding PASS/FAIL");

        int beforeOther = frames.Count;
        Push(":OTHER,1,99");
        Assert(frames.Count == beforeOther + 1 && !frames[^1].Complete &&
               frames[^1].Connections[1].Contains(99) &&
               frames[^1].FaultHints.TryGetValue((1, 99), out ProductFaultType otherType) &&
               otherType == ProductFaultType.WrongWiring,
            "OTHER is shown immediately and preserved as firmware WRONG_WIRING");

        Push(":SHORT,1,77");
        Assert(!frames[^1].Complete &&
               frames[^1].Connections[1].Contains(77) &&
               frames[^1].FaultHints.TryGetValue((1, 77), out ProductFaultType shortType) &&
               shortType == ProductFaultType.ShortCircuit,
            "SHORT is shown immediately and preserved as firmware SHORT_CIRCUIT");
        Push(":CIRCUIT,1");
        Assert(frames[^1].Complete && frames[^1].Connections[1].Contains(99),
            "CIRCUIT,1 publishes the authoritative wrong-wire topology");

        Push(":CIRCUIT,0");
        Assert(frames[^1].Complete && frames[^1].Connections[1].SetEquals([2]),
            "CIRCUIT,0 authoritatively restores the clean expected topology");

        adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void TestJbzLiveProbeTrace()
    {
        string fixtureRoot = Path.Combine(Environment.CurrentDirectory, "JBZ_Windows", "tests", "fixtures");
        JbzCompiledModel model = JbzModelCompiler.Compile(Path.Combine(fixtureRoot, "WH321798.model"));
        PinRecord pin125 = model.Product.Pins.Single(pin => pin.IoNumber == 125);
        Assert(pin125.Connector == "HOLDER 07" && pin125.PinNumber == "7" && pin125.WireName == "M3C3" && pin125.Color == "P",
            "TESTPIN 125 maps directly to its physical .model row and metadata");
        PinRecord pin200 = model.Product.Pins.Single(pin => pin.IoNumber == 200);
        Assert(pin200.Connector == "BAND" && pin200.PinNumber == "5",
            "A physical pin outside normal network topology remains available to probe presentation");

        var adapter = new JbzBoardTransportAdapter(string.Empty, new ProductionSettings());
        adapter.ConfigureModel(model.Product);
        var previews = new List<int[]>();
        var frames = new List<ScanFrame>();
        adapter.ProductionProbePreviewReceived += (_, preview) => previews.Add(preview.ActiveIo.ToArray());
        adapter.FrameReceived += (_, frame) => frames.Add(frame);
        MethodInfo onEvent = typeof(JbzBoardTransportAdapter).GetMethod("OnEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Push(string line) => onEvent.Invoke(adapter, [null, JbzProtocolParser.ParseLine(line)]);

        Push(":TESTPIN,125,ON");
        Assert(previews[^1].SequenceEqual([125]) && frames.Count == 0,
            "TESTPIN ON updates live contacts without creating an authoritative result");
        Push(":OPEN,82");
        Assert(previews[^1].SequenceEqual([125]) && frames.Count == 1 && !frames[^1].Complete,
            "OPEN changes electrical preview without changing probe contacts or completing production");
        Push(":TESTPIN,125,OFF");
        Assert(previews[^1].Length == 0 && frames.Count == 1,
            "OFF removes the exact contact immediately without publishing a production frame");
        Push(":OPEN,82,82,125");
        Assert(previews[^1].Length == 0 && frames.Count == 2 && !frames[^1].Complete,
            "OPEN after OFF remains independent of the cleared probe state");
        Push(":TESTPIN,189,ON");
        Push(":TESTPIN,190,ON");
        Assert(previews[^1].SequenceEqual([190, 189]), "Two simultaneous contacts render newest first");
        int previewCount = previews.Count;
        Push(":TESTPIN,190,ON");
        Assert(previews.Count == previewCount, "Duplicate ON does not create a duplicate contact");
        Push(":TESTPIN,189,OFF");
        Assert(previews[^1].SequenceEqual([190]), "OFF on one pin preserves the other active pin");
        Push(":TESTPIN,190,OFF");
        Push(":TESTPIN,113,ON");
        Push(":TESTPIN,126,ON");
        Push(":TESTPIN,114,ON");
        Push(":TESTPIN,113,OFF");
        Assert(previews[^1].SequenceEqual([114, 126]), "Three-contact trace keeps surviving contacts in latest-first order");
        Push(":CLEAR");
        Assert(previews[^1].Length == 0, "CLEAR removes all live TESTPIN contacts");
        Push(":TESTPIN,200,ON");
        adapter.ConfigureModel(model.Product);
        Assert(previews[^1].Length == 0, "Model change removes stale TESTPIN contacts");
        Push(":PIN,15,1");
        Assert(previews[^1].SequenceEqual([15]), "Exact PIN,physical,1 compatibility maps to ON");
        Push(":PIN,15,0");
        Assert(previews[^1].Length == 0, "Exact PIN,physical,0 compatibility maps to OFF");
        Push(":TESTPIN,125,ON");
        adapter.StopScanAsync().GetAwaiter().GetResult();
        Assert(previews[^1].Length == 0, "STOP clears all live contacts even when the COM transport is idle");
        Push(":TESTPIN,125,ON");
        adapter.ResetClearAsync().GetAwaiter().GetResult();
        Assert(previews[^1].Length == 0, "Leaving the Test view clears live contacts without UART commands");
        Assert(JbzProtocolParser.ParseLine(":PIN,15,2").Family == JbzEventFamily.Raw &&
               JbzProtocolParser.ParseLine(":TESTPIN,15,MAYBE").Family == JbzEventFamily.Raw,
            "Unproven PIN and TESTPIN forms remain raw diagnostics");
        foreach (string raw in File.ReadAllLines(Path.Combine(fixtureRoot, "pin_probe_trace_rx.txt")))
            Push(raw);
        Assert(previews[^1].Length == 0 && frames.All(frame => !frame.Complete),
            "Reference probe fixture ends with no orphan contact and no authoritative production result");
        string boardSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Services", "JbzSerialBoardTransport.cs"));
        Assert(!boardSource.Contains(":PINTEST", StringComparison.Ordinal),
            "Production UART transport never sends PINTEST");
        adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void TestJbzFirmwareProtocol()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jbz-firmware-{Guid.NewGuid():N}.hex");
        try
        {
            File.WriteAllLines(path,
            [
                ":020000040800F2",
                ":048000000102030472",
                ":0400000508008189E5",
                ":00000001FF"
            ]);
            JbzFirmwareImage image = JbzIntelHexParser.Load(path, requireProvenImageRange: false);
            Assert(image.TotalBytes == 4 && image.FirstAddress == 0x08008000 && image.LastAddressExclusive == 0x08008004,
                "Intel HEX address reconstruction");
            byte[] packet = JbzFirmwareUpdateService.BuildProgramPacket(image.Blocks[0]);
            Assert(packet.SequenceEqual(new byte[] { 0x50, 0x00, 0x80, 0x00, 0x08, 0x04, 0x00, 1, 2, 3, 4, 10 }),
                "Firmware packet must be P + address LE32 + length LE16 + data + additive checksum");
            bool rejected = false;
            try { _ = JbzIntelHexParser.Load(path, requireProvenImageRange: true); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Production preflight must reject a HEX outside the trace-proven exact range");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void TestPartNumberResolver()
    {
        string root = Path.Combine(Path.GetTempPath(), "JBZPartResolver", Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(root, "unused-profile");
        Directory.CreateDirectory(Path.Combine(root, "Models"));
        Directory.CreateDirectory(Path.Combine(root, "Setups"));
        try
        {
            string modelPath = Path.Combine(root, "Models", "321085.model");
            string setupPath = Path.Combine(root, "Setups", "321085.setup");
            File.WriteAllText(modelPath, "[Common]\nModel=321085\n");
            File.WriteAllText(setupPath, "[Common]\nModel=/home/pi/Models/321085.model\n[WaterProof]\nUse=1\n");
            JbzPartFiles pair = JbzPartFileResolver.Resolve("321085", root, profile);
            Assert(pair.ModelPath == modelPath && pair.SetupPath == setupPath,
                "Part number resolves both files beside the application without a file picker");
            Assert(JbzSetupParser.Load(pair.SetupPath, pair.ModelPath).Get("WaterProof", "Use") == "1",
                "Legacy setup remains local configuration, not board commands");
            AssertThrows<InvalidDataException>(() => JbzPartFileResolver.Resolve("../321085", root, profile),
                "Part number cannot escape Models/Setups directories");
            File.Delete(setupPath);
            AssertThrows<FileNotFoundException>(() => JbzPartFileResolver.Resolve("321085", root, profile),
                "Missing paired setup must fail before board upload");

            string uploadDialogSource = File.ReadAllText(Path.Combine(
                Environment.CurrentDirectory, "Views", "JbzPartSelectionWindow.xaml.cs"));
            Assert(uploadDialogSource.Contains("XÁC NHẬN NẠP MODEL", StringComparison.Ordinal) &&
                   uploadDialogSource.Contains("DialogResult = true;", StringComparison.Ordinal) &&
                   !uploadDialogSource.Contains("NẠP MODEL HOÀN TẤT", StringComparison.Ordinal),
                "Model upload asks once and closes its progress window after success");

            string station = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string stationModel = Path.Combine(station, "Models", "321085.model");
            string stationSetup = Path.Combine(station, "Setups", "321085.setup");
            if (File.Exists(stationModel) && File.Exists(stationSetup))
            {
                JbzPartFiles actual = JbzPartFileResolver.Resolve("321085", profile, station);
                JbzCompiledModel compiled = JbzModelCompiler.Compile(actual.ModelPath);
                Assert(compiled.ModelName == "DL3" && compiled.Product.PartNumber == "WH321085" &&
                       compiled.Product.ProductName == "HEAD LAMP" && compiled.Product.VehicleType == "DL3" &&
                       compiled.Product.CustomerCode == "WH321085" && compiled.Commands.Count > 0 &&
                       compiled.Commands.First().Text == ":MODEL,DL3" &&
                       compiled.Commands.Last().Text == ":FINISH",
                    "Operator's 321085.model keeps the separate firmware model and product display fields");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

#if LEGACY_D2XX
    private static void TestProductPickerFilter()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo filter = typeof(HomeViewModel).GetMethod(
            "IsSupportedProductFile",
            flags)
            ?? throw new InvalidOperationException("Product picker filter method not found.");
        FieldInfo filterText = typeof(HomeViewModel).GetField("ProductFileFilter", flags)
            ?? throw new InvalidOperationException("Product picker filter text not found.");

        bool Accepts(string fileName) =>
            (bool)(filter.Invoke(null, [fileName]) ?? false);

        Assert(Accepts("sample.model"), ".model must be visible");
        Assert(Accepts("sample.MODEL"), ".MODEL must be visible");
        Assert(!Accepts("sample.tht"), ".tht must be hidden");
        Assert(!Accepts("sample.json"), ".json must be hidden");
        Assert(!Accepts("sample.jbzproduct.json"), ".jbzproduct.json must be hidden");
        Assert(
            string.Equals(
                filterText.GetRawConstantValue()?.ToString(),
                "JBZ Model (*.model)|*.model|All Files (*.*)|*.*",
                StringComparison.Ordinal),
            "Native dialog filter must select JBZ .model files");

        string pickerSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "HomeViewModel.cs"));
        string pickerGuardSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "FixedPositionOpenFileDialogGuard.cs"));
        Assert(pickerSource.Contains("DefaultExt = \".model\"", StringComparison.Ordinal) &&
               pickerSource.Contains("OriginalItemDirectory = @\"C:\\Item\"", StringComparison.Ordinal) &&
               pickerSource.Contains("FirstOrDefault(window => window.IsActive)", StringComparison.Ordinal) &&
               pickerSource.Contains("AutoUpgradeEnabled = false", StringComparison.Ordinal) &&
               pickerSource.Contains("dialog.ShowDialog(new NativeDialogOwner(owner))", StringComparison.Ordinal) &&
               pickerSource.Contains("FixedPositionOpenFileDialogGuard(owner)", StringComparison.Ordinal),
            "Product picker uses the original filter/directory and owner-bound classic native dialog");
        Assert(pickerGuardSource.Contains("GetWindow(handle, GwOwner) == _ownerHandle", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("MonitorFromWindow(", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("GetMonitorInfo(", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("PreferredDialogWidthDip = 640", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("PreferredDialogHeightDip = 440", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("DialogWidthWorkAreaRatio", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("MaximumDialogWidthDip = 900", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("VisualTreeHelper.GetDpi(owner)", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("DwmwaExtendedFrameBounds = 9", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("DwmGetWindowAttribute(", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("visibleWidth + insetLeft + insetRight", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("~WsThickFrame &", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("~WsMaximizeBox", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("SwpNoZOrder | SwpNoActivate", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("DispatcherPriority.ApplicationIdle", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("if (!IsWindow(dialogHandle))", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("CorrectAndLockLayout(dialogHandle)", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("LayoutCorrectionPasses", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("RunLayoutCorrectionPass", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("WmNcLButtonDown", StringComparison.Ordinal) &&
               !pickerGuardSource.Contains("ScMove", StringComparison.Ordinal) &&
               pickerGuardSource.Contains("ReleaseCreationHook();", StringComparison.Ordinal),
            "OpenFileDialog compensates the DWM frame for the original visible size, centers after Shell layout, and remains movable");
    }

#endif
#if LEGACY_D2XX
    private static void TestDiscardContactInterlock()
    {
        const string thtText =
            "파트번호\t파트명\n" +
            "DISCARD-TEST\tDISCARD SENSOR\n\n" +
            "번 호\t커넥터\t핀 수\n" +
            "1\t1\t2\n\n" +
            "커넥터\t선이름\tI/O\t핀번호\n" +
            "1\tMC01\t1\t1\n" +
            "1\tMC01\t2\t2\n" +
            "_DISCARD\t\t97\t1\n" +
            "_DISCARD\t\t98\t2\n\n" +
            "선이름\t선연결\t굵기\t색깔\n" +
            "MC01\t\t0.5\tB";
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZDiscardThtTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "discard.tht");
            File.WriteAllBytes(path, BuildMinimalThtFile(thtText));
            ProductModel parsed = new ThtModelParser().Load(path);
            Assert(parsed.DiscardContactIo.SequenceEqual([97, 98]) &&
                   parsed.HasDiscardInterlock &&
                   parsed.Pins.All(pin => pin.IoNumber is not 97 and not 98) &&
                   parsed.MaxIo == 98,
                "THT parser must preserve two _DISCARD IO with PinNo 1/2 while excluding them from product topology");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        var model = new ProductModel
        {
            Pins = [new PinRecord("1", "MC01", 1, "1")],
            DiscardContactIo = [97, 98]
        };
        Assert(model.HasDiscardInterlock && model.MaxIo == 98,
            "_DISCARD pair must extend active scan range without becoming a product pin");

        ScanFrame raw = FrameSeq(
            1,
            (1, new[] { 18 }),
            (97, new[] { 98 }));
        Assert(DiscardContactInterlock.IsContactClosed(raw, model.DiscardContactIo),
            "Normally-open _DISCARD contact closes when the configured pair is connected");

        ScanFrame filtered = DiscardContactInterlock.RemoveDiscardIo(
            raw,
            model.DiscardContactIo);
        Assert(!filtered.ActiveIo.Contains(97) &&
               !filtered.ActiveIo.Contains(98) &&
               filtered.Connections.ContainsKey(1) &&
               !filtered.Connections.ContainsKey(97),
            "Discard IO must remain visible to its monitor but never enter continuity/fault evaluation");

        var tracker = new DiscardContactInterlock();
        tracker.Arm(contactClosed: false);
        Assert(tracker.Observe(contactClosed: true) == DiscardContactTransition.FirstPassDetected &&
               tracker.IsArmed && !tracker.IsCompleted,
            "A fresh NG-bin sensor activation starts the one-pass confirmation");
        Assert(tracker.Observe(contactClosed: true) == DiscardContactTransition.None,
            "A held contact cannot complete the one-pass confirmation");
        Assert(tracker.Observe(contactClosed: false) == DiscardContactTransition.Completed &&
               tracker.IsCompleted,
            "The first OFF after ON completes the one-pass confirmation");

        tracker.Arm(contactClosed: true);
        Assert(tracker.Observe(contactClosed: true) == DiscardContactTransition.None &&
               tracker.Observe(contactClosed: false) == DiscardContactTransition.None &&
               tracker.Observe(contactClosed: true) == DiscardContactTransition.FirstPassDetected &&
               tracker.Observe(contactClosed: false) == DiscardContactTransition.Completed,
            "A sensor active at ARM must return open before one new ON-to-OFF pass can complete it");

        ScanFrame inputStyleSensorFrame = FrameSeq(2, (12, new[] { 97 }));
        Assert(DiscardContactInterlock.GetActiveContactIo(inputStyleSensorFrame, model.DiscardContactIo)
                .SequenceEqual([97]),
            "_DISCARD detection accepts a configured IO used as a target instead of requiring IO97<->IO98");

        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            UseTestPointer = true
        };
        TestViewModel discardVm = CreateTestViewModel(production, out FakeBoard discardBoard);
        var discardModel = Model(("PAIR", new[] { 1, 18 }));
        discardModel.ModelName = "SELF-TEST-DISCARD-INTERLOCK";
        discardModel.PartNumber = "SELF-TEST-DISCARD-INTERLOCK";
        discardModel.DiscardContactIo = [97, 98];
        discardVm.LoadPreparedModelAsync(discardModel).GetAwaiter().GetResult();
        discardVm.StartProductionTestAsync().GetAwaiter().GetResult();
        int totalBeforeDiscard = discardVm.Total;
        int failBeforeDiscard = discardVm.Fail;

        discardBoard.Publish(FrameSeq(10, (12, new[] { 97 })));
        Assert(discardVm.IsProductRemovalPending &&
               discardVm.ProbeContacts.Select(row => row.WireName)
                   .SequenceEqual(["_DISCARD IO(97)", "_DISCARD IO(98)"]),
            "First _DISCARD activation locks TEST and temporarily shows both configured _DISCARD rows");
        discardBoard.Publish(FrameSeq(11));
        Assert(!discardVm.IsProductRemovalPending &&
               discardVm.Total == totalBeforeDiscard &&
               discardVm.Fail == failBeforeDiscard,
            $"One _DISCARD ON-to-OFF pass unlocks TEST without recording a product result " +
            $"(pending={discardVm.IsProductRemovalPending}, total={discardVm.Total}, fail={discardVm.Fail}, state={discardVm.State})");

        TestViewModel faultDiscardVm = CreateTestViewModel(production, out FakeBoard faultDiscardBoard);
        faultDiscardVm.LoadPreparedModelAsync(discardModel).GetAwaiter().GetResult();
        faultDiscardVm.StartProductionTestAsync().GetAwaiter().GetResult();
        MethodInfo armFaultRemoval = typeof(TestViewModel).GetMethod(
            "ArmFaultProductRemoval",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-removal ARM method not found");
        MethodInfo tryCompleteFaultRemoval = typeof(TestViewModel).GetMethod(
            "TryCompleteFaultProductRemoval",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-removal completion method not found");
        FieldInfo faultProductRemoved = typeof(TestViewModel).GetField(
            "_faultProductRemoved",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-removal product gate not found");

        armFaultRemoval.Invoke(faultDiscardVm, [discardModel]);
        faultProductRemoved.SetValue(faultDiscardVm, 1);
        tryCompleteFaultRemoval.Invoke(faultDiscardVm, null);
        Assert(faultDiscardVm.IsProductRemovalPending &&
               faultDiscardVm.CenterResultText == "ĐƯA HÀNG VÀO THÙNG HÀNG LỖI" &&
               faultDiscardVm.ResultStatusText == "CHỜ XÁC NHẬN THÙNG LỖI",
            "Product removal alone cannot unlock a discard-required FAIL and both UI regions show the discard gate");

        faultDiscardBoard.Publish(FrameSeq(20, (12, new[] { 97 })));
        Assert(faultDiscardVm.IsProductRemovalPending,
            "Discard OFF-to-ON is only the entry edge and cannot complete fault removal");
        faultDiscardBoard.Publish(FrameSeq(21));
        Assert(!faultDiscardVm.IsProductRemovalPending &&
               faultDiscardVm.State == "CHỜ LẮP SẢN PHẨM" &&
               faultDiscardVm.ResultStatusText == "LẮP SẢN PHẨM" &&
               faultDiscardVm.CenterResultText == "LẮP SẢN PHẨM",
            "One discard ON-to-OFF pass completes both gates and returns every UI state to ready");

        string testViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(
            System.Text.RegularExpressions.Regex.Matches(
                testViewModelSource,
                "new JBZUniveresalLunix\\.Views\\.FaultConfirmationWindow\\(").Count == 2 &&
            System.Text.RegularExpressions.Regex.Matches(
                testViewModelSource,
                "ShowFaultConfirmationDialog\\(").Count == 5,
            "Product FAIL and device communication faults use the shared confirmation window");

        int confirmationStart = testViewModelSource.IndexOf(
            "private void ShowFaultConfirmationDialog(",
            StringComparison.Ordinal);
        int waitingTextStart = testViewModelSource.IndexOf(
            "private static string FaultRemovalWaitingText(",
            confirmationStart,
            StringComparison.Ordinal);
        string confirmationMethod = testViewModelSource[confirmationStart..waitingTextStart];
        Assert(!confirmationMethod.Contains("DiscardPassword", StringComparison.Ordinal),
            "_DISCARD product FAIL confirmation must not request a password");

        string settingsXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "ProductionSettingsPage.xaml"));
        Assert(!settingsXaml.Contains("Settings.DiscardPassword", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Settings.Password", StringComparison.Ordinal) &&
               !settingsXaml.Contains("LabelSettingsLockPanel", StringComparison.Ordinal) &&
               !settingsXaml.Contains("LabelUnlockPasswordBox", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Mật khẩu thùng lỗi", StringComparison.Ordinal),
            "Production settings exposes neither NG-bin nor label-print passwords");

        int finalRejectStart = testViewModelSource.IndexOf(
            "private async Task HandleFinalPassRejectedAsync(",
            StringComparison.Ordinal);
        int nextMethodStart = testViewModelSource.IndexOf(
            "private async Task RecoverAfterUncommittedFailAsync(",
            finalRejectStart,
            StringComparison.Ordinal);
        string finalRejectMethod = testViewModelSource[finalRejectStart..nextMethodStart];
        Assert(finalRejectMethod.Contains(
                   "ShowFaultConfirmationDialog(faults, cycleModel);",
                   StringComparison.Ordinal) &&
               finalRejectMethod.Contains(
                   "ArmFaultProductRemoval(cycleModel);",
                   StringComparison.Ordinal) &&
               !finalRejectMethod.Contains(
                   "Interlocked.Exchange(ref _discardRequiredForFault, 0)",
                   StringComparison.Ordinal),
            "Final PASS rejection must require _DISCARD sensor completion like every other FAIL");
    }

#endif
    private static void TestProductionScanFirstFrameAfterSequenceReset()
    {
        var board = new FakeBoard();
        board.Publish(CreateProductionFrame(sequence: 1));
        board.SetAppliedScanCapacityForTest(1);
        board.StartScanCallback = current =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                current.Publish(CreateProductionFrame(sequence: 1));
            });
        };

        var supervisor = new ScanSupervisor(board, _ => { });
        supervisor.StartProductionScanAndVerifyFrameAsync(
                BoardCapacity.MaxGlobalIo,
                CancellationToken.None,
                "SELF_TEST_10_MODULES")
            .GetAwaiter()
            .GetResult();

        Assert(
            board.Commands.Count(command => command == "START") == 1,
            "Changing active capacity restarts firmware scan exactly once");

        var sameCapacityBoard = new FakeBoard();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            sameCapacityBoard.Publish(CreateProductionFrame(sequence: 2));
        });
        new ScanSupervisor(sameCapacityBoard, _ => { })
            .StartProductionScanAndVerifyFrameAsync(
                BoardCapacity.MaxGlobalIo,
                CancellationToken.None,
                "SELF_TEST_SAME_CAPACITY")
            .GetAwaiter()
            .GetResult();
        Assert(
            sameCapacityBoard.Commands.All(command => command is not "START" and not "STOP"),
            "Same active capacity reuses the healthy scan without STOP/START");

        // Regression cho call path thật của TestViewModel: stream có thể đang chạy
        // active=1 từ lúc Connect, sau đó model được nạp và requested đổi thành 8.
        // EnsureContinuousProductionScanAsync không được return sớm chỉ vì IsScanning.
        TestViewModel vm = CreateTestViewModel(new ProductionSettings(), out FakeBoard vmBoard);
        vmBoard.SetRequestedScanCapacityForTest(8);
        vmBoard.SetAppliedScanCapacityForTest(1);
        MethodInfo ensureContinuous = typeof(TestViewModel).GetMethod(
            "EnsureContinuousProductionScanAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureContinuousProductionScanAsync method not found.");
        ((Task)ensureContinuous.Invoke(vm, [])!).GetAwaiter().GetResult();
        Assert(vmBoard.Commands.All(command => command != "START") &&
               vm.State == "CHỜ CHỌN MÃ HÀNG",
            "Startup without a local model waits for selection without issuing START");

        Assert(
            ScanSupervisor.ResolveFirstFrameTimeoutMs(BoardCapacity.Create(10)) == 15_000,
            "Ten-module first-frame watchdog must allow the measured long hardware frame");
        Assert(
            ScanSupervisor.ResolveProductionStallTimeoutMs(BoardCapacity.Create(10)) == 17_500,
            "Ten-module background stall watchdog must not interrupt a valid frame");

        static ScanFrame CreateProductionFrame(long sequence) => new(
            DateTime.Now,
            BoardCapacity.MaxExpansionCardCount,
            new HashSet<int>(),
            [],
            true,
            0,
            sequence,
            new Dictionary<int, IReadOnlySet<int>>(),
            new Dictionary<int, int>(),
            BoardScanMode.Production);
    }

    private static void TestScanWatchdogRecovery()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var board = new FakeBoard();
        var supervisor = new ScanSupervisor(board, _ => { }, clock);

        supervisor.EnsureProductionScanAsync(BoardCapacity.MaxGlobalIo, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Starting,
            "A reused production stream must pass through the first-frame gate");

        board.Publish(HealthFrame(1, 1, complete: false));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Starting,
            "An incomplete frame cannot arm Monitoring");
        board.Publish(HealthFrame(2, 1));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "The first complete frame enters Monitoring");

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert(!supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
            "A healthy stream below its stall timeout does not recover");

        supervisor.Suspend("Resistance");
        board.StopScanAsync().GetAwaiter().GetResult();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Suspended &&
               !supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
            "An intentional resistance STOP remains healthy beyond watchdog timeout");
        board.SetConnectionStateForTest(false);
        Assert(!supervisor.TryBeginDisconnectedRecovery(out _),
            "Suspended resistance phase cannot trigger watchdog reopen recovery");
        board.SetConnectionStateForTest(true);

        supervisor.EnsureProductionScanAsync(BoardCapacity.MaxGlobalIo, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Starting,
            "START after an intentional pause resets the baseline and enters Starting");
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert(!supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
            "Starting cannot fault a few milliseconds later from the pre-STOP frame age");
        board.Publish(HealthFrame(3, 2, complete: false));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Starting,
            "Raw receive activity without a complete frame keeps the first-frame gate armed");
        board.Publish(HealthFrame(4, 2));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "A complete frame from the new generation resumes Monitoring");

        for (int cycle = 0; cycle < 40; cycle++)
        {
            supervisor.Suspend("Resistance");
            board.StopScanAsync().GetAwaiter().GetResult();
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert(!supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
                $"AUTO-R cycle {cycle + 1}: watchdog stays suspended for the complete resistance window");
            supervisor.EnsureProductionScanAsync(BoardCapacity.MaxGlobalIo, CancellationToken.None)
                .GetAwaiter().GetResult();
            board.Publish(HealthFrame(20 + cycle, 3 + cycle));
            Assert(supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
                $"AUTO-R cycle {cycle + 1}: monitoring resumes only after the requested START and complete frame");
        }

        supervisor.Suspend("ProductionReconfigure");
        board.StopScanAsync().GetAwaiter().GetResult();
        supervisor.EnsureProductionScanAsync(BoardCapacity.MaxGlobalIo, CancellationToken.None)
            .GetAwaiter().GetResult();
        board.Publish(HealthFrame(60, 42));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Starting,
            "A complete frame from the stale generation is ignored");
        board.Publish(HealthFrame(61, 43));
        Assert(supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "A complete frame from the new generation is accepted");

        board.Publish(HealthFrame(62, 43, complete: false));
        clock.Advance(TimeSpan.FromMilliseconds(1_001));
        Assert(supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out ScanHealthSnapshot stalled) &&
               stalled.State == ScanHealthState.Recovering,
            "Raw bytes with frames=0 eventually trigger a Monitoring stall recovery");
        Assert(!supervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
            "A second watchdog tick cannot start duplicate recovery");

        board.StartScanCallback = current => current.Publish(HealthFrame(63, 44));
        Assert(supervisor.RecoverSoftAsync(
                    BoardCapacity.MaxGlobalIo,
                    BoardScanMode.Production,
                    CancellationToken.None)
                .GetAwaiter().GetResult() &&
               supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "Soft STOP/START recovery succeeds after its first complete frame");

        supervisor.BeginRecovery("force-soft-failure");
        board.StartFailuresRemaining = 1;
        Assert(!supervisor.RecoverSoftAsync(
                    BoardCapacity.MaxGlobalIo,
                    BoardScanMode.Production,
                    CancellationToken.None)
                .GetAwaiter().GetResult(),
            "A failed soft restart is reported for reopen escalation");
        board.StartScanCallback = current => current.Publish(HealthFrame(9, 5));
        Assert(supervisor.RecoverReopenAsync(
                    BoardCapacity.MaxGlobalIo,
                    BoardScanMode.Production,
                    CancellationToken.None)
                .GetAwaiter().GetResult() &&
               board.ConnectAttempts == 1 &&
               board.IsConnected && board.IsScanning &&
               board.CurrentScanMode == BoardScanMode.Production &&
               supervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "Reopen recovery restores the same scan mode and reaches Monitoring");

        var uartBoard = new FakeBoard { ProducesPassiveScanFrames = false };
        var uartClock = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var uartSupervisor = new ScanSupervisor(uartBoard, _ => { }, uartClock);
        uartSupervisor.EnsureProductionScanAsync(BoardCapacity.MaxGlobalIo, CancellationToken.None)
            .GetAwaiter().GetResult();
        uartClock.Advance(TimeSpan.FromSeconds(30));
        Assert(uartSupervisor.HealthSnapshot.State == ScanHealthState.Monitoring &&
               !uartSupervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _),
            "UART event scan must not trigger a false first-frame or passive-frame stall recovery");
        uartSupervisor.StartProductionScanAndVerifyFrameAsync(
            BoardCapacity.MaxGlobalIo, CancellationToken.None, "UART_AFTER_MODEL_UPLOAD")
            .GetAwaiter().GetResult();
        Assert(uartBoard.Commands.All(command => command is not "STOP" and not "START"),
            "UART scan reuse does not send another command just to obtain an empty frame");
        uartBoard.StopScanAsync().GetAwaiter().GetResult();
        Assert(uartSupervisor.TryBeginWatchdogRecovery(1_500, 1_000, out _) &&
               uartSupervisor.RecoverSoftAsync(BoardCapacity.MaxGlobalIo,
                   BoardScanMode.Production, CancellationToken.None).GetAwaiter().GetResult() &&
               uartSupervisor.HealthSnapshot.State == ScanHealthState.Monitoring,
            "UART scan-stopped recovery restarts without requiring a passive frame");

        string source = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        int recoveryStart = source.IndexOf(
            "private async Task<bool> RecoverProductionScanAsync",
            StringComparison.Ordinal);
        int recoveryEnd = source.IndexOf(
            "private async Task HardwareMonitorLoopAsync",
            recoveryStart,
            StringComparison.Ordinal);
        string recoveryMethod = source[recoveryStart..recoveryEnd];
        Assert(recoveryMethod.Contains("_lifetimeCts.Token", StringComparison.Ordinal) &&
               !recoveryMethod.Contains("CurrentCycleToken", StringComparison.Ordinal) &&
               !recoveryMethod.Contains("Record", StringComparison.Ordinal) &&
               !recoveryMethod.Contains("SetRelay", StringComparison.Ordinal) &&
               !recoveryMethod.Contains("Print", StringComparison.Ordinal),
            "Recovery is scoped to transport lifetime and cannot repeat production side effects");
        Assert(source.Split("_scanSupervisor.Suspend(\"Resistance\")", StringSplitOptions.None).Length - 1 >= 3,
            "Manual, Master and automatic resistance paths suspend ScanSupervisor before TestEngine stops scan");
        int uploadStart = source.IndexOf("public async Task<ProductModel?> LoadSelectedModelFromPathAsync", StringComparison.Ordinal);
        int uploadEnd = source.IndexOf("public async Task<ProductModel?> LoadPreparedModelAsync", uploadStart, StringComparison.Ordinal);
        string uploadMethod = source[uploadStart..uploadEnd];
        Assert(uploadMethod.Contains("await _scanRecoveryGate.WaitAsync(ct)", StringComparison.Ordinal) &&
               uploadMethod.Contains("_scanSupervisor.Suspend(\"ModelUpload\")", StringComparison.Ordinal) &&
               uploadMethod.Contains("await _board.ConfigureModelAsync(model, progress, ct)", StringComparison.Ordinal) &&
               uploadMethod.Contains("_scanRecoveryGate.Release()", StringComparison.Ordinal),
            "Operator model transfer delegates atomic STOP/MODEL to the adapter while scan recovery stays suspended");
        string adapterSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory, "Services", "JbzBoardTransportAdapter.cs"));
        Assert(adapterSource.Split("await _modelOperationGate.WaitAsync(ct)", StringSplitOptions.None).Length - 1 >= 4 &&
               adapterSource.Split("_modelOperationGate.Release()", StringSplitOptions.None).Length - 1 >= 4,
            "UART START and explicit model transfer share one board-side model operation gate");
        Assert(adapterSource.Contains("case JbzEventFamily.Open:", StringComparison.Ordinal) &&
               adapterSource.Contains("PublishContinuityPreview", StringComparison.Ordinal) &&
               adapterSource.Contains("case JbzEventFamily.Short:", StringComparison.Ordinal),
            "Rev 1.42 OPEN/SHORT/OTHER events must drive realtime Test-screen presentation previews");
        string serialSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory, "Services", "JbzSerialBoardTransport.cs"));
        Assert(serialSource.Contains("Task<JbzBoardEvent> acknowledgment = WaitAsync(JbzEventFamily.Stop", StringComparison.Ordinal) &&
               serialSource.Contains("await acknowledgment", StringComparison.Ordinal) &&
               serialSource.Contains("await WaitAsync(JbzEventFamily.Start", StringComparison.Ordinal) &&
               serialSource.Contains("await SendCoreAsync($\":MAXEXT,{maxExt}\"", StringComparison.Ordinal),
            "UART STOP/START transitions must wait for firmware state ACK and START must send MAXEXT immediately");
        Assert(!source.Contains("StartTestCycleAsync(cycleToken)", StringComparison.Ordinal) &&
               !adapterSource.Contains("IJbzOriginalTestCycleControl", StringComparison.Ordinal) &&
               !serialSource.Contains("TestOnAsync", StringComparison.Ordinal),
            "Universal Tester New Production ARM must reuse START and have no legacy TESTON sender");

        static ScanFrame HealthFrame(long sequence, long generation, bool complete = true) => new(
            DateTime.Now,
            BoardCapacity.MaxExpansionCardCount,
            new HashSet<int>(),
            [],
            complete,
            0,
            sequence,
            new Dictionary<int, IReadOnlySet<int>>(),
            new Dictionary<int, int>(),
            BoardScanMode.Production,
            TerminatorKnown: complete,
            ScanGeneration: generation);
    }

    private static void TestProductionDataUpgrade()
    {
        string root = Path.Combine(Path.GetTempPath(), "JBZUpgradeTest_" + Guid.NewGuid().ToString("N"));
        string older = Path.Combine(root, "V16.0.41");
        string previous = Path.Combine(root, "V16.0.44");
        string target = Path.Combine(root, "V16.0.45");
        try
        {
            Directory.CreateDirectory(Path.Combine(older, "Data"));
            Directory.CreateDirectory(Path.Combine(previous, "Data"));
            Directory.CreateDirectory(target);

            File.WriteAllText(Path.Combine(older, "production.statistics.json"), "older-statistics-with-real-production-data");
            File.WriteAllText(Path.Combine(older, "production.settings.json"), "{\"LotNo\":3456}");
            File.WriteAllText(Path.Combine(previous, "production.statistics.json"), "latest-statistics");
            File.WriteAllText(Path.Combine(previous, "production.settings.json"), "{\"LotNo\":2000}");
            File.WriteAllText(Path.Combine(previous, "JBZUniveresalLunix.cfg"), "[LotNo]3456\r\n");
            File.WriteAllText(Path.Combine(previous, "JBZUniveresalLunix.log"), "canonical-log");
            File.WriteAllText(Path.Combine(previous, "PartCnt.txt"), "PART-A 200000 4321");
            File.WriteAllBytes(
                Path.Combine(previous, "Data", "JBZUniveresalLunix.db"),
                [1, 2, 3, 4]);

            // A value already created on the destination machine is authoritative.
            File.WriteAllText(Path.Combine(target, "PartCnt.txt"), "PART-A 200000 5000");

            IReadOnlyList<string> migrated = ProductionDataUpgradeService.MigrateMissingProductionData(
                target,
                [previous, older]);

            Assert(
                !File.Exists(Path.Combine(target, "production.statistics.json")) &&
                !File.Exists(Path.Combine(target, "production.settings.json")),
                "Legacy JSON files must not be copied into the canonical runtime directory");
            Assert(
                File.ReadAllText(Path.Combine(target, "JBZUniveresalLunix.cfg")) == "[LotNo]3456\r\n",
                "Canonical CFG must be inherited");
            Assert(
                File.ReadAllText(Path.Combine(target, "PartCnt.txt")) == "PART-A 200000 5000",
                "Existing destination PartCnt must never be overwritten");
            Assert(
                File.ReadAllBytes(Path.Combine(target, "Data", "JBZUniveresalLunix.db"))
                    .SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "Canonical SQLite database must be inherited");
            Assert(
                !File.Exists(Path.Combine(target, "JBZUniveresalLunix.log")),
                "Runtime logs must never be inherited from an older version");
            Assert(migrated.Count == 2, "Only missing production state files must be reported as migrated");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void TestFaultDisplayFormatter()
    {
        var open = new FaultDetail
        {
            Type = ProductFaultType.OpenCircuit,
            ConnectorFrom = "CN1",
            PinFrom = "4",
            ConnectorTo = "CN3",
            PinTo = "6",
            WireColor = "WH"
        };
        OperatorFaultDisplay openOperator = FaultDisplayFormatter.FormatOperator(open);
        CustomerFaultDisplay openCustomer = FaultDisplayFormatter.FormatCustomer(open);
        Assert(openOperator.Title == "KIỂM TRA LỖI HỞ MẠCH", "Open operator instruction");
        Assert(openOperator.Lines.Any(line => line.Value.Contains("CN1 - Chân 4 ↔ CN3 - Chân 6", StringComparison.Ordinal)), "Open standard connection");
        Assert(openOperator.Lines.Any(line => line.Label == "Màu dây tiêu chuẩn" && line.Value == "TRẮNG"), "Open standard color");
        Assert(openOperator.Lines.Any(line => line.Value == "KHÔNG CÓ KẾT NỐI"), "Open actual condition");
        Assert(openCustomer.FaultType == "OPEN CIRCUIT" && openCustomer.Actual == "NO CONTINUITY", "Open customer mapping");

        var wrong = new FaultDetail
        {
            Type = ProductFaultType.WrongWiring,
            WireName = "W12",
            WireColor = "RED",
            ConnectorFrom = "CN1",
            PinFrom = "3",
            ConnectorTo = "CN2",
            PinTo = "8",
            ActualConnectorFrom = "CN1",
            ActualPinFrom = "3",
            ActualConnectorTo = "CN1",
            ActualPinTo = "5"
        };
        OperatorFaultDisplay wrongOperator = FaultDisplayFormatter.FormatOperator(wrong);
        Assert(wrongOperator.Title == "KIỂM TRA LỖI SAI DÂY", "Wrong connection operator instruction");
        Assert(wrongOperator.Lines.Any(line => line.Label == "Vị trí tiêu chuẩn"), "Wrong standard position");
        Assert(wrongOperator.Lines.Any(line => line.Label == "Vị trí thực tế"), "Wrong actual position");
        Assert(FaultDisplayFormatter.FormatCustomer(wrong).FaultType == "INCORRECT CONNECTION", "Wrong customer mapping");

        var shortFault = new FaultDetail
        {
            Type = ProductFaultType.ShortCircuit,
            ActualConnectorFrom = "CN4",
            ActualPinFrom = "2",
            ActualConnectorTo = "CN6",
            ActualPinTo = "9"
        };
        OperatorFaultDisplay shortOperator = FaultDisplayFormatter.FormatOperator(shortFault);
        Assert(shortOperator.Title == "KIỂM TRA LỖI CHẬP MẠCH", "Short operator instruction");
        Assert(shortOperator.Lines.Any(line => line.Value.Contains("CN4 - Chân 2 ↔ CN6 - Chân 9", StringComparison.Ordinal)), "Short actual connection");
        Assert(FaultDisplayFormatter.FormatCustomer(shortFault).FaultType == "SHORT CIRCUIT", "Short customer mapping");

        var resistanceHigh = new FaultDetail
        {
            Type = ProductFaultType.ResistanceOutOfRange,
            WireName = "CN3 Pin 4 ↔ CN5 Pin 2",
            ResistanceMin = 950,
            ResistanceMax = 1050,
            MeasuredResistance = 1370
        };
        OperatorFaultDisplay resistanceHighOperator = FaultDisplayFormatter.FormatOperator(resistanceHigh);
        CustomerFaultDisplay resistanceHighCustomer = FaultDisplayFormatter.FormatCustomer(resistanceHigh);
        Assert(resistanceHighOperator.Lines.Any(line => line.Value == "CAO HƠN GIỚI HẠN"), "Resistance high assessment");
        Assert(resistanceHighOperator.Title == "KIỂM TRA LỖI ĐIỆN TRỞ", "Resistance operator instruction");
        Assert(resistanceHighCustomer.Assessment == "ABOVE UPPER LIMIT", "Resistance high customer assessment");
        Assert(resistanceHighCustomer.Deviation == "+0.32 kΩ", "Resistance high deviation");

        var resistanceLow = new FaultDetail
        {
            Type = ProductFaultType.ResistanceOutOfRange,
            ResistanceMin = 950,
            ResistanceMax = 1050,
            MeasuredResistance = 900
        };
        Assert(FaultDisplayFormatter.FormatCustomer(resistanceLow).Assessment == "BELOW LOWER LIMIT", "Resistance low assessment");
        Assert(FaultDisplayFormatter.FormatCustomer(resistanceLow).Deviation == "-0.05 kΩ", "Resistance low deviation");

        Assert(FaultDisplayFormatter.OperatorFaultType("WRONG_WIRE_COLOR") == "SAI MÀU DÂY", "Wire color operator mapping");
        Assert(FaultDisplayFormatter.CustomerFaultType("TERMINAL_MISPOSITION") == "TERMINAL MISPOSITION", "Terminal customer mapping");
        Assert(FaultDisplayFormatter.CustomerFaultType("CROSSED_TERMINALS") == "CROSSED TERMINALS", "Crossed terminal mapping");
        Assert(FaultDisplayFormatter.FormatOperator(new FaultDetail()).Lines.Count > 0, "Missing fault fields remain displayable");
    }

    private static void TestUiPerformanceGuards()
    {
        Assert(!AsyncFileLogService.Current.FileLoggingEnabled,
            "Production startup keeps Data/Logs diagnostic file writing disabled");

        var red1 = WireColorToBrushConverter.ToBrush("R");
        var red2 = WireColorToBrushConverter.ToBrush("R");
        var stripe1 = WireColorToBrushConverter.ToBrush("W/B");
        var stripe2 = WireColorToBrushConverter.ToBrush("W/B");
        Assert(ReferenceEquals(red1, red2) && red1.IsFrozen, "Single wire color brush is cached and frozen");
        Assert(ReferenceEquals(stripe1, stripe2) && stripe1.IsFrozen, "Composite wire color brush is cached and frozen");
        AssertBalancedTwoColorBrush("R/W", "#FF0000", "#FFFFFF");
        AssertBalancedTwoColorBrush("W/R", "#FFFFFF", "#FF0000");
        AssertBalancedTwoColorBrush("R/W/G", "#FF0000", "#FFFFFF");
        AssertBalancedTwoColorBrush("B/Or", "#000000", "#FF8C00");
        AssertBalancedTwoColorBrush("Or/B", "#FF8C00", "#000000");

        var wrongRow = new FaultRow { Kind = FaultKind.WrongWiring, Color = "B/Br" };
        Assert(BrushHex(wrongRow.RowBackgroundBrush) == "#FFFFFF" &&
               BrushHex(wrongRow.RowForegroundBrush) == "#C62828",
            "Wrong wiring uses red text on an absolute white row");

        var probeRow = new FaultRow { Kind = FaultKind.Probe, Color = "B/L" };
        Assert(BrushHex(probeRow.RowBackgroundBrush) == "#FFFFFF" &&
               BrushHex(probeRow.RowForegroundBrush) == "#C62828",
            "Probe uses red text on an absolute white row");

        var connectedRow = new FaultRow { Kind = FaultKind.Info, FaultType = "THÔNG MẠCH" };
        Assert(BrushHex(connectedRow.RowBackgroundBrush) == "#FFFFFF" &&
               BrushHex(connectedRow.RowForegroundBrush) == "#111111",
            "Connected network uses black text on an absolute white row");

        var openGreenRow = new FaultRow { Kind = FaultKind.MissingConnection, Color = "G" };
        var openBlueRow = new FaultRow { Kind = FaultKind.Open, Color = "L" };
        var normalCheckRow = new FaultRow { Kind = FaultKind.Start, Status = "KIỂM TRA" };
        Assert(BrushHex(openGreenRow.RowBackgroundBrush) == "#FFFFFF" &&
               BrushHex(openBlueRow.RowBackgroundBrush) == "#FFFFFF" &&
               BrushHex(openGreenRow.RowForegroundBrush) == "#111111" &&
               BrushHex(openBlueRow.RowForegroundBrush) == "#111111",
            "Open/missing rows use bold black text on a white background");
        Assert(BrushHex(normalCheckRow.RowForegroundBrush) == "#555555" &&
               BrushHex(normalCheckRow.RowBackgroundBrush) == "#FFFFFF" &&
               normalCheckRow.Status == "KIỂM TRA",
            "Normal KIỂM TRA row uses the original dark text on absolute white");
        Assert(BrushHex(openGreenRow.WireColorBrush) == "#00D000" &&
               BrushHex(openBlueRow.WireColorBrush) == "#0000FF" &&
               BrushHex(new FaultRow { Color = "R" }.WireColorBrush) == "#FF0000" &&
               BrushHex(new FaultRow { Color = "P" }.WireColorBrush) == "#EAADEA" &&
               BrushHex(new FaultRow { Color = "B" }.WireColorBrush) == "#000000" &&
               BrushHex(new FaultRow { Color = "W" }.WireColorBrush) == "#FFFFFF" &&
               BrushHex(new FaultRow { Color = "Gr" }.WireColorBrush) == "#606060" &&
               BrushHex(new FaultRow { Color = "Br" }.WireColorBrush) == "#A62A2A" &&
               BrushHex(new FaultRow { Color = "Or" }.WireColorBrush) == "#FF8C00" &&
               BrushHex(new FaultRow { Color = "O" }.WireColorBrush) == "#FF8C00" &&
               BrushHex(new FaultRow { Color = "OG" }.WireColorBrush) == "#FF8C00" &&
               new FaultRow { Color = "Y/B" }.WireColorBrush is LinearGradientBrush &&
               new FaultRow { Color = "W" }.WireColorBrush is SolidColorBrush,
            "Single and striped wire-color backgrounds preserve the original tester palette");
        var cachedRow = new FaultRow { Io = 42, Connector = " CN1 ", Pin = " 7 ", Color = "L" };
        string cachedIoCnPn = cachedRow.IoCnPnText;
        Assert(cachedIoCnPn == "42-CN1-7" && ReferenceEquals(cachedIoCnPn, cachedRow.IoCnPnText),
            "FaultRow caches the formatted IO-CN-PN text after first access");
        Assert(new FaultRow { Color = "B/G" }.WireColorBrush is LinearGradientBrush,
            "Multi-color wire is combined into one striped Màu cell");
        Assert(typeof(FaultRow).GetProperty("Color1Brush") is null &&
               typeof(FaultRow).GetProperty("Color4Brush") is null,
            "FaultRow exposes only the single one-or-two-color cell presentation");

        Assert(typeof(TestViewModel).GetProperty("Logs") is null,
            "TestViewModel does not maintain an unused UI log collection");

        string testViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(testViewModelSource.Contains(
                   "ProcessScheduledEngineChangedOnUi(request, rows)",
                   StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "TryAcceptProductionUiSnapshot(request, rows)",
                   StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "AdvanceProductionUiCycleEpoch();",
                   StringComparison.Ordinal) &&
               !testViewModelSource.Contains(
                   "ProcessScheduledEngineChangedOnUi(Volatile.Read(ref _runtimeGeneration))",
                   StringComparison.Ordinal),
            "Coalesced engine UI callbacks preserve version identity and reject stale product cycles");

        var disabled = new ResistanceChannelEditor(
            new ResistanceChannelSetting { Enabled = true, Name = "R3", Channel = 3, MinOhm = 1, MaxOhm = 2 },
            3)
        {
            Enabled = false,
            ChannelSelection = 0
        }.ToSetting();
        Assert(!disabled.Enabled && disabled.Channel == 0 && disabled.Name == "R3",
            "Resistance UI channel 'Không dùng' saves disabled without losing internal name");

        var enabled = new ResistanceChannelEditor(
            new ResistanceChannelSetting { Enabled = false, Name = "R1", Channel = 0, MinOhm = 0.1, MaxOhm = 0.5 },
            1)
        {
            Enabled = true,
            ChannelSelection = 2
        }.ToSetting();
        Assert(enabled.Enabled && enabled.Channel == 2 && enabled.MinOhm == 0.1 && enabled.MaxOhm == 0.5,
            "Resistance UI active channel saves enabled and preserves min/max");

        var resistanceProduction = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ResistanceChannels =
            [
                new ResistanceChannelSetting
                {
                    Enabled = true,
                    Name = "R1",
                    Channel = 2,
                    MinOhm = 100,
                    MaxOhm = 200
                }
            ]
        };
        ProductModel modelWithoutThtResistance = Model(("PAIR", new[] { 1, 18 }));
        List<ResistanceStep> configuredSteps =
            ResistanceMeasurementPlan.BuildEnabledSteps(resistanceProduction);
        Assert(modelWithoutThtResistance.ResistanceSteps.Count == 0 &&
               configuredSteps is [{ Name: "R1", Channel: 2, MinOhm: 100, MaxOhm: 200 }],
            "Canonical Production Settings plan creates steps without THT resistance/legacy RouteA/RouteB");

        using var engine = CreateEngine(out _);
        engine.SetModel(Model(("PAIR", new[] { 1, 18 })));
        int changed = 0;
        engine.Changed += (_, _) => changed++;

        ScanFrame frame = Frame((1, new[] { 18 }));
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < 10_000; index++)
            engine.ProcessFrame(frame);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert(changed <= 1, "Identical complete frames do not raise unbounded engine UI updates");
        Console.WriteLine(
            $"PERF: 10,000 identical ProcessFrame calls: {stopwatch.ElapsedMilliseconds} ms, {allocated:N0} bytes allocated");

        (string Name, int[] Io)[] largeNetDefinitions = Enumerable.Range(0, 250)
            .Select(index => ($"NET{index + 1:000}", new[] { (index * 2) + 1, (index * 2) + 2 }))
            .ToArray();
        ProductModel largeModel = Model(largeNetDefinitions);
        Assert(ReferenceEquals(
                largeModel.Nets[0].ExpectedActiveIo,
                largeModel.Nets[0].ExpectedActiveIo),
            "WireNet caches its receiver endpoint list instead of rebuilding it on every engine query");

        (int Source, int[] Targets)[] largeConnections = largeNetDefinitions
            .Select(net => (net.Io[0], new[] { net.Io[1] }))
            .ToArray();
        using TestEngine largeEngine = CreateEngine(out _);
        largeEngine.SetModel(largeModel);
        ScanFrame allConnected = FrameSeq(1, largeConnections);
        largeEngine.ProcessFrame(allConnected);
        Assert(largeEngine.ContinuityPassed && largeEngine.BuildRows().Count == 0,
            "A 500-IO/250-network model still completes only when every required network is connected");

        largeEngine.ProcessFrame(FrameSeq(2, largeConnections[..^1]));
        FaultRow[] reopenedRows = largeEngine.BuildRows().ToArray();
        Assert(!largeEngine.ContinuityPassed &&
               reopenedRows.Length == 2 &&
               reopenedRows.All(row => row.WireName == "NET250"),
            "Removing one connection from a 500-IO model immediately reopens exactly that network");

        largeEngine.ProcessFrame(allConnected with { Sequence = 3 });
        ScanFrame repeatedLarge = allConnected with { Sequence = 4 };
        long largeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var largeStopwatch = Stopwatch.StartNew();
        for (int index = 0; index < 2_000; index++)
            largeEngine.ProcessFrame(repeatedLarge);
        largeStopwatch.Stop();
        long largeAllocated = GC.GetAllocatedBytesForCurrentThread() - largeAllocatedBefore;
        Assert(largeEngine.ContinuityPassed && largeEngine.BuildRows().Count == 0,
            "Repeated complete frames cannot lose a passed network in the optimized large-model path");
        Console.WriteLine(
            $"PERF: 2,000 identical 500-IO/250-network frames: {largeStopwatch.ElapsedMilliseconds} ms, " +
            $"{largeAllocated:N0} bytes allocated");
    }

    private static void TestAuthoritativeProductionState()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            IoConfirm1 = 1,
            IoConfirmN = 1
        };
        ProductModel model = Model(
            ("PAIR-A", new[] { 1, 3 }),
            ("PAIR-B", new[] { 2, 4 }));
        using TestEngine engine = CreateEngine(out _, production);
        engine.SetModel(model);

        ProductionElectricalSnapshot waiting = engine.GetProductionElectricalSnapshot();
        Assert(!waiting.ProductEvidence &&
               !waiting.RealtimeEvaluationEnabled &&
               !waiting.ContinuityComplete,
            "No product evidence is authoritative WaitingForProduct input");

        engine.ProcessFrame(FrameSeq(100, (1, new[] { 3 })));
        TestEnginePresentationSnapshot installing = engine.CapturePresentationSnapshot(removal: false);
        Assert(installing.Electrical.ProductEvidence &&
               installing.Electrical.RealtimeEvaluationEnabled &&
               !installing.Electrical.ContinuityComplete &&
               installing.Rows.Count(row => row.WireName == "PAIR-A") == 0 &&
               installing.Rows.Count(row => row.WireName == "PAIR-B") == 2,
            "One correct pair immediately enables realtime evaluation and removes only its rows");

        engine.ProcessFrame(FrameSeq(101));
        TestEnginePresentationSnapshot released = engine.CapturePresentationSnapshot(removal: false);
        Assert(!released.Electrical.ProductEvidence &&
               !released.Electrical.RealtimeEvaluationEnabled &&
               released.Rows.Count(row => row.WireName == "PAIR-A") == 2 &&
               released.Rows.Count(row => row.WireName == "PAIR-B") == 2,
            "Disconnecting the pair immediately restores its rows without waiting for full product state");

        engine.ProcessFrame(FrameSeq(102, (1, new[] { 4 })));
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(FrameSeq(103, (1, new[] { 4 })));
        ProductionElectricalSnapshot wrong = engine.GetProductionElectricalSnapshot();
        Assert(wrong.ProductEvidence &&
               wrong.RealtimeEvaluationEnabled &&
               !wrong.ContinuityComplete &&
               wrong.HasConfirmedWiringFault,
            "Wrong wiring remains confirmed during partial installation");

        var gate = new ProductionUiVersionGate();
        Assert(gate.TryAccept(10, 20, 30, 100, 1),
            "First production UI snapshot is accepted");
        Assert(!gate.TryAccept(9, 20, 30, 101, 2),
            "Older runtime generation cannot update UI");
        Assert(!gate.TryAccept(10, 19, 30, 101, 3),
            "Older product cycle cannot update UI");
        Assert(!gate.TryAccept(10, 20, 29, 999, 4),
            "Older D2XX scan generation cannot update UI");
        Assert(!gate.TryAccept(10, 20, 30, 99, 5),
            "Older complete-frame sequence cannot overwrite the latest UI");
        Assert(gate.TryAccept(10, 20, 30, 100, 6),
            "A newer confirmation revision of the same frame remains applicable");
        Assert(gate.TryAccept(10, 21, 0, 0, 7) &&
               !gate.TryAccept(10, 20, 30, 200, 8),
            "ProductRemoved cycle epoch invalidates every queued callback from the removed product");

        TestViewModel pipelineVm = CreateTestViewModel(production, out FakeBoard pipelineBoard);
        pipelineVm.SetModel(model);
        pipelineVm.StartProductionTestAsync().GetAwaiter().GetResult();
        long requestsBefore = pipelineVm.EngineUiRequestCount;
        long renderedBefore = pipelineVm.EngineUiRenderedCount;
        pipelineBoard.Publish(FrameSeq(104, (1, new[] { 3 })));
        Assert(pipelineVm.EngineUiRequestCount > requestsBefore &&
               pipelineVm.EngineUiRenderedCount > renderedBefore &&
               pipelineVm.EngineUiDispatcherEnqueueCount == 0,
            "Headless UI pipeline counts request and accepted inline render without claiming a Dispatcher enqueue");

        string pipelineSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(pipelineSource.Contains(
                   "Interlocked.Increment(ref _uiDispatcherEnqueueCount);\r\n                    await dispatcher.InvokeAsync",
                   StringComparison.Ordinal) ||
               pipelineSource.Contains(
                   "Interlocked.Increment(ref _uiDispatcherEnqueueCount);\n                    await dispatcher.InvokeAsync",
                   StringComparison.Ordinal),
            "Dispatcher enqueue counter increments only at the actual InvokeAsync call site");
        Assert(pipelineSource.Contains("UI_PIPELINE requests=", StringComparison.Ordinal) &&
               pipelineSource.Contains("dispatcher_enqueued=", StringComparison.Ordinal) &&
               pipelineSource.Contains("rendered={pipelineRendered} coalesced={coalesced}", StringComparison.Ordinal),
            "Periodic diagnostics expose separate request, Dispatcher enqueue, rendered and coalesced totals");
    }

    private static void TestModelAwareProductEvidence()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            IoConfirm1 = 1,
            IoConfirmN = 1
        };
        var board = new FakeBoard();
        using var engine = new TestEngine(
            board,
            new KeysightVisaService(),
            new AppSettings(),
            production,
            clock);
        engine.SetModel(Model(
            ("PAIR-A", new[] { 1, 2 }),
            ("PAIR-B", new[] { 3, 4 })));

        AssertWaiting("No activity", FrameSeq(1));
        for (int sequence = 2; sequence <= 101; sequence++)
            AssertWaiting("Sustained raw IO18", FrameSeq(sequence, (18, new[] { 18 })));
        AssertWaiting("Raw IO26 touch", FrameSeq(102, (26, new[] { 26 })));
        AssertWaiting("Raw IO26 release", FrameSeq(103));
        AssertWaiting("Sequential raw IO10", FrameSeq(104, (10, new[] { 10 })));
        AssertWaiting("Sequential raw IO18", FrameSeq(105, (18, new[] { 18 })));
        AssertWaiting("Sequential raw IO26", FrameSeq(106, (26, new[] { 26 })));
        AssertWaiting(
            "Two raw IOs without a logical edge",
            FrameSeq(107, (10, new[] { 10 }), (18, new[] { 18 })));
        engine.ProcessFrame(FrameSeq(108, (10, new[] { 18 })));
        ProductEvidenceSnapshot unmappedWrong = engine.GetProductEvidenceSnapshot();
        Assert(unmappedWrong.ValidProductEvidence &&
               unmappedWrong.WrongCandidateCount == 1 &&
               unmappedWrong.Reason == "WRONG_CANDIDATE" &&
               !engine.HasWiringFault,
            "A real non-self edge outside the THT is product evidence and becomes a realtime wrong-wire candidate");

        engine.ProcessFrame(FrameSeq(109, (1, new[] { 2 })) with { ScanGeneration = 2 });
        ProductEvidenceSnapshot expected = engine.GetProductEvidenceSnapshot();
        Assert(expected.ValidProductEvidence &&
               expected.State == ProductPresenceState.Present &&
               expected.ExpectedConnectedCount == 1 &&
               expected.Reason == "EXPECTED_CONNECTIVITY" &&
               engine.GetProductionElectricalSnapshot().ProductEvidence,
            "A valid expected pair immediately creates authoritative product presence");
        Assert(engine.BuildRows().All(row => row.WireName != "PAIR-A") &&
               engine.BuildRows().Count(row => row.WireName == "PAIR-B") == 2,
            "Realtime row delta removes a connected pair and restores it after disconnect");

        engine.ProcessFrame(FrameSeq(110));
        Assert(!engine.HasProductActivity &&
               engine.BuildRows().Count(row => row.WireName == "PAIR-A") == 2,
            "Disconnecting the valid pair restores Waiting and its rows");

        engine.ProcessFrame(FrameSeq(111, (1, new[] { 4 })));
        ProductEvidenceSnapshot wrongCandidate = engine.GetProductEvidenceSnapshot();
        Assert(wrongCandidate.ValidProductEvidence &&
               wrongCandidate.WrongCandidateCount == 1 &&
               !engine.ReadyToEvaluateProductFaults,
            "A first wrong model-aware edge creates product presence before full source coverage");
        clock.Advance(TimeSpan.FromMilliseconds(
            ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 1));
        engine.ProcessFrame(FrameSeq(112, (1, new[] { 4 })));
        Assert(engine.HasWiringFault &&
               engine.GetProductEvidenceSnapshot().WrongConfirmedCount == 1,
            "Wrong wiring confirmation remains realtime after semantic presence detection");

        engine.SetModel(Model(
            ("PAIR-A", new[] { 1, 2 }),
            ("PAIR-B", new[] { 3, 4 })));
        engine.ProcessFrame(FrameSeq(113, (2, new[] { 3 })));
        ProductEvidenceSnapshot shortCandidate = engine.GetProductEvidenceSnapshot();
        Assert(shortCandidate.ValidProductEvidence &&
               shortCandidate.ShortCandidateCount == 1,
            "A first model-aware cross-network short also creates product presence");
        clock.Advance(TimeSpan.FromMilliseconds(
            ProductionTimingPolicy.DefaultShortCircuitConfirmMs + 1));
        engine.ProcessFrame(FrameSeq(114, (2, new[] { 3 })));
        Assert(engine.HasWiringFault &&
               engine.GetProductEvidenceSnapshot().ShortConfirmedCount == 1,
            "Short confirmation remains realtime without waiting for an expected pair");

        // Universal Tester New already classifies :OTHER/:SHORT in firmware.
        // Those explicit hints must not pass through the legacy D2XX debounce gate.
        engine.SetModel(Model(
            ("PAIR-A", new[] { 1, 2 }),
            ("PAIR-B", new[] { 3, 4 })));
        var firmwareWrong = FrameSeq(1141, (1, new[] { 4 })) with
        {
            ExplicitFaults = new Dictionary<(int SourceIo, int TargetIo), ProductFaultType>
            {
                [(1, 4)] = ProductFaultType.WrongWiring
            }
        };
        engine.ProcessFrame(firmwareWrong);
        Assert(engine.HasWiringFault &&
               engine.GetProductEvidenceSnapshot().WrongConfirmedCount == 1,
            "Firmware OTHER is authoritative and confirms wrong wiring in the first CIRCUIT snapshot");

        engine.SetModel(Model(
            ("PAIR-A", new[] { 1, 2 }),
            ("PAIR-B", new[] { 3, 4 })));
        var firmwareShort = FrameSeq(1142, (2, new[] { 3 })) with
        {
            ExplicitFaults = new Dictionary<(int SourceIo, int TargetIo), ProductFaultType>
            {
                [(2, 3)] = ProductFaultType.ShortCircuit
            }
        };
        engine.ProcessFrame(firmwareShort);
        Assert(engine.HasWiringFault &&
               engine.GetProductEvidenceSnapshot().ShortConfirmedCount == 1,
            "Firmware SHORT is authoritative and confirms short circuit in the first CIRCUIT snapshot");

        engine.SetModel(Model(("PAIR-A", new[] { 1, 2 })));
        Assert(engine.ApplyContinuityPreviewSource(1, new[] { 2 }, sequence: 115) &&
               engine.HasContinuityPreviewProductActivity &&
               !engine.HasProductActivity &&
               !engine.GetProductEvidenceSnapshot().ProbeEvidence,
            "Presentation/Probe evidence is independent from authoritative ProductPresence");

        TestViewModel vm = CreateTestViewModel(production, out FakeBoard vmBoard);
        vm.SetModel(Model(("PAIR-A", new[] { 1, 2 })));
        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        ((Task)(typeof(TestViewModel).GetField(
            "_statisticsLoadTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(vm) ?? Task.CompletedTask)).GetAwaiter().GetResult();
        int totalBefore = vm.Total;
        int passBefore = vm.Pass;
        int failBefore = vm.Fail;
        string lotBefore = vm.Lot;
        for (int sequence = 200; sequence < 300; sequence++)
            vmBoard.Publish(FrameSeq(sequence, (10, new[] { 10 })));

        int productSoundFlag = (int)(typeof(TestViewModel).GetField(
            "_productStartSoundPlayed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(vm) ?? -1);
        bool productDetectedThisCycle = (bool)(typeof(TestViewModel).GetField(
            "_productDetectedThisCycle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(vm) ?? true);
        Assert(vm.CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct &&
               vm.Total == totalBefore && vm.Pass == passBefore && vm.Fail == failBefore &&
               vm.Lot == lotBefore && productSoundFlag == 0 && !productDetectedThisCycle &&
               !vmBoard.Commands.Any(command => command.StartsWith("SET:", StringComparison.Ordinal)),
            $"100 raw single-IO frames cannot start product lifecycle side effects " +
            $"(state={vm.CurrentProductionRuntimeState}, totals={vm.Total}/{vm.Pass}/{vm.Fail}, " +
            $"lot={vm.Lot}/{lotBefore}, sound={productSoundFlag}, detected={productDetectedThisCycle}, " +
            $"commands={string.Join(',', vmBoard.Commands)})");

        string uartSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "JbzSerialBoardTransport.cs"));
        string supervisorSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "ScanSupervisor.cs"));
        Assert(uartSource.Contains(
                   "generation != Volatile.Read(ref _generation)",
                   StringComparison.Ordinal) &&
               supervisorSource.Contains("frame.ScanGeneration != _previousScanGeneration", StringComparison.Ordinal),
            "Stale scan generations remain rejected by transport and ScanSupervisor gates");

        void AssertWaiting(string scenario, ScanFrame frame)
        {
            engine.ProcessFrame(frame);
            ProductEvidenceSnapshot evidence = engine.GetProductEvidenceSnapshot();
            Assert(!evidence.ValidProductEvidence &&
                   evidence.State == ProductPresenceState.WaitingForProduct &&
                   !engine.HasProductActivity &&
                   !engine.ReadyToEvaluateProductFaults &&
                   !engine.HasWiringFault &&
                   !engine.ContinuityPassed,
                $"{scenario} remains raw-only WaitingForProduct");
        }
    }

    private static void TestFinalTestStatusGuards()
    {
        var settings = new ProductionSettings { MasterFaultRequiredCount = 0 };
        ProductionConfigService.SetMasterFaultRequiredCountForPath(settings, "PN-0.tht", 0);
        ProductModel model0 = new() { ModelName = "PN-0", PartNumber = "PN-0", SourcePath = "PN-0.tht" };
        Assert(ProductionConfigService.GetMasterFaultRequiredCount(settings, model0) == 0,
            "Master minimum 0 must be preserved and disable Master");

        ProductionConfigService.SetMasterFaultRequiredCountForPath(settings, "PN-1.tht", 1);
        ProductModel model1 = Model(("PN-1-WIRE", new[] { 1, 2 }));
        model1.ModelName = "PN-1";
        model1.PartNumber = "PN-1";
        model1.SourcePath = "PN-1.tht";
        Assert(ProductionConfigService.GetMasterFaultRequiredCount(settings, model1) == 1,
            "Master minimum 1 must be preserved");

        ProductionConfigService.SetMasterFaultRequiredCountForPath(settings, "PN-2.tht", 2);
        ProductModel model2 = Model(("PN-2-WIRE", new[] { 1, 2 }));
        model2.ModelName = "PN-2";
        model2.PartNumber = "PN-2";
        model2.SourcePath = "PN-2.tht";
        Assert(ProductionConfigService.GetMasterFaultRequiredCount(settings, model2) == 2,
            "Master minimum 2 must be preserved");

        TestViewModel disabledMasterVm = CreateTestViewModel(new ProductionSettings { MasterFaultRequiredCount = 0 });
        disabledMasterVm.LoadPreparedModelAsync(model0).GetAwaiter().GetResult();
        Assert(disabledMasterVm.MasterApproved, "Master min 0 unlocks production immediately");
        Assert(disabledMasterVm.MasterState == MasterSequenceState.Completed, "Master min 0 marks Master completed/disabled");
        Assert(!disabledMasterVm.IsMasterSequenceActive &&
               typeof(TestViewModel).GetProperty("IsMasterBannerVisible") is null,
            "Master workflow no longer exposes a separate Master banner");
        Assert(disabledMasterVm.ProductionEnabled, "Master min 0 allows production");
        Assert(disabledMasterVm.ResultStatusText == "LẮP SẢN PHẨM", "Waiting-product result text is canonical");
        Assert(disabledMasterVm.StateBackground == "#FFF3A0" && disabledMasterVm.StateForeground == "#222222",
            "Ready status uses yellow/dark mapping");

        TestViewModel enabledMasterVm = CreateTestViewModel(new ProductionSettings { MasterFaultRequiredCount = 1 });
        enabledMasterVm.LoadPreparedModelAsync(model1).GetAwaiter().GetResult();
        Assert(!enabledMasterVm.MasterApproved && enabledMasterVm.IsMasterSequenceActive,
            "Master min 1 keeps Master workflow enabled");
        Assert(enabledMasterVm.MasterRequiredFaultCount == 1, "Master min 1 requires one unique fault");
        Assert(enabledMasterVm.ResultStatusText == "KIỂM TRA MASTER ĐẠT" &&
               enabledMasterVm.State == "KIỂM TRA MASTER PASS" &&
               enabledMasterVm.StateBackground == "#FFF3A0",
            "Waiting Master identifies the required good sample and keeps the canonical yellow background");

        TestViewModel masterExitVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 1 },
            out FakeBoard masterExitBoard);
        ProductModel masterExitModel = Model(("MASTER-PAIR", new[] { 1, 18 }));
        masterExitVm.LoadPreparedModelAsync(masterExitModel).GetAwaiter().GetResult();
        typeof(TestViewModel).GetField("_runtimeMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(masterExitVm, 1);
        TestEngine masterExitEngine =
            (TestEngine)(typeof(TestViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(masterExitVm) ?? throw new InvalidOperationException("Master exit TestEngine not found"));
        masterExitEngine.SetFrameProcessingEnabled(true);
        masterExitBoard.Publish(FrameSeq(100, (1, new[] { 18 })));
        Assert(masterExitVm.MasterState == MasterSequenceState.TestingGoodMaster,
            "Master sample activity starts the good-Master test");
        masterExitVm.StopViewAsync().GetAwaiter().GetResult();
        Assert(masterExitVm.IsProductRemovalPending,
            "Returning to Main during Master keeps the removal gate until a fresh frame is received");
        masterExitBoard.Publish(FrameSeq(101, (1, new[] { 18 })));
        Assert(masterExitVm.IsProductRemovalPending,
            "Master removal gate remains locked while the sample is physically connected");
        masterExitBoard.Publish(FrameSeq(102));
        Assert(!masterExitVm.IsProductRemovalPending &&
               masterExitVm.ResultStatusText == "KIỂM TRA MASTER ĐẠT" &&
               masterExitVm.Faults.Count == 0,
            "Fresh empty frame after leaving Master clears the stale removal latch/table without losing the required Master type");

        FieldInfo masterGoodVerified = typeof(TestViewModel).GetField(
            "_masterGoodVerified",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Master-good verification field not found");
        masterGoodVerified.SetValue(enabledMasterVm, true);
        MethodInfo transitionToBadMaster = typeof(TestViewModel).GetMethod(
            "TransitionToBadMaster",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Bad-Master transition not found");
        transitionToBadMaster.Invoke(enabledMasterVm, null);
        Assert(enabledMasterVm.MasterState == MasterSequenceState.WaitingBadMaster &&
               enabledMasterVm.ResultStatusText == "KIỂM TRA MASTER LỖI" &&
               enabledMasterVm.WrongCountText == "0/1" &&
               enabledMasterVm.Faults.Count == 0,
            "Bad Master starts at 0/N in the wrong-wiring counter and keeps an empty table");

        TestViewModel twoFaultMasterVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 2 });
        twoFaultMasterVm.LoadPreparedModelAsync(model2).GetAwaiter().GetResult();
        masterGoodVerified.SetValue(twoFaultMasterVm, true);
        transitionToBadMaster.Invoke(twoFaultMasterVm, null);
        Assert(twoFaultMasterVm.WrongCountText == "0/2",
            "Two-fault bad Master displays 0/2 in the wrong-wiring counter");

        MethodInfo buildMasterFaultGridRow = typeof(TestViewModel).GetMethod(
            "BuildMasterFaultGridRow",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Master fault grid row builder not found");
        FaultRow masterWrongWireRow = (FaultRow)(buildMasterFaultGridRow.Invoke(
            enabledMasterVm,
            [new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                ExpectedSourceIo = 1,
                ExpectedTargetIo = 18,
                ActualSourceIo = 1,
                ActualTargetIo = 19,
                RelatedIos = [1, 18, 19]
            }]) ?? throw new InvalidOperationException("Master fault row was not built"));
        Assert(masterWrongWireRow.Kind == FaultKind.WrongWiring &&
               masterWrongWireRow.Status == "SAI DÂY",
            "Confirmed bad-Master wrong wiring uses the same red SAI DÂY row semantics as production");

        TestViewModel statusVm = CreateTestViewModel(new ProductionSettings { MasterFaultRequiredCount = 0 });
        statusVm.State = "PASS";
        Assert(statusVm.ResultStatusText == "PASS" && statusVm.StateBackground == "#2AA84A" && statusVm.StateForeground == "#FFFFFF",
            "PASS state keeps the canonical PASS header mapping");
        statusVm.State = "PASS - THÁO SẢN PHẨM";
        Assert(statusVm.ResultStatusText == "THÁO SẢN PHẨM" && statusVm.StateBackground == "#2AA84A",
            "Committed PASS explicitly asks for product removal until ProductRemoved returns the UI to ready");
        statusVm.State = "ĐANG TEST LEAK";
        Assert(statusVm.ResultStatusText == "ĐANG TEST LEAK" &&
               statusVm.StateBackground == "#FFF3A0",
            "Leak stage has an explicit in-progress presentation before PASS");
        statusVm.State = "CHƯA ĐẠT";
        Assert(statusVm.ResultStatusText == "KHÔNG ĐẠT" && statusVm.StateBackground == "#C62828" && statusVm.StateForeground == "#FFFFFF",
            "FAIL state keeps the existing Vietnamese KHÔNG ĐẠT header mapping");
        statusVm.State = "CHỜ THÁO SẢN PHẨM";
        Assert(statusVm.ResultStatusText == "THÁO SẢN PHẨM" &&
               statusVm.StateBackground == "#FFF3A0" &&
               statusVm.StateForeground == "#222222",
            "Removal interlock must not be presented as LẮP SẢN PHẨM");
        statusVm.State = "ĐANG KIỂM TRA...";
        Assert(statusVm.ResultStatusText == "ĐANG TEST" && statusVm.StateBackground == "#FFF3A0" && statusVm.StateForeground == "#222222",
            "Testing status mapping");
        statusVm.State = "ĐANG KẾT NỐI BO";
        Assert(statusVm.ResultStatusText == "ĐANG KẾT NỐI BO" &&
               statusVm.StateBackground == "#FFF3A0" &&
               statusVm.StateForeground == "#222222",
            "Board connection progress must not be presented as production testing");
        statusVm.State = "LỖI THIẾT BỊ";
        Assert(statusVm.ResultStatusText == "KHÔNG ĐẠT" &&
               statusVm.StateBackground == "#C62828" &&
               statusVm.StateForeground == "#FFFFFF",
            "A non-latched equipment error keeps the generic red error presentation");

        statusVm.LoadPreparedModelAsync(model0).GetAwaiter().GetResult();
        MethodInfo buildFinalPassRejectionFaults = typeof(TestViewModel).GetMethod(
            "BuildFinalPassRejectionFaults",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Final PASS rejection fault builder not found");
        FaultDetail[] finalRejectionFaults =
            (FaultDetail[])(buildFinalPassRejectionFaults.Invoke(statusVm, [model0])
                ?? throw new InvalidOperationException("Final PASS rejection fault builder returned null"));
        Assert(finalRejectionFaults.Length == 1 &&
               finalRejectionFaults[0].Message.Contains("Continuity=", StringComparison.Ordinal) &&
               finalRejectionFaults[0].Message.Contains("Resistance", StringComparison.Ordinal),
            "Final PASS rejection always provides operator-visible continuity/resistance detail");

        TestViewModel recoveryVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard recoveryBoard);
        ProductModel recoveryModel = Model(("RECOVERY-PAIR", new[] { 1, 18 }));
        recoveryVm.LoadPreparedModelAsync(recoveryModel).GetAwaiter().GetResult();
        typeof(TestViewModel).GetField("_runtimeMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(recoveryVm, 1);
        TestEngine recoveryEngine =
            (TestEngine)(typeof(TestViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(recoveryVm) ?? throw new InvalidOperationException("Recovery TestEngine not found"));
        recoveryEngine.SetFrameProcessingEnabled(true);
        recoveryBoard.Publish(FrameSeq(1, (1, new[] { 18 })));
        Assert(recoveryVm.Faults.Count == 0, "Passed recovery model hides completed network rows before removal");
        long recoveryGeneration = (long)(typeof(TestViewModel).GetField(
            "_runtimeGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(recoveryVm) ?? 0L);
        MethodInfo recoverUncommittedFail = typeof(TestViewModel).GetMethod(
            "RecoverAfterUncommittedFailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Uncommitted FAIL recovery method not found");
        Task publishRecoveryFrame = Task.Run(async () =>
        {
            await Task.Delay(50);
            recoveryBoard.Publish(FrameSeq(2));
        });
        ((Task)(recoverUncommittedFail.Invoke(
            recoveryVm,
            [recoveryModel, recoveryGeneration, CancellationToken.None, "SELF_TEST"])
            ?? throw new InvalidOperationException("Uncommitted FAIL recovery returned null")))
            .GetAwaiter()
            .GetResult();
        publishRecoveryFrame.GetAwaiter().GetResult();
        Assert(recoveryVm.ResultStatusText == "LẮP SẢN PHẨM" &&
               recoveryVm.Faults.Count(row => row.WireName == "RECOVERY-PAIR") == 2 &&
               recoveryVm.CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct &&
               !recoveryVm.IsProductRemovalPending &&
               !recoveryBoard.Commands.Contains("START") &&
               !recoveryBoard.Commands.Contains("SET:2"),
            "Rejected FAIL commit returns to WaitingForProduct with both pending wire rows visible and no stale removal latch");

        string xaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml"));
        Assert(!xaml.Contains("ProbeToggleText", StringComparison.Ordinal) &&
               !xaml.Contains("HIỆN DÒ CHÂN", StringComparison.Ordinal) &&
               !xaml.Contains("ẨN DÒ CHÂN", StringComparison.Ordinal),
            "Production TestView must not expose a Probe toggle button");
        Assert(xaml.Contains(
                   "Text=\"{Binding Lot, Mode=OneWay}\" Style=\"{StaticResource PiCounterTextStyle}\"",
                   StringComparison.Ordinal) &&
               !xaml.Contains(
                   "Text=\"{Binding ProbeCycleCount, Mode=OneWay}\" Style=\"{StaticResource PiCounterTextStyle}\"",
                   StringComparison.Ordinal),
            "TestView Số LOT must display the daily accepted quantity, never the probe maintenance counter");
        Assert(xaml.Contains(
                   "Text=\"{Binding WrongCountText, Mode=OneWay}\"",
                   StringComparison.Ordinal) &&
               !xaml.Contains("IsMasterBannerVisible", StringComparison.Ordinal) &&
               !xaml.Contains("MasterBannerText", StringComparison.Ordinal) &&
               !xaml.Contains("MasterProgressText", StringComparison.Ordinal),
            "Master progress uses the wrong-wiring counter without a separate yellow banner");
        Assert(xaml.Contains("x:Key=\"WireColorCellTemplate\"", StringComparison.Ordinal) &&
               !xaml.Contains("Background=\"#F2FFFFFF\"", StringComparison.Ordinal) &&
               xaml.Contains("views:OutlinedTextBlock Text=\"{Binding WireColorText}\"", StringComparison.Ordinal) &&
               xaml.Contains("Foreground=\"White\"", StringComparison.Ordinal) &&
               xaml.Contains("Stroke=\"#111111\"", StringComparison.Ordinal) &&
               xaml.Contains("StrokeThickness=\"1\"", StringComparison.Ordinal) &&
               xaml.Contains("FontFamily=\"Malgun Gothic\"", StringComparison.Ordinal) &&
               xaml.Contains("FontSize=\"22\"", StringComparison.Ordinal) &&
               !xaml.Contains("BorderBrush=\"#606060\"", StringComparison.Ordinal) &&
               xaml.Contains("x:Key=\"HtdrvGridTextStyle\"", StringComparison.Ordinal) &&
               xaml.Contains("x:Key=\"HtdrvGridCenterTextStyle\"", StringComparison.Ordinal) &&
               xaml.Contains("ElementStyle=\"{StaticResource HtdrvGridStrongCenterTextStyle}\"", StringComparison.Ordinal) &&
               !xaml.Contains("x:Key=\"OperatorWireNameTextStyle\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Mã Dây\" Binding=\"{Binding WireName}\" Width=\"1.25*\" MinWidth=\"100\" CanUserSort=\"False\" CanUserReorder=\"False\" CanUserResize=\"False\" CellStyle=\"{StaticResource PiCenterCellStyle}\" ElementStyle=\"{StaticResource OperatorWireTextStyle}\"", StringComparison.Ordinal) &&
               xaml.Contains("TestFaultGridFontSize", StringComparison.Ordinal) &&
               xaml.Contains("TestGridRowHeight", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"M&#224;u\" Width=\"0.85*\" MinWidth=\"90\"", StringComparison.Ordinal) &&
               xaml.IndexOf("Header=\"Mã Dây\"", StringComparison.Ordinal) <
               xaml.IndexOf("Header=\"M&#224;u\"", StringComparison.Ordinal) &&
               xaml.IndexOf("Header=\"M&#224;u\"", StringComparison.Ordinal) <
               xaml.IndexOf("Header=\"Tiết Diện\"", StringComparison.Ordinal) &&
               !xaml.Contains("Header=\"#1\"", StringComparison.Ordinal) &&
               !xaml.Contains("Header=\"#2\"", StringComparison.Ordinal) &&
               !xaml.Contains("Header=\"#3\"", StringComparison.Ordinal) &&
               !xaml.Contains("Header=\"#4\"", StringComparison.Ordinal),
            "TestView renders full-cell wire colors with outlined readable codes and no #1..#4 columns");
        Assert(xaml.Contains("<DataTrigger Binding=\"{Binding KindName}\" Value=\"Open\">", StringComparison.Ordinal) &&
               xaml.Contains("<DataTrigger Binding=\"{Binding KindName}\" Value=\"MissingConnection\">", StringComparison.Ordinal) &&
               xaml.Contains("<Setter Property=\"BorderBrush\" Value=\"#23E6E6\"/>", StringComparison.Ordinal) &&
               xaml.Contains("<Setter Property=\"BorderThickness\" Value=\"0,8,0,0\"/>", StringComparison.Ordinal),
            "TestView uses bold open rows and a thick cyan separator between wire networks");
        Assert(xaml.Contains("Content=\"TH&#7916; L&#7840;I IN TEM\"", StringComparison.Ordinal) &&
               xaml.Contains("Style=\"{StaticResource LabelRetryButtonStyle}\"", StringComparison.Ordinal) &&
               xaml.Contains("Content=\"IN TH&#202;M B&#7842;N SAO\"", StringComparison.Ordinal) &&
               xaml.Contains("Style=\"{StaticResource LabelReprintButtonStyle}\"", StringComparison.Ordinal) &&
               xaml.Contains("kh&#244;ng t&#259;ng LOT", StringComparison.Ordinal) &&
               !xaml.Contains("Text=\"{Binding LabelStatusText}\"", StringComparison.Ordinal),
            "TestView clearly separates failed first-print retry from audited duplicate printing without showing LabelStatusText");
        Assert(xaml.Contains("Click=\"BackToMain_Click\"", StringComparison.Ordinal) &&
               xaml.Contains("Style=\"{StaticResource TestExitButtonStyle}\"", StringComparison.Ordinal) &&
               xaml.Contains("x:Key=\"TestExitBackground\" Color=\"#FFF0F0\"", StringComparison.Ordinal) &&
               xaml.Contains("x:Key=\"TestExitForeground\" Color=\"#A61B1B\"", StringComparison.Ordinal),
            "TestView back navigation uses the shared light-red exit treatment");
        Assert(xaml.Contains("x:Name=\"HeaderNavigationRow\"", StringComparison.Ordinal) &&
               !xaml.Contains("x:Name=\"StatusLedPanel\"", StringComparison.Ordinal) &&
               !xaml.Contains("YellowStatusLed", StringComparison.Ordinal) &&
               !xaml.Contains("WhiteStatusLed", StringComparison.Ordinal) &&
               !xaml.Contains("GreenStatusLed", StringComparison.Ordinal) &&
               !xaml.Contains("RedStatusLed", StringComparison.Ordinal) &&
               xaml.Contains("x:Name=\"LabelActionButtonsPanel\"", StringComparison.Ordinal) &&
               xaml.Contains("<ColumnDefinition Width=\"6\"/>", StringComparison.Ordinal),
            "TestView removes all four legacy status LEDs while preserving navigation and label actions");

        string testWindowSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml.cs"));
        Assert(testWindowSource.Contains("ApplyResponsiveTestGridLayout", StringComparison.Ordinal) &&
               testWindowSource.Contains("Math.Min(widthScale, heightScale)", StringComparison.Ordinal) &&
               testWindowSource.Contains("TestGridMaximumScale = 1.25", StringComparison.Ordinal) &&
               testWindowSource.Contains("Resources[\"TestFaultGridFontSize\"]", StringComparison.Ordinal),
            "TestView keeps large 1280x768 text and scales grid typography/row spacing uniformly through Full HD");
        Assert(!testWindowSource.Contains("StatusLed", StringComparison.Ordinal) &&
               !testWindowSource.Contains("PulseYellowLed", StringComparison.Ordinal) &&
               !testWindowSource.Contains("PulseWhiteLed", StringComparison.Ordinal) &&
               !testWindowSource.Contains("GreenPassBlink", StringComparison.Ordinal),
            "Removing the four LEDs also removes their frame handlers, pulse timers and PASS blink workload");

        string mainWindowSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "MainWindow.xaml.cs"));
        Assert(mainWindowSource.Contains("bool hardwareReady =", StringComparison.Ordinal) &&
               mainWindowSource.Contains(
                   "StartTestButton.IsEnabled = hardwareReady && _viewModel.Model is not null;",
                   StringComparison.Ordinal) &&
               mainWindowSource.Contains(
                   "SelectModelButton.IsEnabled = hardwareReady && !blocked;",
                   StringComparison.Ordinal) &&
               !mainWindowSource.Contains("offlinePreview", StringComparison.Ordinal),
            "Main hardware actions stay locked for the remainder of a disconnected session");

        string mainViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "MainViewModel.cs"));
        Assert(mainViewModelSource.Contains(
                   "MẤT KẾT NỐI BO - THOÁT VÀ MỞ LẠI ỨNG DỤNG",
                   StringComparison.Ordinal),
            "Main status directs the operator to restart instead of advertising an offline model");
        Assert(mainViewModelSource.Contains("requireStartupIoClear: false", StringComparison.Ordinal),
            "Production startup accepts the first live frame for the remembered model without requiring a clean baseline");

        Assert(xaml.Contains("x:Name=\"OperationTablesHost\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Lo&#7841;i\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"IO\" Binding=\"{Binding IoText}\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Connector\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Chân Pin\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Mã Dây\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Tiết Diện\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"M&#224;u\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"Tr&#7841;ng th&#225;i\"", StringComparison.Ordinal) &&
               xaml.Contains("Header=\"IO-CN-PN\"", StringComparison.Ordinal) &&
               !testWindowSource.Contains("OperationTablesHost.Visibility = Visibility.Collapsed;", StringComparison.Ordinal) &&
               !testWindowSource.Contains("offlinePreview", StringComparison.Ordinal) &&
               testWindowSource.Contains("if (_autoStartProduction)", StringComparison.Ordinal),
            "TestWindow has no offline preview path; MainWindow must reject entry without a healthy board");
        int typeColumnIndex = xaml.IndexOf("Header=\"Lo&#7841;i\"", StringComparison.Ordinal);
        int ioColumnIndex = xaml.IndexOf("Header=\"IO\" Binding=\"{Binding IoText}\"", typeColumnIndex, StringComparison.Ordinal);
        int connectorColumnIndex = xaml.IndexOf("Header=\"Connector\"", ioColumnIndex, StringComparison.Ordinal);
        Assert(typeColumnIndex >= 0 &&
               ioColumnIndex > typeColumnIndex &&
               connectorColumnIndex > ioColumnIndex,
            "TestWindow places the THT IO column directly after Loại and before Connector");

        string testViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(testViewModelSource.Contains("HOÀN THÀNH MẪU MASTER ĐẠT", StringComparison.Ordinal) &&
               testViewModelSource.Contains("HOÀN THÀNH MẪU MASTER LỖI", StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "MasterDetectedFaultCount < MasterRequiredFaultCount",
                   StringComparison.Ordinal),
            "Master completion appears in the main status box and remains gated by the configured N/N fault count");
        Assert(testViewModelSource.Contains("resumeCurrentStartupModel", StringComparison.Ordinal) &&
               testViewModelSource.Contains("BoardFrameActivity?.Invoke(this, frame);", StringComparison.Ordinal),
            "Remembered-model START resumes real frame processing while UI LEDs observe the existing board frame stream");
        int initializeHardwareIndex = testViewModelSource.IndexOf(
            "await InitializeHardwareAsync();",
            StringComparison.Ordinal);
        int loadLastModelIndex = testViewModelSource.IndexOf(
            "await LoadLastTestedModelAsync();",
            StringComparison.Ordinal);
        Assert(initializeHardwareIndex >= 0 && loadLastModelIndex > initializeHardwareIndex,
            "Startup connects the board before loading the last product model");

        string settingsXaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "ProductionSettingsPage.xaml"));
        string settingsSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "ProductionSettingsPage.xaml.cs"));
        string labelPrintServiceSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "LabelPrintService.cs"));
        string historySource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "HistoryPage.xaml.cs"));
        string topologyLearningSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "TopologyLearningWindow.xaml.cs"));
        string appSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "App.xaml.cs"));
        Assert(settingsXaml.Contains(
                   "SelectionChanged=\"PrinterComComboBox_SelectionChanged\"",
                   StringComparison.Ordinal) &&
               !settingsXaml.Contains("Content=\"KẾT NỐI\"", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Click=\"ConnectPrinter_Click\"", StringComparison.Ordinal) &&
               !settingsXaml.Contains("x:Name=\"PrinterConnectionStatusText\"", StringComparison.Ordinal) &&
               settingsSource.Contains("ProductionConfigService.Save(_vm.Settings);", StringComparison.Ordinal) &&
               settingsSource.Contains("ConnectLabelPrinterAsync(_vm.Settings.Label)", StringComparison.Ordinal) &&
               settingsSource.Contains("DisconnectLabelPrinterAsync()", StringComparison.Ordinal) &&
               settingsSource.Contains("new ComPortOption(savedPort, savedPort)", StringComparison.Ordinal) &&
               !settingsSource.Contains("$\"{savedPort} - chưa kết nối\"", StringComparison.Ordinal),
            "Selecting a printer COM persists and applies it immediately without a connect button/status label");
        Assert(settingsXaml.Contains("<ColumnDefinition Width=\"110\"/>", StringComparison.Ordinal) &&
               (settingsXaml.Contains("Content=\"QU&#201;T\"", StringComparison.Ordinal) ||
                settingsXaml.Contains("Content=\"QUÉT\"", StringComparison.Ordinal)) &&
               settingsXaml.Contains("Width=\"115\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("Grid.Row=\"2\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("Grid.Column=\"2\"", StringComparison.Ordinal),
            "Label printer controls use a compact three-row layout so manual resistance stays visible");
        Assert(settingsXaml.Contains("RELAY 1 (OUT0)", StringComparison.Ordinal) &&
               settingsXaml.Contains("RELAY 5 (OUT4)", StringComparison.Ordinal) &&
               !settingsXaml.Contains("JigRelayChannelComboBox", StringComparison.Ordinal) &&
               !settingsXaml.Contains("MarkingRelayChannelComboBox", StringComparison.Ordinal),
            "Production settings fixes physical Relay 1 to OUT0 and Relay 5 to OUT4");
        Assert(xaml.Contains("x:Name=\"TestHeaderSurface\" Width=\"1344\" Height=\"234\"", StringComparison.Ordinal) &&
               xaml.Contains("x:Name=\"TestAppVersionText\"", StringComparison.Ordinal) &&
               xaml.Contains("Grid.Row=\"8\"", StringComparison.Ordinal) &&
               xaml.Contains("ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\"", StringComparison.Ordinal) &&
               xaml.Contains("MinWidth=\"125\"", StringComparison.Ordinal),
            "TestView scales its fixed header and preserves table text at 1024/1368 widths");
        Assert(settingsXaml.Contains("<ScrollViewer Grid.Row=\"1\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("x:Name=\"SettingsPanelsHost\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("x:Name=\"LabelSettingsPanel\"", StringComparison.Ordinal),
            "Production settings wraps panels and scrolls instead of clipping at 1024x768");
        Assert(settingsSource.Contains("if (available >= 1160)", StringComparison.Ordinal) &&
               settingsXaml.Contains("ScrollChanged=\"SettingsScrollViewer_ScrollChanged\"", StringComparison.Ordinal) &&
               settingsSource.Contains("e.ViewportWidth", StringComparison.Ordinal) &&
               settingsSource.Contains("double.IsFinite(e.ViewportWidth)", StringComparison.Ordinal) &&
               settingsSource.Contains("if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)", StringComparison.Ordinal) &&
               settingsSource.Contains("SettingsPanelsHost.Margin.Left", StringComparison.Ordinal) &&
               settingsXaml.Contains("<UniformGrid x:Name=\"LabelPrintActionsPanel\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("Columns=\"3\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("x:Name=\"WaterProofChannelRows\"", StringComparison.Ordinal) &&
               settingsXaml.Contains("<RowDefinition Height=\"96\"/>", StringComparison.Ordinal) &&
               settingsXaml.Contains("<RowDefinition Height=\"32\"/>", StringComparison.Ordinal) &&
               settingsXaml.Contains("<Setter Property=\"Height\" Value=\"30\"/>", StringComparison.Ordinal) &&
               settingsXaml.Contains("<RowDefinition Height=\"46\"/>", StringComparison.Ordinal) &&
               settingsXaml.Contains("<RowDefinition Height=\"44\"/>", StringComparison.Ordinal) &&
               settingsXaml.Contains("<RowDefinition Height=\"130\"/>", StringComparison.Ordinal),
            "Production settings uses the real viewport, preserves the right border, and evenly sizes label buttons");
        Assert(!settingsXaml.Contains("Settings.ItemHeight", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Settings.PageDelay", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Settings.ShowTitle", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Settings.ShowConnector", StringComparison.Ordinal) &&
               !settingsXaml.Contains("Settings.MinimumErrorLogValue", StringComparison.Ordinal) &&
               settingsXaml.Contains("x:Name=\"WaterProofSettingsPanel\"", StringComparison.Ordinal) &&
               settingsXaml.IndexOf("x:Name=\"WaterProofSettingsPanel\"", StringComparison.Ordinal) >
                   settingsXaml.IndexOf("x:Name=\"RelaySettingsPanel\"", StringComparison.Ordinal) &&
               settingsXaml.IndexOf("x:Name=\"WaterProofSettingsPanel\"", StringComparison.Ordinal) <
                   settingsXaml.IndexOf("Text=\"MANUAL OUTPUT BO MỚI (0-4)\"", StringComparison.Ordinal) &&
               !settingsSource.Contains("MoveWaterProofSettingsToRelayColumn", StringComparison.Ordinal),
            "Unused legacy UI fields are hidden and TEST LEAK is grouped in the relay/maintenance column");
        System.Xml.Linq.XElement[] settingsButtons =
            System.Xml.Linq.XDocument.Parse(settingsXaml)
                .Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .ToArray();
        Assert(settingsButtons.Length == 21 &&
               settingsButtons.All(button =>
                   button.Attribute("Style")?.Value.Contains("StaticResource", StringComparison.Ordinal) == true) &&
               settingsXaml.Contains("SettingsPrimaryButtonStyle", StringComparison.Ordinal) &&
               settingsXaml.Contains("SettingsInfoButtonStyle", StringComparison.Ordinal) &&
               settingsXaml.Contains("SettingsActionButtonStyle", StringComparison.Ordinal) &&
               settingsXaml.Contains("SettingsWarningButtonStyle", StringComparison.Ordinal) &&
               settingsXaml.Contains("SettingsExitButtonStyle", StringComparison.Ordinal) &&
               settingsXaml.Contains("x:Name=\"InteractionOverlay\"", StringComparison.Ordinal),
            "Every Production Settings button uses the shared semantic palette and interaction template");
        Assert(settingsXaml.Contains("Tag=\"TEM_BE_QR\"", StringComparison.Ordinal),
            "Production settings exposes the dedicated TEM BE QR selection");

        Assert(System.Text.RegularExpressions.Regex.Matches(
                   testViewModelSource,
                   @"CrashReportService\.Write\(").Count == 1 &&
               testViewModelSource.Contains("$\"Hardware.DeviceFault.{source}\"", StringComparison.Ordinal) &&
               !labelPrintServiceSource.Contains("CrashReportService.Write", StringComparison.Ordinal) &&
               !settingsSource.Contains("CrashReportService.Write", StringComparison.Ordinal),
            "Within TestViewModel, RPT is limited to the latched main-tester DeviceFault; Leak and label-printer paths never create it");
        string crashReportSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "CrashReportService.cs"));
        Assert(crashReportSource.Contains("IsDeviceConnectionFault(source) &&", StringComparison.Ordinal) &&
               crashReportSource.Contains("!AsyncFileLogService.Current.FileLoggingEnabled", StringComparison.Ordinal) &&
               crashReportSource.Contains("Loại lỗi: LỖI KẾT NỐI THIẾT BỊ", StringComparison.Ordinal) &&
               crashReportSource.Contains("BuildOperatorConnectionReport()", StringComparison.Ordinal) &&
               !crashReportSource[crashReportSource.IndexOf("BuildOperatorConnectionReport() =>", StringComparison.Ordinal)..
                   crashReportSource.IndexOf("private static string BuildTechnicalReport", StringComparison.Ordinal)]
                   .Contains("Source:", StringComparison.Ordinal),
            "Device-fault RPT hides technical details unless the operator explicitly enables all system logs");
        Assert(testViewModelSource.Contains(
                   "Mất kết nối máy Leak. Hãy rút/cắm lại cáp, chọn lại cổng COM rồi thử lại.",
                   StringComparison.Ordinal) &&
               labelPrintServiceSource.Contains(
                   "Không kết nối được máy in. Hãy rút/cắm lại cáp và chọn lại cổng COM.",
                   StringComparison.Ordinal),
            "Leak and label-printer connection failures guide the operator to reconnect and reselect COM");
        Assert(!appSource.Contains("$\"Chi tiết: {e.Exception.Message}\"", StringComparison.Ordinal) &&
               !mainWindowSource.Contains("ex.ToString()", StringComparison.Ordinal) &&
               !settingsSource.Contains("ShowMessage(ex.Message", StringComparison.Ordinal) &&
               !historySource.Contains("| DB: {_historyPath}", StringComparison.Ordinal) &&
               !topologyLearningSource.Contains("LearningStatusText.Text = ex.Message", StringComparison.Ordinal) &&
               !testViewModelSource.Contains(
                   "MasterStatus = $\"LỖI KIỂM TRA MASTER PASS: {ex.Message}\"",
                   StringComparison.Ordinal) &&
               !testViewModelSource.Contains(
                   "MasterStatus = $\"LỖI EJECT MASTER: {ex.Message}\"",
                   StringComparison.Ordinal),
            "Operator-facing screens do not expose exception details or internal persistence paths");

        Assert(settingsXaml.Contains(
                   "Settings.EnableSystemLogs, Mode=TwoWay",
                   StringComparison.Ordinal) &&
               settingsXaml.Contains(
                   "Data/History vẫn luôn được lưu",
                   StringComparison.Ordinal),
            "Production settings exposes a system-log master switch that preserves History");

        string soundSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Services", "AppSoundService.cs"));
        Assert(appSource.Contains("AppSoundService.Current.PlayStartup();", StringComparison.Ordinal) &&
               appSource.Contains("DispatcherPriority.ApplicationIdle", StringComparison.Ordinal) &&
               soundSource.Contains("SafePlaySync(player)", StringComparison.Ordinal) &&
               soundSource.Contains("_startupPlaybackActive", StringComparison.Ordinal),
            "START.wav begins after first-render idle and is protected from Probe reset/click interruption");
        Assert(typeof(AppSoundService).Assembly.GetManifestResourceNames().Any(name =>
                   name.EndsWith(".Assets.Sounds.COMPUTER.wav", StringComparison.OrdinalIgnoreCase)) &&
               soundSource.Contains("PlayProductStart()", StringComparison.Ordinal) &&
               soundSource.Contains("CreatePlayer(\"COMPUTER.wav\"", StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "PlayProductStartSoundOnce(generation, preserveProductionFaultsForProbe);",
                   StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "Interlocked.CompareExchange(ref _productStartSoundPlayed, 1, 0)",
                   StringComparison.Ordinal) &&
               testViewModelSource.Contains(
                   "CurrentProductionPhase != ProductionPhase.Continuity",
                   StringComparison.Ordinal),
            "COMPUTER.wav is embedded and requested once on the first real Production connection of each cycle");

        TestViewModel deviceFaultVm = CreateTestViewModel(new ProductionSettings { MasterFaultRequiredCount = 0 });
        deviceFaultVm.LoadPreparedModelAsync(model0).GetAwaiter().GetResult();
        deviceFaultVm.SelectedOperationTabIndex = 3;
        deviceFaultVm.Faults.Add(new FaultRow
        {
            Kind = FaultKind.MissingConnection,
            Io = 1,
            Status = "CHƯA KẾT NỐI"
        });
        MethodInfo reportDeviceFault = typeof(TestViewModel).GetMethod(
            "ReportDeviceFaultForTest",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeviceFault test reporter not found");
        for (int index = 0; index < 100; index++)
            reportDeviceFault.Invoke(deviceFaultVm, [new ArgumentOutOfRangeException("index", "simulated index fault"), -1]);
        Assert(deviceFaultVm.IsDeviceFault, "DeviceFault latches after index exception");
        Assert(deviceFaultVm.DeviceFaultTransitionCount == 1, "Repeated index exceptions produce one DeviceFault transition");
        Assert(deviceFaultVm.DeviceFaultDialogCount == 1, "Repeated index exceptions produce one operator dialog episode");
        Assert(deviceFaultVm.ResultStatusText == "LỖI THIẾT BỊ" &&
               deviceFaultVm.StateBackground == "#C62828" &&
               deviceFaultVm.StateForeground == "#FFFFFF",
            "DeviceFault status mapping");
        Assert(deviceFaultVm.SelectedOperationTabIndex == 0 && deviceFaultVm.Faults.Count == 0,
            "DeviceFault returns to the empty continuity grid so headers remain visible without stale frame rows");

        reportDeviceFault.Invoke(deviceFaultVm, [new InvalidOperationException("second episode"), -1]);
        Assert(deviceFaultVm.IsDeviceFault &&
               deviceFaultVm.DeviceFaultTransitionCount == 1 &&
               deviceFaultVm.DeviceFaultDialogCount == 1,
            "DeviceFault remains latched for the process lifetime and cannot be reset in-app");
        Assert(deviceFaultVm.DeviceFaultMessage ==
               "Mất kết nối với máy test. Vui lòng khởi động lại.",
            "The single board-fault message is short and explicitly requests a restart");
        Assert(testViewModelSource.Contains("ExitApplicationAfterDeviceFaultAsync", StringComparison.Ordinal) &&
               testViewModelSource.Contains("await _deviceFaultHardwareLockTask;", StringComparison.Ordinal) &&
               testViewModelSource.Contains("mainWindow.Close();", StringComparison.Ordinal) &&
               testViewModelSource.Contains("windowHeader: \"LỖI THIẾT BỊ\"", StringComparison.Ordinal) &&
               testWindowSource.Contains("deviceFaultViewModel.IsDeviceFault", StringComparison.Ordinal),
            "DeviceFault uses the shared fault confirmation window, then safely locks hardware and exits");

        string testWindowXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml"));
        Assert(!testWindowXaml.Contains("ResetDeviceFaultCommand", StringComparison.Ordinal) &&
               !testWindowXaml.Contains("KH&#7902;I T&#7840;O L&#7840;I", StringComparison.Ordinal) &&
               !testWindowXaml.Contains("Visibility=\"{Binding IsDeviceFault", StringComparison.Ordinal) &&
               !testWindowXaml.Contains("Text=\"{Binding DeviceFaultMessage}\"", StringComparison.Ordinal),
            "DeviceFault UI has no separate banner or reinitialize button");

        TestViewModel startupFaultVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard startupFaultBoard);
        startupFaultBoard.SetConnectionStateForTest(false);
        startupFaultBoard.ThrowOnConnect = true;
        startupFaultVm.InitializeHardwareAsync().GetAwaiter().GetResult();
        startupFaultVm.InitializeHardwareAsync().GetAwaiter().GetResult();
        Assert(startupFaultVm.IsDeviceFault &&
               startupFaultVm.DeviceFaultTransitionCount == 1 &&
               startupFaultVm.DeviceFaultDialogCount == 1 &&
               startupFaultBoard.ConnectAttempts == 1,
            "Startup board failure tries once, shows one fault episode, and never reconnects in the same process");
    }

    private static void TestManualModeInterlock()
    {
        var settings = new ProductionSettings
        {
            ManualModeEnabled = false,
            MasterFaultRequiredCount = 0,
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50
        };
        TestViewModel vm = CreateTestViewModel(settings, out FakeBoard board);
        vm.LoadPreparedModelAsync(Model(("PAIR", new[] { 1, 18 }))).GetAwaiter().GetResult();

        typeof(TestViewModel).GetField("_runtimeMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(vm, 1);
        Assert(vm.CanEnterManualMode && !vm.IsManualModeActive,
            "Manual relay menu is ready without a saved ManualModeEnabled setting");

        board.Commands.Clear();
        int relay = vm.SetManualRelayAsync(0, true).GetAwaiter().GetResult();
        Assert(relay == 0 && vm.IsManualModeActive && vm.State == "MANUAL" && !board.IsScanning,
            "Manual physical Relay 1 / OUT0 holds one relay and keeps Production scan stopped");
        Assert(board.Commands.Count(command => command == "OFF") >= 1 &&
               board.Commands.Last() == "SET:0",
            "Manual Relay 1 / OUT0 forces both physical relays OFF before selecting Relay 1");

        foreach (int nonRelayOutput in new[] { 1, 2, 3 })
        {
            try
            {
                vm.SetManualRelayAsync(nonRelayOutput, true).GetAwaiter().GetResult();
                throw new InvalidOperationException($"OUT{nonRelayOutput} must not be exposed as a physical relay");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        board.Commands.Clear();
        relay = vm.SetManualRelayAsync(4, true).GetAwaiter().GetResult();
        Assert(relay == 4 && vm.IsManualModeActive && !board.IsScanning && board.Commands.Last() == "SET:4",
            "Manual physical Relay 5 / OUT4 replaces Relay 1 while Manual remains active");

        board.Commands.Clear();
        vm.ResetManualOutputsAsync().GetAwaiter().GetResult();
        Assert(!vm.IsManualModeActive &&
               string.Join(",", board.Commands) == "OFF,RESET,OFF,START",
            "RESET while a Universal Tester New output is ON forces OFF, clears the board, confirms OFF, then resumes scan");

        board.Commands.Clear();
        relay = vm.SetManualRelayAsync(0, true).GetAwaiter().GetResult();
        Assert(relay == 0 && vm.IsManualModeActive && board.Commands.Last() == "SET:0",
            "Manual Relay 1 / OUT0 can be selected again after RESET");

        board.Commands.Clear();
        relay = vm.SetManualRelayAsync(0, false).GetAwaiter().GetResult();
        Assert(relay == -1 && !vm.IsManualModeActive &&
               board.Commands.Contains("OFF") && board.Commands.Last() == "START",
            "TẮT TẤT CẢ forces all Universal Tester New outputs OFF and resumes Production scan");

        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(vm.State != "MANUAL", "Production is no longer locked after direct Relay OFF/RESET");

        var faultSettings = new ProductionSettings
        {
            ManualModeEnabled = false,
            MasterFaultRequiredCount = 0,
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50
        };
        TestViewModel faultVm = CreateTestViewModel(faultSettings, out FakeBoard faultBoard);
        faultVm.LoadPreparedModelAsync(Model(("PAIR", new[] { 1, 18 }))).GetAwaiter().GetResult();
        faultBoard.ThrowOnSetRelay = true;
        try
        {
            faultVm.SetManualRelayAsync(0, true).GetAwaiter().GetResult();
            throw new InvalidOperationException("Manual relay failure should throw");
        }
        catch (InvalidOperationException)
        {
        }

        Assert(faultVm.IsDeviceFault, "Manual relay hardware failure latches DeviceFault");
        Assert(faultBoard.Commands.Contains("OFF"), "Manual relay failure attempts safe OFF");

        string settingsPageSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "ProductionSettingsPage.xaml.cs"));
        string mainWindowSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "MainWindow.xaml.cs"));
        string settingsXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "ProductionSettingsPage.xaml"));
        Assert(settingsPageSource.Contains("await _main.Test.ResetManualOutputsAsync();", StringComparison.Ordinal) &&
               mainWindowSource.Contains("await settingsPage.ReleaseManualOutputsAsync();", StringComparison.Ordinal),
            "Every Settings-page close path awaits Manual RESET before releasing the page");
        Assert(settingsXaml.Contains("MANUAL RELAY BO MỚI", StringComparison.Ordinal) &&
               settingsXaml.Contains("ManualRelay0OnCommand", StringComparison.Ordinal) &&
               settingsXaml.Contains("ManualRelay4OnCommand", StringComparison.Ordinal) &&
               !settingsXaml.Contains("ManualRelay1OnCommand", StringComparison.Ordinal) &&
               !settingsXaml.Contains("ManualRelay2OnCommand", StringComparison.Ordinal) &&
               !settingsXaml.Contains("ManualRelay3OnCommand", StringComparison.Ordinal) &&
               settingsXaml.Contains("TẮT CẢ 2 RELAY", StringComparison.Ordinal),
            "Manual settings expose only physical Relay 1/OUT0 and Relay 5/OUT4");

        string uartSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "JbzBoardTransportAdapter.cs"));
        Assert(uartSource.Contains("_serial.OutputAsync(relay, true, ct)", StringComparison.Ordinal),
            "Manual relay control uses the serialized UART board owner");
        string serialSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "JbzSerialBoardTransport.cs"));
        Assert(serialSource.Contains("OUTPUT_ACK channel=", StringComparison.Ordinal) &&
               serialSource.Contains("WaitAsync(JbzEventFamily.Output", StringComparison.Ordinal) &&
               serialSource.Contains(":OUTPUTTEST,{channel}", StringComparison.Ordinal),
            "Universal Tester New OUTPUTTEST waits for the matching :OUTPUT channel/state acknowledgment");
    }

    private static void TestStartupIoInterlock()
    {
        ScanFrame duplicatedDirections = FrameSeq(
            100,
            (1, new[] { 18 }),
            (18, new[] { 1 }));
        IReadOnlyList<StartupIoContactPair> normalized =
            StartupIoInterlock.FindConnectedPairs(duplicatedDirections);
        Assert(normalized.Count == 1 && normalized[0] == new StartupIoContactPair(1, 18),
            "Startup IO detector normalizes and de-duplicates bidirectional edges");

        var resumeProduction = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };
        TestViewModel resumeVm = CreateTestViewModel(
            resumeProduction,
            out FakeBoard resumeBoard,
            requireStartupIoClear: false);
        resumeVm.SetModel(Model(("RESUME-PAIR", new[] { 1, 18 })));
        resumeBoard.Publish(duplicatedDirections);
        Assert(resumeVm.IsProductRemovalPending,
            "Installed product in background locks changing the current model");
        AssertThrows<InvalidOperationException>(
            () => resumeVm.SetModel(Model(("OTHER", new[] { 2, 19 }))),
            "Background installed product cannot be assigned to another model");
        resumeVm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(!resumeVm.IsProductRemovalPending &&
               !resumeVm.State.Contains("THÁO SẢN PHẨM", StringComparison.OrdinalIgnoreCase),
            "START resumes the remembered model without requiring a clean frame first");

        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };
        TestViewModel vm = CreateTestViewModel(
            production,
            out FakeBoard board,
            requireStartupIoClear: true);
        ProductModel startupInterlockModel = Model(("PAIR", new[] { 1, 18 }));
        startupInterlockModel.ModelName = "SELF-TEST-STARTUP-INTERLOCK";
        startupInterlockModel.PartNumber = "SELF-TEST-STARTUP-INTERLOCK";
        vm.SetModel(startupInterlockModel);

        board.Publish(duplicatedDirections);
        Assert(vm.IsProductRemovalPending &&
               vm.ResultStatusText == "VUI LÒNG THÁO SẢN PHẨM",
            "Background startup scan locks product selection and START while a product or stuck pin remains");
        AssertThrows<InvalidOperationException>(
            () => vm.SetModel(Model(("OTHER", new[] { 2, 19 }))),
            "Product model cannot change while the startup removal gate is locked");
        int commandsBeforeBlockedStart = board.Commands.Count;
        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(board.Commands.Count == commandsBeforeBlockedStart && vm.IsProductRemovalPending,
            "START cannot arm production while the startup removal gate is locked");

        board.Publish(FrameSeq(101));
        Assert(!vm.IsProductRemovalPending && vm.ResultStatusText == "LẮP SẢN PHẨM",
            "A complete clean background frame unlocks product selection and START");

        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        int totalBeforeWarning = vm.Total;
        int passBeforeWarning = vm.Pass;
        int failBeforeWarning = vm.Fail;

        Assert(vm.State.Contains("ĐỒNG BỘ DỮ LIỆU BO", StringComparison.Ordinal) &&
               vm.ResultStatusText == "ĐỒNG BỘ BO",
            "Production waits for a clean baseline frame without reporting an IO-capacity warning");

        board.Publish(FrameSeq(102, (1, new[] { 18 }), (18, new[] { 1 })));
        Assert(vm.ResultStatusText == "VUI LÒNG THÁO SẢN PHẨM" &&
               vm.IsProductRemovalPending &&
               vm.Faults.Count == 1 &&
               vm.Faults[0].Kind == FaultKind.Info &&
               vm.Faults[0].ProductFaultType == ProductFaultType.None &&
               vm.Faults[0].ActualSourceIo == 1 &&
               vm.Faults[0].ActualTargetIo == 18,
            "A product left on the jig asks for removal without reporting a false IO fault");
        Assert(vm.Total == totalBeforeWarning &&
               vm.Pass == passBeforeWarning &&
               vm.Fail == failBeforeWarning &&
               !board.Commands.Any(command => command.StartsWith("SET:", StringComparison.Ordinal)),
            $"Startup IO warning cannot commit production or activate a relay " +
            $"(totals {totalBeforeWarning}/{passBeforeWarning}/{failBeforeWarning} -> " +
            $"{vm.Total}/{vm.Pass}/{vm.Fail}; commands={string.Join(',', board.Commands)})");

        board.Publish(FrameSeq(103));
        Assert(!vm.IsProductRemovalPending &&
               vm.State == "CHỜ LẮP SẢN PHẨM" && vm.ResultStatusText == "LẮP SẢN PHẨM",
            "A complete clean frame clears the startup interlock and arms Production");

        // Closing TestView while a product is only partially assembled is a
        // background installed-product interlock, not the post-result removal state.
        TestViewModel incompleteVm = CreateTestViewModel(
            new ProductionSettings
            {
                MasterFaultRequiredCount = 0,
                ProductSettleTimeMs = 0
            },
            out FakeBoard incompleteBoard);
        incompleteVm.SetModel(Model(
            ("INCOMPLETE-A", new[] { 1, 2 }),
            ("INCOMPLETE-B", new[] { 3, 4 })));
        incompleteVm.StartProductionTestAsync().GetAwaiter().GetResult();
        incompleteBoard.Publish(FrameSeq(900, (1, new[] { 2 })));
        Assert(incompleteVm.CurrentProductionRuntimeState == ProductionRuntimeState.TestingRealtime,
            "Partial assembly is detected as realtime product presence");
        incompleteVm.StopViewAsync().GetAwaiter().GetResult();
        Assert(incompleteVm.IsProductRemovalPending &&
               incompleteVm.CurrentProductionRuntimeState != ProductionRuntimeState.WaitingForRemoval &&
               incompleteVm.State.Contains("LẮP DỞ", StringComparison.Ordinal),
            "Leaving TestView mid-assembly locks the product in background without pretending a PASS/FAIL removal phase");
        incompleteBoard.Publish(FrameSeq(901, (1, new[] { 2 })));
        Assert(incompleteVm.IsProductRemovalPending,
            "Background incomplete-product lock remains while any product edge is still present");
        incompleteBoard.Publish(FrameSeq(902));
        Assert(!incompleteVm.IsProductRemovalPending,
            "A complete empty background frame releases the incomplete-product lock");
    }

    private static void TestDuplicateClipFaultRows()
    {
        TestViewModel vm = CreateTestViewModel(new ProductionSettings { MasterFaultRequiredCount = 0 });
        MethodInfo synchronize = typeof(TestViewModel).GetMethod(
            "SynchronizeFaultRows",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-row synchronizer not found");

        static FaultRow DuplicateRow(string status) => new()
        {
            Kind = FaultKind.WrongWiring,
            ProductFaultType = ProductFaultType.WrongWiring,
            FaultType = "SAI KẾT NỐI CLIP",
            Io = 7,
            ActualSourceIo = 7,
            ActualTargetIo = 11,
            Status = status
        };

        for (int index = 0; index < 7; index++)
            vm.Faults.Add(DuplicateRow($"old-{index}"));

        FaultRow[] desired = Enumerable.Range(0, 10)
            .Select(index => DuplicateRow($"new-{index}"))
            .ToArray();
        synchronize.Invoke(vm, [desired]);

        Assert(!vm.IsDeviceFault,
            "Duplicate CLIP display rows are a UI/configuration case, not unstable hardware");
        Assert(vm.Faults.Count == desired.Length &&
               vm.Faults.Select(row => row.Status).SequenceEqual(desired.Select(row => row.Status)),
            "Duplicate CLIP rows synchronize without an out-of-range Move");
    }

    private static void TestWaterProofConfigurationAndPresentation()
    {
        var profile = new WaterProofModelSettings
        {
            Enabled = true,
            Channel1Enabled = true,
            Channel2Enabled = false,
            Channel3Enabled = true,
            Channel1Connector = "CN-A",
            Channel3Connector = "CN-C",
            PressTimeMs = 1200,
            WaitTimeMs = 600
        };

        WaterProofModelSettings clone = profile.Clone();
        Assert(clone.Channel1Connector == "CN-A" &&
               clone.Channel3Connector == "CN-C" &&
               clone.ConnectorForChannel(2) == string.Empty,
            "Leak profile clone preserves explicit THT connector mapping");

        var settings = new ProductionSettings();
        ProductionConfigService.SetWaterProofProfileForPath(settings, "LEAK-PART.tht", profile);
        WaterProofModelSettings loaded = ProductionConfigService.GetWaterProofProfileForPath(
            settings,
            "LEAK-PART.tht");
        Assert(loaded.Channel1Connector == "CN-A" && loaded.Channel3Connector == "CN-C",
            "Per-THT Leak profile stores connector mapping");

        Assert(WaterProofSerialService.BuildTestCommand(loaded) == ":TEST,1,0,1,0,1200,600\r\n",
            "Connector metadata must not change the proven Leak UART command format");

        var fixedBaudSettings = new ProductionSettings
        {
            WaterProofMachine = new WaterProofMachineSettings
            {
                PortName = " COM7 ",
                BaudRate = 9600
            }
        };
        string fixedBaudPath = Path.Combine(
            Path.GetTempPath(),
            $"jbz-leak-fixed-baud-{Guid.NewGuid():N}.cfg");
        try
        {
            ProductionConfigService.SaveLegacyCfg(fixedBaudSettings, fixedBaudPath);
            string fixedBaudConfig = File.ReadAllText(fixedBaudPath);
            Assert(fixedBaudSettings.WaterProofMachine.PortName == "COM7" &&
                   fixedBaudSettings.WaterProofMachine.BaudRate == WaterProofMachineSettings.DefaultBaudRate &&
                   fixedBaudConfig.Contains("[WaterProofPortName]COM7", StringComparison.Ordinal) &&
                   fixedBaudConfig.Contains("[WaterProofBaudRate]115200", StringComparison.Ordinal),
                "Leak COM selection is trimmed and the removed baud setting remains fixed at 115200");
        }
        finally
        {
            if (File.Exists(fixedBaudPath))
                File.Delete(fixedBaudPath);
        }

        var passRow = new WaterProofChannelResult
        {
            Channel = 1,
            Connector = "CN-A",
            IsMeasured = true,
            Passed = true
        };
        var failRow = new WaterProofChannelResult
        {
            Channel = 3,
            Connector = "CN-C",
            IsMeasured = true,
            Passed = false
        };
        Assert(passRow.ChannelText == "CH1 • CN-A" && passRow.ResultText == "PASS" &&
               failRow.ChannelText == "CH3 • CN-C" && failRow.ResultText == "FAIL",
            "Leak result rows identify both machine channel and mapped THT connector");
        Assert(passRow.LiveCellBackground == "#2AA84A" &&
               failRow.LiveCellBackground == "#C62828" &&
               passRow.LiveCellForeground == "#FFFFFF" &&
               failRow.LiveCellForeground == "#FFFFFF" &&
               new WaterProofChannelResult().LiveCellBackground == "#FFFFFF" &&
               new WaterProofChannelResult().LiveCellForeground == "#0F172A",
            "Compact Leak cells use professional PASS/FAIL backgrounds with readable contrast");

        passRow.PressPressure = 83.9;
        passRow.WaitPressure = 81.8;
        passRow.FirstResultPressure = 83.9;
        passRow.SecondResultPressure = 81.8;
        Assert(passRow.PressureText == "---",
            "Leak summary card does not display fill/hold pressure before a drop result exists");
        passRow.Leak = 2.1;
        Assert(passRow.PressureText == "2.1" && passRow.LeakText == "2.1",
            "Leak summary card displays machine-reported pressure drop instead of fill/hold pressure");

        var settingsLeakVm = new ProductionSettingsViewModel();
        MethodInfo resetSettingsLeak = typeof(ProductionSettingsViewModel).GetMethod(
            "ResetManualWaterProofResults",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings Leak result reset method not found");
        MethodInfo updateSettingsLeak = typeof(ProductionSettingsViewModel).GetMethod(
            "UpdateManualWaterProofProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings Leak realtime update method not found");
        MethodInfo finishSettingsLeak = typeof(ProductionSettingsViewModel).GetMethod(
            "ApplyManualWaterProofResult",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings Leak final result method not found");
        resetSettingsLeak.Invoke(settingsLeakVm, [profile]);
        updateSettingsLeak.Invoke(settingsLeakVm,
        [
            "COM7",
            new WaterProofProgress(WaterProofStage.Pressurizing, [84.0, 0.0, 83.5], ":PRESS,84,0,83.5")
        ]);
        updateSettingsLeak.Invoke(settingsLeakVm,
        [
            "COM7",
            new WaterProofProgress(WaterProofStage.Waiting, [83.4, 0.0, 82.7], ":WAIT,83.4,0,82.7")
        ]);
        Assert(settingsLeakVm.ManualWaterProofResults.Count == 3 &&
               Math.Abs(settingsLeakVm.ManualWaterProofResults[0].Leak.GetValueOrDefault() - 0.6) < 0.0001 &&
               !settingsLeakVm.ManualWaterProofResults[0].IsMeasured &&
               !settingsLeakVm.ManualWaterProofResults[1].Enabled &&
               Math.Abs(settingsLeakVm.ManualWaterProofResults[2].LiveMachineValue.GetValueOrDefault() - 82.7) < 0.0001,
            "Settings Leak CH1/CH2/CH3 cards update PRESS/WAIT values in realtime without an early result");
        finishSettingsLeak.Invoke(settingsLeakVm,
        [
            new WaterProofRunResult(
            [
                new WaterProofChannelMeasurement(1, true, 84.0, 83.4, 0.6, true),
                new WaterProofChannelMeasurement(2, false, 0.0, 0.0, 0.0, false),
                new WaterProofChannelMeasurement(3, true, 83.5, 82.7, 0.8, false)
            ],
            false,
            ":RESULT,84,83.4,0.6,1,0,0,0,0,83.5,82.7,0.8,0")
        ]);
        Assert(settingsLeakVm.ManualWaterProofResults[0].ResultText == "PASS" &&
               settingsLeakVm.ManualWaterProofResults[1].ResultText == "---" &&
               settingsLeakVm.ManualWaterProofResults[1].LiveMachineValue is null &&
               settingsLeakVm.ManualWaterProofResults[2].ResultText == "FAIL",
            "Settings Leak cards publish PASS/FAIL only from the final machine result");

        string xaml = File.ReadAllText(
    Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml"));

        Assert(
            xaml.Contains("Text=\"ĐỘ RÒ RỈ\"", StringComparison.Ordinal) &&
            xaml.Contains("Width=\"294\"", StringComparison.Ordinal) &&
            xaml.Contains("Height=\"92\"", StringComparison.Ordinal) &&
            xaml.Contains("Text=\"{Binding LiveMachineValueText}\"", StringComparison.Ordinal) &&
            xaml.Contains("Background=\"{Binding LiveCellBackground}\"", StringComparison.Ordinal) &&
            xaml.Contains("Foreground=\"{Binding LiveCellForeground}\"", StringComparison.Ordinal) &&
            !xaml.Contains("x:Name=\"WaterProofGrid\"", StringComparison.Ordinal) &&
            !xaml.Contains("WaterProofOperationGridStyle", StringComparison.Ordinal),
            "Leak uses only the compact TEST LEAK card; the lower area remains dedicated to continuity/final results");

        // Regression: :PRESS lưu áp cuối làm baseline, từng :WAIT phải cập nhật
        // Leak ngay trên UI nhưng tuyệt đối chưa được chốt PASS/FAIL trước :RESULT.
        TestViewModel realtimeLeakVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 });

        FieldInfo realtimeProfileField = typeof(TestViewModel).GetField(
            "_waterProofProfile",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak realtime profile field not found");

        MethodInfo resetRealtimeLeak = typeof(TestViewModel).GetMethod(
            "ResetWaterProofDisplay",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak realtime reset method not found");

        MethodInfo applyRealtimeLeak = typeof(TestViewModel).GetMethod(
            "ApplyWaterProofProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak realtime progress method not found");

        realtimeProfileField.SetValue(
            realtimeLeakVm,
            new WaterProofModelSettings
            {
                Enabled = true,
                Channel1Enabled = true,
                Channel2Enabled = true,
                Channel3Enabled = true,
                LeakLimit = 2.0
            });

        resetRealtimeLeak.Invoke(realtimeLeakVm, null);

        Assert(
            realtimeLeakVm.WaterProofChannels.Count == 3 &&
            realtimeLeakVm.WaterProofChannels.All(row => !row.IsMeasured),
            "Leak realtime reset creates three enabled channels without marking a result");

        applyRealtimeLeak.Invoke(
            realtimeLeakVm,
            [
                new WaterProofProgress(
                    WaterProofStage.Pressurizing,
                    new double[] { 84.1, 84.2, 84.0 },
                    ":PRESS,84.1,84.2,84.0")
            ]);

        Assert(
            Math.Abs(realtimeLeakVm.WaterProofChannels[0].PressPressure.GetValueOrDefault() - 84.1) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[1].PressPressure.GetValueOrDefault() - 84.2) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[2].PressPressure.GetValueOrDefault() - 84.0) < 0.0001 &&
            realtimeLeakVm.WaterProofChannels[0].LiveMachineValueText == "84.1" &&
            realtimeLeakVm.WaterProofChannels[1].LiveMachineValueText == "84.2" &&
            realtimeLeakVm.WaterProofChannels[2].LiveMachineValueText == "84.0" &&
            realtimeLeakVm.WaterProofChannels[0].FirstPressureText == "84.1" &&
            realtimeLeakVm.WaterProofChannels[0].SecondPressureText == "---" &&
            realtimeLeakVm.WaterProofChannels.All(row => Math.Abs(row.Leak.GetValueOrDefault()) < 0.0001) &&
            realtimeLeakVm.WaterProofChannels.All(row => !row.IsMeasured),
            "PRESS updates live pressure/table baseline, starts Leak at zero, and cannot mark PASS/FAIL");

        applyRealtimeLeak.Invoke(
            realtimeLeakVm,
            [
                new WaterProofProgress(
                    WaterProofStage.Waiting,
                    new double[] { 84.0, 84.1, 83.9 },
                    ":WAIT,84.0,84.1,83.9")
            ]);

        Assert(
            Math.Abs(realtimeLeakVm.WaterProofChannels[0].Leak.GetValueOrDefault() - 0.1) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[1].Leak.GetValueOrDefault() - 0.1) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[2].Leak.GetValueOrDefault() - 0.1) < 0.0001 &&
            realtimeLeakVm.WaterProofChannels[0].LiveMachineValueText == "84.0" &&
            realtimeLeakVm.WaterProofChannels[1].LiveMachineValueText == "84.1" &&
            realtimeLeakVm.WaterProofChannels[2].LiveMachineValueText == "83.9" &&
            realtimeLeakVm.WaterProofChannels[0].SecondPressureText == "84.0" &&
            realtimeLeakVm.WaterProofStageText == "ĐANG ĐO ĐỘ RÒ",
            "First WAIT frame updates displayed hold pressure and Leak immediately for all channels");

        applyRealtimeLeak.Invoke(
            realtimeLeakVm,
            [
                new WaterProofProgress(
                    WaterProofStage.Waiting,
                    new double[] { 83.5, 83.7, 83.4 },
                    ":WAIT,83.5,83.7,83.4")
            ]);

        Assert(
            Math.Abs(realtimeLeakVm.WaterProofChannels[0].Leak.GetValueOrDefault() - 0.6) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[1].Leak.GetValueOrDefault() - 0.5) < 0.0001 &&
            Math.Abs(realtimeLeakVm.WaterProofChannels[2].Leak.GetValueOrDefault() - 0.6) < 0.0001 &&
            realtimeLeakVm.WaterProofChannels[0].LiveMachineValueText == "83.5" &&
            realtimeLeakVm.WaterProofChannels[1].LiveMachineValueText == "83.7" &&
            realtimeLeakVm.WaterProofChannels[2].LiveMachineValueText == "83.4",
            "Later WAIT frames continuously replace both calculated Leak and compact live values");

        Assert(
            realtimeLeakVm.WaterProofChannels.All(row => !row.IsMeasured) &&
            realtimeLeakVm.WaterProofChannels.All(row => row.ResultText == "---"),
            "Realtime PRESS/WAIT display never publishes PASS/FAIL before the final RESULT");

        MethodInfo showLeakPanel = typeof(TestViewModel).GetMethod(
            "ShowWaterProofOperationPanel",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak operation panel method not found");
        realtimeLeakVm.SelectedOperationTabIndex = 0;
        showLeakPanel.Invoke(realtimeLeakVm, null);
        Assert(realtimeLeakVm.SelectedOperationTabIndex == 0,
            "Leak run keeps the lower continuity table visible and reports realtime values only in the TEST LEAK card");
        int resultStyleUses = xaml.Split(
            "CellStyle=\"{StaticResource PassFailResultCellStyle}\"",
            StringSplitOptions.None).Length - 1;
        Assert(resultStyleUses == 1 &&
               !xaml.Contains("WaterProofResultCellStyle", StringComparison.Ordinal),
            "Only the resistance/final result table remains; the removed Leak detail grid has no result column");
        Assert(xaml.Contains("<Viewbox Grid.Row=\"0\"", StringComparison.Ordinal) &&
               xaml.Contains("StretchDirection=\"DownOnly\"", StringComparison.Ordinal),
            "The fixed Htdrv header surface scales down at narrow resolutions without scaling up above design size");

        ProductModel connectorModel = HtdrvTwoEndpointModel();
        using TestEngine connectorEngine = CreateEngine(out _);
        connectorEngine.SetModel(connectorModel);
        Assert(!connectorEngine.HasConnectorActivity("1") &&
               !connectorEngine.IsConnectorConnected("1") &&
               connectorEngine.IsConnectorDisconnected("1"),
            "Leak connector gate remains closed and confirms full disconnection before fitting");
        connectorEngine.ProcessFrame(FrameSeq(1, (1, new[] { 2 })));
        Assert(connectorEngine.HasConnectorActivity("1") &&
               !connectorEngine.HasConnectedRetWire("1") &&
               connectorEngine.IsConnectorConnected("1") &&
               !connectorEngine.IsConnectorDisconnected("1") &&
               !connectorEngine.IsConnectorConnected("2") &&
               !connectorEngine.IsConnectorConnected(string.Empty),
            "Leak connector gate opens only for the exact connected THT connector");

        ProductModel retTriggerModel = TopologyModel(
            new Terminal(40, "1", "1", "2", "RET1"),
            new Terminal(41, "2", "1", "2", "RET1"),
            new Terminal(42, "1", "2", "2", "NORMAL"),
            new Terminal(43, "2", "2", "2", "NORMAL"));
        using TestEngine retTriggerEngine = CreateEngine(out _);
        retTriggerEngine.SetModel(retTriggerModel);
        retTriggerEngine.ProcessFrame(FrameSeq(2, (42, new[] { 43 })));
        Assert(!retTriggerEngine.HasConnectedRetWire("1"),
            "A correctly connected non-RET wire cannot start Leak");
        retTriggerEngine.ProcessFrame(FrameSeq(3, (40, new[] { 41 })));
        Assert(retTriggerEngine.HasConnectedRetWire("1") &&
               !retTriggerEngine.HasConnectedRetWire("UNKNOWN"),
            "RET1 connected to its matching RET1 topology through the selected connector starts Leak");

        foreach (string supportedRetName in new[] { "RET1", "RET01", "RT1" })
        {
            ProductModel supportedRetModel = TopologyModel(
                new Terminal(50, "LEAK", "1", "2", supportedRetName),
                new Terminal(51, "OTHER", "1", "2", supportedRetName));
            using TestEngine supportedRetEngine = CreateEngine(out _);
            supportedRetEngine.SetModel(supportedRetModel);
            supportedRetEngine.ProcessFrame(FrameSeq(4, (50, new[] { 51 })));
            Assert(supportedRetEngine.HasConnectedRetWire("LEAK"),
                $"{supportedRetName} must be accepted as a Leak trigger wire name");
        }

        ProductModel multiNetConnectorModel = TopologyModel(
            new Terminal(1, "LEAK", "1", "2", "A"),
            new Terminal(2, "OTHER-A", "1", "1", "A"),
            new Terminal(3, "LEAK", "2", "2", "B"),
            new Terminal(4, "OTHER-B", "1", "1", "B"));
        using TestEngine multiNetConnectorEngine = CreateEngine(out _);
        multiNetConnectorEngine.SetModel(multiNetConnectorModel);
        multiNetConnectorEngine.ProcessFrame(FrameSeq(2, (1, new[] { 2 }), (3, new[] { 4 })));
        Assert(multiNetConnectorEngine.IsConnectorConnected("LEAK"),
            "All Leak connector relations fitted report connected");
        multiNetConnectorEngine.ProcessFrame(FrameSeq(3, (1, new[] { 2 })));
        Assert(!multiNetConnectorEngine.IsConnectorConnected("LEAK") &&
               multiNetConnectorEngine.HasConnectorActivity("LEAK") &&
               !multiNetConnectorEngine.IsConnectorDisconnected("LEAK"),
            "Any Leak connector pin activity can start Leak even before every connector relation is fitted");
        multiNetConnectorEngine.ProcessFrame(FrameSeq(4));
        Assert(multiNetConnectorEngine.IsConnectorDisconnected("LEAK"),
            "Leak connector removal requires every mapped continuity relation to disappear");

        TestViewModel removalVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard removalBoard);
        removalVm.LoadPreparedModelAsync(Model(("LEAK-PAIR", new[] { 1, 18 })))
            .GetAwaiter()
            .GetResult();
        typeof(TestViewModel).GetField("_runtimeMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(removalVm, 1);
        TestEngine removalEngine =
            (TestEngine)(typeof(TestViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(removalVm) ?? throw new InvalidOperationException("Leak removal TestEngine not found"));
        removalEngine.SetFrameProcessingEnabled(true);
        removalVm.SelectedOperationTabIndex = 0;

        MethodInfo armRemoval = typeof(TestViewModel).GetMethod(
            "ArmWaterProofFaultRemovalWait",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak removal arm method not found");
        armRemoval.Invoke(removalVm, null);

        FieldInfo waitForFaultRemoval = typeof(TestViewModel).GetField(
            "_waitForFaultProductRemoval",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak removal wait flag not found");
        Assert(removalVm.SelectedOperationTabIndex == 0 &&
               (bool)(waitForFaultRemoval.GetValue(removalVm) ?? false),
            "Leak FAIL keeps the continuity/final result area visible and arms ProductRemoved confirmation");

        removalBoard.Publish(FrameSeq(1, (1, new[] { 18 })));
        Assert((bool)(waitForFaultRemoval.GetValue(removalVm) ?? false) &&
               removalVm.SelectedOperationTabIndex == 0,
            "Leak FAIL must keep the continuity/final area while any product IO remains connected");
        removalBoard.Publish(FrameSeq(2));
        Assert(!(bool)(waitForFaultRemoval.GetValue(removalVm) ?? true) &&
               removalVm.ResultStatusText == "LẮP SẢN PHẨM" &&
               removalVm.SelectedOperationTabIndex == 0,
            "Fresh empty frame after Leak FAIL resets the cycle instead of hanging on results");

        TestViewModel faultMainVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard faultMainBoard);
        faultMainVm.LoadPreparedModelAsync(Model(("FAIL-PAIR", new[] { 1, 18 })))
            .GetAwaiter()
            .GetResult();
        typeof(TestViewModel).GetField("_runtimeMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(faultMainVm, 1);
        TestEngine faultMainEngine =
            (TestEngine)(typeof(TestViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(faultMainVm) ?? throw new InvalidOperationException("FAIL MainWindow TestEngine not found"));
        faultMainEngine.SetFrameProcessingEnabled(true);
        armRemoval.Invoke(faultMainVm, null);
        faultMainBoard.Publish(FrameSeq(20, (1, new[] { 18 })));
        faultMainVm.StopViewAsync().GetAwaiter().GetResult();
        Assert(faultMainVm.IsProductRemovalPending &&
               faultMainVm.State == "VUI LÒNG THÁO SẢN PHẨM",
            "Returning to MainWindow during FAIL removal preserves the shared removal lock and warning");
        faultMainBoard.Publish(FrameSeq(21, (1, new[] { 18 })));
        Assert(faultMainVm.IsProductRemovalPending,
            "FAIL MainWindow removal lock remains while any product connection is present");
        faultMainBoard.Publish(FrameSeq(22));
        Assert(!faultMainVm.IsProductRemovalPending &&
               faultMainVm.ResultStatusText == "LẮP SẢN PHẨM",
            "FAIL MainWindow removal lock clears only after a complete empty frame");

        FieldInfo cycleActiveAfterLeakFail = typeof(TestViewModel).GetField(
            "_cycleActive",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak cycle-active flag not found");
        FieldInfo postContinuityAfterLeakFail = typeof(TestViewModel).GetField(
            "_postContinuityStarted",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak post-continuity flag not found");
        FieldInfo waterProofRunningAfterLeakFail = typeof(TestViewModel).GetField(
            "_waterProofRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak running flag not found");
        Assert((bool)(cycleActiveAfterLeakFail.GetValue(removalVm) ?? false) &&
               (int)(postContinuityAfterLeakFail.GetValue(removalVm) ?? -1) == 0 &&
               (int)(waterProofRunningAfterLeakFail.GetValue(removalVm) ?? -1) == 0,
            "Leak FAIL removal fully re-arms cycle 2 and releases both automatic-test locks");

        removalVm.SelectedOperationTabIndex = 0;
        MethodInfo armPassRemoval = typeof(TestViewModel).GetMethod(
            "ArmPassProductRemovalWait",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS removal arm method not found");
        armPassRemoval.Invoke(removalVm, null);
        FieldInfo waitForPassRemoval = typeof(TestViewModel).GetField(
            "_waitForProductRelease",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS removal wait flag not found");
        Assert((bool)(waitForPassRemoval.GetValue(removalVm) ?? false) &&
               removalVm.IsProductRemovalPending &&
               removalVm.SelectedOperationTabIndex == 0 &&
               removalVm.ResultStatusText == "THÁO SẢN PHẨM" &&
               removalVm.StateBackground == "#2AA84A",
            "Committed final PASS keeps the continuity/final area, stays green, and requests ProductRemoved before scan restart");
        removalVm.StopViewAsync().GetAwaiter().GetResult();
        Assert(removalVm.IsProductRemovalPending &&
               removalVm.State.Contains("VUI LÒNG THÁO SẢN PHẨM", StringComparison.Ordinal),
            "Returning to MainWindow preserves the committed PASS removal lock and background IO monitoring");
        removalBoard.Publish(FrameSeq(3, (1, new[] { 18 })));
        Assert((bool)(waitForPassRemoval.GetValue(removalVm) ?? false) &&
               removalVm.IsProductRemovalPending &&
               removalVm.SelectedOperationTabIndex == 0 &&
               removalVm.ResultStatusText == "VUI LÒNG THÁO SẢN PHẨM",
            "Final PASS remains visible over the continuity area while any product IO is still connected");
        removalBoard.Publish(FrameSeq(4));
        FieldInfo cycleActiveAfterMainRemoval = typeof(TestViewModel).GetField(
            "_cycleActive",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS main-screen cycle-active flag not found");
        Assert(!(bool)(waitForPassRemoval.GetValue(removalVm) ?? true) &&
               !removalVm.IsProductRemovalPending &&
               !(bool)(cycleActiveAfterMainRemoval.GetValue(removalVm) ?? true) &&
               removalVm.ResultStatusText == "LẮP SẢN PHẨM" &&
               removalVm.SelectedOperationTabIndex == 0,
            "After committed Leak PASS, a fresh empty frame clears the MainWindow lock without auto-arming a new test");

        TestViewModel pauseVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard pauseBoard);
        MethodInfo pauseD2xx = typeof(TestViewModel).GetMethod(
            "PauseProductionScanForWaterProofAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak D2XX pause method not found");
        ((Task)(pauseD2xx.Invoke(pauseVm, [CancellationToken.None])
            ?? throw new InvalidOperationException("Leak D2XX pause task not returned")))
            .GetAwaiter()
            .GetResult();
        Assert(!pauseBoard.IsScanning && pauseBoard.Commands.Contains("STOP"),
            "Leak stage pauses D2XX scan so pressure activity cannot invalidate continuity PASS");

        TestViewModel finalPassPauseVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 },
            out FakeBoard finalPassPauseBoard);
        MethodInfo pauseFinalPass = typeof(TestViewModel).GetMethod(
            "PauseProductionScanForFinalPassAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Final PASS D2XX pause method not found");
        ((Task)(pauseFinalPass.Invoke(finalPassPauseVm, [CancellationToken.None])
            ?? throw new InvalidOperationException("Final PASS D2XX pause task not returned")))
            .GetAwaiter()
            .GetResult();
        Assert(!finalPassPauseBoard.IsScanning && finalPassPauseBoard.Commands.Contains("STOP"),
            "Every final PASS path freezes D2XX continuity before relay execution, even when Leak is disabled");

        MethodInfo shouldRestart = typeof(TestViewModel).GetMethod(
            "ShouldRestartAfterPass",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS restart policy method not found");
        Assert((bool)(shouldRestart.Invoke(null, [false, true]) ?? false) &&
               !(bool)(shouldRestart.Invoke(null, [false, false]) ?? true),
            "Completed Leak always enters removal/reset lifecycle even when legacy auto-restart is disabled");

        TestViewModel retestArmVm = CreateTestViewModel(
            new ProductionSettings { MasterFaultRequiredCount = 0 });
        retestArmVm.LoadPreparedModelAsync(HtdrvTwoEndpointModel())
            .GetAwaiter()
            .GetResult();
        typeof(TestViewModel).GetField("_waterProofProfile", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(retestArmVm, new WaterProofModelSettings
            {
                Enabled = true,
                Channel1Enabled = true,
                Channel1Connector = "1"
            });
        typeof(TestViewModel).GetField("_lastWaterProofMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(retestArmVm, new WaterProofChannelMeasurement[]
            {
                new(1, true, 84.0, 83.8, 0.2, true)
            });
        typeof(TestViewModel).GetField("_productionPhase", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(retestArmVm, 3);
        MethodInfo armLeakRetest = typeof(TestViewModel).GetMethod(
            "ArmWaterProofRetestConnectorCycle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak retest connector ARM method not found");
        FieldInfo leakRetestState = typeof(TestViewModel).GetField(
            "_waterProofRetestConnectorState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Leak retest connector state not found");
        armLeakRetest.Invoke(retestArmVm, null);
        Assert((int)(leakRetestState.GetValue(retestArmVm) ?? 0) == 1,
            "A pre-continuity Leak FAIL arms connector remove/reinsert retest");

        retestArmVm.Faults.Add(new FaultRow
        {
            Kind = FaultKind.MissingConnection,
            Io = 40,
            WireName = "RET1",
            Status = "CHƯA KẾT NỐI"
        });
        MethodInfo refreshLeakFaults = typeof(TestViewModel).GetMethod(
            "RefreshFaults",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-grid refresh method not found");
        refreshLeakFaults.Invoke(retestArmVm, null);
        Assert(retestArmVm.Faults.Count == 0,
            "The lower continuity table stays empty while Leak is running or awaiting RET reconnection");
        typeof(TestViewModel).GetField("_waterProofProfile", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(retestArmVm, new WaterProofModelSettings { Enabled = false });
        armLeakRetest.Invoke(retestArmVm, null);
        Assert((int)(leakRetestState.GetValue(retestArmVm) ?? -1) == 0,
            "Models without Leak enabled cannot arm connector retest");
        string testViewModelSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "TestViewModel.cs"));
        int leakGateStart = testViewModelSource.IndexOf(
            "private bool TryValidateWaterProofConnectorGate",
            StringComparison.Ordinal);
        int leakGateEnd = testViewModelSource.IndexOf(
            "private string[] ConfiguredWaterProofConnectorIds",
            leakGateStart,
            StringComparison.Ordinal);
        string leakGateSource = testViewModelSource[leakGateStart..leakGateEnd];
        Assert(leakGateSource.Contains("HasConnectedRetWire", StringComparison.Ordinal) &&
               !leakGateSource.Contains("HasConnectorActivity", StringComparison.Ordinal),
            "Production Leak gate requires a correctly connected RET-number wire, not arbitrary connector activity");
        int retestMethodStart = testViewModelSource.IndexOf(
            "private async Task RunWaterProofRetestOnlyAsync",
            StringComparison.Ordinal);
        int retestMethodEnd = testViewModelSource.IndexOf(
            "private async Task<bool> SaveWaterProofRetestHistoryAsync",
            retestMethodStart,
            StringComparison.Ordinal);
        string retestMethodSource = testViewModelSource[retestMethodStart..retestMethodEnd];
        Assert(!retestMethodSource.Contains("SaveWaterProofRetestHistoryAsync", StringComparison.Ordinal) &&
               retestMethodSource.Contains("FinalizeWaterProofProductFailureAsync", StringComparison.Ordinal) &&
               !retestMethodSource.Contains("RecordCompletedProductAsync", StringComparison.Ordinal) &&
               !retestMethodSource.Contains("CompletePassAsync", StringComparison.Ordinal) &&
               !retestMethodSource.Contains("EjectFaultProductAsync", StringComparison.Ordinal),
            "Transient Leak retest does not write separate production history; only the centralized full-continuity FAIL gate may finalize it");

        int processStart = testViewModelSource.IndexOf(
            "private void ProcessEngineChangedOnUi",
            StringComparison.Ordinal);
        int processEnd = testViewModelSource.IndexOf(
            "private void CaptureProductTestStartedAt",
            processStart,
            StringComparison.Ordinal);
        string processSource = testViewModelSource[processStart..processEnd];
        int leakFirstGate = processSource.IndexOf(
            "phase == ProductionPhase.Continuity",
            StringComparison.Ordinal);
        int wiringFaultGate = processSource.IndexOf(
            "_engine.LastFrameValid &&",
            StringComparison.Ordinal);
        int postContinuityStart = testViewModelSource.IndexOf(
            "private async Task RunAutomaticPostContinuityAsync",
            StringComparison.Ordinal);
        int postContinuityEnd = testViewModelSource.IndexOf(
            "private async Task HandleFinalPassRejectedAsync",
            postContinuityStart,
            StringComparison.Ordinal);
        string postContinuitySource = testViewModelSource[postContinuityStart..postContinuityEnd];
        int finalizeLeakStart = testViewModelSource.IndexOf(
            "private async Task FinalizeWaterProofProductFailureAsync",
            StringComparison.Ordinal);
        int finalizeLeakEnd = testViewModelSource.IndexOf(
            "private async Task<bool> RunAutomaticWaterProofAsync",
            finalizeLeakStart,
            StringComparison.Ordinal);
        string finalizeLeakSource = testViewModelSource[finalizeLeakStart..finalizeLeakEnd];
        Assert(leakFirstGate >= 0 && leakFirstGate < wiringFaultGate &&
               processSource.Contains("RunPreContinuityWaterProofAsync", StringComparison.Ordinal) &&
               !postContinuitySource.Contains("RunAutomaticWaterProofAsync", StringComparison.Ordinal) &&
               finalizeLeakSource.Contains("_engine.ContinuityPassed", StringComparison.Ordinal) &&
               finalizeLeakSource.Contains("RecordCompletedProductAsync", StringComparison.Ordinal) &&
               finalizeLeakSource.Contains("failureDetails: faults", StringComparison.Ordinal) &&
               finalizeLeakSource.Contains("ShowFaultConfirmationDialog", StringComparison.Ordinal),
            "Leak starts before continuity evaluation; Leak FAIL is recorded and confirmed only after full continuity PASS");

        var leakDisplayRow = new ResistanceResult
        {
            Name = "TEST LEAK",
            ChannelTextOverride = "CH1",
            MinDisplayTextOverride = "—",
            DisplayOverride = "CH1 Δ0.2",
            MaxDisplayTextOverride = "≤ 2",
            ResultTextOverride = "PASS"
        };
        Assert(xaml.Contains("ItemsSource=\"{Binding ResistanceDisplayRows}\"", StringComparison.Ordinal) &&
               leakDisplayRow.Name == "TEST LEAK" &&
               leakDisplayRow.ChannelText == "CH1" &&
               leakDisplayRow.Display == "CH1 Δ0.2" &&
               leakDisplayRow.ResultText == "PASS",
            "Resistance result table can show two resistance measurements plus a separate TEST LEAK PASS row");

        var idleLeakService = new WaterProofSerialService();
        Stopwatch disposeWatch = Stopwatch.StartNew();
        idleLeakService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert(disposeWatch.ElapsedMilliseconds < 1_000,
            "Idle Leak COM disposal is bounded and cannot hang application shutdown");

        string leakServiceSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Services",
            "WaterProofSerialService.cs"));
        string productionSettingsXaml = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Views",
            "ProductionSettingsPage.xaml"));
        string productionSettingsVmSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "ProductionSettingsViewModel.cs"));
        string manualLeakMethodSource = testViewModelSource[
            testViewModelSource.IndexOf("public async Task<WaterProofRunResult> TestManualWaterProofAsync", StringComparison.Ordinal)..
            testViewModelSource.IndexOf("private string ReadyStateForCurrentModel", StringComparison.Ordinal)];
        Assert(
            leakServiceSource.Contains(
                "ReleaseRunPort(runNumber, port)",
                StringComparison.Ordinal) &&
            leakServiceSource.Contains(
                "WaitForPendingCloseBestEffortAsync",
                StringComparison.Ordinal) &&
            leakServiceSource.Contains(
                "next run will reconnect cleanly",
                StringComparison.Ordinal),
            "A completed Leak result releases only its owned COM session and waits bounded cleanup before cycle 2");
        Assert(productionSettingsXaml.Contains("Content=\"CHẠY TEST\"", StringComparison.Ordinal) &&
               productionSettingsXaml.Contains("ManualWaterProofTestCommand", StringComparison.Ordinal) &&
               productionSettingsXaml.Contains("ManualWaterProofResults", StringComparison.Ordinal) &&
               productionSettingsXaml.Contains("LiveMachineValueText", StringComparison.Ordinal) &&
               !productionSettingsXaml.Contains("Settings.WaterProofMachine.BaudRate", StringComparison.Ordinal) &&
               productionSettingsVmSource.Contains("ManualWaterProofStatus", StringComparison.Ordinal) &&
               productionSettingsVmSource.Contains("UpdateManualWaterProofProgress", StringComparison.Ordinal) &&
               productionSettingsVmSource.Contains("ApplyManualWaterProofResult", StringComparison.Ordinal) &&
               productionSettingsVmSource.Contains("Máy Leak phải dùng một cổng COM riêng", StringComparison.Ordinal) &&
               manualLeakMethodSource.Contains("_waterProof.RunTestAsync", StringComparison.Ordinal) &&
               manualLeakMethodSource.Contains("AllRelaysOffAsync", StringComparison.Ordinal) &&
               !manualLeakMethodSource.Contains("RecordCompletedProductAsync", StringComparison.Ordinal) &&
               !manualLeakMethodSource.Contains("CompletePassAsync", StringComparison.Ordinal) &&
               !manualLeakMethodSource.Contains("EjectFaultProductAsync", StringComparison.Ordinal),
            "Settings manual Leak test uses the owned COM path without production count or relay output");
    }

    private static void TestProductionScanTokenSurvivesCycleCancel()
    {
        var settings = new ProductionSettings { MasterFaultRequiredCount = 0 };
        TestViewModel vm = CreateTestViewModel(settings, out FakeBoard board);
        vm.LoadPreparedModelAsync(Model(("PAIR", new[] { 1, 18 }))).GetAwaiter().GetResult();

        board.StopScanAsync().GetAwaiter().GetResult();
        int commandsBeforeArm = board.Commands.Count;
        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(board.Commands.Count == commandsBeforeArm && !board.IsScanning,
            "START does not reconnect, initialize, or start hardware when background scan is unavailable");

        board.StartScanAsync(BoardScanMode.Production, CancellationToken.None).GetAwaiter().GetResult();
        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(board.LastStartScanToken.HasValue, "Background lifecycle owns the production START_SCAN token");
        CancellationToken scanToken = board.LastStartScanToken.GetValueOrDefault();

        MethodInfo cancelCycle = typeof(TestViewModel).GetMethod(
            "CancelCycleOperations",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CancelCycleOperations method not found.");
        cancelCycle.Invoke(vm, []);

        Assert(board.IsScanning, "Canceling ProductCycleToken does not stop the background D2XX scan session");
        Assert(!scanToken.IsCancellationRequested, "START_SCAN token is independent from ProductCycleToken");
    }

    private static void TestLegacyDatabaseWithoutSchemaInfo()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZLegacyDatabaseWithoutSchemaInfo",
            Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "JBZUniveresalLunix.db");
        string backupPath = dbPath + $".pre-schema-v{TestHistoryStore.CurrentSchemaVersion}.backup";
        try
        {
            Directory.CreateDirectory(root);
            using (var legacy = new SqliteConnection($"Data Source={dbPath}"))
            {
                legacy.Open();
                using SqliteCommand create = legacy.CreateCommand();
                create.CommandText = "CREATE TABLE LegacyMarker(Value TEXT NOT NULL); " +
                                     "INSERT INTO LegacyMarker(Value) VALUES ('KEEP');";
                create.ExecuteNonQuery();
            }

            var store = new TestHistoryStore(dbPath);
            Assert(store.SchemaVersion == TestHistoryStore.CurrentSchemaVersion,
                "Legacy database initializes the current relational schema");
            Assert(File.Exists(backupPath),
                "Legacy database is backed up before schema migration");

            using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            verify.Open();
            using SqliteCommand probe = verify.CreateCommand();
            probe.CommandText = "SELECT " +
                                "(SELECT COUNT(*) FROM SchemaInfo), " +
                                "(SELECT COUNT(*) FROM LegacyMarker WHERE Value='KEEP');";
            using SqliteDataReader reader = probe.ExecuteReader();
            Assert(reader.Read() && reader.GetInt32(0) == 1 && reader.GetInt32(1) == 1,
                "Schema initialization preserves tables from a database without SchemaInfo");
            reader.Close();
            verify.Close();

            string v3Path = Path.Combine(root, "schema-v3.db");
            var v3Store = new TestHistoryStore(v3Path);
            v3Store.Add(new TestHistoryRecord
            {
                Started = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Local),
                Finished = new DateTime(2026, 9, 1, 8, 0, 1, DateTimeKind.Local),
                PartNumber = "V3-PART",
                ModelName = "V3-MODEL",
                ModelFile = "V3.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "v3-existing-cycle"
            });
            SqliteConnection.ClearAllPools();
            using (var downgrade = new SqliteConnection($"Data Source={v3Path};Pooling=False"))
            {
                downgrade.Open();
                using SqliteCommand command = downgrade.CreateCommand();
                command.CommandText = """
                    DROP TABLE ActiveTestCycles;
                    UPDATE SchemaInfo SET SchemaVersion=3 WHERE Id=1;
                    """;
                command.ExecuteNonQuery();
            }

            var migratedV3 = new TestHistoryStore(v3Path);
            Assert(File.Exists(v3Path + $".pre-schema-v{TestHistoryStore.CurrentSchemaVersion}.backup"),
                "Schema v3 database is backed up before v4 migration");
            using var migratedVerify = new SqliteConnection($"Data Source={v3Path};Pooling=False");
            migratedVerify.Open();
            using SqliteCommand migratedProbe = migratedVerify.CreateCommand();
            migratedProbe.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ActiveTestCycles'),
                    (SELECT COUNT(*) FROM Tests WHERE CycleId='v3-existing-cycle');
                """;
            using SqliteDataReader migratedReader = migratedProbe.ExecuteReader();
            Assert(migratedReader.Read() && migratedReader.GetInt32(0) == 1 &&
                   migratedReader.GetInt32(1) == 1 &&
                   migratedV3.SchemaVersion == TestHistoryStore.CurrentSchemaVersion,
                "Schema v3 migration is idempotent and preserves existing test rows");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void TestDatabaseSchemaV5()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZDatabaseSchemaV5Tests",
            Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "JBZUniveresalLunix.db");
        try
        {
            Directory.CreateDirectory(root);
            var initial = new TestHistoryStore(dbPath);
            DateTime at = new(2026, 9, 7, 8, 0, 0, DateTimeKind.Local);

            static ProductionResultCommitRequest Request(
                string partNumber,
                string cycleId,
                DateTime at)
            {
                var model = new ProductModel
                {
                    PartNumber = partNumber,
                    ProductName = partNumber,
                    ModelName = "SHARED-HARNESS",
                    SourcePath = @"C:\Models\shared.tht",
                    SourceHash = new string('A', 64),
                    SourceLength = 1234,
                    SourceModifiedAt = at
                };
                var history = new TestHistoryRecord
                {
                    Started = at.AddSeconds(-1),
                    TestStartedAt = at.AddMilliseconds(-500),
                    ResultAt = at,
                    Finished = at,
                    PartName = partNumber,
                    PartNumber = partNumber,
                    ModelName = model.ModelName,
                    ModelFile = model.SourcePath,
                    Result = "PASS",
                    Passed = true,
                    CycleId = cycleId,
                    InspectionType = HistoryInspectionType.Product,
                    LabelPayload = new string('P', 4096)
                };
                return ProductionResultCommitRequest.Capture(
                    history,
                    model,
                    new ProductionSettings(),
                    [],
                    [new ResistanceResult
                    {
                        Channel = 1,
                        Name = "R1",
                        ValueOhm = 100,
                        MinOhm = 90,
                        MaxOhm = 110,
                        Passed = true,
                        SampleCount = 3,
                        StabilizationTimeMs = 5
                    }],
                    [new WaterProofChannelMeasurement(1, true, 84, 83.8, 0.2, true)],
                    "SELF-TEST");
            }

            ProductionCommitResult first = initial.CommitResult(
                Request("PART-M2M-A", "schema-v5-a", at),
                null);
            ProductionCommitResult second = initial.CommitResult(
                Request("PART-M2M-B", "schema-v5-b", at.AddSeconds(1)),
                null);
            Assert(first.TestId > 0 && second.TestId > first.TestId,
                "Two Parts using one harness both commit normally");

            HistorySearchCriteria allHistory = new(
                at.Date, at.Date.AddDays(1), null, string.Empty, "ALL", MaxRows: 1);
            HistorySummary allSummary = initial.GetHistorySummary(allHistory);
            HistorySummary partASummary = initial.GetHistorySummary(
                allHistory with { PartKeyword = "PART-M2M-A" });
            HistorySummary failSummary = initial.GetHistorySummary(
                allHistory with { Result = "FAIL" });
            IReadOnlyList<HistoryPartOption> historyParts = initial.GetHistoryPartOptions();
            Assert(allSummary.Total == 2 && allSummary.ProductTotal == 2 &&
                   allSummary.ProductPass == 2 && allSummary.ProductFail == 0 &&
                   partASummary.Total == 1 && partASummary.ProductPass == 1 &&
                   failSummary.Total == 0,
                "History SQL summary uses the complete filtered dataset independently of page size");
            Assert(historyParts.Count == 2 && historyParts[0].Keyword.Length == 0 &&
                   historyParts.Any(item => item.Keyword == "shared.tht"),
                "History ComboBox is populated from distinct saved THT filenames, not PartNumber");

            IReadOnlyList<PartModelRelationSnapshot> partA = initial.GetModelsForPart("PN:PART-M2M-A");
            IReadOnlyList<PartModelRelationSnapshot> partB = initial.GetModelsForPart("PN:PART-M2M-B");
            Assert(partA.Count == 1 && partB.Count == 1 && partA[0].ModelId == partB[0].ModelId,
                "PartModels resolves one physical model for multiple Parts");
            Assert(initial.GetPartsForModel(partA[0].ModelId).Count == 2,
                "Reverse PartModels index resolves both Parts for one model");

            IReadOnlyList<TestHistoryRecord> firstPage = initial.SearchSummary(
                new HistorySearchCriteria(null, null, null, string.Empty, "ALL", MaxRows: 1));
            Assert(firstPage.Count == 1 && firstPage[0].LabelPayload.Length == 0,
                "History summary omits large label payloads");
            IReadOnlyList<TestHistoryRecord> allRows = initial.SearchAllSummary(
                new HistorySearchCriteria(null, null, null, string.Empty, "ALL", MaxRows: 1));
            Assert(allRows.Count == 2 && allRows.All(row => row.LabelPayload.Length == 0),
                "History full summary returns every matching row without the UI page limit");
            IReadOnlyList<TestHistoryRecord> secondPage = initial.SearchSummary(
                new HistorySearchCriteria(
                    null,
                    null,
                    null,
                    string.Empty,
                    "ALL",
                    MaxRows: 1,
                    AfterHistoryAt: firstPage[0].EffectiveTestStartedAt,
                    AfterId: firstPage[0].Id));
            Assert(secondPage.Count == 1 && secondPage[0].Id != firstPage[0].Id,
                "History keyset cursor returns the next stable page without OFFSET");
            Assert(firstPage.Count + secondPage.Count == allSummary.Total,
                "Incremental keyset pages can reach the final matching row while summary remains global");

            SqliteConnection.ClearAllPools();
            using (var downgrade = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                downgrade.Open();
                using SqliteCommand command = downgrade.CreateCommand();
                command.CommandText = """
                    DROP TABLE PartModels;
                    DROP INDEX IF EXISTS IX_Tests_ResultAt_Id;
                    DROP INDEX IF EXISTS IX_Tests_Part_Inspection_ResultAt_Id;
                    DROP INDEX IF EXISTS IX_Tests_ModelId_ResultAt_Id;
                    DROP INDEX IF EXISTS IX_Tests_HistoryAt_Id;
                    DROP INDEX IF EXISTS IX_Tests_Part_Inspection_HistoryAt_Id;
                    DROP INDEX IF EXISTS IX_TestFaults_TestId_FaultOrder_Id;
                    DROP INDEX IF EXISTS IX_ResistanceMeasurements_TestId;
                    DROP INDEX IF EXISTS IX_WaterProofMeasurements_TestId;
                    CREATE INDEX IF NOT EXISTS IX_Tests_ResultAt ON Tests(ResultAt DESC);
                    CREATE INDEX IF NOT EXISTS IX_Tests_Part_ResultAt ON Tests(PartId,ResultAt DESC);
                    CREATE INDEX IF NOT EXISTS IX_TestFaults_TestId ON TestFaults(TestId);
                    ALTER TABLE Tests DROP COLUMN TestStartedAt;
                    UPDATE SchemaInfo SET SchemaVersion=4 WHERE Id=1;
                    """;
                command.ExecuteNonQuery();
            }

            var migrated = new TestHistoryStore(dbPath);
            var rerun = new TestHistoryStore(dbPath);
            Assert(File.Exists(dbPath + $".pre-schema-v{TestHistoryStore.CurrentSchemaVersion}.backup"),
                "Schema v4 is backed up before v7 migration");
            Assert(migrated.SchemaVersion == TestHistoryStore.CurrentSchemaVersion &&
                   rerun.SchemaVersion == TestHistoryStore.CurrentSchemaVersion,
                "Schema v7 migration is idempotent");
            Assert(migrated.SearchSummary(new HistorySearchCriteria(
                       null, null, null, "PART-M2M", "ALL", MaxRows: 10)).Count == 2,
                "Migration adds TestStartedAt before creating History indexes and preserves legacy rows");
            Assert(migrated.GetModelsForPart("PN:PART-M2M-A").Count == 1 &&
                   migrated.Search(new HistorySearchCriteria(
                       null, null, null, "PART-M2M", "ALL", MaxRows: 10)).Count == 2,
                "v4 Tests remain readable and legacy Models.PartId backfills without inferred relations");

            using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            verify.Open();
            using SqliteCommand probe = verify.CreateCommand();
            probe.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM PartModels),
                    (SELECT COUNT(*) FROM (SELECT PartId,ModelId FROM PartModels GROUP BY PartId,ModelId HAVING COUNT(*)>1)),
                    (SELECT COUNT(*) FROM Tests),
                    (SELECT COUNT(*) FROM TestFaults),
                    (SELECT COUNT(*) FROM ResistanceMeasurements),
                    (SELECT COUNT(*) FROM WaterProofMeasurements),
                    (SELECT COUNT(*) FROM TestFaults c LEFT JOIN Tests t ON t.Id=c.TestId WHERE t.Id IS NULL) +
                    (SELECT COUNT(*) FROM ResistanceMeasurements c LEFT JOIN Tests t ON t.Id=c.TestId WHERE t.Id IS NULL) +
                    (SELECT COUNT(*) FROM WaterProofMeasurements c LEFT JOIN Tests t ON t.Id=c.TestId WHERE t.Id IS NULL),
                    (SELECT integrity_check FROM pragma_integrity_check LIMIT 1);
                """;
            using SqliteDataReader reader = probe.ExecuteReader();
            Assert(reader.Read() && reader.GetInt32(0) == 1 && reader.GetInt32(1) == 0 &&
                   reader.GetInt32(2) == 2 && reader.GetInt32(3) == 0 &&
                   reader.GetInt32(4) == 2 && reader.GetInt32(5) == 2 &&
                   reader.GetInt32(6) == 0 && reader.GetString(7) == "ok",
                "v7 migration preserves Tests and child rows without orphans and integrity_check remains clean");
            reader.Close();

            string latestPlan = ExplainPlan(
                verify,
                "SELECT Id FROM Tests ORDER BY ResultAt DESC,Id DESC LIMIT 200;");
            string partPlan = ExplainPlan(
                verify,
                "SELECT Id FROM Tests WHERE PartId=1 AND InspectionType='PRODUCT' ORDER BY ResultAt DESC,Id DESC LIMIT 200;");
            string resistancePlan = ExplainPlan(
                verify,
                $"SELECT * FROM ResistanceMeasurements WHERE TestId={first.TestId};");
            string waterproofPlan = ExplainPlan(
                verify,
                $"SELECT * FROM WaterProofMeasurements WHERE TestId={first.TestId};");
            string reversePlan = ExplainPlan(
                verify,
                $"SELECT PartId FROM PartModels WHERE ModelId={partA[0].ModelId};");
            Assert(latestPlan.Contains("IX_Tests_ResultAt_Id", StringComparison.Ordinal) &&
                   partPlan.Contains("IX_Tests_Part_Inspection_ResultAt_Id", StringComparison.Ordinal) &&
                   resistancePlan.Contains("IX_ResistanceMeasurements_TestId", StringComparison.Ordinal) &&
                   waterproofPlan.Contains("IX_WaterProofMeasurements_TestId", StringComparison.Ordinal) &&
                   reversePlan.Contains("IX_PartModels_ModelId_PartId", StringComparison.Ordinal),
                "EXPLAIN QUERY PLAN selects every v5 workload index");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static string ExplainPlan(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join(" | ", details);
    }

    private static void TestHistoryInitializationWaitsForWriter()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZHistoryInitializationLockTests",
            Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "JBZUniveresalLunix.db");
        try
        {
            Directory.CreateDirectory(root);
            _ = new TestHistoryStore(dbPath);

            using var writerStarted = new ManualResetEventSlim(false);
            Task writer = Task.Run(() =>
            {
                using var connection = new SqliteConnection(
                    $"Data Source={dbPath};Cache=Shared;Pooling=False;Default Timeout=5");
                connection.Open();
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE SchemaInfo SET UpdatedAt=UpdatedAt WHERE Id=1;";
                command.ExecuteNonQuery();
                writerStarted.Set();
                Thread.Sleep(250);
                transaction.Commit();
            });

            Assert(writerStarted.Wait(TimeSpan.FromSeconds(5)),
                "Concurrent SQLite writer entered its transaction");
            var reopened = new TestHistoryStore(dbPath);
            writer.GetAwaiter().GetResult();
            Assert(reopened.SchemaVersion == TestHistoryStore.CurrentSchemaVersion,
                "History store opens after the active writer commits instead of throwing SQLITE_LOCKED");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void TestHistoryInterruptedTransactionRecovery()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZHistoryInterruptedTransactionTests",
            Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "JBZUniveresalLunix.db");
        try
        {
            Directory.CreateDirectory(root);
            _ = new TestHistoryStore(dbPath);

            using (var connection = new SqliteConnection(
                       $"Data Source={dbPath};Pooling=False;Default Timeout=2"))
            {
                connection.Open();
                using (SqliteCommand seed = connection.CreateCommand())
                {
                    seed.CommandText =
                        "CREATE TABLE IF NOT EXISTS PowerLossProbe(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);" +
                        "INSERT OR REPLACE INTO PowerLossProbe(Id,Value) VALUES(1,'COMMITTED');";
                    seed.ExecuteNonQuery();
                }

                using SqliteTransaction interrupted = connection.BeginTransaction();
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = interrupted;
                update.CommandText = "UPDATE PowerLossProbe SET Value='UNCOMMITTED' WHERE Id=1;";
                update.ExecuteNonQuery();
                // Không Commit: mô phỏng transaction bị ngắt giữa chừng ở mức
                // ứng dụng. Dispose phải rollback phần chưa durable.
            }

            var reopened = new TestHistoryStore(dbPath);
            using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            verify.Open();
            using SqliteCommand command = verify.CreateCommand();
            command.CommandText =
                "SELECT Value, (SELECT quick_check FROM pragma_quick_check LIMIT 1) " +
                "FROM PowerLossProbe WHERE Id=1;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert(reader.Read() && reader.GetString(0) == "COMMITTED" && reader.GetString(1) == "ok",
                "WAL reopens cleanly and discards only the interrupted transaction");
            Assert(reopened.SchemaVersion == TestHistoryStore.CurrentSchemaVersion,
                "Recovered production database keeps the current schema");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void TestProductionPersistenceRetriesTransientLock()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZProductionPersistenceRetryTests",
            Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "JBZUniveresalLunix.db");
        ProductionPersistenceService? persistence = null;
        try
        {
            Directory.CreateDirectory(root);
            var repository = new TestHistoryStore(dbPath);
            persistence = new ProductionPersistenceService(
                repository,
                new ProductionSettings(),
                "SELF-TEST");
            persistence.Initialization.GetAwaiter().GetResult();

            using var writerStarted = new ManualResetEventSlim(false);
            Task writer = Task.Run(() =>
            {
                using var connection = new SqliteConnection(
                    $"Data Source={dbPath};Pooling=False;Default Timeout=2");
                connection.Open();
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE SchemaInfo SET UpdatedAt=UpdatedAt WHERE Id=1;";
                command.ExecuteNonQuery();
                writerStarted.Set();
                Thread.Sleep(2150);
                transaction.Commit();
            });

            Assert(writerStarted.Wait(TimeSpan.FromSeconds(5)),
                "Transient SQLite writer acquired the database lock");
            var part = new PartIdentitySnapshot(
                "PN:LOCK-RETRY",
                "LOCK-RETRY",
                "", "", "", "", "", "");
            ProbeCounterSnapshot counter = persistence.IncrementProbeCounterAsync(part, 200000)
                .GetAwaiter().GetResult();
            writer.GetAwaiter().GetResult();
            Assert(counter.Counter == 1 && string.IsNullOrEmpty(persistence.LastDatabaseError),
                "Serialized writer retries BUSY and commits once after the transient lock clears");
        }
        finally
        {
            if (persistence is not null)
                persistence.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void TestCanonicalRuntimePersistence()
    {
        Assert(Path.GetFileName(RuntimePaths.ConfigFile) == "JBZUniveresalLunix.cfg",
            "Canonical config filename");
        Assert(Path.GetFileName(RuntimePaths.PartCounterFile) == "PartCnt.txt",
            "Canonical PartCnt filename");
        Assert(Path.GetFileName(RuntimePaths.LogFile) == "JBZUniveresalLunix.log",
            "Canonical log filename");
        Assert(Path.GetFileName(RuntimePaths.DatabaseFile) == "JBZUniveresalLunix.db" &&
               string.Equals(Path.GetFileName(Path.GetDirectoryName(RuntimePaths.DatabaseFile)), "Data", StringComparison.OrdinalIgnoreCase),
            "Canonical database path");
        Assert(Path.GetFileName(RuntimePaths.CrashReportFile) == "JBZUniveresalLunix.RPT" &&
               string.Equals(Path.GetFileName(Path.GetDirectoryName(RuntimePaths.CrashReportFile)), "Crash", StringComparison.OrdinalIgnoreCase),
            "Canonical lazy crash-report path");
        Assert(RuntimePaths.PassRoot == @"C:\Pass" && RuntimePaths.ErrorRoot == @"C:\Error" &&
               RuntimePaths.ItemDirectory == @"C:\ITEM",
            "Canonical external ITEM/Pass/Error roots");

        bool crashDirectoryExisted = Directory.Exists(RuntimePaths.CrashDirectory);
        bool crashReportExisted = File.Exists(RuntimePaths.CrashReportFile);
        if (!crashReportExisted)
        {
            CrashReportService.Write(
                new InvalidOperationException("SIMULATED_CRASH_REPORT_TEST"),
                "SELF-TEST",
                "Model=TEST; Cycle=TEST-CYCLE");
            string report = File.ReadAllText(RuntimePaths.CrashReportFile, Encoding.UTF8);
            Assert(report.Contains("SIMULATED_CRASH_REPORT_TEST", StringComparison.Ordinal) &&
                   report.Contains("Model=TEST; Cycle=TEST-CYCLE", StringComparison.Ordinal) &&
                   report.Contains("AppVersion:", StringComparison.Ordinal),
                "Crash RPT is created lazily with exception and runtime context");
            File.Delete(RuntimePaths.CrashReportFile);

            bool loggingWasEnabled = AsyncFileLogService.Current.FileLoggingEnabled;
            AsyncFileLogService.Current.Configure(false);
            CrashReportService.Write(
                new IOException("FTDI D2XX board secret detail"),
                "Hardware.DeviceFault.BoardStartup",
                "BoardGeneration=7; transport=D2XX");
            string operatorReport = File.ReadAllText(RuntimePaths.CrashReportFile, Encoding.UTF8);
            Assert(operatorReport.Contains("LỖI KẾT NỐI THIẾT BỊ", StringComparison.Ordinal) &&
                   !operatorReport.Contains("Hardware.DeviceFault", StringComparison.Ordinal) &&
                   !operatorReport.Contains("FTDI", StringComparison.OrdinalIgnoreCase) &&
                   !operatorReport.Contains("D2XX", StringComparison.OrdinalIgnoreCase) &&
                   !operatorReport.Contains("Board", StringComparison.OrdinalIgnoreCase) &&
                   !operatorReport.Contains("StackTrace", StringComparison.Ordinal),
                "Device connection RPT hides code, transport, and board details while system logs are disabled");
            File.Delete(RuntimePaths.CrashReportFile);
            AsyncFileLogService.Current.Configure(loggingWasEnabled);
            if (!crashDirectoryExisted && Directory.Exists(RuntimePaths.CrashDirectory) &&
                !Directory.EnumerateFileSystemEntries(RuntimePaths.CrashDirectory).Any())
            {
                Directory.Delete(RuntimePaths.CrashDirectory);
            }
        }

        string root = Path.Combine(Path.GetTempPath(), "JBZCanonicalPersistenceTests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(root, "Data", "JBZUniveresalLunix.db");
        string counterPath = Path.Combine(root, "PartCnt.txt");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(counterPath, "PART-A 50000 42\r\n", new UTF8Encoding(false));
            var repository = new TestHistoryStore(dbPath);
            var mirror = new PartCounterStore(counterPath);
            Assert(repository.ImportPartCountersOnce(mirror.ReadAll(), counterPath) == 1,
                "Existing PartCnt is imported only as initial migration input");
            ProbeCounterSnapshot imported = repository.GetAllProbeCounters().Single();
            Assert(imported.PartNumber == "PART-A" && imported.Counter == 42 && imported.ReplacementThreshold == 50000,
                "SQLite receives initial PartCnt counter and threshold");

            File.WriteAllText(counterPath, "PART-A 50000 999\r\n", new UTF8Encoding(false));
            Assert(repository.ImportPartCountersOnce(mirror.ReadAll(), counterPath) == 0 &&
                   repository.GetAllProbeCounters().Single().Counter == 42,
                "Later PartCnt edits cannot overwrite authoritative SQLite state");

            File.Delete(counterPath);
            mirror.MirrorAll(repository.GetAllProbeCounters());
            Assert(File.ReadAllText(counterPath, Encoding.UTF8) == "PART-A 50000 42\r\n",
                "Missing PartCnt mirror is rebuilt from SQLite");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void TestSystemLogMasterSwitch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZSystemLogSwitchTests",
            Guid.NewGuid().ToString("N"));
        string logsRoot = Path.Combine(root, "Logs");

        try
        {
            var logger = new AsyncFileLogService();
            try
            {
                logger.Configure(enabled: false, rootDirectory: logsRoot);
                logger.Application("NORMAL_LIFECYCLE_RECORD");
                Assert(!logger.FileLoggingEnabled && logger.Level == AppLogLevel.Normal,
                    "Disabled system logging blocks the canonical runtime log");
                Assert(!Directory.Exists(logsRoot),
                    "Disabled system logging does not create a log directory");

                logger.Configure(enabled: true);
                Assert(logger.FileLoggingEnabled && logger.Level == AppLogLevel.ProtocolTrace,
                    "Enabled system logging includes protocol trace level");
                logger.Board("ENABLED_PROTOCOL_TRACE", AppLogLevel.ProtocolTrace);

                logger.Configure(enabled: false);
                logger.Error("NORMAL_ERROR_AFTER_DIAGNOSTIC_DISABLE");
                Assert(!logger.FileLoggingEnabled,
                    "Disabling system logging takes effect without restarting the application");
            }
            finally
            {
                logger.Dispose();
            }

            string[] logFiles = Directory.EnumerateFiles(logsRoot, "*.log").ToArray();
            Assert(logFiles.Length == 1 && Path.GetFileName(logFiles[0]) == "JBZUniveresalLunix.log",
                "Runtime writes one canonical main log instead of category/day files");
            string logText = File.ReadAllText(logFiles[0]);
            Assert(!logText.Contains("NORMAL_LIFECYCLE_RECORD", StringComparison.Ordinal) &&
                   logText.Contains("ENABLED_PROTOCOL_TRACE", StringComparison.Ordinal) &&
                   !logText.Contains("NORMAL_ERROR_AFTER_DIAGNOSTIC_DISABLE", StringComparison.Ordinal),
                "Main log contains only records written while system logging is enabled");

            string historyPath = Path.Combine(root, "History", "test-history.db");
            var history = new TestHistoryStore(historyPath);
            DateTime now = DateTime.Now;
            history.Add(new TestHistoryRecord
            {
                Started = now,
                Finished = now,
                PartNumber = "LOG-OFF-HISTORY",
                Result = "PASS",
                Passed = true
            });
            Assert(File.Exists(historyPath) && history.Search(
                    new HistorySearchCriteria(null, null, null, "LOG-OFF-HISTORY", "ALL", 10)).Count == 1,
                "History remains writable while the system log master switch is off");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertBalancedTwoColorBrush(string code, string first, string second)
    {
        Assert(WireColorToBrushConverter.ToBrush(code) is LinearGradientBrush brush &&
               brush.GradientStops.Count == 4 &&
               brush.GradientStops[0].Offset == 0.0 &&
               brush.GradientStops[1].Offset == 0.5 &&
               brush.GradientStops[2].Offset == 0.5 &&
               brush.GradientStops[3].Offset == 1.0 &&
               $"#{brush.GradientStops[0].Color.R:X2}{brush.GradientStops[0].Color.G:X2}{brush.GradientStops[0].Color.B:X2}" == first &&
               $"#{brush.GradientStops[3].Color.R:X2}{brush.GradientStops[3].Color.G:X2}{brush.GradientStops[3].Color.B:X2}" == second,
            $"Two-color wire '{code}' keeps THT order and uses an exact 50/50 split");
    }

    private static string BrushHex(Brush brush)
    {
        if (brush is not SolidColorBrush solid)
            return "<non-solid>";

        Color color = solid.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static void AssertCapacity(int expansion, int physical, int scan, int io)
    {
        BoardCapacity capacity = BoardCapacity.Create(expansion);
        Assert(capacity.PortCount == physical, $"Expansion {expansion}: internal port count");
        Assert(capacity.ScanCardCount == scan, $"Expansion {expansion}: scan");
        Assert(capacity.TotalIoCapacity == io, $"Expansion {expansion}: IO");
    }

#if LEGACY_D2XX
#endif
    private static void TestEngineVectors()
    {
        using var engine = CreateEngine(out _);

        ProductModel pair = Model(("PAIR", new[] { 1, 18 }));
        engine.SetModel(pair);
        IReadOnlyList<FaultRow> initialPairRows = engine.BuildRows().Where(row => row.WireName == "PAIR").ToArray();
        Assert(initialPairRows.Count == 2 &&
               initialPairRows.Any(row => row.Io == 1 && row.Kind == FaultKind.MissingConnection && row.FaultType == "Đơn" && row.Pin == "1") &&
               initialPairRows.Any(row => row.Io == 18 && row.Kind == FaultKind.MissingConnection && row.FaultType == "Đơn" && row.Pin == "18") &&
               initialPairRows.All(row => row.Status == "CHƯA KẾT NỐI") &&
               initialPairRows.All(row => row.ExpectedSourceIo == 1 && row.ExpectedTargetIo == 18) &&
               initialPairRows.All(row => !row.IoText.Contains("<->", StringComparison.Ordinal) &&
                                          !row.Pin.Contains("<->", StringComparison.Ordinal)),
            "Model load shows one Htdrv-style endpoint row per pin and both endpoint rows keep the same canonical IO1<->IO18 pair");

        ProductModel aoDisplay = Model(("AO", new[] { 32, 33, 34, 35 }));
        engine.SetModel(aoDisplay);
        FaultRow[] aoRows = engine.BuildRows().Where(row => row.WireName == "AO").ToArray();
        Assert(aoRows.Length == 4 &&
               aoRows.Single(row => row.Io == 32).ExpectedSourceIo == 32 &&
               aoRows.Single(row => row.Io == 32).ExpectedTargetIo == 33 &&
               aoRows.Single(row => row.Io == 33).ExpectedSourceIo == 32 &&
               aoRows.Single(row => row.Io == 33).ExpectedTargetIo == 33 &&
               aoRows.Single(row => row.Io == 34).ExpectedSourceIo == 32 &&
               aoRows.Single(row => row.Io == 34).ExpectedTargetIo == 34 &&
               aoRows.Single(row => row.Io == 35).ExpectedSourceIo == 32 &&
               aoRows.Single(row => row.Io == 35).ExpectedTargetIo == 35,
            "AO multi-endpoint rows display source-to-own-endpoint pairs instead of repeating IO32<->IO32");

        engine.SetModel(pair);
        ScanFrame pairPassFrame = Frame((1, new[] { 18 }));
        engine.ProcessFrame(pairPassFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(pairPassFrame);
        IReadOnlyList<FaultRow> pairMapped = engine.BuildRows().Where(row => row.WireName == "PAIR").ToArray();
        Assert(engine.ContinuityPassed &&
               !engine.HasWiringFault &&
               pairMapped.Count == 0,
            "Expected IO1-IO18 pass removes connected production rows from TestWindow");
        engine.ProcessFrame(Frame((1, Array.Empty<int>())));
        IReadOnlyList<FaultRow> pairReleased = engine.BuildRows().Where(row => row.WireName == "PAIR").ToArray();
        Assert(pairReleased.Count == 2 &&
               pairReleased.All(row => row.FaultType == "Đơn" && row.Status == "CHƯA KẾT NỐI"),
            "Releasing/jig-open frame shows endpoint rows again");

        engine.SetModel(pair);
        ScanFrame wrongFrame = Frame((1, new[] { 40 }));
        engine.ProcessFrame(wrongFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(wrongFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(wrongFrame);
        Assert(engine.HasWiringFault, "IO1-IO40 is wiring fault");

        engine.SetModel(pair);
        engine.ProcessFrame(Frame());
        Assert(engine.BuildRows().Count(row => row.Kind == FaultKind.MissingConnection) == 2,
            "Missing IO1-IO18 keeps both endpoint rows as display-only pending rows");
        Assert(!engine.BuildRows().Any(row => row.Kind == FaultKind.Open), "Missing IO1-IO18 is not a production fault row");
        Assert(!engine.HasConfirmedOpenCircuit, "Empty fixture is not inferred as product OPEN");

        engine.SetModel(pair);
        ScanFrame oneEndOnly = Frame((1, Array.Empty<int>()));
        engine.ProcessFrame(oneEndOnly);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 25);
        engine.ProcessFrame(oneEndOnly);
        Assert(engine.HasExpectedSourceCoverage &&
               !engine.ReadyToEvaluateProductFaults &&
               !engine.HasConfirmedOpenCircuit &&
               !engine.HasWiringFault &&
               engine.BuildRows().Any(row => row.Kind == FaultKind.MissingConnection),
            "One endpoint held during install remains display-only and does not become OPEN product FAIL");

        ProductModel twoPairs = Model(("PAIR-A", new[] { 1, 18 }), ("PAIR-B", new[] { 2, 8 }));
        engine.SetModel(twoPairs);
        ScanFrame twoPairPass = Frame((1, new[] { 18 }), (2, new[] { 8 }));
        engine.ProcessFrame(twoPairPass);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(twoPairPass);
        engine.ProcessFrame(Frame((2, new[] { 8 })));
        Assert(!engine.IsProductReleased,
            "Partial unplugging does not complete removal; Universal Tester New keeps one THÁO SẢN PHẨM state until all product edges are gone");

        engine.SetModel(twoPairs);
        engine.ProcessFrame(Frame((1, new[] { 18 })));
        Assert(!engine.ReadyToEvaluateProductFaults &&
               !engine.HasConfirmedOpenCircuit &&
               !engine.HasWiringFault,
            "Partial source coverage is installing, not product FAIL");

        // Một cạnh sai vật lý phải được xác nhận ngay cả khi sản phẩm mới lắp
        // một phần. PASS vẫn bị khóa bởi coverage đầy đủ, nhưng không được xóa
        // candidate sai dây chỉ vì các source khác chưa xuất hiện.
        engine.SetModel(twoPairs);
        ScanFrame partialWrongWire = Frame((1, new[] { 40 }));
        engine.ProcessFrame(partialWrongWire);
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(partialWrongWire);
        Assert(!engine.ReadyToEvaluateProductFaults && engine.HasWiringFault,
            "A stable wrong physical pair fails without waiting for full model source coverage");

        // Source==target là trạng thái một đầu chạm/từ decoder target-replace,
        // không phải một dây nối giữa hai đầu và không được tạo FAIL giả.
        engine.SetModel(twoPairs);
        ScanFrame oneEndTouch = Frame((1, new[] { 1 }));
        engine.ProcessFrame(oneEndTouch);
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(oneEndTouch);
        Assert(!engine.HasWiringFault &&
               !engine.HasProductActivity &&
               !engine.ReadyToEvaluateProductFaults,
            "One-end source self-edge never becomes ProductPresence or a wrong-wire FAIL");

        ScanFrame fullCoverageOpen = Frame((1, new[] { 18 }), (2, Array.Empty<int>()));
        engine.ProcessFrame(fullCoverageOpen);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(fullCoverageOpen);
        Assert(engine.ReadyToEvaluateProductFaults &&
               !engine.HasConfirmedOpenCircuit &&
               !engine.HasWiringFault &&
               engine.BuildRows().Count(row => row.Kind == FaultKind.MissingConnection) == 2,
            "Full source coverage with a pending pair keeps both endpoint rows display-only");

        ProductModel splice = Model(("SPLICE", new[] { 5, 20, 33 }));
        engine.SetModel(splice);
        ScanFrame spliceOpenFrame = Frame((5, new[] { 20 }));
        engine.ProcessFrame(spliceOpenFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(spliceOpenFrame);
        IReadOnlyList<FaultDetail> confirmedSpliceOpen = engine.BuildConfirmedOpenFaults();
        Assert(confirmedSpliceOpen.Count == 0 &&
               !engine.BuildRows().Any(row => row.Kind == FaultKind.Open) &&
               engine.BuildRows().Count(row => row.Kind == FaultKind.MissingConnection) == 1 &&
               engine.BuildRows().Single(row => row.Kind == FaultKind.MissingConnection).Io == 33 &&
               engine.BuildRows().Where(row => row.WireName == "SPLICE")
                   .All(row => row.FaultType == "Nối chung" && row.Status == "CHƯA KẾT NỐI"),
            "Splice hides reached endpoints and keeps only the missing target as display-only, not production OPEN");

        engine.SetModel(splice);
        ScanFrame splicePassFrame = Frame((5, new[] { 20, 33 }));
        engine.ProcessFrame(splicePassFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(splicePassFrame);
        FaultRow[] spliceMapped = engine.BuildRows().Where(row => row.WireName == "SPLICE").ToArray();
        Assert(engine.ContinuityPassed &&
               !engine.HasWiringFault &&
               spliceMapped.Length == 0,
            "Splice component pass removes connected endpoint rows");

        engine.ProcessFrame(spliceOpenFrame);
        Assert(engine.BuildRows().Any(row => row.Kind == FaultKind.MissingConnection),
            "Removing a completed connection re-adds display-only pending row");

        ProductModel sharedCrimp = Model(
            ("BE31", new[] { 31, 518 }),
            ("BE32", new[] { 32, 518 }),
            ("BE33", new[] { 33, 518 }),
            ("BC13", new[] { 90, 518 }),
            ("BC14", new[] { 91, 518 }),
            ("IC03", new[] { 230, 518 }));
        engine.SetModel(sharedCrimp);
        engine.ProcessFrame(Frame((31, new[] { 32, 33, 90, 91, 230, 518 })));
        Assert(engine.ContinuityPassed &&
               !engine.HasWiringFault &&
               engine.PassedNets.Count == 6 &&
               !engine.BuildRows().Any(row =>
                   row.WireName is "BE31" or "BE32" or "BE33" or "BC13" or "BC14" or "IC03"),
            "Shared crimp IO518 passes all six nets and removes connector 29 rows when firmware reports one electrical component");

        engine.SetModel(sharedCrimp);
        engine.ProcessFrame(Frame((31, new[] { 32, 33, 90, 91, 518 })));
        Assert(!engine.ContinuityPassed &&
               engine.PassedNets.Count == 5 &&
               engine.BuildRows().Any(row => row.WireName == "IC03") &&
               !engine.BuildRows().Any(row =>
                   row.WireName is "BE31" or "BE32" or "BE33" or "BC13" or "BC14"),
            "Shared crimp keeps only the missing IC03 branch visible when IO230 is absent");

        engine.SetModel(pair);
        engine.ProcessFrame(new ScanFrame(
            DateTime.Now, 1, new HashSet<int> { 1, 40 }, [], true, 0, 1,
            new Dictionary<int, IReadOnlySet<int>> { [1] = new HashSet<int> { 40 } },
            new Dictionary<int, int> { [40] = 1 }, BoardScanMode.Probe));
        Assert(!engine.HasWiringFault && !engine.ContinuityPassed, "Probe frame never enters production evaluation");
    }

    private static void TestPendingContinuityPresentation()
    {
        using var engine = CreateEngine(out _);
        ProductModel twoPairs = Model(
            ("PAIR-A", new[] { 1, 3 }),
            ("PAIR-B", new[] { 2, 4 }));
        engine.SetModel(twoPairs);

        FaultRow[] initialRows = engine.BuildRows()
            .Where(row => row.WireName is "PAIR-A" or "PAIR-B")
            .ToArray();
        int[] initialOrder = initialRows.Select(row => row.Io).ToArray();
        Dictionary<int, int> initialDisplayOrder = initialRows.ToDictionary(row => row.Io, row => row.DisplayOrder);
        Assert(initialRows.Length == 4 && initialOrder.SequenceEqual([1, 3, 2, 4]),
            "Two pending pairs keep the model/display endpoint order");
        Assert(initialRows.All(row =>
                   row.Kind == FaultKind.MissingConnection &&
                   row.FaultType == "Đơn" &&
                   row.Status == "CHƯA KẾT NỐI") &&
               initialRows.All(row => row.FaultType != "KIỂM TRA" && row.Status != "CHỜ KẾT NỐI"),
            "Pending point-to-point rows use only Đơn / CHƯA KẾT NỐI");

        ScanFrame pairAPass = Frame((1, new[] { 3 }));
        engine.ProcessFrame(pairAPass);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(pairAPass);
        FaultRow[] afterPairA = engine.BuildRows()
            .Where(row => row.WireName is "PAIR-A" or "PAIR-B")
            .ToArray();
        Assert(afterPairA.Select(row => row.Io).SequenceEqual([2, 4]) &&
               afterPairA.All(row => row.Status == "CHƯA KẾT NỐI") &&
               !engine.ContinuityPassed,
            "Passing PAIR-A hides only IO1/IO3 and leaves PAIR-B pending");

        ScanFrame bothPass = Frame((1, new[] { 3 }), (2, new[] { 4 }));
        engine.ProcessFrame(bothPass);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(bothPass);
        Assert(engine.ContinuityPassed &&
               !engine.HasWiringFault &&
               !engine.BuildRows().Any(row => row.WireName is "PAIR-A" or "PAIR-B"),
            "Both correct pairs remove all normal pending rows without false wiring faults");

        engine.ProcessFrame(Frame((2, new[] { 4 })));
        FaultRow[] restoredPairA = engine.BuildRows().Where(row => row.WireName == "PAIR-A").ToArray();
        Assert(restoredPairA.Select(row => row.Io).SequenceEqual([1, 3]) &&
               restoredPairA.All(row =>
                   row.Status == "CHƯA KẾT NỐI" &&
                   row.DisplayOrder == initialDisplayOrder[row.Io]),
            "A lost completed pair reappears at its original DisplayOrder");

        ProductModel splice = Model(("SPLICE", new[] { 5, 20, 33 }));
        engine.SetModel(splice);
        FaultRow[] spliceRows = engine.BuildRows().Where(row => row.WireName == "SPLICE").ToArray();
        Assert(spliceRows.Length == 3 &&
               spliceRows.All(row => row.FaultType == "Nối chung" && row.Status == "CHƯA KẾT NỐI"),
            "A multi-endpoint network remains one splice topology presented as Nối chung");

        var common = new PinRecord("CLIP", "AO", 201, "AO", PinType: "AO", OriginalOrder: 1);
        var a1 = new PinRecord("CLIP", "a1", 202, "a1", PinType: "a1", OriginalOrder: 2);
        var a2 = new PinRecord("CLIP", "a2", 203, "a2", PinType: "a2", OriginalOrder: 3);
        var a3 = new PinRecord("CLIP", "a3", 204, "a3", PinType: "a3", OriginalOrder: 4);
        var clipModel = new ProductModel { ModelName = "CLIP", PartNumber = "CLIP" };
        clipModel.Pins.AddRange([common, a1, a2, a3]);
        clipModel.Clip = new ClipTopology(
            common,
            [
                new ClipBranch("a1", 1, 202, a1, null),
                new ClipBranch("a2", 2, 203, a2, null),
                new ClipBranch("a3", 3, 204, a3, null)
            ]);
        engine.SetModel(clipModel);
        FaultRow[] initialClipRows = engine.BuildRows().ToArray();
        Assert(initialClipRows.Length == 4 &&
               initialClipRows.All(row => row.FaultType == "Nối chung" && row.Status == "CHƯA KẾT NỐI"),
            "CLIP common and all unlatch branches use Nối chung / CHƯA KẾT NỐI");

        engine.ProcessFrame(Frame((201, new[] { 202 })));
        FaultRow[] remainingClipRows = engine.BuildRows().ToArray();
        Assert(remainingClipRows.Select(row => row.Io).Order().SequenceEqual([203, 204]) &&
               remainingClipRows.All(row => row.FaultType == "Nối chung" && row.Status == "CHƯA KẾT NỐI") &&
               !engine.HasWiringFault,
            "Latching CLIP a1 hides only common/a1 per existing common behavior; a2/a3 remain without false SHORT");
        FaultRow[] clipRemovalRows = engine.BuildRemovalRows().ToArray();
        Assert(clipRemovalRows.Select(row => row.Io).Order().SequenceEqual([203, 204]) &&
               clipRemovalRows.All(row => row.Status == "CHƯA KẾT NỐI"),
            "Universal Tester New removal uses the same inverse-open CLIP table; no D2XX CHỜ THÁO rows remain");
    }

    private static void TestFinalHtdrvTestWindowPresentation()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };
        ProductModel model = Model(
            ("BG1", new[] { 1, 3 }),
            ("BG2", new[] { 2, 4 }));

        TestViewModel vm = CreateTestViewModel(production, out FakeBoard board);
        vm.SetModel(model);
        vm.StartProductionTestAsync().GetAwaiter().GetResult();
        Assert(vm.Faults.Count(row => row.WireName == "BG1") == 2 &&
               vm.Faults.Count(row => row.WireName == "BG2") == 2 &&
               vm.Faults.All(row => row.Status == "CHƯA KẾT NỐI"),
            "Universal Tester New shows every pending wire row before the product is installed");

        board.Publish(FrameSeq(100, (1, new[] { 3 })));
        Assert(vm.Faults.Count(row => row.WireName == "BG2") == 2 &&
               !vm.Faults.Any(row => row.WireName == "BG1") &&
               vm.Faults.All(row => row.FaultType == "Đơn" && row.Status == "CHƯA KẾT NỐI"),
            "As soon as BG1 becomes electrically complete, only BG1 disappears while BG2 remains visible");

        MethodInfo showBoardUnavailable = typeof(TestViewModel).GetMethod(
            "ShowBoardUnavailablePresentation",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Board-unavailable presentation method not found");
        showBoardUnavailable.Invoke(vm, [false]);
        Assert(vm.SelectedOperationTabIndex == 0 && vm.Faults.Count == 0,
            "Transient board loss clears stale rows but leaves the continuity table selected");
        board.Publish(FrameSeq(101, (1, new[] { 3 })));
        Assert(vm.Faults.Count(row => row.WireName == "BG2") == 2 &&
               !vm.Faults.Any(row => row.WireName == "BG1"),
            "First complete frame after reconnect rebuilds rows even when physical topology is unchanged");

        TestViewModel overlayVm = CreateTestViewModel(production);
        typeof(TestViewModel).GetField("_productionPhase", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(overlayVm, 6);
        overlayVm.State = "PASS";
        Assert(overlayVm.IsCenterResultVisible &&
               overlayVm.IsCenterPassPresentation &&
               overlayVm.CenterResultText == "PASS",
            "Legacy center-result state remains internally coherent even though TestWindow no longer renders the overlay");

        using TestEngine engine = CreateEngine(out _, production);
        engine.SetModel(model);
        ScanFrame crossed = FrameSeq(110, (1, new[] { 4 }));
        engine.ProcessFrame(crossed);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(crossed with { Sequence = 111 });
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(crossed with { Sequence = 112 });
        FaultRow[] crossedRows = engine.BuildRows().ToArray();
        Assert(engine.HasWiringFault &&
               crossedRows.Count(row => row.Io == 1 && row.WireName == "BG1" && row.Status == "SAI DÂY") == 1 &&
               crossedRows.Count(row => row.Io == 4 && row.WireName == "BG2" && row.Status == "CHẬP MẠCH") == 1 &&
               crossedRows.Count(row => row.Io == 3 && row.WireName == "BG1" && row.Status == "HỞ MẠCH") == 1 &&
               crossedRows.Where(row => row.Io is 1 or 3 or 4).All(row => row.FaultType == "Đơn") &&
               crossedRows.Where(row => row.Io is 1 or 3 or 4).All(row => !string.IsNullOrWhiteSpace(row.IoCnPnText)),
            "Confirmed crossed wire preserves topology/operator metadata and emits SAI/CHẬP/HỞ exactly once. Rows=" +
            string.Join(" | ", crossedRows.Select(row => $"{row.Io}/{row.WireName}/{row.FaultType}/{row.Status}/{row.IoCnPnText}")));

        engine.SetModel(model);
        ScanFrame unused = FrameSeq(120, (1, new[] { 40 }));
        engine.ProcessFrame(unused);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(unused with { Sequence = 121 });
        Thread.Sleep(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs + 5);
        engine.ProcessFrame(unused with { Sequence = 122 });
        FaultRow unusedRow = engine.BuildRows().Single(row => row.Io == 40);
        Assert(unusedRow.FaultType.Length == 0 &&
               unusedRow.IoText == "IO (40)" &&
               unusedRow.Connector.Length == 0 &&
               unusedRow.Pin.Length == 0 &&
               unusedRow.WireName.Length == 0 &&
               unusedRow.Section.Length == 0 &&
               unusedRow.Color.Length == 0 &&
               unusedRow.Status == "CHẬP MẠCH" &&
               unusedRow.IoCnPnText == "IO40",
            "Unused actual IO is identified in the IO column while Connector stays empty");

        engine.ProcessFrame(FrameSeq(123));
        FaultRow[] repairedRows = engine.BuildRows().ToArray();
        Assert(!engine.HasWiringFault &&
               repairedRows.All(row => row.Status == "CHƯA KẾT NỐI") &&
               !repairedRows.Any(row => row.Status is "SAI DÂY" or "CHẬP MẠCH" or "HỞ MẠCH"),
            "Removing a confirmed physical fault immediately restores normal pending presentation");

        engine.SetModel(model);
        engine.ProcessFrame(FrameSeq(130, (1, new[] { 3 }), (2, new[] { 4 })));
        FaultRow[] removalRowsWhileConnected = engine.BuildRemovalRows().ToArray();
        Assert(removalRowsWhileConnected.Length == 0,
            "Universal Tester New keeps the same inverse-open table during removal: connected pairs stay hidden");
        engine.ProcessFrame(FrameSeq(131, (2, new[] { 4 })));
        FaultRow[] removalRowsAfterOneOpen = engine.BuildRemovalRows().ToArray();
        Assert(removalRowsAfterOneOpen.Length == 2 &&
               removalRowsAfterOneOpen.All(row => row.WireName == "BG1" && row.Status == "CHƯA KẾT NỐI") &&
               !engine.IsProductReleased,
            "When one network is removed it reappears as CHƯA KẾT NỐI; the remaining connected network stays hidden");
        engine.ProcessFrame(FrameSeq(132));
        Assert(engine.BuildRemovalRows().Count == 4 && engine.IsProductReleased,
            "Complete product removal restores all pending rows and confirms release without D2XX CHỜ THÁO rows");

        string testWindowXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml"));
        Assert(testWindowXaml.Contains("EnableRowVirtualization\" Value=\"True", StringComparison.Ordinal) &&
               testWindowXaml.Contains("EnableColumnVirtualization\" Value=\"True", StringComparison.Ordinal) &&
               testWindowXaml.Contains("VirtualizingPanel.IsVirtualizing\" Value=\"True", StringComparison.Ordinal) &&
               testWindowXaml.Contains("VirtualizingPanel.VirtualizationMode\" Value=\"Recycling", StringComparison.Ordinal) &&
               !testWindowXaml.Contains("CenterResultText", StringComparison.Ordinal) &&
               !testWindowXaml.Contains("IsCenterResultVisible", StringComparison.Ordinal),
            "TestWindow keeps recycling virtualization and no longer overlays a large center status over the wire table");
        Assert(!testWindowXaml.Contains("DropShadowEffect", StringComparison.Ordinal),
            "Wire-color cells avoid per-row DropShadowEffect rendering");

        TestViewModel deltaVm = CreateTestViewModel(production);
        MethodInfo synchronize = typeof(TestViewModel).GetMethod(
            "SynchronizeFaultRows",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault row synchronizer not found");
        FaultRow[] largeRows = Enumerable.Range(1, 200)
            .Select(io => new FaultRow
            {
                Kind = FaultKind.MissingConnection,
                Io = io,
                WireName = $"BG{io:000}",
                Status = "CHƯA KẾT NỐI"
            })
            .ToArray();
        int collectionEvents = 0;
        deltaVm.Faults.CollectionChanged += (_, _) => collectionEvents++;
        synchronize.Invoke(deltaVm, [largeRows]);
        Assert(collectionEvents == 1 && deltaVm.Faults.Count == 200,
            "First large 10-card presentation reaches DataGrid with one collection reset");

        collectionEvents = 0;
        synchronize.Invoke(deltaVm, [largeRows.Skip(2).ToArray()]);
        Assert(collectionEvents == 2 &&
               deltaVm.Faults.Count == 198 &&
               deltaVm.Faults[0].Io == 3,
            "Passing the first network removes only its two rows instead of moving the remaining 198 rows");

        TestViewModel tenCardVm = CreateTestViewModel(production);
        FaultRow[] tenCardRows = Enumerable.Range(1, 640)
            .Select(io => new FaultRow
            {
                Kind = FaultKind.MissingConnection,
                Io = io,
                WireName = $"TEN{io:000}",
                Status = "CHƯA KẾT NỐI"
            })
            .ToArray();
        int tenCardEvents = 0;
        tenCardVm.Faults.CollectionChanged += (_, _) => tenCardEvents++;
        synchronize.Invoke(tenCardVm, [tenCardRows]);
        Assert(tenCardEvents == 1 && tenCardVm.Faults.Count == 640,
            "Initial 640-row presentation uses one collection reset");

        tenCardEvents = 0;
        synchronize.Invoke(tenCardVm, [tenCardRows.Skip(2).ToArray()]);
        Assert(tenCardEvents == 2 && tenCardVm.Faults.Count == 638,
            "Large active table removes only the changed endpoint rows without rebuilding all 638 rows");

        tenCardEvents = 0;
        synchronize.Invoke(tenCardVm, [tenCardRows]);
        Assert(tenCardEvents == 2 &&
               tenCardVm.Faults.Count == 640 &&
               ReferenceEquals(tenCardVm.Faults[0], tenCardRows[0]) &&
               ReferenceEquals(tenCardVm.Faults[1], tenCardRows[1]),
            "Reopened network inserts only its two cached rows without resetting the 640-row DataGrid");

        string testViewModelCode = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(testViewModelCode.Contains("TryRemoveMissingFaultRowsInOrder(desiredRows)", StringComparison.Ordinal) &&
               testViewModelCode.Contains("TryInsertMissingFaultRowsInOrder(desiredRows)", StringComparison.Ordinal) &&
               testViewModelCode.Contains("ReferenceEquals(Faults[currentIndex], desiredRows[desiredIndex])", StringComparison.Ordinal),
            "Large row remove/reinsert deltas use reference subsequence fast paths without rebuilding row keys");

        tenCardEvents = 0;
        synchronize.Invoke(tenCardVm, [Array.Empty<FaultRow>()]);
        Assert(tenCardEvents == 1 && tenCardVm.Faults.Count == 0,
            "Clearing a large presentation uses one collection reset");

        tenCardEvents = 0;
        synchronize.Invoke(tenCardVm, [Array.Empty<FaultRow>()]);
        Assert(tenCardEvents == 0,
            "An already empty presentation does not issue redundant DataGrid resets");

        string testWindowCode = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "TestWindow.xaml.cs"));
        Assert(testWindowCode.Contains("_scrollDispatchQueued", StringComparison.Ordinal) &&
               testWindowCode.Contains("Interlocked.Exchange(ref _scrollDispatchQueued, 1)", StringComparison.Ordinal) &&
               testWindowCode.Contains("IsFaultRowVisible(firstFault)", StringComparison.Ordinal),
            "Fault-grid auto-scroll coalesces collection bursts and skips rows already in view");

        MethodInfo shouldAutoScroll = typeof(TestWindow).GetMethod(
            "ShouldAutoScrollToFirstFault",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fault-grid auto-scroll filter not found");
        bool scrollOnAdd = (bool)shouldAutoScroll.Invoke(
            null,
            [new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new object())])!;
        bool scrollOnReset = (bool)shouldAutoScroll.Invoke(
            null,
            [new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)])!;
        bool scrollOnPassRemoval = (bool)shouldAutoScroll.Invoke(
            null,
            [new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, new object(), 0)])!;
        Assert(scrollOnAdd && scrollOnReset && !scrollOnPassRemoval,
            "Passing a network removes rows without scheduling DataGrid ScrollIntoView/layout");

        Assert(testViewModelCode.Contains(
                   "if (!AsyncFileLogService.Current.FileLoggingEnabled ||",
                   StringComparison.Ordinal) &&
               testViewModelCode.Contains(
                   "private void LogFaultGate(long generation)",
                   StringComparison.Ordinal) &&
               testViewModelCode.Contains(
                   "Tránh dựng PassGateDiagnostics lần thứ hai trên UI thread",
                   StringComparison.Ordinal),
            "Disabled system logging skips full-network diagnostics on frame and UI hot paths");
    }

    private static void TestProductionFaultConfirmation()
    {
        var settings = new ProductionSettings
        {
            OpenCircuitConfirmMs = 100,
            ShortCircuitConfirmMs = 80,
            WrongConnectionConfirmMs = 90,
            ProductSettleTimeMs = 50,
            JigContactUnstableWindowMs = 500
        };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        var gate = new ProductionFaultConfirmationGate(settings, clock);
        var clean = new Dictionary<string, bool> { ["A"] = true, ["B"] = true };
        var openA = new Dictionary<string, bool> { ["A"] = false, ["B"] = true };

        gate.Observe(clean, [], hasProductActivity: true);
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultProductSettleTimeMs + 1));
        Assert(gate.Observe(clean, [], true).ProductStable, "Clean product settles before PASS");

        gate.Reset();
        gate.Observe(openA, [], hasProductActivity: false);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert(gate.Observe(openA, [], false).ConfirmedOpenKeys.Count == 0, "Jig empty is not inferred as product OPEN");

        gate.Reset();
        gate.Observe(openA, [], true);
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultProductSettleTimeMs - 1));
        Assert(gate.Observe(openA, [], true).ConfirmedOpenKeys.Count == 0, "OPEN is ignored before product settle gate");
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert(gate.Observe(openA, [], true).ConfirmedOpenKeys.Count == 0, "OPEN remains ignored after product settle gate");
        Assert(gate.Observe(clean, [], true).ConfirmedOpenKeys.Count == 0, "OPEN recovery has no product fault to clear");

        gate.Reset();
        gate.Observe(clean, [], true);
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultProductSettleTimeMs + 1));
        gate.Observe(clean, [], true);
        gate.Observe(openA, [], true);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        gate.Observe(clean, [], true);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        gate.Observe(openA, [], true);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        ProductionFaultConfirmationSnapshot bounce = gate.Observe(clean, [], true);
        Assert(!bounce.ContactUnstable && bounce.ConfirmedOpenKeys.Count == 0, "Repeated OPEN bounce is ignored, not product FAIL");
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultProductSettleTimeMs + 1));
        ProductionFaultConfirmationSnapshot recovered = gate.Observe(clean, [], true);
        Assert(!recovered.ContactUnstable && recovered.ProductStable, "Clean re-evaluation clears jig warning");

        ProductionFaultConfirmationSnapshot fullLoss = gate.Observe(
            new Dictionary<string, bool> { ["A"] = false, ["B"] = false },
            [],
            hasProductActivity: false);
        Assert(fullLoss.ContactUnstable && fullLoss.ConfirmedOpenKeys.Count == 0, "Full contact loss is not inferred as product OPEN");

        gate.Reset();
        var shortFault = new[] { new UnexpectedFaultObservation(1, 2, ProductFaultType.ShortCircuit) };
        gate.Observe(clean, shortFault, true);
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultShortCircuitConfirmMs - 1));
        Assert(gate.Observe(clean, shortFault, true).ConfirmedUnexpectedPairs.Count == 0,
            "Transient SHORT is not confirmed, without waiting for product settle first");
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert(gate.Observe(clean, shortFault, true).ConfirmedUnexpectedPairs.Contains((1, 2)),
            "Stable SHORT is confirmed on the next stable frame without extra product-settle latency");
        Assert(gate.Observe(clean, [], true).ConfirmedUnexpectedPairs.Count == 0, "SHORT recovery resets candidate");

        var wrongFault = new[] { new UnexpectedFaultObservation(3, 4, ProductFaultType.WrongWiring) };
        gate.Observe(clean, wrongFault, true);
        clock.Advance(TimeSpan.FromMilliseconds(ProductionTimingPolicy.DefaultWrongConnectionConfirmMs - 1));
        Assert(gate.Observe(clean, wrongFault, true).ConfirmedUnexpectedPairs.Count == 0, "Transient wrong connection not confirmed");
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert(gate.Observe(clean, wrongFault, true).ConfirmedUnexpectedPairs.Contains((3, 4)), "Stable wrong connection confirmed");

        string testViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        Assert(testViewModelSource.IndexOf("TriggerPassUi();", StringComparison.Ordinal) <
               testViewModelSource.IndexOf("await PauseProductionScanForFinalPassAsync(ct);", StringComparison.Ordinal),
            "PASS UI is raised before waiting for the hardware scan to stop");

        string soundSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "AppSoundService.cs"));
        int productSoundStart = soundSource.IndexOf("public void PlayProductStart()", StringComparison.Ordinal);
        int passSoundStart = soundSource.IndexOf("public void PlayTestOk()", StringComparison.Ordinal);
        string productSoundMethod = soundSource[productSoundStart..passSoundStart];
        Assert(productSoundMethod.Contains("SafePlay(player);", StringComparison.Ordinal) &&
               !productSoundMethod.Contains("SafePlaySync(player);", StringComparison.Ordinal),
            "Product-start sound never holds the UI sound path for the full WAV duration");

        string faultDialogXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "FaultConfirmationWindow.xaml"));
        string faultDialogSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "FaultConfirmationWindow.xaml.cs"));
        Assert(faultDialogXaml.Contains("SizeToContent=\"Height\"", StringComparison.Ordinal) &&
               faultDialogXaml.Contains("x:Name=\"FaultTypeText\"", StringComparison.Ordinal) &&
               faultDialogXaml.Contains("x:Name=\"SummaryText\"", StringComparison.Ordinal) &&
               faultDialogSource.Contains("ApplyCompactSummary(summary);", StringComparison.Ordinal),
            "FAIL dialog uses a compact auto-height layout without duplicating its fault title");

        MethodInfo compactFaultSummary = typeof(FaultConfirmationWindow).GetMethod(
            "BuildShortSummary",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Compact fault summary formatter not found");
        var popupPins = new Dictionary<int, PinRecord>
        {
            [4] = new PinRecord("CN1", "BG1", 4, "1"),
            [8] = new PinRecord("CN2", "BF2", 8, "2")
        };
        Func<int, PinRecord?> popupResolver = io => popupPins.GetValueOrDefault(io);
        string popupMappedWrongSummary = (string)(compactFaultSummary.Invoke(null, [
            new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                ActualSourceIo = 4,
                ActualTargetIo = 8
            },
            popupResolver
        ]) ?? string.Empty);
        string popupUnmappedWrongSummary = (string)(compactFaultSummary.Invoke(null, [
            new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                ActualSourceIo = 4,
                ActualTargetIo = 12
            },
            popupResolver
        ]) ?? string.Empty);
        Assert(popupMappedWrongSummary == "LỖI SAI DÂY\n\nBG1 NỐI NHẦM BF2" &&
               popupUnmappedWrongSummary == "LỖI SAI DÂY\n\nBG1 NỐI IO(12)",
            "Fault popup prioritizes THT wire names and falls back to compact IO(n) only for unmapped endpoints");
        PinRecord bg1 = new("CN1", "BG1", 4, "1");
        PinRecord bf2 = new("CN2", "BF2", 8, "2");
        Func<int, PinRecord?> namedResolver = io => io switch
        {
            4 => bg1,
            8 => bf2,
            _ => null
        };
        string namedWrongSummary = (string)(compactFaultSummary.Invoke(null, [
            new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                ActualSourceIo = 4,
                ActualTargetIo = 8
            },
            namedResolver
        ]) ?? string.Empty);
        string unmappedWrongSummary = (string)(compactFaultSummary.Invoke(null, [
            new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                ActualSourceIo = 4,
                ActualTargetIo = 12
            },
            namedResolver
        ]) ?? string.Empty);
        Assert(namedWrongSummary == "LỖI SAI DÂY\n\nBG1 NỐI NHẦM BF2" &&
               unmappedWrongSummary == "LỖI SAI DÂY\n\nBG1 NỐI IO(12)",
            "FAIL popup prioritizes THT wire names and falls back to compact IO(n) only for unmapped endpoints");

        string mainWindowXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "MainWindow.xaml"));
        string mainWindowSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "MainWindow.xaml.cs"));
        Assert(!mainWindowXaml.Contains("KẾT NỐI LẠI BO", StringComparison.Ordinal) &&
               !mainWindowXaml.Contains("Test.ConnectBoardCommand", StringComparison.Ordinal) &&
               mainWindowSource.Contains("StartupControlUnlockTimeout", StringComparison.Ordinal) &&
               mainWindowSource.Contains("Task.WhenAny(", StringComparison.Ordinal) &&
               !mainWindowSource.Contains("ReportStartupBoardTimeout()", StringComparison.Ordinal) &&
               mainWindowSource.Contains("ObserveDeferredStartupAsync(initialization)", StringComparison.Ordinal) &&
               mainWindowSource.Contains("IsBoardIdentityVerified", StringComparison.Ordinal) &&
               !mainWindowSource.Contains("ĐANG TỰ THỬ LẠI", StringComparison.Ordinal),
            "Slow UART discovery keeps production locked until firmware identity is verified");
        string scanSupervisorSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "ScanSupervisor.cs"));
        string uartTransportSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "JbzSerialBoardTransport.cs"));
        Assert(testViewModelSource.Contains("StopScanIntentionallyAsync", StringComparison.Ordinal) &&
               testViewModelSource.Contains("RecoverProductionScanAsync", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("ScanHealthState.Suspended", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("ScanHealthState.Starting", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("ScanHealthState.Monitoring", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("RecoverSoftAsync", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("RecoverReopenAsync", StringComparison.Ordinal) &&
               scanSupervisorSource.Contains("_board.DisconnectAsync()", StringComparison.Ordinal) &&
               !uartTransportSource.Contains("attempt <= 6", StringComparison.Ordinal),
            "UART watchdog uses explicit pause, first-frame state, soft recovery, then one clean reopen");
        Assert(mainWindowXaml.Contains("Color=\"#273F91\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Color=\"#B45309\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Color=\"#0F766E\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Style=\"{StaticResource HistoryButtonStyle}\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Style=\"{StaticResource SettingsButtonStyle}\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Style=\"{StaticResource ModelButtonStyle}\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Style=\"{StaticResource TopologyButtonStyle}\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("Style=\"{StaticResource ExitButtonStyle}\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("x:Name=\"InteractionOverlay\"", StringComparison.Ordinal) &&
               !mainWindowXaml.Contains("Value=\"#DDDDDA\"", StringComparison.Ordinal) &&
               !mainWindowXaml.Contains("Value=\"#CBCBC8\"", StringComparison.Ordinal),
            "MainWindow buttons use semantic colors and preserve them through shared hover feedback");
        Assert(mainWindowXaml.Contains("x:Name=\"MainActionBar\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("UseLayoutRounding=\"True\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("SnapsToDevicePixels=\"True\"", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("<ColumnDefinition Width=\"1.25*\"/>", StringComparison.Ordinal) &&
               mainWindowXaml.Contains("<ColumnDefinition Width=\"0.75*\"/>", StringComparison.Ordinal) &&
               !mainWindowXaml.Contains("<Setter Property=\"Width\" Value=\"260\"/>", StringComparison.Ordinal) &&
               !mainWindowXaml.Contains("<ColumnDefinition Width=\"230\"/>", StringComparison.Ordinal),
            "MainWindow action buttons use DPI-safe proportional columns without fixed-width border clipping");

        string bootstrapSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "StartupBootstrapService.cs"));
        Assert(bootstrapSource.Contains("Critical filesystem bootstrap completed.", StringComparison.Ordinal) &&
               !bootstrapSource.Contains("ImportLegacyHistoryForMaintenanceAsync(", StringComparison.Ordinal) &&
               !bootstrapSource.Contains("LEGACY_PHT_UNDERSCORE_IMPORT", StringComparison.Ordinal),
            "Legacy underscore history remains external and has no runtime path into SQLite");

        string persistenceSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Services", "ProductionPersistenceService.cs"));
        Assert(persistenceSource.Contains("SQLITE_BUSY_RETRY", StringComparison.Ordinal) &&
               persistenceSource.Contains("exception.SqliteErrorCode is 5 or 6", StringComparison.Ordinal),
            "Production SQLite writer retries transient BUSY/LOCKED transactions");

        string mainViewModelSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "MainViewModel.cs"));
        Assert(mainViewModelSource.Contains(
                   "Task.WhenAll(productionDataTask, boardInitializationTask)",
                   StringComparison.Ordinal) &&
               mainViewModelSource.Contains(
                   "await Test.InitializeAsync();",
                   StringComparison.Ordinal),
            "Board connects alongside DB bootstrap while model/history wait for canonical database migration");

        string historyPageXaml = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "HistoryPage.xaml"));
        string historyPageSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "Views", "HistoryPage.xaml.cs"));
        Assert(!historyPageXaml.Contains("NHẬP LỊCH SỬ CŨ", StringComparison.Ordinal) &&
               historyPageXaml.Contains("x:Name=\"CloseButton\"", StringComparison.Ordinal) &&
               historyPageSource.Contains("GetHistorySummary(criteria)", StringComparison.Ordinal) &&
               historyPageSource.Contains("MaxRows = PageSize", StringComparison.Ordinal) &&
               historyPageSource.Contains("AfterHistoryAt = cursor.EffectiveTestStartedAt", StringComparison.Ordinal) &&
               !historyPageSource.Contains("UiRowLimit", StringComparison.Ordinal),
            "History uses full SQL summary plus incremental keyset pages and exposes no legacy import action");
        System.Xml.Linq.XElement[] historyButtons =
            System.Xml.Linq.XDocument.Parse(historyPageXaml)
                .Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .ToArray();
        Assert(historyButtons.Length == 6 &&
               historyButtons.All(button =>
                   button.Attribute("Style")?.Value.Contains("StaticResource", StringComparison.Ordinal) == true) &&
               historyPageXaml.Contains("HistoryCsvButtonStyle", StringComparison.Ordinal) &&
               historyPageXaml.Contains("HistoryExcelButtonStyle", StringComparison.Ordinal) &&
               historyPageXaml.Contains("HistorySearchButtonStyle", StringComparison.Ordinal) &&
               historyPageXaml.Contains("HistoryCloseButtonStyle", StringComparison.Ordinal) &&
               historyPageXaml.Contains("x:Name=\"InteractionOverlay\"", StringComparison.Ordinal),
            "Every HistoryPage button uses the shared semantic palette and interaction template");

        string appSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "App.xaml.cs"));
        Assert(appSource.Contains("Local\\JBZUniveresalLunix.Production", StringComparison.Ordinal) &&
               appSource.Contains("_ownsSingleInstanceMutex", StringComparison.Ordinal),
            "A station cannot open two app processes that compete for the same board and SQLite database");

        var invalid = new ProductionSettings
        {
            IoScanIntervalMs = -1,
            OpenCircuitConfirmMs = -1,
            ShortCircuitConfirmMs = -1,
            WrongConnectionConfirmMs = int.MaxValue,
            ProductSettleTimeMs = -1,
            JigContactUnstableWindowMs = -1,
            ProbeReplacementThreshold = -1
        };
        ProductionTimingPolicy.Normalize(invalid);
        Assert(invalid.IoScanIntervalMs == ProductionTimingPolicy.DefaultIoScanIntervalMs &&
               invalid.ShortCircuitConfirmMs == ProductionTimingPolicy.DefaultShortCircuitConfirmMs &&
               invalid.WrongConnectionConfirmMs == ProductionTimingPolicy.DefaultWrongConnectionConfirmMs &&
               invalid.ProductSettleTimeMs == ProductionTimingPolicy.DefaultProductSettleTimeMs &&
               invalid.JigContactUnstableWindowMs == ProductionTimingPolicy.DefaultJigContactUnstableWindowMs &&
               invalid.ProbeReplacementThreshold >= 1_000,
            "Timing settings normalize to internal defaults and maintenance setting keeps bounds");

        string settingsJson = JsonSerializer.Serialize(settings);
        ProductionSettings reloaded = JsonSerializer.Deserialize<ProductionSettings>(settingsJson)
            ?? throw new InvalidOperationException("Timing settings JSON reload");
        ProductionTimingPolicy.Normalize(reloaded);
        Assert(reloaded.OpenCircuitConfirmMs == 100 &&
               reloaded.ShortCircuitConfirmMs == ProductionTimingPolicy.DefaultShortCircuitConfirmMs &&
               reloaded.WrongConnectionConfirmMs == ProductionTimingPolicy.DefaultWrongConnectionConfirmMs &&
               reloaded.ProductSettleTimeMs == ProductionTimingPolicy.DefaultProductSettleTimeMs &&
               reloaded.JigContactUnstableWindowMs == ProductionTimingPolicy.DefaultJigContactUnstableWindowMs,
            "Legacy timing settings load but normalize to internal runtime defaults");
    }

    private static void TestIncompleteProductFullReleaseResetsClipCycle()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };
        TestViewModel vm = CreateTestViewModel(production, out FakeBoard board);

        // Tham chiếu C:\ITEM\WH322244.tht: AO common dùng IO201;
        // a1/a2/a3 lấy IO thật từ cột I/O lần lượt 202/203/204.
        ProductModel model = Model(("NORMAL", new[] { 1, 18 }));
        var common = new PinRecord("CLIP", "AO", 201, "AO", PinType: "AO");
        var a1 = new PinRecord("CLIP", "a1", 202, "a1", PinType: "a1");
        var a2 = new PinRecord("CLIP", "a2", 203, "a2", PinType: "a2");
        var a3 = new PinRecord("CLIP", "a3", 204, "a3", PinType: "a3");
        model.Pins.AddRange([common, a1, a2, a3]);
        model.Clip = new ClipTopology(
            common,
            [
                new ClipBranch("a1", 1, 202, a1, null),
                new ClipBranch("a2", 2, 203, a2, null),
                new ClipBranch("a3", 3, 204, a3, null)
            ]);

        vm.SetModel(model);
        vm.StartProductionTestAsync().GetAwaiter().GetResult();

        // Lắp dở: một dây thường và hai nhánh CLIP đã nối.
        board.Publish(FrameSeq(
            1,
            (1, new[] { 18 }),
            (201, new[] { 202, 203 })));
        Assert(vm.PassedNetworkCount == 3 && vm.State == "ĐANG KIỂM TRA...",
            "The first complete frame with real product connectivity shows testing immediately");

        // Tháo dây thường nhưng AO-a1 vẫn còn: tuyệt đối chưa reset.
        board.Publish(FrameSeq(2, (201, new[] { 202 })));
        Assert(vm.PassedNetworkCount > 0 && vm.State != "LẮP SẢN PHẨM",
            "One remaining AO-a1 connection prevents cycle reset");

        // Complete frame đầu tiên không còn bất kỳ cặp dây thường/CLIP nào
        // phải xóa toàn bộ latch và trả UI về LẮP SẢN PHẨM ngay.
        board.Publish(FrameSeq(3));
        Assert(vm.PassedNetworkCount == 0 &&
               vm.State == "LẮP SẢN PHẨM" &&
               vm.ResultStatusText == "LẮP SẢN PHẨM",
            "First authoritative full-release frame resets normal/CLIP latches and returns UI immediately");
    }

    private static void TestPartCounterStore()
    {
        Assert(
            string.Equals(
                new PartCounterStore().StoragePath,
                Path.Combine(AppContext.BaseDirectory, "PartCnt.txt"),
                StringComparison.OrdinalIgnoreCase),
            "Default PartCnt path is only the file beside the running EXE");

        string root = Path.Combine(Path.GetTempPath(), "JBZSelfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "PartCnt.txt");
            var store = new PartCounterStore(path);
            ProductModel modelA = Model(("A", new[] { 1, 2 }));
            modelA.PartNumber = "M030066900";

            PartCounterEntry created = store.GetOrCreate(modelA, 7);
            Assert(created.ReplacementThreshold == 200_000 && created.Counter == 7,
                "New PartCnt row uses original default threshold and migrated counter");
            Assert(File.ReadAllText(path, Encoding.UTF8) == "M030066900 200000 7\r\n",
                "PartCnt row matches original three-column CRLF format");

            File.WriteAllText(path, "M030066900 10 9\r\nDONG_KHONG_HOP_LE\r\n", new UTF8Encoding(false));
            PartCounterEntry edited = store.GetOrCreate(modelA);
            Assert(edited.ReplacementThreshold == 10 && edited.Counter == 9,
                "Manual PartCnt threshold/counter edits are reloaded");

            PartCounterEntry incremented = store.Increment(modelA);
            Assert(incremented.Counter == 10 && incremented.ReplacementThreshold == 10,
                "PartCnt increments the selected part without changing its threshold");
            Assert(store.LastWarning.Length > 0 && File.ReadAllText(path).Contains("DONG_KHONG_HOP_LE", StringComparison.Ordinal),
                "Malformed user line is reported and preserved when PartCnt is rewritten");

            PartCounterEntry reset = store.Reset(modelA);
            Assert(reset.Counter == 0 && reset.ReplacementThreshold == 10,
                "Password-authorized maintenance reset can preserve threshold and zero counter");

            ProductModel modelB = Model(("B", new[] { 3, 4 }));
            modelB.PartNumber = "M030076100";
            PartCounterEntry b = store.GetOrCreate(modelB);
            for (int cycle = 0; cycle < 4; cycle++)
                b = store.Increment(modelB);

            Assert(b.Counter == 4 && store.GetOrCreate(modelA).Counter == 0,
                "PartCnt counters remain isolated by part number");
            Assert(
                File.ReadAllText(path, Encoding.UTF8) ==
                "M030066900 10 0\r\nM030076100 200000 4\r\nDONG_KHONG_HOP_LE\r\n",
                "PartCnt persists one independent row per part number");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestLegacyPhtHistory()
    {
        string root = Path.Combine(Path.GetTempPath(), "JBZSelfTests", Guid.NewGuid().ToString("N"));
        string passRoot = Path.Combine(root, "Pass_");
        string errorRoot = Path.Combine(root, "Error_");
        Directory.CreateDirectory(root);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding cp949 = Encoding.GetEncoding(949);
            Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var writer = new LegacyPhtHistoryService(passRoot, errorRoot);
            ProductModel model = Model(("PAIR", new[] { 1, 2 }));
            model.PartNumber = "sssss";
            model.ProductName = "VOLTAGE_6S";
            model.VehicleType = "AE EV PE";
            model.Eco = "AE EV PE";

            DateTime masterTime = new(2026, 8, 7, 16, 25, 39);
            string masterPath = writer.AppendMaster(model, masterTime, 7000, goodMaster: true);
            var pass = new CompletedTestResult
            {
                Started = new DateTime(2026, 8, 7, 16, 25, 40),
                Finished = new DateTime(2026, 8, 7, 16, 25, 47),
                Passed = true,
                ResultText = "PASS"
            };
            string passPath = writer.AppendProduct(model, pass, 7001);
            string passText = utf8.GetString(File.ReadAllBytes(passPath));
            string expectedPass =
                "|[정상마스터]101|..|260807|162539|2608077000|||sssss7000|VOLTAGE_6S|AE EV PE|AE EV PE||||\r\n" +
                "|1|..|260807|162547|2608077001|||sssss7001|VOLTAGE_6S|AE EV PE|AE EV PE||||\r\n";
            Assert(masterPath == passPath && passPath.EndsWith(
                    Path.Combine("Year2026", "Month08", "Day07.dat"),
                    StringComparison.OrdinalIgnoreCase),
                "PHT PASS path uses original Year/Month/Day.dat hierarchy");
            Assert(passText == expectedPass,
                "PHT PASS/master records match the supplied UTF-8 pipe format and append without truncation");

            var smallCounterPass = new CompletedTestResult
            {
                Started = new DateTime(2026, 7, 1, 18, 17, 0),
                Finished = new DateTime(2026, 7, 1, 18, 17, 9),
                Passed = true,
                ResultText = "PASS"
            };
            string smallCounterPath = writer.AppendProduct(model, smallCounterPass, 1);
            string smallCounterText = utf8.GetString(File.ReadAllBytes(smallCounterPath));
            Assert(smallCounterText == "|1|..|260701|181709|2607010001|||sssss0001|VOLTAGE_6S|AE EV PE|AE EV PE||||\r\n",
                "PHT PASS LOT is padded to four digits like the original application");

            model.PartNumber = "1200020430";
            var productionLotPass = new CompletedTestResult
            {
                Started = new DateTime(2026, 8, 26, 7, 53, 50),
                Finished = new DateTime(2026, 8, 26, 7, 53, 58),
                Passed = true,
                ResultText = "PASS"
            };
            string productionLotPath = writer.AppendProduct(model, productionLotPass, 2001);
            string productionLotText = utf8.GetString(File.ReadAllBytes(productionLotPath));
            Assert(productionLotText ==
                   "|1|..|260826|075358|2608262001|||12000204302001|VOLTAGE_6S|AE EV PE|AE EV PE||||\r\n",
                "PHT PASS record uses configured production LOT instead of PartCnt counter");

            model.ProductName = "BMS EXT";
            model.VehicleType = "US4 HEV";
            model.CustomerCode = "12000/20430";
            var failed = new CompletedTestResult
            {
                Started = new DateTime(2026, 8, 22, 8, 55, 34),
                Finished = new DateTime(2026, 8, 22, 8, 55, 36),
                Passed = false,
                ResultText = "FAIL",
                Faults =
                [
                    new FaultDetail
                    {
                        Type = ProductFaultType.ShortCircuit,
                        ActualSourceIo = 12,
                        ActualTargetIo = 14
                    },
                    new FaultDetail
                    {
                        Type = ProductFaultType.OpenCircuit,
                        ExpectedSourceIo = 10,
                        ExpectedTargetIo = 11
                    }
                ]
            };
            string errorPath = writer.AppendProduct(model, failed, 2000);
            byte[] errorBytes = File.ReadAllBytes(errorPath);
            string errorText = cp949.GetString(errorBytes);
            Assert(errorPath.EndsWith(
                    Path.Combine("Year2026", "Month08", "Day22.err"),
                    StringComparison.OrdinalIgnoreCase),
                "PHT ERR path uses original Year/Month/Day.err hierarchy");
            Assert(errorText.StartsWith(
                    "[정상마스터 Short Open] *BMS EXT; 1200020430; US4 HEV; 12000/20430; ; ; 2000 2026/08/22 08:55:34 - 08:55:36|\r\n",
                    StringComparison.Ordinal) &&
                   errorText.Contains(" >검사 IO:12\r\n -합선 IO:14", StringComparison.Ordinal) &&
                   errorText.Contains(" >검사 IO:10\r\n  단선 IO:11", StringComparison.Ordinal) &&
                   errorText.EndsWith("\r\n\r\n", StringComparison.Ordinal),
                "PHT ERR header, Korean IO detail and CRLF record separator match original format");
            Assert(errorBytes.Length > 4 && !(errorBytes[0] == 0xEF && errorBytes[1] == 0xBB && errorBytes[2] == 0xBF),
                "PHT shared files use CP949 without UTF-8 BOM");

            var reader = new LegacyPhtHistoryReader(passRoot, errorRoot);
            LegacyProductionSnapshot shared = reader.GetProductionSnapshot(
                model,
                new DateTime(2026, 8, 26, 12, 0, 0));
            Assert(shared.DailyTotal == 1 && shared.DailyPass == 1 && shared.DailyFail == 0 &&
                   shared.MonthlyTotal == 2 && shared.LifetimeTotal == 2,
                "Shared production counters include original PASS/ERR files and exclude MASTER rows");

            HistorySearchCriteria allCriteria = new(null, null, null, string.Empty, "ALL", 100);
            IReadOnlyList<TestHistoryRecord> legacyRows = reader.Search(allCriteria);
            Assert(legacyRows.Count == 4 && legacyRows.Count(row => row.Passed) == 3 &&
                   legacyRows.Count(row => !row.Passed) == 1,
                "Original PHT history reader returns every product record from both shared roots");
            Assert(reader.SearchForExport(allCriteria with { MaxRows = 1 }).Count == 4,
                "Original PHT export returns all matching rows instead of the screen row limit");

            TestHistoryRecord duplicateDetailed = legacyRows.Single(row =>
                row.Passed && row.PartNumber == "1200020430" && row.LotNo == 2001);
            duplicateDetailed = new TestHistoryRecord
            {
                Id = 123,
                Started = duplicateDetailed.Started,
                Finished = duplicateDetailed.Finished,
                PartNumber = duplicateDetailed.PartNumber,
                LotNo = duplicateDetailed.LotNo,
                Result = "PASS",
                Passed = true,
                CycleId = "NEW-APP-CYCLE"
            };
            IReadOnlyList<TestHistoryRecord> merged = LegacyPhtHistoryReader.MergeWithoutDuplicates(
                [duplicateDetailed], legacyRows, exportOrder: false);
            Assert(merged.Count == 4 && merged.Count(row => row.Id == 123) == 1,
                "Shared history merge keeps detailed new-app row and removes its compatible PHT duplicate");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestProductionCounters()
    {
        string root = Path.Combine(Path.GetTempPath(), "JBZSelfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var perModelMeasurement = new ProductionSettings();
            ProductionConfigService.SetResistanceProfileForPath(
                perModelMeasurement, @"C:\ITEM\PART-A.tht",
                [new ResistanceChannelSetting { Name = "R1", Enabled = true, Channel = 3, MinOhm = 10, MaxOhm = 20 }]);
            ProductionConfigService.SetResistanceProfileForPath(
                perModelMeasurement, @"C:\ITEM\PART-B.tht",
                [new ResistanceChannelSetting { Name = "R1", Enabled = false, Channel = 7, MinOhm = 30, MaxOhm = 40 }]);
            ResistanceChannelSetting resistanceA = ProductionConfigService
                .GetResistanceProfileForPath(perModelMeasurement, @"C:\ITEM\PART-A.tht")[0];
            ResistanceChannelSetting resistanceB = ProductionConfigService
                .GetResistanceProfileForPath(perModelMeasurement, @"C:\ITEM\PART-B.tht")[0];
            Assert(resistanceA.Enabled && resistanceA.Channel == 3 && resistanceA.MinOhm == 10 &&
                   !resistanceB.Enabled && resistanceB.Channel == 7 && resistanceB.MinOhm == 30,
                "Resistance Enabled/channel/limits are isolated and restored by THT model key");
            string perModelCfg = Path.Combine(root, "per-model-measurement.cfg");
            ProductionConfigService.SaveLegacyCfg(perModelMeasurement, perModelCfg);
            string cfgText = File.ReadAllText(perModelCfg);
            Assert(cfgText.Contains("[Resistance.Model.PART-A.R1]1;3;10;20", StringComparison.Ordinal) &&
                   cfgText.Contains("[Resistance.Model.PART-B.R1]0;7;30;40", StringComparison.Ordinal),
                "Per-model Resistance profiles persist Enabled/channel/min/max in station configuration");

            string path = Path.Combine(root, "production.statistics.json");
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero));
            var store = new ProductionStatisticsStore(path, clock);
            ProductModel modelA = Model(("A", new[] { 1, 2 }));
            modelA.ModelName = "MODEL-A";
            modelA.PartNumber = "ABC123";
            ProductModel modelB = Model(("B", new[] { 3, 4 }));
            modelB.ModelName = "MODEL-B";
            modelB.PartNumber = "XYZ456";

            store.RecordProbeCycle(modelA, 2);
            store.Record(modelA, true, 1, "PASS");
            store.Record(modelA, false, 2, "FAIL");
            store.Record(modelB, true, 3, "PASS");

            ModelProductionStatistics a = store.Get(modelA);
            ModelProductionStatistics b = store.Get(modelB);
            Assert(a.DailyTestCount == 2 && a.DailyPassCount == 1 && a.DailyFailCount == 1 &&
                   a.MonthlyTestCount == 2 && a.LifetimeTestCount == 2,
                "Model A production periods/lifetime and daily result split");
            Assert(a.ProbeCycleCount == 1 && b.ProbeCycleCount == 0 && b.LifetimeTestCount == 1, "Per-model counter isolation");

            ProductModel renamedModelA = Model(("A-RENAMED", new[] { 5, 6 }));
            renamedModelA.ModelName = "MODEL-A-NEW-REVISION";
            renamedModelA.PartNumber = " abc123 ";
            Assert(store.Get(renamedModelA).Total == 2,
                "Same part number restores production count across model name changes");
            store.Record(renamedModelA, true, 4, "PASS");
            Assert(store.Get(modelA).Total == 3 && store.Get(modelB).Total == 1,
                "Production count is isolated by part number and shared by revisions");

            var restarted = new ProductionStatisticsStore(path, clock);
            Assert(restarted.Get(modelA).ProbeCycleCount == 1 && restarted.Get(modelA).LifetimeTestCount == 3, "Counters persist after restart");

            clock.Advance(TimeSpan.FromDays(1));
            ModelProductionStatistics rolledDay = restarted.Get(modelA);
            Assert(rolledDay.DailyTestCount == 0 && rolledDay.DailyPassCount == 0 &&
                   rolledDay.DailyFailCount == 0 && rolledDay.MonthlyTestCount == 3 &&
                   rolledDay.ProbeCycleCount == 1,
                "Daily production resets without resetting month or cumulative PartCnt mirror");
            restarted.Record(modelA, true, 4, "PASS");
            ModelProductionStatistics newDay = restarted.Get(modelA);
            Assert(newDay.DailyTestCount == 1 && newDay.DailyPassCount == 1 &&
                   newDay.DailyFailCount == 0 && newDay.MonthlyTestCount == 4,
                "New day increments the correct daily production counters");

            TestViewModel lotDisplayVm = CreateTestViewModel(
                new ProductionSettings { LotNo = 2000, MasterFaultRequiredCount = 0 });
            MethodInfo applyDailyStatistics = typeof(TestViewModel).GetMethod(
                "ApplyDailyProductionStatistics",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Daily production UI method not found");
            applyDailyStatistics.Invoke(lotDisplayVm,
            [
                new ModelProductionStatistics
                {
                    DailyTestCount = 10,
                    DailyPassCount = 10,
                    DailyFailCount = 0
                }
            ]);
            Assert(lotDisplayVm.Lot == "2010" && lotDisplayVm.Total == 10 &&
                   lotDisplayVm.Pass == 10 && lotDisplayVm.Fail == 0,
                "LOT display is starting LOT 2000 plus daily PASS 10");
            applyDailyStatistics.Invoke(lotDisplayVm, [new ModelProductionStatistics()]);
            Assert(lotDisplayVm.Lot == "2000" && lotDisplayVm.Total == 0,
                "New daily period resets production to zero and LOT display to its starting value");

            var switchSettings = new ProductionSettings
            {
                MasterFaultRequiredCount = 0,
                LotSettingsByProduct = new Dictionary<string, ProductLotSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PART-A"] = new() { StartLotNo = 2000, LotNo = 2010, LotNoDate = DateTime.Now.ToString("yyyy-MM-dd") },
                    ["PART-B"] = new() { StartLotNo = 7000, LotNo = 7003, LotNoDate = DateTime.Now.ToString("yyyy-MM-dd") }
                }
            };
            TestViewModel switchVm = CreateTestViewModel(switchSettings);
            ProductModel partA = Model(("SHARED", new[] { 1, 2 }));
            partA.PartNumber = "PART-A";
            partA.ModelName = "SAME-MODEL";
            ProductModel partB = Model(("SHARED", new[] { 1, 2 }));
            partB.PartNumber = "PART-B";
            partB.ModelName = "SAME-MODEL";
            MethodInfo applyProductionStatistics = typeof(TestViewModel).GetMethod(
                "ApplyProductionStatistics", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Production statistics UI method not found");
            MethodInfo activeStatisticsContext = typeof(TestViewModel).GetMethod(
                "IsActiveStatisticsContext", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Statistics context guard not found");

            switchVm.SetModel(partA);
            long generationA = (long)(typeof(TestViewModel).GetField(
                "_statisticsLoadGeneration", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(switchVm) ?? 0L);
            applyProductionStatistics.Invoke(switchVm,
                [new ProductionStatisticsSnapshot(12, 10, 2, 12, 12, 10, 2, 2009, "FAIL")]);
            Assert(switchVm.Total == 12 && switchVm.Pass == 10 && switchVm.Fail == 2 && switchVm.Lot == "2010",
                "CASE A: Part A shows only A daily totals and StartLot 2000 + PASS 10");

            switchVm.SetModel(partB);
            long generationB = (long)(typeof(TestViewModel).GetField(
                "_statisticsLoadGeneration", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(switchVm) ?? 0L);
            applyProductionStatistics.Invoke(switchVm,
                [new ProductionStatisticsSnapshot(4, 3, 1, 4, 4, 3, 1, 7002, "FAIL")]);
            Assert(switchVm.Total == 4 && switchVm.Pass == 3 && switchVm.Fail == 1 && switchVm.Lot == "7003",
                "CASE A: Part B replaces A totals and uses B StartLot");
            bool staleAAllowed = (bool)(activeStatisticsContext.Invoke(switchVm,
                [partA, PartIdentitySnapshot.Capture(partA), generationA]) ?? true);
            bool currentBAllowed = (bool)(activeStatisticsContext.Invoke(switchVm,
                [partB, PartIdentitySnapshot.Capture(partB), generationB]) ?? false);
            Assert(!staleAAllowed && currentBAllowed,
                "CASE D: stale Part A statistics cannot apply after Part B becomes active");

            switchVm.SetModel(partA);
            applyProductionStatistics.Invoke(switchVm,
                [new ProductionStatisticsSnapshot(13, 11, 2, 13, 13, 11, 2, 2010, "PASS")]);
            Assert(switchVm.Total == 13 && switchVm.Pass == 11 && switchVm.Fail == 2 && switchVm.Lot == "2011",
                "CASE A/E: switching back restores A and one PASS advances only A Total/PASS/LOT");
            switchVm.SetModel(partB);
            applyProductionStatistics.Invoke(switchVm,
                [new ProductionStatisticsSnapshot(4, 3, 1, 4, 4, 3, 1, 7002, "FAIL")]);
            Assert(switchVm.Total == 4 && switchVm.Pass == 3 && switchVm.Fail == 1 && switchVm.Lot == "7003",
                "CASE B/C: B remains isolated; FAIL contributes to Total/FAIL but not LOT");

            clock.Advance(TimeSpan.FromDays(24));
            Assert(restarted.Get(modelA).MonthlyTestCount == 0 && restarted.Get(modelA).ProbeCycleCount == 1, "Monthly period rolls without resetting probe");

            ModelProductionStatistics due = restarted.RecordProbeCycle(modelA, 2);
            Assert(due.ProbeCycleCount == 2 && due.ProbeCycleCount >= due.ProbeReplacementThreshold, "Probe replacement threshold transition");
            long lifetimeBeforeReset = due.LifetimeTestCount;
            ProbeMaintenanceRecord maintenance = restarted.ResetProbeCycle(modelA, 2, "ADMIN", "STATION-1");
            ModelProductionStatistics reset = restarted.Get(modelA);
            Assert(reset.ProbeCycleCount == 0 && reset.LifetimeTestCount == lifetimeBeforeReset, "Probe reset does not reset production counters");
            Assert(maintenance.PreviousProbeCycleCount == 2 && maintenance.Action == "PROBE PIN REPLACED", "Maintenance record values");
            Assert(restarted.GetMaintenanceRecords(modelA).Count == 1 && restarted.GetMaintenanceRecords(modelB).Count == 0, "Maintenance history separated per model");

            Assert(!AdminAuthenticationService.Verify(string.Empty, string.Empty), "Empty admin password cannot authorize reset");
            Assert(!AdminAuthenticationService.Verify("admin-secret", "wrong"), "Wrong admin password rejected");
            Assert(AdminAuthenticationService.Verify("admin-secret", "admin-secret"), "Configured admin password accepted");
            Assert(!AdminAuthenticationService.VerifyProbeMaintenance("wrong"),
                "Probe maintenance rejects a wrong independent password");
            Assert(AdminAuthenticationService.VerifyProbeMaintenance("admin"),
                "Probe maintenance uses the independent admin password");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestInlineProbeDoesNotClearWiringFaults()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };

        ProductModel model = Model(("PAIR-A", new[] { 1, 86 }), ("PAIR-B", new[] { 2, 87 }));
        TestViewModel vm = CreateTestViewModel(production, out FakeBoard board);
        vm.SetModel(model);
        vm.StartProductionTestAsync().GetAwaiter().GetResult();

        board.Publish(FrameSeq(21, (1, new[] { 86 }), (2, new[] { 87 }), (86, new[] { 87 })));
        TestEngine engine = (TestEngine)(typeof(TestViewModel).GetField(
            "_engine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(vm) ?? throw new InvalidOperationException("TestViewModel engine field not found"));
        PassGateDiagnostics beforeProbe = engine.GetPassGateDiagnostics();
        Assert(beforeProbe.ShortCandidateCount + beforeProbe.WrongCandidateCount > 0,
            "Wiring fault candidate exists before TESTPIN event");

        board.PublishProbePreview(new ProductionProbePreview(
            DateTime.UtcNow, new[] { 1 }, 1, 1, 22, 1));
        Assert(vm.HasInlineProbeContacts,
            "Explicit TESTPIN preview appears immediately in TestView");

        MethodInfo productionFaultContextMethod = typeof(TestViewModel).GetMethod(
            "IsProductionFaultContext",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("IsProductionFaultContext method not found.");
        long runtimeGeneration = (long)(typeof(TestViewModel).GetField(
            "_runtimeGeneration", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(vm) ?? throw new InvalidOperationException("runtime generation field not found."));
        Assert((bool)productionFaultContextMethod.Invoke(vm, [runtimeGeneration])!,
            "Inline TESTPIN must not suppress Production SHORT/OTHER fault context");

        string vmSource = File.ReadAllText(
            Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestViewModel.cs"));
        int productionContextStart = vmSource.IndexOf(
            "private bool IsProductionFaultContext", StringComparison.Ordinal);
        int productionContextEnd = vmSource.IndexOf(
            "public void PrepareProbeUiMode", productionContextStart, StringComparison.Ordinal);
        Assert(productionContextStart >= 0 && productionContextEnd > productionContextStart &&
               !vmSource[productionContextStart..productionContextEnd].Contains(
                   "_inlineProbeContactIo", StringComparison.Ordinal),
            "Inline TESTPIN must not participate in the Production fault-context gate");

        int recordCompletedStart = vmSource.IndexOf(
            "private async Task<bool> RecordCompletedProductAsync", StringComparison.Ordinal);
        int recordCompletedGuardEnd = vmSource.IndexOf(
            "ProductModel model = cycleModel;", recordCompletedStart, StringComparison.Ordinal);
        Assert(recordCompletedStart >= 0 && recordCompletedGuardEnd > recordCompletedStart &&
               !vmSource[recordCompletedStart..recordCompletedGuardEnd].Contains(
                   "_inlineProbeContactIo", StringComparison.Ordinal),
            "Inline TESTPIN must not block committing an authoritative SHORT/OTHER FAIL");

        int passReasonStart = vmSource.IndexOf(
            "private string ResolvePassGateReason", StringComparison.Ordinal);
        int passReasonEnd = vmSource.IndexOf(
            "private bool HasInstalledProductEvidenceForProbe", passReasonStart, StringComparison.Ordinal);
        string passReasonSource = passReasonStart >= 0 && passReasonEnd > passReasonStart
            ? vmSource[passReasonStart..passReasonEnd]
            : string.Empty;
        Assert(passReasonSource.IndexOf("SHORT_CONFIRMED", StringComparison.Ordinal) >= 0 &&
               passReasonSource.IndexOf("SHORT_CONFIRMED", StringComparison.Ordinal) <
               passReasonSource.IndexOf("PROBE_INTERLOCK", StringComparison.Ordinal),
            "Confirmed SHORT must outrank TESTPIN/Probe interlock in PASS/FAIL diagnostics");

        PassGateDiagnostics afterProbe = engine.GetPassGateDiagnostics();
        Assert(afterProbe.ShortCandidateCount + afterProbe.WrongCandidateCount > 0,
            "TESTPIN presentation must not clear existing SHORT/WRONG state");

        TestViewModel cleanProbeVm = CreateTestViewModel(production, out FakeBoard cleanProbeBoard);
        cleanProbeVm.SetModel(model);
        cleanProbeVm.StartProductionTestAsync().GetAwaiter().GetResult();
        TestEngine cleanProbeEngine = (TestEngine)(typeof(TestViewModel).GetField(
            "_engine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cleanProbeVm) ?? throw new InvalidOperationException("Clean Probe engine not found"));
        cleanProbeBoard.PublishProbePreview(new ProductionProbePreview(
            DateTime.UtcNow, new[] { 125 }, 1, 1, 24, 1));
        Assert(cleanProbeVm.HasInlineProbeContacts &&
               cleanProbeVm.CurrentProductionRuntimeState == ProductionRuntimeState.WaitingForProduct &&
               !cleanProbeEngine.GetProductEvidenceSnapshot().ValidProductEvidence &&
               !cleanProbeVm.Faults.Any(row => row.Kind is FaultKind.WrongWiring or FaultKind.Short),
            "TESTPIN is display-only and cannot create ProductPresence, WRONG or SHORT");

        cleanProbeBoard.PublishProbePreview(new ProductionProbePreview(
            DateTime.UtcNow, Array.Empty<int>(), 1, 0, 25, 1));
        MethodInfo waitMethod = typeof(TestViewModel).GetMethod(
            "WaitForProbeRelayInterlockAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Probe relay interlock method not found.");
        Stopwatch sw = Stopwatch.StartNew();
        ((Task)waitMethod.Invoke(cleanProbeVm, [CancellationToken.None])!).GetAwaiter().GetResult();
        sw.Stop();
        Assert(sw.ElapsedMilliseconds < 100,
            "Released TESTPIN adds no production PASS delay");
    }

    private static void TestFiveHundredCycleScanProbeFaultStress()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            ProductSettleTimeMs = 10_000,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0,
            UseTestPointer = true
        };
        // Keep one network intentionally pending so this stress test exercises
        // the continuous scan/probe/fault path without legitimately entering
        // the separate final-PASS flow that stops scanning for relay handling.
        ProductModel model = Model(
            ("PAIR-A", new[] { 1, 86 }),
            ("PAIR-B", new[] { 2, 87 }),
            ("PAIR-C", new[] { 3, 88 }));
        TestViewModel vm = CreateTestViewModel(production, out FakeBoard board);
        vm.SetModel(model);
        vm.StartProductionTestAsync().GetAwaiter().GetResult();

        // Prime each branch once so the bounded-handle check measures repeated
        // cycles, rather than one-time WPF/diagnostic initialization.
        board.Publish(FrameSeq(990, (1, new[] { 86 }), (2, new[] { 87 })));
        board.Publish(FrameSeq(991, (1, Array.Empty<int>())));
        board.Publish(FrameSeq(992, (1, new[] { 86, 87 }), (2, new[] { 87 })));
        board.Publish(FrameSeq(993, Enumerable.Range(10, 20)
            .Select(source => (source, new[] { 1 })).ToArray()));
        board.Publish(FrameSeq(994));

        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        int threadCountBefore = Process.GetCurrentProcess().Threads.Count;
        int handleCountBefore = Process.GetCurrentProcess().HandleCount;

        for (int cycle = 1; cycle <= 500; cycle++)
        {
            long seq = 1000 + cycle;
            switch (cycle % 5)
            {
                case 0:
                    board.Publish(FrameSeq(seq, (1, new[] { 86 }), (2, new[] { 87 })));
                    break;
                case 1:
                    board.Publish(FrameSeq(seq, (1, Array.Empty<int>())));
                    break;
                case 2:
                    board.Publish(FrameSeq(seq, (1, new[] { 86, 87 }), (2, new[] { 87 })));
                    break;
                case 3:
                    board.Publish(FrameSeq(
                        seq,
                        Enumerable.Range(10, 20)
                            .Select(source => (source, new[] { 1 }))
                            .ToArray()));
                    break;
                default:
                    board.Publish(FrameSeq(seq));
                    break;
            }
        }

        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        int threadCountAfter = Process.GetCurrentProcess().Threads.Count;
        int handleCountAfter = Process.GetCurrentProcess().HandleCount;

        Assert(board.FramesReceived >= 500, "Stress: FramesReceived keeps increasing through 500 cycles");
        Assert(vm.ProductionFramesProcessed >= 500, "Stress: Probe/fault frames are not suppressed before TestEngine");
        Assert(board.IsScanning, "Stress: Product/probe processing does not stop D2XX scan");
        Assert(board.Commands.Count(command => command == "START") <= 1,
            "Stress: logical cycles reuse one healthy production scan");
        Assert(threadCountAfter <= threadCountBefore + 4,
            $"Stress: thread count remains bounded ({threadCountBefore} -> {threadCountAfter})");
        Assert(handleCountAfter <= handleCountBefore + 16,
            $"Stress: handle count remains bounded ({handleCountBefore} -> {handleCountAfter})");
        Assert(memoryAfter <= memoryBefore + (32L * 1024 * 1024),
            $"Stress: managed memory remains bounded ({memoryBefore} -> {memoryAfter})");
        Assert(vm.Faults.Count(row => row.Kind == FaultKind.Probe && row.Io == 1) <= 1,
            "Stress: Probe display does not duplicate the same IO row");
        Assert(!vm.IsDeviceFault, "Stress: no DeviceFault/deadlock from mixed scan/probe/fault frames");
    }

#if LEGACY_D2XX
    private static void TestThtColumnSemantics()
    {
        var production = new ProductionSettings
        {
            MasterFaultRequiredCount = 0,
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50,
            PassMarkingToJigDelayMs = 0,
            ProductSettleTimeMs = 0,
            WrongConnectionConfirmMs = 0,
            ShortCircuitConfirmMs = 0
        };

        ProductModel supplied = TopologyModel(
            new Terminal(1, "1", "1", "2", "~1", "B"),
            new Terminal(2, "1", "2", "2", "~1", "B"));
        Assert(supplied.Connectors.Count == 1 &&
               supplied.Connectors[0].ConnectorId == "1" &&
               supplied.Connectors[0].DeclaredPinCount == 2 &&
               supplied.Connectors[0].Pins.Count == 2 &&
               supplied.Connectors[0].Pins.Any(pin => pin.LocalPinNumber == "1" && pin.PhysicalIo == 1) &&
               supplied.Connectors[0].Pins.Any(pin => pin.LocalPinNumber == "2" && pin.PhysicalIo == 2),
            "Connector/Pin metadata remains separate from tester I/O");
        Assert(supplied.Nets.Count == 1 &&
               supplied.Nets[0].Name == "~1" &&
               supplied.Nets[0].IoNumbers.SequenceEqual([1, 2]),
            "Supplied two-row THT builds one string WireName network");

        using TestEngine suppliedEngine = CreateEngine(out _, production);
        suppliedEngine.SetModel(supplied);
        suppliedEngine.ProcessFrame(FrameSeq(1));
        Assert(suppliedEngine.ExpectedNetCount == 1 &&
               suppliedEngine.GetPassGateDiagnostics().PassedNetCount == 0 &&
               !suppliedEngine.ContinuityPassed &&
               !suppliedEngine.HasWiringFault,
            "Missing expected WireName network is live progress, not product FAIL");
        suppliedEngine.ProcessFrame(FrameSeq(2, (2, new[] { 1 })));
        FaultRow[] suppliedMapped = suppliedEngine.BuildRows().Where(row => row.WireName == "~1").ToArray();
        Assert(suppliedEngine.GetPassGateDiagnostics().PassedNetCount == 1 &&
               suppliedEngine.ContinuityPassed &&
               !suppliedEngine.HasWiringFault &&
               suppliedMapped.Length == 0,
            "IO1/IO2 pass removes connected endpoint rows from production table");

        foreach (string wireName in new[] { "1", "2", "BG1", "BG2", "EA1", "MC01", "~1", "A01", "B/G1" })
        {
            ProductModel model = TopologyModel(
                new Terminal(1, "C1", "1", "2", wireName),
                new Terminal(20, "C2", "7", "8", wireName));
            Assert(model.Nets.Count == 1 &&
                   model.Nets[0].Name == wireName &&
                   model.Nets[0].IoNumbers.SequenceEqual([1, 20]),
                $"WireName '{wireName}' remains a string network identifier");
        }

        ProductModel stringDistinct = TopologyModel(
            new Terminal(1, "C1", "1", "2", "1"),
            new Terminal(2, "C1", "2", "2", "01"),
            new Terminal(3, "C1", "3", "3", "MC01"),
            new Terminal(4, "C1", "4", "4", "MC1"),
            new Terminal(5, "C1", "5", "5", "BG1"),
            new Terminal(6, "C1", "6", "6", "BG01"));
        Assert(stringDistinct.Nets.Count == 0,
            "Different WireName strings are not merged by numeric or invented normalization");

        using TestEngine differentWireEngine = CreateEngine(out _, production);
        differentWireEngine.SetModel(TopologyModel(
            new Terminal(1, "1", "1", "2", "1"),
            new Terminal(2, "1", "2", "2", "2")));
        differentWireEngine.ProcessFrame(FrameSeq(3, (1, new[] { 2 })));
        PassGateDiagnostics differentWireGate = differentWireEngine.GetPassGateDiagnostics();
        Assert(!differentWireEngine.ContinuityPassed &&
               differentWireGate.PassedNetCount == 0 &&
               differentWireGate.WrongCandidateCount + differentWireGate.ShortCandidateCount > 0,
            "Same connector and PinCount do not make different WireNames a valid connection");

        using TestEngine samePinDifferentConnector = CreateEngine(out _, production);
        samePinDifferentConnector.SetModel(TopologyModel(
            new Terminal(1, "1", "1", "10", "A"),
            new Terminal(11, "2", "1", "20", "B")));
        samePinDifferentConnector.ProcessFrame(FrameSeq(4, (1, new[] { 11 })));
        PassGateDiagnostics samePinGate = samePinDifferentConnector.GetPassGateDiagnostics();
        Assert(!samePinDifferentConnector.ContinuityPassed &&
               samePinGate.WrongCandidateCount + samePinGate.ShortCandidateCount > 0,
            "Same local Pin number across connectors is unrelated unless WireName matches");

        using TestEngine acrossConnector = CreateEngine(out _, production);
        acrossConnector.SetModel(TopologyModel(
            new Terminal(1, "1", "1", "10", "ABC"),
            new Terminal(40, "2", "8", "20", "ABC")));
        acrossConnector.ProcessFrame(FrameSeq(5, (40, new[] { 1 })));
        Assert(acrossConnector.ExpectedNetCount == 1 && acrossConnector.ContinuityPassed,
            "Same WireName across different connectors is one valid electrical network");

        using TestEngine threeEndpoint = CreateEngine(out _, production);
        threeEndpoint.SetModel(TopologyModel(
            new Terminal(1, "1", "1", "10", "A"),
            new Terminal(5, "1", "5", "10", "A"),
            new Terminal(20, "2", "4", "20", "A")));
        threeEndpoint.ProcessFrame(FrameSeq(6, (1, new[] { 5 }), (5, new[] { 20 })));
        Assert(threeEndpoint.ExpectedNetCount == 1 && threeEndpoint.ContinuityPassed,
            "Three endpoints with the same WireName are one connected-component network, not three networks");

        ProductModel pinCountOnly = TopologyModel(
            new Terminal(1, "1", "1", "2", "A"),
            new Terminal(2, "1", "2", "2", "B"));
        Assert(pinCountOnly.Nets.Count == 0,
            "Connector PinCount metadata does not create electrical networks");

        ProductModel blankWire = TopologyModel(
            new Terminal(1, "1", "1", "2", ""),
            new Terminal(2, "1", "2", "2", ""));
        Assert(blankWire.Nets.Count == 0 && blankWire.Connectors.Single().DeclaredPinCount == 2,
            "Blank WireName rows do not form one giant blank network");

        ProductModel mismatch = TopologyModel(
            new Terminal(1, "1", "1", "10", "M"),
            new Terminal(2, "1", "2", "12", "M"));
        Assert(mismatch.TopologyWarnings.Any(warning =>
                warning.Contains("MODEL_WARNING_CONNECTOR_PINCOUNT_MISMATCH", StringComparison.Ordinal)),
            "Connector PinCount mismatch is logged as model warning, not used as topology");

        TestViewModel vm = CreateTestViewModel(production);
        vm.SetModel(supplied);
        Assert(vm.ExpectedNetworkCount == 1 && vm.ProductionEnabled,
            "Production/Master UI uses the same corrected WireName topology source of truth");

        ProductModel wh322244Extract = TopologyModel(
            new Terminal(1, "1", "1", "43", "BG01"),
            new Terminal(2, "1", "2", "43", "BG02"),
            new Terminal(3, "1", "3", "43", "BF03"),
            new Terminal(4, "1", "4", "43", ""),
            new Terminal(5, "1", "5", "43", ""),
            new Terminal(16, "1", "16", "43", "BG16"),
            new Terminal(34, "1", "34", "43", "NUT01"),
            new Terminal(35, "1", "35", "43", "NUT01"),
            new Terminal(43, "1", "43", "43", "TAIL43"),
            new Terminal(44, "2", "1", "6", "FH01"),
            new Terminal(49, "2", "6", "6", "TAIL49"),
            new Terminal(79, "7", "1", "30", "BG16"),
            new Terminal(86, "7", "8", "30", "BG01"),
            new Terminal(87, "7", "9", "30", "BG02"),
            new Terminal(122, "8", "14", "34", "BF03"));

        Assert(PinByIo(wh322244Extract, 1) is { Connector: "1", PinNumber: "1", WireName: "BG01" } &&
               PinByIo(wh322244Extract, 2) is { Connector: "1", PinNumber: "2", WireName: "BG02" } &&
               PinByIo(wh322244Extract, 43) is { Connector: "1", PinNumber: "43" } &&
               PinByIo(wh322244Extract, 44) is { Connector: "2", PinNumber: "1", WireName: "FH01" } &&
               PinByIo(wh322244Extract, 49) is { Connector: "2", PinNumber: "6" } &&
               PinByIo(wh322244Extract, 79) is { Connector: "7", PinNumber: "1", WireName: "BG16" } &&
               PinByIo(wh322244Extract, 86) is { Connector: "7", PinNumber: "8", WireName: "BG01" } &&
               PinByIo(wh322244Extract, 87) is { Connector: "7", PinNumber: "9", WireName: "BG02" } &&
               PinByIo(wh322244Extract, 122) is { Connector: "8", PinNumber: "14", WireName: "BF03" },
            "WH322244 extracted THT rows preserve Connector/WireName/Physical I/O/Local Pin semantics");

        Assert(ConnectorPinCount(wh322244Extract, "1") == 43 &&
               ConnectorPinCount(wh322244Extract, "2") == 6 &&
               ConnectorPinCount(wh322244Extract, "7") == 30 &&
               ConnectorPinCount(wh322244Extract, "8") == 34,
            "WH322244 connector pin counts remain connector metadata");

        Assert(NetIos(wh322244Extract, "BG01").SequenceEqual([1, 86]) &&
               NetIos(wh322244Extract, "BG02").SequenceEqual([2, 87]) &&
               NetIos(wh322244Extract, "BF03").SequenceEqual([3, 122]) &&
               NetIos(wh322244Extract, "BG16").SequenceEqual([16, 79]) &&
               NetIos(wh322244Extract, "NUT01").SequenceEqual([34, 35]),
            "WH322244 extracted networks are grouped by string WireName only");

        const string multiPartText =
            "파트번호\t파트명\tECO\tNCO\tALC\n" +
            "PART-A\tPRODUCT A\tECO-A\tNCO-A\tALC-A\n" +
            "PART-B\tPRODUCT B\tECO-B\tNCO-B\tALC-B\n\n" +
            "번 호\t커넥터\t핀 수\n" +
            "1\tCN1\t2\n\n" +
            "커넥터\t선이름\tI/O\t핀번호\n" +
            "CN1\tW1\t1\t1\n" +
            "CN1\tW1\t2\t2\n\n" +
            "선이름\t선연결\t굵기\t색깔\n" +
            "W1\t\t0.5\tR";
        string multiPartRoot = Path.Combine(
            Path.GetTempPath(), "JBZMultiPartThtTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(multiPartRoot);
        try
        {
            string multiPartPath = Path.Combine(multiPartRoot, "multi-part.tht");
            File.WriteAllBytes(multiPartPath, BuildMinimalThtFile(multiPartText));
            IReadOnlyList<ProductModel> parts = new ThtModelParser().LoadAll(multiPartPath);
            Assert(parts.Count == 2 &&
                   parts[0].PartNumber == "PART-A" && parts[0].ProductName == "PRODUCT A" &&
                   parts[1].PartNumber == "PART-B" && parts[1].ProductName == "PRODUCT B" &&
                   parts.All(part => part.Nets.Single().IoNumbers.SequenceEqual([1, 2])) &&
                   PartSelectionWindow.PartKey(parts[0]) != PartSelectionWindow.PartKey(parts[1]),
                "Multi-Part THT returns one explicit candidate per Part row without inferring topology conversion");
        }
        finally
        {
            Directory.Delete(multiPartRoot, recursive: true);
        }
    }

#endif
    private static void TestRelayOrdering()
    {
        using TestEngine engine = CreateEngine(out FakeBoard board);
        engine.SetModel(Model(("PAIR", new[] { 1, 18 })));
        ScanFrame passFrame = Frame((1, new[] { 18 }));
        engine.ProcessFrame(passFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        engine.ProcessFrame(passFrame);
        bool ok = engine.CompletePassAsync([]).GetAwaiter().GetResult();
        Assert(ok, "PASS relay workflow accepted");
        Assert(board.Commands.Count(command => command == "SET:4") == 1, "PASS physical Relay 5 / OUT4 exactly once");
        Assert(board.Commands.Count(command => command == "SET:0") == 1, "PASS physical Relay 1 / OUT0 exactly once");
        Assert(board.Commands.IndexOf("SET:4") < board.Commands.IndexOf("SET:0"), "PASS Relay 5 marking before Relay 1 jig");
        Assert(board.Commands.Skip(board.Commands.IndexOf("SET:4") + 1).TakeWhile(c => c != "SET:0").Contains("OFF"), "Relay 5 OFF before Relay 1 ON");

        board.Commands.Clear();
        engine.EjectFaultProductAsync().GetAwaiter().GetResult();
        Assert(board.Commands.Count(command => command == "SET:0") == 1, "FAIL Relay 1 / OUT0 exactly once");
        Assert(!board.Commands.Contains("SET:4"), "FAIL never drives Relay 5 marking");
        Assert(board.Commands.Last() == "OFF", "FAIL ends OFF");

        using TestEngine latchedLeakEngine = CreateEngine(out FakeBoard latchedLeakBoard);
        latchedLeakEngine.SetModel(Model(("LEAK-PAIR", new[] { 1, 18 })));
        latchedLeakEngine.ProcessFrame(passFrame);
        latchedLeakEngine.ProcessFrame(Frame());
        Assert(!latchedLeakEngine.ContinuityPassed,
            "Trailing empty STOP frame can clear live continuity after Leak starts");
        bool latchedLeakPass = latchedLeakEngine.CompletePassAsync(
            [],
            continuityAlreadyValidated: true).GetAwaiter().GetResult();
        Assert(latchedLeakPass && latchedLeakBoard.Commands.Contains("SET:0"),
            "Leak PASS uses the pre-Leak validated continuity latch instead of becoming a false product FAIL");

        board.Commands.Clear();
        using (var cts = new CancellationTokenSource(10))
        {
            bool canceled = false;
            try
            {
                engine.PulseJigRelayAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert(canceled, "Relay pulse cancellation is observed");
            Assert(board.Commands.Contains("SET:0") && board.Commands.Last() == "OFF",
                "Relay cancellation still ends with safe OFF");
        }

        board.Commands.Clear();
        board.ThrowOnSetRelay = true;
        bool failedSet = false;
        try
        {
            engine.PulseJigRelayAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            failedSet = true;
        }
        finally
        {
            board.ThrowOnSetRelay = false;
        }

        Assert(failedSet && board.Commands.Last() == "OFF",
            "Relay set exception still forces safe OFF");

        var noMarkingProduction = new ProductionSettings
        {
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50,
            PassMarkingToJigDelayMs = 0,
            ProductSettleTimeMs = 0,
            PassMarkingRelayEnabled = false
        };
        using TestEngine noMarkingEngine = CreateEngine(out FakeBoard noMarkingBoard, noMarkingProduction);
        noMarkingEngine.SetModel(Model(("PAIR", new[] { 1, 18 })));
        noMarkingEngine.ProcessFrame(passFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        noMarkingEngine.ProcessFrame(passFrame);
        bool noMarkingOk = noMarkingEngine.CompletePassAsync([]).GetAwaiter().GetResult();
        Assert(noMarkingOk, "PASS relay workflow accepts disabled marking option");
        Assert(!noMarkingBoard.Commands.Contains("SET:4") &&
               noMarkingBoard.Commands.Count(command => command == "SET:0") == 1,
            "Disabled PASS marking skips R2 and still opens JIG once");

        var reversedFaultJigProduction = new ProductionSettings
        {
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50,
            JigRelayChannel = 4,
            MarkingRelayChannel = 0,
            JigEjectRelayEnabled = true,
            PassMarkingRelayEnabled = true
        };
        using TestEngine reversedFaultJigEngine = CreateEngine(
            out FakeBoard reversedFaultJigBoard,
            reversedFaultJigProduction);
        reversedFaultJigEngine.EjectFaultProductAsync().GetAwaiter().GetResult();
        Assert(reversedFaultJigBoard.Commands.Count(command => command == "SET:0") == 1 &&
               !reversedFaultJigBoard.Commands.Contains("SET:4") &&
               reversedFaultJigBoard.Commands.Last() == "OFF",
            "FAIL confirmation always uses fixed physical Relay 1 / OUT0 even if legacy cfg contains another mapping");
        reversedFaultJigBoard.Commands.Clear();
        reversedFaultJigEngine.EjectMasterSampleAsync().GetAwaiter().GetResult();
        Assert(reversedFaultJigBoard.Commands.Count(command => command == "SET:0") == 1 &&
               !reversedFaultJigBoard.Commands.Contains("SET:4"),
            "Master eject always uses fixed physical Relay 1 / OUT0");

        var reversedRelayProduction = new ProductionSettings
        {
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50,
            PassMarkingToJigDelayMs = 0,
            ProductSettleTimeMs = 0,
            JigRelayChannel = 4,
            MarkingRelayChannel = 0
        };
        using TestEngine reversedRelayEngine = CreateEngine(out FakeBoard reversedRelayBoard, reversedRelayProduction);
        reversedRelayEngine.SetModel(Model(("PAIR", new[] { 1, 18 })));
        reversedRelayEngine.ProcessFrame(passFrame);
        Thread.Sleep(ProductionTimingPolicy.DefaultProductSettleTimeMs + 5);
        reversedRelayEngine.ProcessFrame(passFrame);
        bool reversedRelayOk = reversedRelayEngine.CompletePassAsync([]).GetAwaiter().GetResult();
        Assert(reversedRelayOk, "PASS relay workflow ignores obsolete configurable OUTPUT mapping");
        Assert(reversedRelayBoard.Commands.IndexOf("SET:4") >= 0 &&
               reversedRelayBoard.Commands.IndexOf("SET:0") >= 0 &&
               reversedRelayBoard.Commands.IndexOf("SET:4") < reversedRelayBoard.Commands.IndexOf("SET:0"),
            "PASS always uses fixed Relay 5 / OUT4 marking before Relay 1 / OUT0 jig");
    }

    private static void TestHistory()
    {
        Assert(
            ProgramIdentityService.BuildHtdrvName() ==
            $"JBZUniveresalLunix V{ProgramIdentityService.VersionText}",
            "HtdrvName is exactly the current software name and release version");

        string root = Path.Combine(Path.GetTempPath(), "JBZSelfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime finished = new(2026, 8, 9, 14, 7, 8, DateTimeKind.Local);
            var record = new TestHistoryRecord
            {
                Started = finished.AddSeconds(-5),
                Finished = finished,
                InstallStartedAt = finished.AddSeconds(-5),
                TestStartedAt = finished.AddSeconds(-3),
                ResultAt = finished,
                RemovalStartedAt = null,
                RemovedAt = null,
                InspectionType = HistoryInspectionType.Product,
                PartName = "PRODUCT",
                PartNumber = "NI375C1000",
                VehicleType = "NE N EV",
                Eco = "NE N EV",
                Alc = "NI375/C1000",
                LotNo = 2001,
                ProductionCounter = 321,
                Result = "PASS",
                Passed = true,
                ModelName = "MODEL-A",
                ModelFile = @"C:\Models\A.tht",
                HtdrvName = "JBZUniveresalLunix V15.2.0",
                LotText = "VOLVO Radio",
                InspectionTrace =
                    "14:07:08 회로검사:PASS 14:07:08~14:07:08 저항검사 [CH1: 100 Ω < 101.5 Ω < 110 Ω :PASS]",
                OpenCount = 0,
                BarcodeValue = "NI375C10002608092001",
                LabelProfile = "KS91",
                LabelTemplateType = LabelSettings.LargeTemplate,
                LabelPayload = "N\r\nNI375C10002608092001\r\nP1\r\n",
                PrintStatus = LabelPrintStatus.Printed.ToString(),
                CycleId = "history-cycle",
                Resistance = "CH1=101.5 Ω(PASS)",
                MeasuredResistance = 101.5,
                ResistanceMin = 100,
                ResistanceMax = 110
            };

            var store = new TestHistoryStore(Path.Combine(root, "history.db"));
            store.UpsertActiveCycle(
                record.CycleId,
                record.PartNumber,
                record.ModelFile,
                record.Started,
                "TEST_STARTED");
            Assert(CountActiveCycles(store.DatabasePath, record.CycleId) == 1,
                "Active production cycle is persisted before final PASS/FAIL commit");
            store.Add(record);
            Assert(CountActiveCycles(store.DatabasePath, record.CycleId) == 0,
                "Final result atomically clears its active cycle");
            store.UpsertActiveCycle(
                record.CycleId,
                record.PartNumber,
                record.ModelFile,
                record.Started,
                "RETRY");
            store.Add(record);
            Assert(CountActiveCycles(store.DatabasePath, record.CycleId) == 0,
                "Idempotent duplicate commit also clears a recreated active-cycle row");
            DateTime removalStarted = finished.AddMilliseconds(250);
            DateTime removedAt = removalStarted.AddSeconds(2);
            Assert(store.UpdateRemovalTiming(record.CycleId, removalStarted, null),
                "History removal start updates the immutable cycle row");
            Assert(store.UpdateRemovalTiming(record.CycleId, removalStarted.AddSeconds(1), removedAt),
                "History removal completion updates the immutable cycle row");
            IReadOnlyList<TestHistoryRecord> found = store.Search(new HistorySearchCriteria(
                finished.Date, finished.Date.AddDays(1), 2001, "NI375", "PASS"));
            Assert(found.Count == 1 && found[0].PartNumber == "NI375C1000" &&
                   found[0].VehicleType == "NE N EV" && found[0].ProductionCounter == 321 &&
                   found[0].LabelTemplateType == LabelSettings.LargeTemplate &&
                   found[0].InspectionType == HistoryInspectionType.Product &&
                   found[0].InstallDurationSeconds == 2 &&
                   found[0].TestDurationSeconds == 3 &&
                   found[0].RemovalDurationSeconds == 2 &&
                   found[0].RemovalStartedAt == removalStarted &&
                   found[0].RemovedAt == removedAt &&
                   found[0].LotText == "VOLVO Radio" &&
                   found[0].InspectionTrace.Contains("저항검사", StringComparison.Ordinal) &&
                   found[0].LabelPayload == record.LabelPayload,
                "SQLite search preserves 14-column values, phase trace and immutable print payload");

            Assert(found[0].DateText == "2026/08/09" &&
                   found[0].TimeText == "14:07:05" &&
                   found[0].EffectiveResultAt == finished,
                "History Ngày/Thời gian must display TEST START time, not later ResultAt time");

            string csv = Path.Combine(root, "history.csv");
            HistoryExportService.ExportCsv(csv, found);
            byte[] csvBytes = File.ReadAllBytes(csv);
            Assert(csvBytes.Length >= 3 && !(csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF),
                "ALL13 CSV uses CP949 without UTF-8 BOM");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string csvText = File.ReadAllText(csv, Encoding.GetEncoding(949));
            Assert(csvText.StartsWith(
                    "일 자,시 간,파 일,품 명,품 번,차 종,Lot,결 과,순 번,검 사 기 록,바코드,200 %,수입검사,프로그램\n",
                    StringComparison.Ordinal),
                "History CSV preserves the exact original 14-column Korean header");
            Assert(csvText.Contains(
                    "2026-08-09,14:07:05,A.tht,PRODUCT,NI375C1000,NE N EV,VOLVO Radio,합격,2001",
                    StringComparison.Ordinal) &&
                   csvText.Contains("장착 14:07:03~14:07:05(2.000초) 14:07:05 검사시작", StringComparison.Ordinal) &&
                   csvText.Contains("저항검사 [CH1: 100 Ω < 101.5 Ω < 110 Ω :PASS]", StringComparison.Ordinal) &&
                   csvText.Contains("탈거 14:07:08~14:07:10(2.000초)", StringComparison.Ordinal) &&
                   csvText.Contains(",NI375C10002608092001,,,JBZUniveresalLunix V15.2.0", StringComparison.Ordinal) &&
                   !csvText.Contains("N\r\nNI375C10002608092001", StringComparison.Ordinal),
                "Sample history CSV keeps three test phases, sequence and barcode without raw EPL payload");

            string xlsx = Path.Combine(root, "history.xlsx");
            HistoryExportService.ExportXlsx(xlsx, found);
            using ZipArchive archive = ZipFile.OpenRead(xlsx);
            string sheet = ReadEntry(archive, "xl/worksheets/sheet1.xml");
            string styles = ReadEntry(archive, "xl/styles.xml");
            Assert(sheet.Contains("<c r=\"A2\" s=\"2\"><v>", StringComparison.Ordinal) &&
                   sheet.Contains("<c r=\"B2\" s=\"4\"><v>", StringComparison.Ordinal),
                "XLSX date and time use separate native numeric cells");
            Assert(sheet.Contains("<c r=\"I2\"><v>2001</v></c>", StringComparison.Ordinal),
                "XLSX sequence uses the PASS LOT number");
            Assert(sheet.Contains("바코드", StringComparison.Ordinal) &&
                   sheet.Contains("<c r=\"K2\" t=\"inlineStr\"><is><t xml:space=\"preserve\">NI375C10002608092001</t>", StringComparison.Ordinal) &&
                   !sheet.Contains("N&#xD;", StringComparison.Ordinal) &&
                   sheet.Contains("autoFilter ref=\"A1:N2\"", StringComparison.Ordinal),
                "XLSX preserves Korean headers, barcode value and 14-column filter");
            Assert(styles.Contains("numFmtId=\"164\"", StringComparison.Ordinal) &&
                   styles.Contains("numFmtId=\"165\"", StringComparison.Ordinal) &&
                   styles.Contains("wrapText=\"1\"", StringComparison.Ordinal),
                "XLSX date, time and wrapped test-log styles");

            string historyXaml = File.ReadAllText(
                Path.Combine(Environment.CurrentDirectory, "Views", "HistoryPage.xaml"));
            string[] sampleHeaders =
            [
                "Ngày", "Thời gian", "File", "Tên sản phẩm", "Mã hàng", "Loại xe", "LOT",
                "Kết quả", "Số thứ tự", "Lịch sử kiểm tra", "Mã vạch", "200 %",
                "Kiểm tra đầu vào", "Chương trình"
            ];
            int previousHeader = -1;
            foreach (string header in sampleHeaders)
            {
                int headerIndex = historyXaml.IndexOf($"Header=\"{header}\"", previousHeader + 1, StringComparison.Ordinal);
                Assert(headerIndex > previousHeader, $"History UI Vietnamese 14-column order: {header}");
                previousHeader = headerIndex;
            }
            Assert(historyXaml.Contains("CanUserResizeColumns=\"False\"", StringComparison.Ordinal) &&
                   historyXaml.Contains("CanUserReorderColumns=\"False\"", StringComparison.Ordinal) &&
                   historyXaml.Contains("CanUserSortColumns=\"False\"", StringComparison.Ordinal) &&
                   historyXaml.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Visible\"", StringComparison.Ordinal) &&
                   historyXaml.Contains("Header=\"Lịch sử kiểm tra\" Width=\"1200\"", StringComparison.Ordinal),
                "History UI locks column layout and keeps the inspection record widest with horizontal scrolling");

            var failed = new TestHistoryRecord
            {
                Finished = finished,
                Passed = false,
                ProductionCounter = 2001,
                Result = "FAIL",
                FaultCode = "OPEN_CIRCUIT",
                FaultType = "DÂY CHƯA KẾT NỐI",
                FaultDetailsJson = JsonSerializer.Serialize(new[]
                {
                    new FaultDetail
                    {
                        Type = ProductFaultType.OpenCircuit,
                        ConnectorFrom = "CN1",
                        PinFrom = "4",
                        ConnectorTo = "CN3",
                        PinTo = "6",
                        WireColor = "WHITE"
                    }
                })
            };
            string customerCsv = Path.Combine(root, "customer-fault.csv");
            HistoryExportService.ExportCsv(customerCsv, [failed]);
            string customerText = File.ReadAllText(customerCsv, Encoding.GetEncoding(949));
            Assert(failed.ExportSequenceNo is null &&
                   customerText.Contains(",불량,,", StringComparison.Ordinal) &&
                   customerText.Contains("단선 CN1-4↔CN3-6", StringComparison.Ordinal) &&
                   !customerText.Contains("OPEN CIRCUIT", StringComparison.Ordinal),
                "History CSV/UI FAIL uses concise Korean fault detail and blank sequence");

            var masterBad = new TestHistoryRecord
            {
                Started = finished,
                Finished = finished.AddSeconds(1),
                InstallStartedAt = finished,
                TestStartedAt = finished.AddMilliseconds(250),
                ResultAt = finished.AddSeconds(1),
                InspectionType = HistoryInspectionType.MasterBad,
                Passed = true,
                InspectionTrace = "14:07:09 회로검사:FAIL",
                FaultDetailsJson = failed.FaultDetailsJson
            };
            Assert(masterBad.IsMasterRecord &&
                   masterBad.ExportAcceptedLotNo is null &&
                   masterBad.ExportResultText == "합격" &&
                   masterBad.ExportBarcodeText.Length == 0 &&
                   masterBad.ExportTestLogText.Contains("회로검사:FAIL", StringComparison.Ordinal) &&
                   masterBad.ExportTestLogText.Contains("단선 CN1-4↔CN3-6", StringComparison.Ordinal),
                "MASTER BAD history is separate from production and preserves Korean fault evidence");

            var leakRetestModel = new ProductModel
            {
                ModelName = "LEAK-RETEST-MODEL",
                PartNumber = "LEAK-RETEST-PART",
                ProductName = "LEAK RETEST PRODUCT",
                SourcePath = Path.Combine(root, "LEAK-RETEST.tht")
            };
            var leakRetestRecord = new TestHistoryRecord
            {
                Started = finished.AddMinutes(1),
                Finished = finished.AddMinutes(1).AddSeconds(3),
                TestStartedAt = finished.AddMinutes(1),
                ResultAt = finished.AddMinutes(1).AddSeconds(3),
                InspectionType = HistoryInspectionType.LeakRetest,
                PartName = leakRetestModel.ProductName,
                PartNumber = leakRetestModel.PartNumber,
                Result = "FAIL",
                Passed = false,
                ModelName = leakRetestModel.ModelName,
                ModelFile = leakRetestModel.SourcePath,
                LotNo = 0,
                LotText = "LOT GỐC",
                CycleId = "history-cycle-leak-retest-0001",
                InspectionTrace = "14:08:08~14:08:11 LEAK RETEST #1 [CH1/CN1: 84→83 Δ1≤0.5:FAIL]",
                FaultType = FaultTypeCatalog.DisplayName(ProductFaultType.WaterProofLeak),
                FaultCode = FaultTypeCatalog.Code(ProductFaultType.WaterProofLeak),
                BarcodeValue = "MUST-NOT-EXPORT",
                PrintStatus = LabelPrintStatus.Printed.ToString()
            };
            var leakRetestFault = new FaultDetail
            {
                Type = ProductFaultType.WaterProofLeak,
                WireName = "CH1 • CN1",
                Message = "Leak retest FAIL"
            };
            leakRetestRecord.FaultDetailsJson = JsonSerializer.Serialize(new[] { leakRetestFault });
            var leakRetestMeasurement = new WaterProofChannelMeasurement(1, true, 84, 83, 1, false);
            ProductionResultCommitRequest leakRetestRequest = ProductionResultCommitRequest.Capture(
                leakRetestRecord,
                leakRetestModel,
                new ProductionSettings(),
                [leakRetestFault],
                [],
                [leakRetestMeasurement],
                ProgramIdentityService.VersionText);
            Assert(!leakRetestRequest.UpdateProductionTotals &&
                   leakRetestRequest.WaterProof.Count == 1 &&
                   !leakRetestRecord.IsProductionRecord &&
                   !leakRetestRecord.IsMasterRecord &&
                   leakRetestRecord.InspectionTypeText == "LEAK RETEST" &&
                   leakRetestRecord.ExportAcceptedLotNo is null &&
                   leakRetestRecord.ExportBarcodeText.Length == 0,
                "Leak retest is a visible history type but never becomes a production LOT/barcode result");
            store.CommitResult(leakRetestRequest, null);
            IReadOnlyList<TestHistoryRecord> leakRetestRows = store.Search(new HistorySearchCriteria(
                finished.Date,
                finished.Date.AddDays(1),
                null,
                leakRetestModel.PartNumber,
                "FAIL",
                InspectionType: HistoryInspectionType.LeakRetest));
            ProductionStatisticsSnapshot leakRetestStats = store.GetStatistics(
                PartIdentitySnapshot.Capture(leakRetestModel),
                finished);
            Assert(leakRetestRows.Count == 1 &&
                   leakRetestRows[0].InspectionType == HistoryInspectionType.LeakRetest &&
                   leakRetestRows[0].Passed == false &&
                   leakRetestStats.DailyTotal == 0 &&
                   leakRetestStats.LifetimeTotal == 0,
                "Leak retest PASS/FAIL persists in history without increasing product statistics");

            var normalProduct = new TestHistoryRecord
            {
                Started = finished,
                Finished = finished.AddSeconds(1),
                TestStartedAt = finished,
                ResultAt = finished.AddSeconds(1),
                Passed = true,
                InspectionTrace = "14:07:09 회로검사:PASS"
            };
            var leakProduct = new TestHistoryRecord
            {
                Started = finished,
                Finished = finished.AddSeconds(6),
                TestStartedAt = finished,
                ResultAt = finished.AddSeconds(6),
                Passed = true,
                InspectionTrace =
                    "14:07:09 회로검사:PASS 14:07:10~14:07:14 기밀검사 " +
                    "[CH1/CN1: 92.3→92 Δ0.3≤20:PASS]"
            };
            Assert(normalProduct.ExportTestLogText.Contains("회로검사:PASS", StringComparison.Ordinal) &&
                   !normalProduct.ExportTestLogText.Contains("저항검사", StringComparison.Ordinal) &&
                   !normalProduct.ExportTestLogText.Contains("기밀검사", StringComparison.Ordinal) &&
                   record.ExportTestLogText.Contains("저항검사", StringComparison.Ordinal) &&
                   leakProduct.ExportTestLogText.Contains("기밀검사", StringComparison.Ordinal) &&
                   leakProduct.ExportTestLogText.Contains("CH1/CN1", StringComparison.Ordinal),
                "Normal, resistance and Leak products share one concise Korean inspection-record column");

            var wrongWiring = new FaultDetail
            {
                Type = ProductFaultType.WrongWiring,
                WireName = "W1",
                ConnectorFrom = "CN1",
                PinFrom = "03",
                ConnectorTo = "CN2",
                PinTo = "07",
                ActualConnectorFrom = "CN1",
                ActualPinFrom = "03",
                ActualConnectorTo = "CN3",
                ActualPinTo = "02"
            };
            Assert(
                KoreanHistoryFormatter.FormatFault(wrongWiring) ==
                "오배선 W1 정상 CN1-03↔CN2-07 실제 CN1-03↔CN3-02",
                "Wrong-wiring history names the expected and actual connector/pin in concise Korean");

            var resistanceFault = new FaultDetail
            {
                Type = ProductFaultType.ResistanceOutOfRange,
                WireName = "R-CH1",
                MeasuredResistance = 115.25,
                ResistanceMin = 100,
                ResistanceMax = 110
            };
            Assert(
                KoreanHistoryFormatter.FormatFault(resistanceFault) ==
                "저항불량 R-CH1 115.25Ω 기준 100~110Ω",
                "Resistance history keeps measured value and limits in concise Korean");

            var exportStore = new TestHistoryStore(Path.Combine(root, "history-export.db"));
            DateTime monthStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);
            exportStore.Add(new TestHistoryRecord
            {
                Started = monthStart.AddDays(2),
                Finished = monthStart.AddDays(2).AddSeconds(1),
                PartNumber = "PART-A",
                ModelFile = @"D:\Models\C.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "export-part-a"
            });
            exportStore.Add(new TestHistoryRecord
            {
                Started = monthStart.AddDays(1),
                Finished = monthStart.AddDays(1).AddSeconds(1),
                PartNumber = "PART-Z",
                ModelFile = @"D:\Models\B.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "export-part-z-b"
            });
            exportStore.Add(new TestHistoryRecord
            {
                Started = monthStart,
                Finished = monthStart.AddSeconds(1),
                PartNumber = "PART-Z",
                ModelFile = @"D:\Models\A.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "export-part-z-a"
            });

            var monthlyCriteria = new HistorySearchCriteria(
                monthStart,
                monthStart.AddMonths(1).AddTicks(-1),
                null,
                string.Empty,
                "ALL",
                MaxRows: 1);
            IReadOnlyList<TestHistoryRecord> limitedRows = exportStore.Search(monthlyCriteria);
            IReadOnlyList<TestHistoryRecord> allExportRows = exportStore.SearchForExport(monthlyCriteria);
            Assert(limitedRows.Count == 1 && allExportRows.Count == 3,
                "History export is independent from the DataGrid row limit");
            Assert(allExportRows[0].CycleId == "export-part-z-a" &&
                   allExportRows[1].CycleId == "export-part-z-b" &&
                   allExportRows[2].CycleId == "export-part-a",
                "History export sorts oldest-to-newest by displayed test-start time and stable Id");
            Assert(allExportRows[0].ExportModelFileName == "A.tht" &&
                   allExportRows[1].ExportModelFileName == "B.tht",
                "Changing A.tht to B.tht snapshots B.tht only for the new cycle");

            // Regression: row cũ bắt đầu trước nhưng test lâu hơn nên ResultAt muộn hơn.
            // History phải vẫn sắp theo giờ BẮT ĐẦU TEST đang hiển thị, không theo ResultAt.
            var orderingStore = new TestHistoryStore(Path.Combine(root, "history-ordering.db"));
            DateTime orderBase = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Local);
            orderingStore.Add(new TestHistoryRecord
            {
                Started = orderBase,
                TestStartedAt = orderBase,
                ResultAt = orderBase.AddMinutes(5),
                Finished = orderBase.AddMinutes(5),
                PartNumber = "ORDER-TEST",
                ModelFile = @"D:\Models\ORDER.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "older-start-later-result"
            });
            orderingStore.Add(new TestHistoryRecord
            {
                Started = orderBase.AddMinutes(1),
                TestStartedAt = orderBase.AddMinutes(1),
                ResultAt = orderBase.AddMinutes(2),
                Finished = orderBase.AddMinutes(2),
                PartNumber = "ORDER-TEST",
                ModelFile = @"D:\Models\ORDER.tht",
                Result = "PASS",
                Passed = true,
                CycleId = "newer-start-earlier-result"
            });

            IReadOnlyList<TestHistoryRecord> orderedRows = orderingStore.SearchSummary(
                new HistorySearchCriteria(
                    orderBase.Date,
                    orderBase.Date.AddDays(1),
                    null,
                    "ORDER-TEST",
                    "ALL",
                    MaxRows: 10));
            Assert(orderedRows.Count == 2 &&
                   orderedRows[0].CycleId == "older-start-later-result" &&
                   orderedRows[0].TimeText == "08:00:00" &&
                   orderedRows[1].CycleId == "newer-start-earlier-result" &&
                   orderedRows[1].TimeText == "08:01:00",
                "History grid is strictly oldest-to-newest by displayed TEST START time even when ResultAt order differs");

            IReadOnlyList<TestHistoryRecord> orderPage1 = orderingStore.SearchSummary(
                new HistorySearchCriteria(
                    orderBase.Date,
                    orderBase.Date.AddDays(1),
                    null,
                    "ORDER-TEST",
                    "ALL",
                    MaxRows: 1));
            IReadOnlyList<TestHistoryRecord> orderPage2 = orderingStore.SearchSummary(
                new HistorySearchCriteria(
                    orderBase.Date,
                    orderBase.Date.AddDays(1),
                    null,
                    "ORDER-TEST",
                    "ALL",
                    MaxRows: 1,
                    AfterHistoryAt: orderPage1[0].EffectiveTestStartedAt,
                    AfterId: orderPage1[0].Id));
            Assert(orderPage1.Count == 1 &&
                   orderPage2.Count == 1 &&
                   orderPage1[0].CycleId == "older-start-later-result" &&
                   orderPage2[0].CycleId == "newer-start-earlier-result",
                "History keyset pagination preserves oldest-to-newest TEST START order across pages");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestLabel()
    {
        var data = new LabelPrintData("PRODUCT", "NI375C1000", "NE N EV", "", "NI375/C1000", 2001,
            new DateTime(2024, 7, 15, 14, 7, 8));
        string epl = EplLabelService.BuildPassLabel(data, new LabelSettings());
        int part = epl.IndexOf("NI375C1000", StringComparison.Ordinal);
        int eco = epl.IndexOf("NE N EV", part + 1, StringComparison.Ordinal);
        int name = epl.IndexOf("PRODUCT", eco + 1, StringComparison.Ordinal);
        int serial = epl.IndexOf("2407152001WH", name + 1, StringComparison.Ordinal);
        int barcode = epl.IndexOf("NI375C10002407152001", serial + 1, StringComparison.Ordinal);
        Assert(part >= 0 && part < eco && eco < name && name < serial && serial < barcode, "ALL6 EPL value order");
    }

#if LEGACY_D2XX
    private static void TestThtLabelAndLotLifecycle()
    {
        var unconfiguredPrinter = new LabelPrintService();
        LabelPrinterConnectionResult unconfiguredConnection = unconfiguredPrinter
            .ConnectAsync(new LabelSettings())
            .GetAwaiter()
            .GetResult();
        Assert(!unconfiguredConnection.Connected &&
               unconfiguredConnection.Message.Contains("Chưa chọn cổng COM", StringComparison.Ordinal),
            "Printer connect requires an explicitly configured saved COM port");
        unconfiguredPrinter.DisposeAsync().AsTask().GetAwaiter().GetResult();

        DateTime testedAt = new(2026, 8, 22, 10, 15, 30, DateTimeKind.Local);
        var data = new LabelPrintData(
            "VOLTAGE_6S",
            "BE331-G2000",
            "AE EV PE",
            "NCO-7",
            "SQDZQ7V7001",
            2044,
            testedAt,
            "AE EV PE",
            "SQDZQ7V7001",
            "cycle-a");
        const string template = "FR\"KS91\"\n?\n{PART_NUMBER}\n{PRODUCT_NAME}\n{VEHICLE_TYPE}\n{CUSTOMER_CODE}\n{LOT_NO}\n{TEST_DATE}\n{TEST_TIME}\n{CYCLE_ID}\nP1";
        string rendered = LabelTemplateRenderer.Render(template, data);
        Assert(rendered == "FR\"KS91\"\n?\nBE331-G2000\nVOLTAGE_6S\nAE EV PE\nSQDZQ7V7001\n2044\n20260822\n101530\ncycle-a\nP1",
            "Template variables render from immutable THT product snapshot");

        var resolverModel = new ProductModel
        {
            PartNumber = data.PartNumber,
            ProductName = data.PartName,
            Eco = data.Eco,
            VehicleType = data.VehicleType,
            CustomerCode = data.CustomerCode,
            LabelVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FACTORY"] = "WH",
                ["LINE"] = "KS91",
                ["PART_NUMBER"] = "MUST-NOT-OVERRIDE-MODEL"
            }
        };
        var resolverSettings = new LabelSettings { FormatName = "KS91" };
        IReadOnlyDictionary<string, string> resolved =
            LabelVariableResolver.Resolve(resolverModel, data, resolverSettings);
        string dynamicRendered = LabelTemplateRenderer.Render(
            "{part_number}|{FACTORY}|{LINE}|{DATE_YYMMDD}|{DATE_YYYYMMDD}|{TIME_HHMMSS}|$DATA$|$LOTNO$|$PARTNO|$PARTNAME",
            resolved,
            resolverModel.PartNumber,
            resolverSettings.FormatName);
        Assert(dynamicRendered ==
               "MUST-NOT-OVERRIDE-MODEL|WH|KS91|260822|20260822|101530|260822|2044|MUST-NOT-OVERRIDE-MODEL|VOLTAGE_6S",
            "Canonical, arbitrary THT, runtime date and verified legacy aliases render case-insensitively");
        AssertThrows<InvalidDataException>(
            () => LabelTemplateRenderer.Render(
                "N\n{ABC_NOT_DEFINED}\n$INTERFACE$\nP1",
                resolved,
                resolverModel.PartNumber,
                resolverSettings.FormatName),
            "Unknown canonical/SendSerial variables block printing before transport");

        resolverModel.LabelVariables["INTERFACE"] = "IF-A";
        string productA = LabelTemplateRenderer.Render(
            "{PART_NUMBER}|{INTERFACE}",
            LabelVariableResolver.Resolve(resolverModel, data, resolverSettings));
        var productBModel = new ProductModel
        {
            PartNumber = "PART-B",
            ProductName = "PRODUCT-B",
            LabelVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["INTERFACE"] = "IF-B"
            }
        };
        LabelPrintData productBData = data with { PartNumber = "PART-B", PartName = "PRODUCT-B" };
        string productB = LabelTemplateRenderer.Render(
            "{PART_NUMBER}|{INTERFACE}",
            LabelVariableResolver.Resolve(productBModel, productBData, resolverSettings));
        Assert(productA == "MUST-NOT-OVERRIDE-MODEL|IF-A" && productB == "PART-B|IF-B" && productA != productB,
            "Two THT-derived product snapshots render different labels without product-specific code");

        DateTime lotClock = new(2026, 8, 29, 8, 0, 0);
        var settings = new ProductionSettings { LotNo = 2044, LotNoDate = "2026-08-29" };
        int persistCount = 0;
        var lots = new LotSequenceService(settings, _ => persistCount++, () => lotClock);
        long cycleA = lots.ReserveForCycle("cycle-a");
        Assert(cycleA == 2044 && lots.ReserveForCycle("cycle-a") == 2044,
            "Duplicate PASS callback keeps the same reserved LOT");
        Assert(lots.NextLot == 2044 && persistCount == 0,
            "Reservation/printer failure does not advance persisted LOT");
        Assert(lots.TryCommitSuccessfulPrint("cycle-a", 2044, out string errorA) && errorA.Length == 0,
            "Successful print commits reserved LOT");
        Assert(lots.NextLot == 2045 && persistCount == 1,
            "Successful print advances and persists next LOT exactly once");

        long cycleB = lots.ReserveForCycle("cycle-b");
        Assert(cycleB == 2045 && lots.NextLot == 2045,
            "Next PASS receives the next LOT without early commit");
        Assert(!lots.TryCommitSuccessfulPrint("cycle-b", 2046, out _),
            "Mismatched LOT cannot commit");
        Assert(lots.NextLot == 2045,
            "Failed/mismatched print leaves next LOT unchanged for retry");

        var restartedSettings = new ProductionSettings
        {
            LotNo = settings.LotNo,
            LotNoDate = settings.LotNoDate,
            LotSettingsByProduct = settings.LotSettingsByProduct.ToDictionary(
                pair => pair.Key,
                pair => new ProductLotSettings
                {
                    StartLotNo = pair.Value.StartLotNo,
                    LotNo = pair.Value.LotNo,
                    LotNoDate = pair.Value.LotNoDate
                },
                StringComparer.OrdinalIgnoreCase)
        };
        var restarted = new LotSequenceService(restartedSettings, _ => { }, () => lotClock);
        Assert(restarted.NextLot == 2045,
            "Restart resumes from last successfully committed LOT");

        lotClock = lotClock.AddDays(1);
        Assert(restarted.StartLot == 2044 &&
               restarted.NextLot == 2044 &&
               restartedSettings.LotNoDate == "2026-08-30",
            "New production day resets next LOT to the per-product starting LOT");

        var migratedSettings = new ProductionSettings { LotNo = 9876 };
        int migrationPersistCount = 0;
        var migratedLots = new LotSequenceService(
            migratedSettings,
            _ => migrationPersistCount++,
            () => lotClock);
        Assert(
            migratedLots.NextLot == 9876 &&
            migratedSettings.LotNoDate == "2026-08-30" &&
            migrationPersistCount == 0,
            "First upgrade stamps LOTNO date in memory without constructor disk I/O or discarding the current sequence");

        var perProductSettings = new ProductionSettings
        {
            LotNoDate = "2026-08-30",
            LotSettingsByProduct = new Dictionary<string, ProductLotSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["PART-2000"] = new() { StartLotNo = 2000, LotNo = 2000, LotNoDate = "2026-08-30" },
                ["PART-7000"] = new() { StartLotNo = 7000, LotNo = 7000, LotNoDate = "2026-08-30" },
                ["PART-5000"] = new() { StartLotNo = 5000, LotNo = 5000, LotNoDate = "2026-08-30" }
            }
        };
        var perProductLots = new LotSequenceService(perProductSettings, _ => { }, () => lotClock);
        perProductLots.SelectProduct("PART-2000", migrateCurrentLotIfMissing: false);
        Assert(perProductLots.NextLot == 2000, "PART-2000 loads its own starting LOT");
        Assert(perProductLots.TryCommitSuccessfulPrint(
                "part-2000-cycle",
                perProductLots.ReserveForCycle("part-2000-cycle"),
                out _),
            "PART-2000 commits its own LOT");
        perProductLots.SelectProduct("PART-7000", migrateCurrentLotIfMissing: false);
        Assert(perProductLots.NextLot == 7000, "PART-7000 loads its own starting LOT");
        perProductLots.SelectProduct("PART-5000", migrateCurrentLotIfMissing: false);
        Assert(perProductLots.NextLot == 5000, "PART-5000 loads its own starting LOT");
        perProductLots.SelectProduct("PART-2000", migrateCurrentLotIfMissing: false);
        Assert(perProductLots.NextLot == 2001,
            "Switching back restores the previously advanced LOT for that product");
        lotClock = lotClock.AddDays(1);
        Assert(perProductLots.NextLot == 2000,
            "PART-2000 returns to its own starting LOT when the production date changes");

        string root = Path.Combine(Path.GetTempPath(), "JBZLabelProfileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string templatePath = Path.Combine(root, "EXTERNAL_VERIFIED.epl");
            string rawDestination = Path.Combine(root, "raw-printer.bin");
            File.WriteAllText(templatePath, """
                N
                PART={PART_NUMBER}
                NAME={PRODUCT_NAME}
                VEHICLE={VEHICLE_TYPE}
                CUSTOMER={CUSTOMER_CODE}
                ECO={ECO}
                NCO={NCO}
                ALC={ALC}
                LOT={LOT_NO}
                DATE={TEST_DATE}
                TIME={TEST_TIME}
                BAR={BARCODE}
                P1
                """ + "\n", new UTF8Encoding(false));

            var history = new TestHistoryRecord
            {
                Finished = testedAt,
                PartName = data.PartName,
                PartNumber = data.PartNumber,
                Eco = data.Eco,
                Nco = data.Nco,
                Alc = data.Alc,
                LotNo = data.LotNo,
                ModelName = "MODEL-LABEL",
                ModelFile = "MODEL-LABEL.tht",
                CycleId = data.CycleId
            };
            var model = new ProductModel
            {
                ProductName = data.PartName,
                PartNumber = data.PartNumber,
                VehicleType = data.VehicleType,
                CustomerCode = data.CustomerCode,
                Eco = data.Eco,
                Nco = data.Nco,
                Alc = data.Alc,
                LabelTemplate = new LabelTemplateDefinition(
                    ProfileId: "EXTERNAL_VERIFIED",
                    BarcodeTemplate: "{PART_NUMBER}-{LOT_NO}")
            };
            var labelSettings = new LabelSettings
            {
                FormatName = "EXTERNAL_VERIFIED",
                TemplatePath = templatePath,
                RawDestination = rawDestination,
                EncodingName = "us-ascii",
                Copies = 1
            };

            LabelPrintRequest request = LabelPrintRequest.Capture(history, model, labelSettings);
            string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LabelGolden", "EXTERNAL_VERIFIED_expected.epl"))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert(request.Payload.Replace("\r\n", "\n", StringComparison.Ordinal) == expected,
                "External verified profile matches golden EPL byte layout");

            LabelPrintTransportResult printed = new LabelPrintService()
                .PrintPassLabelAsync(request)
                .GetAwaiter()
                .GetResult();
            Assert(printed.Printed && File.ReadAllBytes(rawDestination).SequenceEqual(Encoding.ASCII.GetBytes(request.Payload)),
                "Raw/LPT transport writes the rendered payload byte-for-byte");

            File.WriteAllText(templatePath, "N\nRELOADED={PART_NUMBER}\nP1\n", new UTF8Encoding(false));
            LabelPrintRequest reloaded = LabelPrintRequest.Capture(history, model, labelSettings);
            Assert(reloaded.Payload == "N\nRELOADED=BE331-G2000\nP1\n",
                "External template is re-read after editor save without app/model restart");

            string helperPrintFile = Path.Combine(root, "helper-print.txt");
            labelSettings.RawDestination = string.Empty;
            labelSettings.ExternalHelperPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            labelSettings.ExternalHelperArgument = "/d /c exit 0";
            labelSettings.ExternalPrintFile = helperPrintFile;
            LabelPrintRequest helperRequest = LabelPrintRequest.Capture(history, model, labelSettings);
            LabelPrintTransportResult helperResult = new LabelPrintService()
                .PrintPassLabelAsync(helperRequest)
                .GetAwaiter()
                .GetResult();
            Assert(helperResult.Printed &&
                   File.ReadAllBytes(helperPrintFile).SequenceEqual(Encoding.ASCII.GetBytes(helperRequest.Payload)),
                "External helper transport writes print file and requires successful helper exit");

            File.WriteAllText(templatePath, "N\n$KIND\nP1\n", new UTF8Encoding(false));
            AssertThrows<InvalidDataException>(
                () => LabelPrintRequest.Capture(history, model, labelSettings),
                "Unverified legacy token is blocked instead of guessed");

            labelSettings.TemplatePath = string.Empty;
            model.LabelTemplate = new LabelTemplateDefinition(ProfileId: "KETQU");
            AssertThrows<InvalidDataException>(
                () => LabelPrintRequest.Capture(history, model, labelSettings),
                "KETQU without original template/trace is marked NEEDS_ORIGINAL_TRACE");

            model = ParseThtLabelModelText("12000/20430/1");
            labelSettings.ExternalHelperPath = string.Empty;
            labelSettings.ExternalHelperArgument = string.Empty;
            labelSettings.ExternalPrintFile = "print.txt";
            history.LotNo = 2001;
            history.Finished = new DateTime(2026, 8, 27, 8, 9, 10);
            labelSettings.TemplateType = LabelSettings.LargeTemplate;
            LabelPrintRequest largeSuffix1 = LabelPrintRequest.Capture(history, model, labelSettings);
            IReadOnlyDictionary<string, string> largeSuffix1Variables =
                LabelVariableResolver.Resolve(model, largeSuffix1.Data, labelSettings);
            Assert(model.Alc == "12000/20430/1" &&
                   largeSuffix1.Data.Barcode == "KL375C100026082720011" &&
                   largeSuffix1Variables["LOT_NO"] == "2001" &&
                   largeSuffix1Variables["LOT_NO_BARCODE"] == "20011" &&
                   largeSuffix1.Payload.Contains("2608272001WH", StringComparison.Ordinal) &&
                   !largeSuffix1.Payload.Contains("26082720011WH", StringComparison.Ordinal) &&
                   largeSuffix1.Payload.Contains("KL375C100026082720011", StringComparison.Ordinal),
                "TEM_TO reads ALC /1 from THT but appends it only to Data Matrix, never the printed LOTNO");

            var customerSuffixModel = new ProductModel
            {
                ModelName = "1210040120",
                PartNumber = "1210040120",
                ProductName = model.ProductName,
                Eco = model.Eco,
                Nco = model.Nco,
                Alc = "12100/40120/1"
            };
            history.LotNo = 2001;
            history.Finished = new DateTime(2026, 9, 8, 8, 9, 10);
            LabelPrintRequest customerSuffixLabel =
                LabelPrintRequest.Capture(history, customerSuffixModel, labelSettings);
            Assert(customerSuffixLabel.Data.Barcode == "121004012026090820011" &&
                   customerSuffixLabel.Payload.Contains("2609082001WH", StringComparison.Ordinal) &&
                   !customerSuffixLabel.Payload.Contains("26090820011WH", StringComparison.Ordinal) &&
                   customerSuffixLabel.Payload.Contains("121004012026090820011", StringComparison.Ordinal),
                "ALC 12100/40120/1 keeps printed LOT 2001 and adds suffix 1 only to Data Matrix");

            customerSuffixModel.LabelTemplate = new LabelTemplateDefinition(
                "N\n?\n$DATA$$LOTNO$WH\n{PART_NUMBER}$DATA$$LOTNO$\nP1",
                ProfileId: LabelSettings.LargeTemplate);
            LabelPrintRequest embeddedLegacySuffixLabel =
                LabelPrintRequest.Capture(history, customerSuffixModel, labelSettings);
            Assert(embeddedLegacySuffixLabel.Payload.Contains("2609082001WH", StringComparison.Ordinal) &&
                   embeddedLegacySuffixLabel.Payload.Contains("121004012026090820011", StringComparison.Ordinal) &&
                   !embeddedLegacySuffixLabel.Payload.Contains("26090820011WH", StringComparison.Ordinal),
                "Embedded legacy TEM_TO expression is upgraded safely to the separated barcode value");

            customerSuffixModel.Alc = "12100/40120/2";
            LabelPrintRequest customerSuffix2Label =
                LabelPrintRequest.Capture(history, customerSuffixModel, labelSettings);
            Assert(customerSuffix2Label.Data.Barcode == "121004012026090820012" &&
                   customerSuffix2Label.Payload.Contains("2609082001WH", StringComparison.Ordinal) &&
                   customerSuffix2Label.Payload.Contains("121004012026090820012", StringComparison.Ordinal) &&
                   !customerSuffix2Label.Payload.Contains("26090820012WH", StringComparison.Ordinal),
                "ALC /2 also remains barcode-only while printed LOTNO stays unchanged");

            history.LotNo = 7;
            customerSuffixModel.Alc = "12100/40120/1";
            customerSuffixModel.LabelTemplate = new LabelTemplateDefinition();
            LabelPrintRequest fourDigitLotLabel =
                LabelPrintRequest.Capture(history, customerSuffixModel, labelSettings);
            Assert(fourDigitLotLabel.Data.Barcode == "121004012026090800071" &&
                   fourDigitLotLabel.Payload.Contains("2609080007WH", StringComparison.Ordinal) &&
                   !fourDigitLotLabel.Payload.Contains("26090800071WH", StringComparison.Ordinal),
                "TEM_TO keeps LOTNO at four digits and leaves the ALC suffix outside the incrementing LOT");

            const string editedLargeTemplate = "N\nEDITED={PART_NUMBER}\nP1\n";
            BuiltInLabelTemplateStore.SaveOverride(
                labelSettings,
                LabelSettings.LargeTemplate,
                editedLargeTemplate);
            LabelPrintRequest editedLarge = LabelPrintRequest.Capture(history, model, labelSettings);
            Assert(editedLarge.Payload == "N\nEDITED=KL375C1000\nP1\n" &&
                   BuiltInLabelTemplateStore.LoadOverride(labelSettings, LabelSettings.LargeTemplate) == editedLargeTemplate,
                "Built-in label editor override is Base64-backed and used without a Labels directory");
            BuiltInLabelTemplateStore.ClearOverride(labelSettings, LabelSettings.LargeTemplate);

            LabelPrintData suffix2Data = largeSuffix1.Data with { Alc = "12000/20430/2" };
            LabelIdentity suffix2Identity = EplLabelService.BuildIdentity(suffix2Data);
            LabelIdentity noSuffixIdentity = EplLabelService.BuildIdentity(
                suffix2Data with { Alc = "12000/20430" });
            Assert(suffix2Identity.SerialText == "2608272001WH" &&
                   suffix2Identity.BarcodeValue == "KL375C100026082720012" &&
                   noSuffixIdentity.SerialText == "2608272001WH" &&
                   noSuffixIdentity.BarcodeValue == "KL375C10002608272001",
                "TEM_TO supports Data Matrix suffix /2 without changing printed LOTNO");

            var qrModel = new ProductModel
            {
                ModelName = "K32000-22401R-260224",
                PartNumber = "K32000-22402",
                ProductName = "WIRING ASSY-MAIN",
                Alc = "HE EV",
                Eco = "HE EV",
                VehicleType = "HE EV",
                SourcePath = Path.Combine(
                    Path.GetTempPath(),
                    "K32000-22401R-260224.tht"),
                LabelTemplate = new LabelTemplateDefinition(ProfileId: LabelSettings.SmallQrTemplate)
            };
            history.LotNo = 4;
            history.Finished = new DateTime(2026, 8, 27, 8, 9, 10);
            labelSettings.TemplateType = LabelSettings.SmallQrTemplate;
            LabelPrintRequest qrLabel = LabelPrintRequest.Capture(history, qrModel, labelSettings);
            IReadOnlyDictionary<string, string> qrValues =
                LabelVariableResolver.Resolve(qrModel, qrLabel.Data, labelSettings);
            string normalizedQrPayload = qrLabel.Payload.Replace("\r\n", "\n", StringComparison.Ordinal);
            byte[] qrPayloadBytes = Encoding.ASCII.GetBytes(qrLabel.Payload);
            int qrCrLfCount = qrLabel.Payload.Count(character => character == '\n');
            int qrBareLfCount = qrPayloadBytes
                .Select((value, index) => (value, index))
                .Count(item => item.value == (byte)'\n' &&
                               (item.index == 0 || qrPayloadBytes[item.index - 1] != (byte)'\r'));
            Assert(LabelProfileResolver.NormalizeTemplateType("TEM_BE_QR") == LabelSettings.SmallQrTemplate &&
                   qrLabel.Profile.Id == LabelSettings.SmallQrTemplate &&
                   qrLabel.Profile.Mode == LabelPrintMode.ExternalTemplate &&
                   qrValues["LOT_NO"] == "0004" &&
                   qrValues["LOT_NO_3"] == "004" &&
                   qrValues["MODEL_FILE_NAME"] == "K32000-22401R-260224.tht" &&
                   qrValues["SMALL_QR_BARCODE"] == "K32000-22402,2608270004" &&
                   qrLabel.Data.Barcode == "K32000-22402,2608270004" &&
                   qrLabel.Data.BarcodePrint == "K32000-22402,2608270004" &&
                   normalizedQrPayload.Contains("b200,13,Q,S3,V00\",\"V05V06", StringComparison.Ordinal) &&
                   normalizedQrPayload.Contains(
                       "?\nK32000-22402\nWIRING ASSY-MAIN\nHE EV\nHE EV\n" +
                       "K32000-22401R-260224.tht\n260827\n0004\n004\nQ\n8\n27\nP1",
                       StringComparison.Ordinal),
                "TEM_BE_QR follows 60-15 EPL, reads product fields from THT and encodes PartNumber,yyMMddLOT4");
            Assert(qrCrLfCount > 0 && qrBareLfCount == 0 &&
                   qrLabel.Payload.EndsWith("P1\r\n", StringComparison.Ordinal) &&
                   !qrLabel.Payload.Contains("X180,0,4,620,100", StringComparison.Ordinal) &&
                   qrLabel.Payload.Contains("A450,72,0,2,1,1,N,V05\"-\"V06", StringComparison.Ordinal) &&
                   qrLabel.Payload.Contains("b200,13,Q,S3,V00\",\"V05V06", StringComparison.Ordinal),
                "TEM_BE_QR matches the original helper's stored-form variables, removes comment lines and terminates P1 with CRLF");

            history.LotNo = 2001;
            LabelPrintRequest qrLot2001 = LabelPrintRequest.Capture(history, qrModel, labelSettings);
            IReadOnlyDictionary<string, string> qrLot2001Values =
                LabelVariableResolver.Resolve(qrModel, qrLot2001.Data, labelSettings);
            Assert(qrLot2001Values["LOT_NO"] == "0001" &&
                   qrLot2001Values["LOT_NO_3"] == "001" &&
                   qrLot2001.Data.Barcode == "K32000-22402,2608270001" &&
                   qrLot2001.Payload.Contains(
                       "260827\r\n0001\r\n001\r\nQ\r\n8\r\n27\r\nP1\r\n",
                       StringComparison.Ordinal),
                "TEM_BE_QR converts internal LOT 2001 to original-label daily sequence 0001/001");

            labelSettings.TemplateType = LabelSettings.SmallTemplate;

            LabelPrintRequest SmallLabel(long lot, DateTime date)
            {
                history.LotNo = lot;
                history.Finished = date;
                return LabelPrintRequest.Capture(history, model, labelSettings);
            }

            LabelPrintRequest smallLabel = SmallLabel(1, new DateTime(2026, 8, 25, 8, 9, 10));
            IReadOnlyDictionary<string, string> smallValues =
                LabelVariableResolver.Resolve(model, smallLabel.Data, labelSettings);
            const string expectedSmallBarcode = "KL375C1000,SQDZQ8P0001";
            Assert(smallValues["YEAR_CODE"] == "Q" &&
                   smallValues["MONTH_CODE"] == "8" &&
                   smallValues["DAY_CODE"] == "P" &&
                   smallValues["LOT_NO"] == "0001",
                "TEM_BE resolves 2026/year, month 8, day 25 and four-digit LOT codes");
            Assert(smallLabel.Copies == 1,
                "TEM_BE keeps the configured single print copy");
            Assert(smallLabel.Payload.Contains(expectedSmallBarcode, StringComparison.Ordinal) &&
                   smallLabel.Data.Barcode == expectedSmallBarcode &&
                   smallLabel.Data.BarcodePrint == expectedSmallBarcode,
                "TEM_BE DATA5 and immutable barcode snapshot use the same SQDZ canonical value from THT data");

            Assert(SmallLabel(25, new DateTime(2026, 8, 25)).Data.Barcode.EndsWith("0025", StringComparison.Ordinal),
                "TEM_BE formats LOT 25 as 0025");
            Assert(SmallLabel(7001, new DateTime(2026, 8, 25)).Data.Barcode.EndsWith("7001", StringComparison.Ordinal),
                "TEM_BE keeps four-digit LOT 7001 unchanged");
            Assert(SmallLabel(1, new DateTime(2026, 8, 1)).Data.Barcode == "KL375C1000,SQDZQ810001",
                "TEM_BE keeps day 1 as digit 1");
            Assert(SmallLabel(1, new DateTime(2026, 8, 26)).Data.Barcode == "KL375C1000,SQDZQ8Q0001",
                "TEM_BE maps day 26 to Q");

            model.PartNumber = "BE331H6000";
            void AssertSmallDateCode(DateTime date, string expectedDateCode, string expectedBarcode)
            {
                LabelPrintRequest request = SmallLabel(7002, date);
                IReadOnlyDictionary<string, string> values =
                    LabelVariableResolver.Resolve(model, request.Data, labelSettings);
                string actualDateCode =
                    values["YEAR_CODE"] + values["MONTH_CODE"] + values["DAY_CODE"];
                Assert(actualDateCode == expectedDateCode &&
                       request.Data.Barcode == expectedBarcode &&
                       values["SMALL_LABEL_BARCODE"] == expectedBarcode,
                    $"TEM_BE date {date:dd/MM/yyyy} resolves to {expectedDateCode}");
            }

            AssertSmallDateCode(
                new DateTime(2026, 7, 29),
                "Q7T",
                "BE331H6000,SQDZQ7T7002");
            AssertSmallDateCode(
                new DateTime(2026, 8, 26),
                "Q8Q",
                "BE331H6000,SQDZQ8Q7002");
            AssertSmallDateCode(
                new DateTime(2026, 8, 31),
                "Q8V",
                "BE331H6000,SQDZQ8V7002");
            AssertSmallDateCode(
                new DateTime(2026, 10, 10),
                "QAA",
                "BE331H6000,SQDZQAA7002");
            AssertSmallDateCode(
                new DateTime(2026, 12, 31),
                "QCV",
                "BE331H6000,SQDZQCV7002");
            AssertSmallDateCode(
                new DateTime(2025, 1, 1),
                "P11",
                "BE331H6000,SQDZP117002");

            InvalidDataException undefinedYear = AssertThrows<InvalidDataException>(
                () => SmallLabel(1, new DateTime(2036, 8, 25)),
                "TEM_BE year 2036 must be rejected before creating a print request");
            Assert(undefinedYear.Message.Contains("LABEL_DATE_CODE_UNDEFINED", StringComparison.Ordinal) &&
                   undefinedYear.Message.Contains("Year=2036", StringComparison.Ordinal),
                "TEM_BE year 2036 reports the undefined date-code marker and exact year");

            string smallDuplicatePath = Path.Combine(root, "LastSmallBarcode.txt");
            var smallDuplicateGuard = new LabelDuplicateGuard(smallDuplicatePath);
            smallDuplicateGuard.RecordSuccessfulPrint(smallLabel, testedAt);
            Assert(smallDuplicateGuard.LoadLast()?.Barcode == expectedSmallBarcode,
                "TEM_BE duplicate state persists the physical SQDZ barcode");

            model.PartNumber = "1200020430";
            model.ProductName = "BMS EXT";
            model.Eco = "US4 HEV";
            model.VehicleType = model.Eco;
            model.Alc = "12000/20430";
            model.CustomerCode = model.Alc;
            model.LabelVariables.Clear();
            history.LotNo = 7001;
            history.Finished = new DateTime(2026, 7, 31, 8, 9, 10);
            labelSettings.TemplateType = LabelSettings.LargeTemplate;
            LabelPrintRequest largeLabel = LabelPrintRequest.Capture(history, model, labelSettings);
            byte[] expectedLargePayload = File.ReadAllBytes(
                Path.Combine(AppContext.BaseDirectory, "LabelGolden", "TEM_TO_regression_expected.epl"));
            Assert(Encoding.UTF8.GetBytes(largeLabel.Payload).SequenceEqual(expectedLargePayload),
                "TEM_TO payload remains byte-for-byte identical for the locked regression input");

            string duplicatePath = Path.Combine(root, "LastBarcode.txt");
            var duplicateGuard = new LabelDuplicateGuard(duplicatePath);
            duplicateGuard.RecordSuccessfulPrint(request, testedAt);
            LabelDuplicateRecord savedDuplicate = duplicateGuard.LoadLast()
                ?? throw new InvalidOperationException("LastBarcode state was not persisted.");
            Assert(savedDuplicate.Barcode == "BE331-G2000-2044" &&
                   savedDuplicate.CycleId == "cycle-a" &&
                   savedDuplicate.LotNo == 2044,
                "LastBarcode compatibility state records barcode/cycle/LOT after successful print");

            var stressSettings = new ProductionSettings { LotNo = 3000 };
            var stressLots = new LotSequenceService(stressSettings, _ => { });
            for (int cycle = 0; cycle < 100; cycle++)
            {
                if (cycle % 2 != 0)
                    continue;
                string cycleId = $"stress-{cycle}";
                long lot = stressLots.ReserveForCycle(cycleId);
                Assert(stressLots.TryCommitSuccessfulPrint(cycleId, lot, out _),
                    $"100-cycle label stress commits cycle {cycle}");
            }
            Assert(stressLots.NextLot == 3050,
                "100-cycle PASS/FAIL stress has no skipped or duplicate committed LOT");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

#endif
    private static void TestLabelPrintingSafety()
    {
        MethodInfo shouldAutoPrint = typeof(TestViewModel).GetMethod(
            "ShouldAutoPrintLabel",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS label eligibility helper not found.");
        Assert((bool)(shouldAutoPrint.Invoke(null, [true, true]) ?? false),
            "Final PASS with auto-print enabled is eligible for one print transaction");
        Assert(!(bool)(shouldAutoPrint.Invoke(null, [false, true]) ?? true) &&
               !(bool)(shouldAutoPrint.Invoke(null, [true, false]) ?? true),
            "NG/FAIL and disabled auto-print never create an automatic print transaction");

        MethodInfo hasLabelTransport = typeof(TestViewModel).GetMethod(
            "HasConfiguredLabelTransport",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Label transport eligibility helper not found.");
        Assert(!(bool)(hasLabelTransport.Invoke(null, [new LabelSettings()]) ?? true) &&
               (bool)(hasLabelTransport.Invoke(null, [new LabelSettings { PrinterCom = "COM3" }]) ?? false) &&
               (bool)(hasLabelTransport.Invoke(null, [new LabelSettings { PrinterName = "ZDesigner" }]) ?? false),
            "Auto-print is skipped on stations without a configured printer transport");

        DateTime finished = new(2026, 8, 10, 9, 8, 7, DateTimeKind.Local);
        var history = new TestHistoryRecord
        {
            Started = finished.AddSeconds(-1),
            Finished = finished,
            PartName = "PRODUCT-A",
            PartNumber = "PART-A",
            Eco = "ECO-A",
            LotNo = 31415,
            Result = "PASS",
            Passed = true,
            ModelName = "MODEL-A",
            ModelFile = @"D:\models\A.tht",
            CycleId = "cycle-a",
            PrintStatus = LabelPrintStatus.NotRequested.ToString()
        };
        var settings = new LabelSettings
        {
            PrinterName = "ZEBRA-A",
            WidthMm = 90,
            HeightMm = 15,
            FormatName = "KS91-A",
            Copies = 1
        };

        var labelModel = new ProductModel
        {
            ProductName = history.PartName,
            PartNumber = history.PartNumber,
            Eco = history.Eco,
            Nco = history.Nco,
            Alc = history.Alc,
            VehicleType = history.Eco,
            CustomerCode = history.Alc,
            LabelTemplate = new LabelTemplateDefinition(
                "N\n{PART_NUMBER}\n{PRODUCT_NAME}\n{LOT_NO}\nP1",
                ProfileId: "KS91-A")
        };
        LabelPrintRequest request = LabelPrintRequest.Capture(history, labelModel, settings);
        LabelIdentity identity = EplLabelService.BuildIdentity(request.Data);
        history.LabelSerial = identity.SerialText;
        history.BarcodeValue = identity.BarcodeValue;
        Assert(history.ExportBarcodeText.Length == 0,
            "Auto-print disabled/not requested hides any prepared barcode from history export");
        history.BarcodeValue = string.Empty;
        history.LabelProfile = request.FormatName;
        history.Printer = request.Printer;
        history.LabelCopies = request.Copies;
        Assert(history.ExportBarcodeText.Length == 0,
            "PASS history keeps barcode blank before the printer confirms success");

        // UI/current model changes after PASS must not mutate the queued label.
        history.PartNumber = "PART-B";
        history.ModelName = "MODEL-B";
        settings.PrinterName = "ZEBRA-B";
        settings.Copies = 2;
        string epl = EplLabelService.BuildPassLabel(request);
        Assert(epl.Contains("PART-A", StringComparison.Ordinal) &&
               !epl.Contains("PART-B", StringComparison.Ordinal), "PASS cycle keeps model A snapshot");
        Assert(request.ModelName == "MODEL-A" && request.PrinterName == "ZEBRA-A" && request.Copies == 1,
            "Printer/model/copies snapshot is immutable");
        Assert(identity.BarcodeValue == "PART-A26081031415", "Barcode is deterministic from PASS snapshot");

        MethodInfo tryCapturePassLabel = typeof(TestViewModel).GetMethod(
            "TryCapturePassLabel",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PASS label isolation helper not found.");
        var unresolvedLabelModel = new ProductModel
        {
            ModelName = "NAM",
            PartNumber = "NAM",
            LabelTemplate = new LabelTemplateDefinition(ProfileId: "MISSING_TEST_PROFILE")
        };
        var unresolvedLabelSettings = new LabelSettings { FormatName = "KS91" };
        object?[] captureArguments =
        [
            history,
            unresolvedLabelModel,
            unresolvedLabelSettings,
            null,
            null,
            null
        ];
        bool unresolvedCaptured = (bool)(tryCapturePassLabel.Invoke(null, captureArguments) ?? true);
        Assert(!unresolvedCaptured &&
               captureArguments[3] is null &&
               captureArguments[4] is null &&
               captureArguments[5] is string captureError &&
               captureError.Contains("NEEDS_ORIGINAL_TRACE", StringComparison.Ordinal),
            "Unresolved KS91 label configuration is contained as a print failure and cannot escape into PASS/ProductRemoved lifecycle");

        var qrHistory = new TestHistoryRecord
        {
            Finished = finished,
            PartName = "QR PRODUCT",
            PartNumber = "K32000-22402",
            Alc = "12000/20430/2",
            LotNo = 1,
            Result = "PASS",
            Passed = true,
            ModelName = "QR MODEL",
            ModelFile = "QR.model.tht",
            CycleId = "cycle-qr"
        };
        var qrModel = new ProductModel
        {
            ProductName = qrHistory.PartName,
            PartNumber = qrHistory.PartNumber,
            Eco = "HE EV",
            VehicleType = "HE EV",
            Alc = qrHistory.Alc,
            CustomerCode = qrHistory.Alc
        };
        var qrSettings = new LabelSettings { TemplateType = LabelSettings.SmallQrTemplate };
        object?[] qrCaptureArguments = [qrHistory, qrModel, qrSettings, null, null, null];
        bool qrCaptured = (bool)(tryCapturePassLabel.Invoke(null, qrCaptureArguments) ?? false);
        Assert(qrCaptured &&
               qrCaptureArguments[3] is LabelPrintRequest capturedQrRequest &&
               capturedQrRequest.Data.Barcode == "K32000-22402,2608100001" &&
               qrCaptureArguments[4] is LabelIdentity capturedQrIdentity &&
               capturedQrIdentity.SerialText == "2608100001WH" &&
               capturedQrIdentity.BarcodeValue == "K32000-22402,2608100001",
            "TEM_BE_QR PASS history keeps four-digit LOT and does not apply TEM_TO ALC suffix");

        string root = Path.Combine(Path.GetTempPath(), "JBZLabelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Persist the original PASS identity, not the later mutable UI values.
            history.PartNumber = request.Data.PartNumber;
            history.ModelName = request.ModelName;
            var store = new TestHistoryStore(Path.Combine(root, "history.db"));
            long id = store.Add(history);
            Assert(store.TryBeginFirstPrint(id, request.CycleId), "First PASS callback claims print transaction");
            Assert(!store.TryBeginFirstPrint(id, request.CycleId), "Duplicate PASS callback is blocked");
            store.UpdateLabelPrintOutcome(
                id,
                request.CycleId,
                LabelPrintStatus.Failed,
                null,
                "printer offline");
            TestHistoryRecord failedPrint = store.Search(new HistorySearchCriteria(
                null, null, 31415, "PART-A", "PASS", 10)).Single();
            Assert(failedPrint.BarcodeValue.Length == 0 && failedPrint.ExportBarcodeText.Length == 0,
                "Failed or disabled printing never writes barcode into history");
            Assert(store.TryBeginFirstPrint(id, request.CycleId), "Explicit retry reuses the failed cycle/LOT transaction");
            Assert(!store.TryBeginFirstPrint(id, request.CycleId), "Concurrent retry callback is blocked while Pending");
            store.UpdateLabelPrintOutcome(
                id,
                request.CycleId,
                LabelPrintStatus.Printed,
                finished.AddMilliseconds(250),
                "software-test",
                identity.BarcodeValue);
            store.IncrementLabelReprint(
                id,
                request.CycleId,
                finished.AddMilliseconds(500),
                "manual-reprint");

            TestHistoryRecord saved = store.Search(new HistorySearchCriteria(
                null, null, 31415, "PART-A", "PASS", 10)).Single();
            Assert(saved.CycleId == "cycle-a" && saved.PrintStatus == "Printed", "Cycle/print status traceability");
            Assert(saved.BarcodeValue == identity.BarcodeValue &&
                   saved.LabelSerial == identity.SerialText &&
                   saved.LabelProfile == "KS91-A" &&
                   saved.Printer == "ZEBRA-A" &&
                   saved.LabelCopies == 1 &&
                   saved.ReprintCount == 1, "Manual reprint keeps the PASS identity and increments only reprint count");
            Assert(saved.PrintTimestamp.HasValue, "Printed transaction records timestamp");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static TestEngine CreateEngine(out FakeBoard board, ProductionSettings? productionOverride = null)
    {
        board = new FakeBoard();
        var app = new AppSettings();
        app.Board.RequiredStableFrames = 1;
        var production = productionOverride ?? new ProductionSettings
        {
            IoConfirm1 = 1,
            IoConfirmN = 1,
            Relay1JigPulseMs = 50,
            Relay2MarkingPulseMs = 50,
            PassMarkingToJigDelayMs = 0,
            OpenCircuitConfirmMs = 0,
            ShortCircuitConfirmMs = 0,
            WrongConnectionConfirmMs = 0,
            ProductSettleTimeMs = 0,
            JigContactUnstableWindowMs = 0
        };
        return new TestEngine(board, new KeysightVisaService(), app, production);
    }

    private static TestViewModel CreateTestViewModel(ProductionSettings production)
        => CreateTestViewModel(production, out _);

    private static TestViewModel CreateTestViewModel(
        ProductionSettings production,
        out FakeBoard board,
        bool requireStartupIoClear = false)
    {
        board = new FakeBoard();
        var app = new AppSettings();
        var engine = new TestEngine(board, new KeysightVisaService(), app, production);
        return new TestViewModel(
            new MainViewModel(),
            engine,
            board,
            new KeysightVisaService(),
            new WaterProofSerialService(),
            app,
            production,
            new LegacyPhtHistoryService(enabled: false),
            requireStartupIoClear);
    }

    private static ProductModel Model(params (string Name, int[] Io)[] nets)
    {
        var model = new ProductModel { ModelName = "SELF-TEST", PartNumber = "SELF-TEST" };
        foreach ((string name, int[] io) in nets)
        {
            PinRecord[] pins = io.Select(value => new PinRecord("C", name, value, value.ToString())).ToArray();
            model.Pins.AddRange(pins);
            model.Nets.Add(new WireNet(name, io, pins));
        }
        return model;
    }

    private static ProductModel HtdrvTwoEndpointModel()
    {
        var model = new ProductModel { ModelName = "HTDRV-ENDPOINT", PartNumber = "HTDRV-ENDPOINT" };
        var pin1 = new PinRecord("1", "1", 1, "1", Section: "0.5", Color: "R", OriginalOrder: 1);
        var pin2 = new PinRecord("1", "1", 2, "2", Section: "0.5", Color: "R", OriginalOrder: 2);
        model.Pins.AddRange([pin1, pin2]);
        model.Nets.Add(new WireNet("1", [1, 2], [pin1, pin2]));
        model.Connectors.Add(new ConnectorDefinition(
            "1",
            2,
            [
                new ConnectorPin("1", 1, "1", pin1),
                new ConnectorPin("2", 2, "1", pin2)
            ]));
        return model;
    }

    private static ProductModel TopologyModel(params Terminal[] terminals)
    {
        var model = new ProductModel { ModelName = "THT-TOPOLOGY", PartNumber = "THT-TOPOLOGY" };

        foreach (Terminal terminal in terminals)
        {
            model.Pins.Add(new PinRecord(
                terminal.Connector,
                terminal.WireName,
                terminal.Io,
                terminal.Pin,
                Section: string.Empty,
                Color: terminal.Color,
                ConnectorPinCount: terminal.PinCount,
                PinType: terminal.PinType,
                WireConnection: terminal.WireConnection));
        }

        model.Nets = model.Pins
            .Where(pin => !string.IsNullOrWhiteSpace(pin.WireName))
            .GroupBy(pin => pin.WireName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                PinRecord[] pins = group.ToArray();
                int[] ios = pins.Select(pin => pin.IoNumber).Distinct().ToArray();
                return new WireNet(group.Key, ios, pins);
            })
            .Where(net => net.IoNumbers.Count >= 2)
            .ToList();

        model.Connectors = model.Pins
            .Where(pin => !string.IsNullOrWhiteSpace(pin.Connector))
            .GroupBy(pin => pin.Connector.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                int[] declaredCounts = group
                    .Select(pin => int.TryParse(pin.ConnectorPinCount, out int value) && value > 0 ? value : 0)
                    .Where(value => value > 0)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
                if (declaredCounts.Length > 1)
                {
                    model.TopologyWarnings.Add(
                        $"MODEL_WARNING_CONNECTOR_PINCOUNT_MISMATCH connector={group.Key} values=[{string.Join(",", declaredCounts)}]");
                }

                ConnectorPin[] pins = group
                    .Select(pin => new ConnectorPin(pin.PinNumber, pin.IoNumber, pin.WireName, pin))
                    .OrderBy(pin => pin.LocalPinNumber, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(pin => pin.PhysicalIo)
                    .ToArray();

                return new ConnectorDefinition(
                    group.Key,
                    declaredCounts.Length > 0 ? declaredCounts[0] : null,
                    pins);
            })
            .OrderBy(connector => connector.ConnectorId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return model;
    }

    private sealed record Terminal(
        int Io,
        string Connector,
        string Pin,
        string PinCount,
        string WireName,
        string Color = "B",
        string PinType = "",
        string WireConnection = "");

    private static PinRecord? PinByIo(ProductModel model, int io) =>
        model.Pins.FirstOrDefault(pin => pin.IoNumber == io);

    private static int? ConnectorPinCount(ProductModel model, string connector) =>
        model.Connectors.FirstOrDefault(item =>
            string.Equals(item.ConnectorId, connector, StringComparison.OrdinalIgnoreCase))
            ?.DeclaredPinCount;

    private static int[] NetIos(ProductModel model, string wireName) =>
        model.Nets.FirstOrDefault(net =>
            string.Equals(net.Name, wireName, StringComparison.OrdinalIgnoreCase))
            ?.IoNumbers.OrderBy(io => io).ToArray() ?? [];

    private static ScanFrame Frame(params (int Source, int[] Targets)[] connections)
        => FrameSeq(1, connections);

    private static ScanFrame FrameSeq(long sequence, params (int Source, int[] Targets)[] connections)
    {
        Dictionary<int, IReadOnlySet<int>> map = connections.ToDictionary(
            pair => pair.Source,
            pair => (IReadOnlySet<int>)pair.Targets.ToHashSet());
        HashSet<int> active = map.Keys.Concat(map.Values.SelectMany(values => values)).ToHashSet();
        Dictionary<int, int> hits = map.Values.SelectMany(values => values)
            .GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        return new ScanFrame(DateTime.Now, 1, active, [], true, 0, sequence, map, hits, BoardScanMode.Production);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using StreamReader reader = new(archive.GetEntry(name)?.Open() ?? throw new InvalidOperationException(name));
        return reader.ReadToEnd();
    }

    private static int CountActiveCycles(string databasePath, string cycleId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActiveTestCycles WHERE CycleId=$CycleId;";
        command.Parameters.AddWithValue("$CycleId", cycleId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static void TestHistoryTransactionAtomicity()
    {
        string root = Path.Combine(Path.GetTempPath(), "JBZHistoryAtomicity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] failurePoints =
            [
                "Tests:BEFORE INSERT ON Tests",
                "Faults:BEFORE INSERT ON TestFaults",
                "Resistance:BEFORE INSERT ON ResistanceMeasurements",
                "WaterProof:BEFORE INSERT ON WaterProofMeasurements",
                "Totals:BEFORE UPDATE OF TotalTests ON Parts",
                "ActiveCycle:BEFORE DELETE ON ActiveTestCycles"
            ];

            foreach (string failurePoint in failurePoints)
            {
                string[] parts = failurePoint.Split(':', 2);
                string databasePath = Path.Combine(root, parts[0] + ".db");
                var store = new TestHistoryStore(databasePath);
                string cycleId = "atomic-" + parts[0].ToLowerInvariant();
                ProductModel model = Model(("ATOMIC-WIRE", new[] { 1, 18 }));
                model.PartNumber = "ATOMIC-PART";
                model.ModelName = "ATOMIC-MODEL";
                model.SourcePath = Path.Combine(root, "atomic.tht");
                DateTime at = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Local);
                var history = new TestHistoryRecord
                {
                    Started = at,
                    Finished = at.AddSeconds(2),
                    ResultAt = at.AddSeconds(2),
                    InspectionType = HistoryInspectionType.Product,
                    PartNumber = model.PartNumber,
                    ModelName = model.ModelName,
                    ModelFile = model.SourcePath,
                    Result = "FAIL",
                    Passed = false,
                    CycleId = cycleId
                };
                var duplicateFault = new FaultDetail
                {
                    Type = ProductFaultType.WrongWiring,
                    ExpectedSourceIo = 1,
                    ExpectedTargetIo = 18,
                    ActualSourceIo = 1,
                    ActualTargetIo = 19,
                    WireName = "ATOMIC-WIRE"
                };
                var resistance = new ResistanceResult
                {
                    Channel = 1,
                    Name = "R1",
                    ValueOhm = 101,
                    MinOhm = 100,
                    MaxOhm = 110,
                    Passed = true
                };
                var waterProof = new WaterProofChannelMeasurement(1, true, 85, 84.8, 0.2, true);
                ProductionResultCommitRequest request = ProductionResultCommitRequest.Capture(
                    history,
                    model,
                    new ProductionSettings(),
                    [duplicateFault, duplicateFault],
                    [resistance],
                    [waterProof],
                    ProgramIdentityService.VersionText);
                Assert(request.Faults.Count == 1,
                    "Repeated physical fault observations collapse before the transaction boundary");

                store.UpsertActiveCycle(cycleId, model.PartNumber, model.SourcePath, at, "TEST_STARTED");
                ExecuteSql(databasePath,
                    $"CREATE TRIGGER InjectFailure {parts[1]} BEGIN SELECT RAISE(ABORT,'injected crash'); END;");
                AssertThrows<SqliteException>(
                    () => store.CommitResult(request, null),
                    $"Injected failure at {parts[0]} must escape the commit");
                Assert(ScalarInt(databasePath,
                           "SELECT (SELECT COUNT(*) FROM Tests) + " +
                           "(SELECT COUNT(*) FROM TestFaults) + " +
                           "(SELECT COUNT(*) FROM ResistanceMeasurements) + " +
                           "(SELECT COUNT(*) FROM WaterProofMeasurements);") == 0 &&
                       CountActiveCycles(databasePath, cycleId) == 1 &&
                       ScalarText(databasePath, "PRAGMA integrity_check;") == "ok",
                    $"Failure at {parts[0]} rolls back the whole result while retaining the recoverable active cycle");

                ExecuteSql(databasePath, "DROP TRIGGER InjectFailure;");
                ProductionCommitResult first = store.CommitResult(request, null);
                ProductionCommitResult duplicate = store.CommitResult(request, null);
                Assert(!first.AlreadyCommitted && duplicate.AlreadyCommitted &&
                       first.TestId == duplicate.TestId &&
                       ScalarInt(databasePath, "SELECT COUNT(*) FROM Tests;") == 1 &&
                       ScalarInt(databasePath, "SELECT COUNT(*) FROM TestFaults;") == 1 &&
                       ScalarInt(databasePath, "SELECT COUNT(*) FROM ResistanceMeasurements;") == 1 &&
                       ScalarInt(databasePath, "SELECT COUNT(*) FROM WaterProofMeasurements;") == 1 &&
                       ScalarInt(databasePath, "SELECT TotalTests FROM Parts WHERE PartNumber='ATOMIC-PART';") == 1 &&
                       CountActiveCycles(databasePath, cycleId) == 0,
                    $"Retry after {parts[0]} commits exactly once with one child row per physical observation");

                DateTime removalStarted = at.AddSeconds(3);
                Assert(store.UpdateRemovalTiming(cycleId, removalStarted, at.AddSeconds(4)) &&
                       ScalarInt(databasePath, "SELECT COUNT(*) FROM Tests;") == 1,
                    "Removal/discard completion updates the same CycleId and never inserts another result");
                Assert(ScalarInt(databasePath,
                           "SELECT COUNT(*) FROM TestFaults f LEFT JOIN Tests t ON t.Id=f.TestId WHERE t.Id IS NULL;") == 0 &&
                       ScalarInt(databasePath,
                           "SELECT COUNT(*) FROM ResistanceMeasurements r LEFT JOIN Tests t ON t.Id=r.TestId WHERE t.Id IS NULL;") == 0 &&
                       ScalarInt(databasePath,
                           "SELECT COUNT(*) FROM WaterProofMeasurements w LEFT JOIN Tests t ON t.Id=w.TestId WHERE t.Id IS NULL;") == 0,
                    "Committed database contains no orphan detail rows");
            }

            string isolationPath = Path.Combine(root, "snapshot-isolation.db");
            var isolationStore = new TestHistoryStore(isolationPath);
            DateTime snapshotAt = new(2026, 9, 14, 0, 0, 1, DateTimeKind.Local);
            ProductModel oldModel = Model(("OLD-WIRE", new[] { 1, 18 }));
            oldModel.PartNumber = "OLD-PART";
            oldModel.ModelName = "OLD-MODEL";
            oldModel.SourcePath = Path.Combine(root, "old.tht");
            var oldHistory = new TestHistoryRecord
            {
                Started = snapshotAt,
                Finished = snapshotAt.AddSeconds(1),
                ResultAt = snapshotAt.AddSeconds(1),
                InspectionType = HistoryInspectionType.Product,
                PartNumber = oldModel.PartNumber,
                ModelName = oldModel.ModelName,
                ModelFile = oldModel.SourcePath,
                Result = "PASS",
                Passed = true,
                CycleId = "old-model-callback"
            };
            ProductionResultCommitRequest oldSnapshot = ProductionResultCommitRequest.Capture(
                oldHistory, oldModel, new ProductionSettings(), [], [], null,
                ProgramIdentityService.VersionText);
            oldModel.PartNumber = "NEW-PART";
            oldModel.ModelName = "NEW-MODEL";
            oldModel.SourcePath = Path.Combine(root, "new.tht");
            isolationStore.CommitResult(oldSnapshot, null);
            Assert(ScalarText(isolationPath,
                       "SELECT p.PartNumber || '|' || m.ModelName FROM Tests t " +
                       "JOIN Parts p ON p.Id=t.PartId JOIN Models m ON m.Id=t.ModelId " +
                       "WHERE t.CycleId='old-model-callback';") == "OLD-PART|OLD-MODEL" &&
                   ScalarInt(isolationPath,
                       "SELECT COUNT(*) FROM Tests t JOIN Parts p ON p.Id=t.PartId WHERE p.PartNumber='NEW-PART';") == 0,
                "A queued persistence callback owns immutable old-model snapshots and cannot write into the newly selected model");

            var masterHistory = oldHistory.ClonePersistenceSnapshot();
            masterHistory.Id = 0;
            masterHistory.CycleId = "master-good-isolated";
            masterHistory.InspectionType = HistoryInspectionType.MasterGood;
            masterHistory.Result = "MASTER_GOOD_PASS";
            ProductionResultCommitRequest masterRequest = ProductionResultCommitRequest.Capture(
                masterHistory, oldModel, new ProductionSettings(), [], [], null,
                ProgramIdentityService.VersionText);
            Assert(!masterRequest.UpdateProductionTotals,
                "Master history request is explicitly excluded from production totals and LOT authority");
            isolationStore.CommitResult(masterRequest, null);
            ProductionStatisticsSnapshot isolatedStats = isolationStore.GetStatistics(oldSnapshot.Part, snapshotAt);
            Assert(ScalarInt(isolationPath,
                       "SELECT COUNT(*) FROM Tests WHERE InspectionType='MASTER_GOOD';") == 1 &&
                   isolatedStats.LifetimeTotal == 1,
                "Master history is stored separately while product counters still contain only the physical product cycle");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ScalarInt(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static TException AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(message);
    }

#if LEGACY_D2XX
    private static ProductModel ParseThtLabelModelText(string alc = "ALC-FROM-THT")
    {
        string modelText =
            "파트번호\t파트명\tECO\tNCO\tALC\n" +
            $"KL375C1000\tVOLTAGE_6S\tAE EV PE\tNCO-7\t{alc}\n\n" +
            "번 호\t커넥터\t핀 수\n" +
            "1\tCN1\t2\n\n" +
            "커넥터\t선이름\tI/O\t핀번호\n" +
            "CN1\tW1\t1\t1\n" +
            "CN1\tW1\t2\t2\n\n" +
            "선이름\t선연결\t굵기\t색깔\n" +
            "W1\t\t0.5\tR";

        string root = Path.Combine(Path.GetTempPath(), "JBZThtLabelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "SQDZ-label-source.tht");
            File.WriteAllBytes(path, BuildMinimalThtFile(modelText));
            return new ThtModelParser().Load(path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

#endif
    private static void TestLearnedTopology()
    {
        var activeIoRow = new ActiveIoDiagnosticRow(42);
        Assert(activeIoRow.IoText == "IO(42)" &&
               activeIoRow.StatusText == "KẸT / CHƯA THÁO",
            "Live diagnostic reports an active physical IO immediately without a THT model or time threshold");

        LearnedTopologySnapshot snapshot = TopologyLearningService.BuildSnapshot(
            FrameSeq(
                80,
                (1, new[] { 18 }),
                (18, new[] { 1, 35 }),
                (35, new[] { 18 }),
                (2, new[] { 19 }),
                (19, new[] { 2 })),
            BoardCapacity.Create(1));

        Assert(snapshot.Networks.Count == 2 &&
               snapshot.Networks[0].Ios.SequenceEqual(new[] { 1, 18, 35 }) &&
               snapshot.Networks[1].Ios.SequenceEqual(new[] { 2, 19 }) &&
               snapshot.Rows[0].Connection == "IO(1) ↔ IO(18) ↔ IO(35)",
            "Learning canonicalizes bidirectional edges into stable connected components");

        string root = Path.Combine(Path.GetTempPath(), "JBZLearnedTopologyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "SAMPLE.jbzscan.json");
            var profile = new LearnedTopologyProfile
            {
                ProductCode = "SAMPLE",
                CreatedAt = DateTime.Now,
                ExpansionCardCount = 1,
                FirstIo = 1,
                LastIo = 64,
                RequiredStableFrames = 20,
                ObservedStableFrames = 20,
                Networks = snapshot.Networks.Select(network => new LearnedTopologyNetwork
                {
                    Name = network.Name,
                    Ios = network.Ios.ToList()
                }).ToList()
            };
            TopologyLearningService.SaveAsync(path, profile).GetAwaiter().GetResult();
            string json = File.ReadAllText(path);
            Assert(json.Contains("\"ProfileType\": \"DiagnosticContinuity\"", StringComparison.Ordinal) &&
                   json.Contains("\"ProductCode\": \"SAMPLE\"", StringComparison.Ordinal) &&
                   !File.Exists(path + ".tmp"),
                "Learned topology is atomically persisted as an explicitly diagnostic non-THT profile");

            string mainXaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "MainWindow.xaml"));
            string learningXaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "TopologyLearningWindow.xaml"));
            Assert(mainXaml.Contains("Content=\"QUÉT / HỌC MÃ\"", StringComparison.Ordinal) &&
                   learningXaml.Contains("Không phải file THT", StringComparison.Ordinal) &&
                   learningXaml.Contains("ItemsSource=\"{Binding ActiveIoRows}\"", StringComparison.Ordinal) &&
                   learningXaml.Contains("KẸT / CHƯA THÁO", StringComparison.Ordinal) &&
                   !learningXaml.Contains("THỜI GIAN", StringComparison.Ordinal) &&
                   !learningXaml.Contains("3 giây", StringComparison.Ordinal) &&
                   learningXaml.Contains("Không tác động relay", StringComparison.Ordinal) &&
                   learningXaml.Contains("EnableRowVirtualization", StringComparison.Ordinal),
                "MainWindow exposes THT-independent live IO and stuck-contact diagnostics without production actions");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildMinimalThtFile(string modelText)
    {
        const int sectorSize = 512;
        const uint freeSector = 0xFFFFFFFF;
        const uint endOfChain = 0xFFFFFFFE;
        const uint fatSector = 0xFFFFFFFD;
        const int contentSectorCount = 8;
        const int contentStartSector = 2;

        byte[] file = new byte[sectorSize * (1 + 2 + contentSectorCount)];
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(24, 2), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(30, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(48, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60, 4), endOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(68, 4), endOfChain);
        for (int index = 0; index < 109; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(76 + index * sizeof(uint), sizeof(uint)),
                index == 0 ? 0u : freeSector);
        }

        Span<byte> fat = file.AsSpan(sectorSize, sectorSize);
        for (int index = 0; index < fat.Length / sizeof(uint); index++)
            BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(index * sizeof(uint), sizeof(uint)), freeSector);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(0, 4), fatSector);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(4, 4), endOfChain);
        for (int index = 0; index < contentSectorCount; index++)
        {
            uint next = index == contentSectorCount - 1
                ? endOfChain
                : (uint)(contentStartSector + index + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(
                fat.Slice((contentStartSector + index) * sizeof(uint), sizeof(uint)),
                next);
        }

        Span<byte> directory = file.AsSpan(sectorSize * 2, sectorSize);
        WriteDirectoryEntry(directory.Slice(0, 128), "Root Entry", 5, endOfChain, 0);
        WriteDirectoryEntry(
            directory.Slice(128, 128),
            "Contents",
            2,
            contentStartSector,
            contentSectorCount * sectorSize);

        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0x389DEFB9u);
            WriteMfcCString(writer, "TEST", Encoding.ASCII);
            WriteMfcCString(writer, modelText, Encoding.GetEncoding(949));
        }

        byte[] contentBytes = content.ToArray();
        Assert(contentBytes.Length <= contentSectorCount * sectorSize,
            "Synthetic .tht Contents stream exceeds its OLE sector allocation");
        contentBytes.CopyTo(file, sectorSize * (contentStartSector + 1));
        return file;

        static void WriteDirectoryEntry(
            Span<byte> entry,
            string name,
            byte type,
            uint startSector,
            long streamSize)
        {
            byte[] nameBytes = Encoding.Unicode.GetBytes(name + '\0');
            nameBytes.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(64, 2), checked((ushort)nameBytes.Length));
            entry[66] = type;
            entry[67] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(68, 4), freeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(72, 4), freeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(76, 4), freeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(116, 4), startSector);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(120, 8), checked((ulong)streamSize));
        }

        static void WriteMfcCString(BinaryWriter writer, string value, Encoding encoding)
        {
            byte[] bytes = encoding.GetBytes(value);
            if (bytes.Length < byte.MaxValue)
            {
                writer.Write((byte)bytes.Length);
            }
            else if (bytes.Length < ushort.MaxValue)
            {
                writer.Write(byte.MaxValue);
                writer.Write((ushort)bytes.Length);
            }
            else
            {
                writer.Write(byte.MaxValue);
                writer.Write(ushort.MaxValue);
                writer.Write((uint)bytes.Length);
            }

            writer.Write(bytes);
        }
    }

    private sealed class FakeBoard : IBoardTransport
    {
        public List<string> Commands { get; } = [];
        public List<ResistanceStep> ResistanceSteps { get; } = [];
        public int ReleaseResistanceRouteCount { get; private set; }
        public bool ThrowOnSetRelay { get; set; }
        public bool ThrowOnConnect { get; set; }
        public int StartFailuresRemaining { get; set; }
        public int ConnectAttempts { get; private set; }
        public bool IsConnected { get; private set; } = true;
        public bool ProducesPassiveScanFrames { get; set; } = true;
        public bool IsScanning { get; private set; } = true;
        public BoardScanMode CurrentScanMode { get; private set; } = BoardScanMode.Production;
        public BoardCapacity InstalledCapacity { get; private set; } = BoardCapacity.Create(10);
        public BoardCapacity Capacity { get; private set; } = BoardCapacity.Create(10);
        public BoardCapacity? AppliedScanCapacity { get; private set; } = BoardCapacity.Create(10);
        public BoardScanCapacity ScanCapacity { get; private set; } = BoardScanCapacity.Create(
            new ProductionSettings { ExpansionCardCount = 10 },
            640);
        public DateTime LastFrameTimestampUtc { get; private set; } = DateTime.UtcNow;
        public long LastFrameSequence { get; private set; }
        public long LastCompleteFrameSequence { get; private set; }
        public long FramesReceived { get; private set; }
        public long CompleteFramesReceived { get; private set; }
        public int LastFrameSourceCount { get; private set; }
        public byte? LastFrameEndMarkerCode { get; private set; }
        public int LastFrameUnknownBytes { get; private set; }
        public void SetAppliedScanCapacityForTest(int scanUnits) =>
            AppliedScanCapacity = BoardCapacity.Create(scanUnits);
        public void SetRequestedScanCapacityForTest(int scanUnits) =>
            Capacity = BoardCapacity.Create(scanUnits);
        public void SetConnectionStateForTest(bool connected) =>
            IsConnected = connected;
        public CancellationToken? LastStartScanToken { get; private set; }
        public Action<FakeBoard>? StartScanCallback { get; set; }
        private event EventHandler<ScanFrame>? FrameReceivedCore;
        public event EventHandler<ScanFrame>? FrameReceived { add { FrameReceivedCore += value; } remove { FrameReceivedCore -= value; } }
        private event EventHandler<ProductionProbePreview>? ProductionProbePreviewReceivedCore;
        public event EventHandler<ProductionProbePreview>? ProductionProbePreviewReceived { add { ProductionProbePreviewReceivedCore += value; } remove { ProductionProbePreviewReceivedCore -= value; } }
        public event EventHandler<string>? Log { add { } remove { } }
        public void PublishProbePreview(ProductionProbePreview preview) =>
            ProductionProbePreviewReceivedCore?.Invoke(this, preview);
        public void Publish(ScanFrame frame)
        {
            LastFrameTimestampUtc = DateTime.UtcNow;
            LastFrameSequence = frame.Sequence;
            FramesReceived++;
            LastFrameSourceCount = frame.SourceCount;
            LastFrameEndMarkerCode = frame.EndMarkerCode;
            LastFrameUnknownBytes = frame.UnknownBytes;
            if (frame.Mode == BoardScanMode.Production &&
                frame.Complete && frame.UnknownBytes == 0 && frame.TerminatorKnown)
            {
                LastCompleteFrameSequence = frame.Sequence;
                CompleteFramesReceived++;
            }
            FrameReceivedCore?.Invoke(this, frame);
        }
        public Task<BoardConnectionInfo> ConnectAsync(CancellationToken ct = default)
        {
            ConnectAttempts++;
            if (ThrowOnConnect)
                throw new InvalidOperationException("Simulated board connection failure");
            IsConnected = true;
            return Task.FromResult(new BoardConnectionInfo("Fake", "Fake"));
        }
        public Task DisconnectAsync() { IsConnected = false; IsScanning = false; return Task.CompletedTask; }
        public Task HandshakeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetClearAsync(CancellationToken ct = default) { Commands.Add("RESET"); return Task.CompletedTask; }
        public void ConfigureActiveScanRange(int maxIo) { }
        public Task StartScanAsync(BoardScanMode mode = BoardScanMode.Production, CancellationToken ct = default)
        {
            if (StartFailuresRemaining > 0)
            {
                StartFailuresRemaining--;
                throw new InvalidOperationException("Simulated scan start failure");
            }

            IsScanning = true;
            CurrentScanMode = mode;
            AppliedScanCapacity = Capacity;
            LastStartScanToken = ct;
            Commands.Add("START");
            StartScanCallback?.Invoke(this);
            return Task.CompletedTask;
        }
        public Task StopScanAsync(CancellationToken ct = default) { IsScanning = false; Commands.Add("STOP"); return Task.CompletedTask; }
        public Task EnterIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectResistanceRouteAsync(ResistanceStep step, CancellationToken ct = default)
        {
            ResistanceSteps.Add(step);
            Commands.Add($"RESISTORTEST:{step.Channel}");
            return Task.CompletedTask;
        }
        public Task ReleaseResistanceRouteAsync(CancellationToken ct = default)
        {
            ReleaseResistanceRouteCount++;
            Commands.Add("RELEASE_R");
            return Task.CompletedTask;
        }
        public Task SetRelayAsync(int relay, CancellationToken ct = default)
        {
            Commands.Add($"SET:{relay}");
            if (ThrowOnSetRelay)
                throw new InvalidOperationException("Simulated relay set failure");

            return Task.CompletedTask;
        }
        public Task AllRelaysOffAsync(CancellationToken ct = default) { Commands.Add("OFF"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeKeysightVisaService(bool connected, double measurement)
        : KeysightVisaService
    {
        public override bool IsConnected => connected;
        public int MeasureCallCount { get; private set; }
        public bool ThrowOnMeasure { get; set; }
        public Queue<double> Measurements { get; } = new();

        public override double MeasureResistance(string command = ":MEASURE:RES?")
        {
            MeasureCallCount++;
            if (ThrowOnMeasure)
                throw new InvalidOperationException("Simulated Keysight failure");
            return Measurements.Count > 0 ? Measurements.Dequeue() : measurement;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override long TimestampFrequency => 1_000;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
            _timestamp += checked((long)Math.Round(value.TotalMilliseconds));
        }
    }
}
