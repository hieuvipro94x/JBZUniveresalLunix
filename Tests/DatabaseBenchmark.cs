using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JBZUniveresalLunix.SelfTests;

internal static class DatabaseBenchmark
{
    private const int Iterations = 30;

    public static int Run(int testRows)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JBZSQLiteBenchmark",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string seedPath = Path.Combine(root, "seed.db");
        string beforePath = Path.Combine(root, "before.db");
        string afterPath = Path.Combine(root, "after.db");
        try
        {
            Console.WriteLine($"SQLite benchmark: synthetic={testRows:N0} tests; iterations={Iterations}; production DB is not used");
            Seed(seedPath, testRows);
            SqliteConnection.ClearAllPools();
            File.Copy(seedPath, beforePath);
            File.Copy(seedPath, afterPath);
            ConfigureBefore(beforePath);
            ConfigureAfter(afterPath);

            BenchmarkResult[] before = RunWorkload(beforePath, testRows, optimized: false);
            BenchmarkResult[] after = RunWorkload(afterPath, testRows, optimized: true);
            Console.WriteLine("| Query | Before plan | After plan | Before avg/p50/p95 ms | After avg/p50/p95 ms | Rows | Index after |");
            Console.WriteLine("|---|---|---|---:|---:|---:|---|");
            for (int i = 0; i < before.Length; i++)
            {
                BenchmarkResult b = before[i];
                BenchmarkResult a = after[i];
                Console.WriteLine(
                    $"| {a.Name} | {Cell(b.Plan)} | {Cell(a.Plan)} | " +
                    $"{b.Average:0.###}/{b.P50:0.###}/{b.P95:0.###} | " +
                    $"{a.Average:0.###}/{a.P50:0.###}/{a.P95:0.###} | {a.Rows:N0} | {Cell(a.Index)} |");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void Seed(string path, int testRows)
    {
        _ = new Services.TestHistoryStore(path);
        using var connection = Open(path);
        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM SchemaInfo; DELETE FROM PartModels; DELETE FROM Models; DELETE FROM Parts; DELETE FROM ConfigSnapshots;");
        Execute(connection, transaction, """
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100)
            INSERT INTO Parts(Id,PartKey,PartNumber,PartName,FirstUseAt,LastUseAt,CreatedAt,UpdatedAt)
            SELECT x,'PN:PART-'||printf('%03d',x),'PART-'||printf('%03d',x),'PRODUCT-'||x,
                   '2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000',
                   '2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000' FROM n;
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<200)
            INSERT INTO Models(Id,PartId,ModelKey,FilePath,FileName,FileHash,ModelName,FirstUsedAt,LastUsedAt,CreatedAt,UpdatedAt)
            SELECT x,((x-1)%100)+1,'MODEL-'||printf('%03d',x),'C:\\Models\\M'||x||'.tht','M'||x||'.tht',
                   printf('%064d',x),'MODEL-'||x,'2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000',
                   '2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000' FROM n;
            INSERT INTO PartModels(PartId,ModelId,FirstUsedAt,LastUsedAt)
            SELECT PartId,Id,'2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000' FROM Models;
            INSERT OR IGNORE INTO PartModels(PartId,ModelId,FirstUsedAt,LastUsedAt)
            SELECT (PartId%100)+1,Id,'2026-01-01T00:00:00.0000000','2026-09-07T00:00:00.0000000' FROM Models;
            INSERT INTO ConfigSnapshots(Id,ConfigHash,CreatedAt) VALUES(1,'BENCH','2026-01-01T00:00:00.0000000');
            """);

        using (SqliteCommand tests = connection.CreateCommand())
        {
            tests.Transaction = transaction;
            tests.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<$Rows)
                INSERT INTO Tests
                    (Id,CycleId,PartId,ModelId,ConfigId,InspectionType,Lot,StartedAt,ResultAt,FinishedAt,
                     Passed,Result,FaultDetailsJson,LabelPayload,CreatedAt)
                SELECT x,'BENCH-'||x,((x-1)%100)+1,((x-1)%200)+1,1,'PRODUCT',x,
                       strftime('%Y-%m-%dT%H:%M:%f','2024-01-01','+'||x||' seconds'),
                       strftime('%Y-%m-%dT%H:%M:%f','2024-01-01','+'||x||' seconds'),
                       strftime('%Y-%m-%dT%H:%M:%f','2024-01-01','+'||x||' seconds'),
                       CASE WHEN x%10=0 THEN 0 ELSE 1 END,
                       CASE WHEN x%10=0 THEN 'FAIL' ELSE 'PASS' END,
                       CASE WHEN x%10=0 THEN '[{"Type":1,"Message":"OPEN"}]' ELSE '' END,
                       CASE WHEN x%10<>0 THEN printf('%.*c',512,'P') ELSE '' END,
                       strftime('%Y-%m-%dT%H:%M:%f','2024-01-01','+'||x||' seconds') FROM n;
                """;
            tests.Parameters.AddWithValue("$Rows", testRows);
            tests.ExecuteNonQuery();
        }
        Execute(connection, transaction, """
            INSERT INTO TestFaults(TestId,FaultOrder,FaultType,FaultCode,Message,CreatedAt)
            SELECT Id,0,'OpenCircuit','OPEN_CIRCUIT','OPEN',ResultAt FROM Tests WHERE Id%10=0;
            INSERT INTO ResistanceMeasurements(TestId,Channel,Name,MeasuredOhm,MinOhm,MaxOhm,Passed)
            SELECT Id,1,'R1',100,90,110,1 FROM Tests
            UNION ALL SELECT Id,2,'R2',101,90,110,1 FROM Tests;
            INSERT INTO WaterProofMeasurements(TestId,Channel,Enabled,FirstPressure,SecondPressure,Leak,Passed)
            SELECT Id,1,1,84,83.8,0.2,1 FROM Tests;
            UPDATE Parts SET
                TotalTests=(SELECT COUNT(*) FROM Tests WHERE PartId=Parts.Id),
                TotalPass=(SELECT COUNT(*) FROM Tests WHERE PartId=Parts.Id AND Passed=1),
                TotalFail=(SELECT COUNT(*) FROM Tests WHERE PartId=Parts.Id AND Passed=0);
            """);
        transaction.Commit();
    }

    private static void ConfigureBefore(string path)
    {
        using var connection = Open(path);
        Execute(connection, null, """
            DROP INDEX IF EXISTS IX_PartModels_ModelId_PartId;
            DROP INDEX IF EXISTS IX_Tests_ResultAt_Id;
            DROP INDEX IF EXISTS IX_Tests_Part_Inspection_ResultAt_Id;
            DROP INDEX IF EXISTS IX_Tests_ModelId_ResultAt_Id;
            DROP INDEX IF EXISTS IX_TestFaults_TestId_FaultOrder_Id;
            DROP INDEX IF EXISTS IX_ResistanceMeasurements_TestId;
            DROP INDEX IF EXISTS IX_WaterProofMeasurements_TestId;
            CREATE INDEX IF NOT EXISTS IX_Tests_ResultAt ON Tests(ResultAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Tests_Part_ResultAt ON Tests(PartId,ResultAt DESC);
            CREATE INDEX IF NOT EXISTS IX_TestFaults_TestId ON TestFaults(TestId);
            """);
    }

    private static void ConfigureAfter(string path)
    {
        using var connection = Open(path);
        Execute(connection, null, "PRAGMA optimize;");
    }

    private static BenchmarkResult[] RunWorkload(string path, int testRows, bool optimized)
    {
        using var connection = Open(path);
        long targetTest = testRows - (testRows % 10);
        string pagingSql = optimized
            ? "SELECT Id,ResultAt FROM Tests WHERE (ResultAt<$Cursor OR (ResultAt=$Cursor AND Id<$Id)) ORDER BY ResultAt DESC,Id DESC LIMIT 200;"
            : $"SELECT Id,ResultAt FROM Tests ORDER BY ResultAt DESC,Id DESC LIMIT 200 OFFSET {Math.Max(0, testRows - 200)};";
        string cursor = ScalarText(connection, $"SELECT ResultAt FROM Tests WHERE Id={Math.Min(testRows, 200)};");
        var queries = new (string Name, string Sql, Action<SqliteCommand>? Bind)[]
        {
            ("latest tests", "SELECT Id,ResultAt,Result FROM Tests ORDER BY ResultAt DESC,Id DESC LIMIT 200;", null),
            ("history by Part", "SELECT Id,ResultAt FROM Tests WHERE PartId=$Id AND InspectionType='PRODUCT' ORDER BY ResultAt DESC,Id DESC LIMIT 200;", c => c.Parameters.AddWithValue("$Id", 42)),
            ("history by Model", "SELECT Id,ResultAt FROM Tests WHERE ModelId=$Id ORDER BY ResultAt DESC,Id DESC LIMIT 200;", c => c.Parameters.AddWithValue("$Id", 42)),
            ("faults by TestId", "SELECT * FROM TestFaults WHERE TestId=$Id ORDER BY FaultOrder,Id;", c => c.Parameters.AddWithValue("$Id", targetTest)),
            ("resistance by TestId", "SELECT * FROM ResistanceMeasurements WHERE TestId=$Id;", c => c.Parameters.AddWithValue("$Id", targetTest)),
            ("waterproof by TestId", "SELECT * FROM WaterProofMeasurements WHERE TestId=$Id;", c => c.Parameters.AddWithValue("$Id", targetTest)),
            ("Part -> Models", "SELECT ModelId FROM PartModels WHERE PartId=$Id;", c => c.Parameters.AddWithValue("$Id", 42)),
            ("Model -> Parts", "SELECT PartId FROM PartModels WHERE ModelId=$Id;", c => c.Parameters.AddWithValue("$Id", 42)),
            ("export/history paging", pagingSql, optimized ? c => { c.Parameters.AddWithValue("$Cursor", cursor); c.Parameters.AddWithValue("$Id", 200); } : null)
        };

        var results = new List<BenchmarkResult>();
        results.Add(BenchmarkInsert(connection));
        foreach ((string name, string sql, Action<SqliteCommand>? bind) in queries)
            results.Add(BenchmarkQuery(connection, name, sql, bind));
        return results.ToArray();
    }

    private static BenchmarkResult BenchmarkInsert(SqliteConnection connection)
    {
        var samples = new List<double>();
        for (int i = 0; i < Iterations + 3; i++)
        {
            long id = 2_000_000 + i;
            long started = Stopwatch.GetTimestamp();
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, transaction,
                $"INSERT INTO Tests(Id,CycleId,PartId,ModelId,ConfigId,StartedAt,ResultAt,FinishedAt,CreatedAt) VALUES({id},'INSERT-{id}',1,1,1,'2026-09-07T00:00:00','2026-09-07T00:00:00','2026-09-07T00:00:00','2026-09-07T00:00:00');" +
                $"INSERT INTO TestFaults(TestId,FaultOrder,CreatedAt) VALUES({id},0,'2026-09-07T00:00:00');" +
                $"INSERT INTO ResistanceMeasurements(TestId,Channel) VALUES({id},1);" +
                $"INSERT INTO WaterProofMeasurements(TestId,Channel) VALUES({id},1);");
            transaction.Commit();
            if (i >= 3)
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return Result("insert completed test", "atomic transaction", samples, 1, "all FK indexes");
    }

    private static BenchmarkResult BenchmarkQuery(
        SqliteConnection connection,
        string name,
        string sql,
        Action<SqliteCommand>? bind)
    {
        string plan = Explain(connection, sql, bind);
        var samples = new List<double>();
        int rows = 0;
        for (int i = 0; i < Iterations + 3; i++)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);
            long started = Stopwatch.GetTimestamp();
            using SqliteDataReader reader = command.ExecuteReader();
            int currentRows = 0;
            while (reader.Read())
                currentRows++;
            if (i >= 3)
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            rows = currentRows;
        }
        string index = plan.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("IX_", StringComparison.Ordinal)) ?? "none/PK";
        return Result(name, plan, samples, rows, index);
    }

    private static BenchmarkResult Result(
        string name,
        string plan,
        List<double> samples,
        int rows,
        string index)
    {
        samples.Sort();
        return new BenchmarkResult(
            name,
            plan,
            samples.Average(),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            rows,
            index);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile) =>
        values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];

    private static string Explain(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand>? bind)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        bind?.Invoke(command);
        using SqliteDataReader reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join("; ", details);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=5");
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Cell(string value) => value.Replace("|", "/", StringComparison.Ordinal);

    private sealed record BenchmarkResult(
        string Name,
        string Plan,
        double Average,
        double P50,
        double P95,
        int Rows,
        string Index);
}
