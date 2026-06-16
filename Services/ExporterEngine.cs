using System.Threading.Channels;
using ImageExtractor.Models;
using Microsoft.Data.SqlClient;

namespace ImageExtractor.Services;

internal record ImageRecord(string FilePath, byte[] Data, string BinaryColumn, string RowKey);

public class ExportResult
{
    public long Written { get; set; }
    public long Skipped { get; set; }
    public long Failed { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class ExporterEngine
{
    public static async Task<ExportResult> RunAsync(ExtractionConfig config, CancellationToken ct)
    {
        var errorLock = new object();
        var errors = new List<string>();

        var patternColumns = ExtractPatternColumns(config);
        var binaryColumns = config.Columns.Select(c => c.BinaryColumn).ToList();
        var allSelectColumns = patternColumns.Concat(binaryColumns).ToList();

        var selectList = string.Join(", ", allSelectColumns.Select(c => $"[{c}]"));
        var sql = $"SELECT {selectList} FROM {config.Table}";

        long totalRows = await GetCountAsync(config, ct);
        Console.WriteLine("Total rows in table: {0:N0}", totalRows);
        Console.WriteLine("Starting extraction...\n");

        var channel = Channel.CreateBounded<ImageRecord>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });

        int dop = config.DegreeOfParallelism > 0 ? config.DegreeOfParallelism : Environment.ProcessorCount;
        long totalExpected = totalRows * config.Columns.Count;
        var progress = new Progress(Math.Max(totalExpected, 1));
        progress.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var producer = RunProducerAsync(config, sql, patternColumns, cts.Token, channel, errors, errorLock);

        var consumers = new Task[dop];
        for (int i = 0; i < dop; i++)
            consumers[i] = RunConsumerAsync(config, cts.Token, channel, progress, errors, errorLock);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancellation requested, finishing current work...");
            cts.Cancel();
        };

        try
        {
            await producer;
            await Task.WhenAll(consumers);
        }
        catch (OperationCanceledException) { }

        progress.Stop();

        if (errors.Count > 0)
        {
            string errorPath = Path.Combine(AppContext.BaseDirectory, "extraction-errors.csv");
            var lines = new List<string> { "RowKey,BinaryColumn,FilePath,ErrorMessage" };
            lines.AddRange(errors);
            await File.WriteAllLinesAsync(errorPath, lines);
            Console.WriteLine("Errors logged to: {0}", errorPath);
        }

        return new ExportResult
        {
            Written = progress.WrittenCount,
            Skipped = progress.SkippedCount,
            Failed = progress.FailedCount,
            Elapsed = progress.ElapsedTime,
            Errors = errors
        };
    }

    private static HashSet<string> ExtractPatternColumns(ExtractionConfig config)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in config.Columns)
            foreach (string refCol in ImageNaming.ExtractColumnReferences(col.FileNamePattern))
                cols.Add(refCol);
        return cols;
    }

    private static async Task<long> GetCountAsync(ExtractionConfig config, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(config.ConnectionString);
            await conn.OpenAsync(ct);
            var cmd = new SqlCommand($"SELECT COUNT(*) FROM {config.Table}", conn);
            return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task RunProducerAsync(
        ExtractionConfig config,
        string sql,
        HashSet<string> patternColumns,
        CancellationToken ct,
        Channel<ImageRecord> channel,
        List<string> errors,
        object errorLock)
    {
        try
        {
            await using var conn = new SqlConnection(config.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess, ct);

            var ordinalMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinalMap[reader.GetName(i)] = i;

            var patternColList = patternColumns.ToList();
            var patternOrdinals = patternColList
                .Select(c => (Name: c, Ordinal: ordinalMap.GetValueOrDefault(c, -1)))
                .Where(x => x.Ordinal >= 0)
                .ToList();

            var orderedMappings = config.Columns
                .Select(m => (Mapping: m, Ordinal: ordinalMap.GetValueOrDefault(m.BinaryColumn, -1)))
                .Where(x => x.Ordinal >= 0)
                .OrderBy(x => x.Ordinal)
                .ToList();

            while (await reader.ReadAsync(ct))
            {
                var patternValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool skipRow = false;

                foreach (var (name, ord) in patternOrdinals)
                {
                    if (reader.IsDBNull(ord))
                    {
                        skipRow = true;
                        break;
                    }
                    patternValues[name] = reader.GetValue(ord).ToString() ?? "";
                }
                if (skipRow) continue;

                foreach (var (mapping, _) in orderedMappings)
                {
                    if (!ordinalMap.TryGetValue(mapping.BinaryColumn, out var binaryOrd)) continue;
                    if (reader.IsDBNull(binaryOrd)) continue;

                    byte[] data;

                    try
                    {
                        data = reader.GetSqlBytes(binaryOrd).Value;
                    }
                    catch (Exception ex)
                    {
                        lock (errorLock)
                            errors.Add($"NA,{mapping.BinaryColumn},NA,READ_FAILED:{ex.Message}");
                        continue;
                    }

                    string fileName = ImageNaming.Resolve(
                        mapping.FileNamePattern, patternValues, mapping.BinaryColumn);

                    string fileDir = string.IsNullOrEmpty(mapping.SubDirectory)
                        ? config.OutputRoot
                        : Path.Combine(config.OutputRoot, mapping.SubDirectory);

                    string filePath = Path.Combine(fileDir, fileName + mapping.FileExtension);

                    await channel.Writer.WriteAsync(
                        new ImageRecord(filePath, data, mapping.BinaryColumn, fileName), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine("\nProducer error: {0}", ex.Message);
            lock (errorLock) errors.Add($"PRODUCER,NA,NA,{ex.Message}");
        }
        finally
        {
            channel.Writer.Complete();
        }
    }

    private static async Task RunConsumerAsync(
        ExtractionConfig config,
        CancellationToken ct,
        Channel<ImageRecord> channel,
        Progress progress,
        List<string> errors,
        object errorLock)
    {
        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    string dir = Path.GetDirectoryName(record.FilePath)!;
                    Directory.CreateDirectory(dir);

                    bool shouldWrite = ShouldWriteFile(record.FilePath, record.Data.Length, config.ResumeMode);

                    if (shouldWrite)
                    {
                        await File.WriteAllBytesAsync(record.FilePath, record.Data, ct);
                        progress.Written();
                    }
                    else
                    {
                        progress.Skipped();
                    }
                }
                catch (Exception ex)
                {
                    progress.Failed();
                    lock (errorLock)
                        errors.Add($"{record.RowKey},{record.BinaryColumn},{record.FilePath},{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                lock (errorLock) errors.Add($"CONSUMER,NA,NA,{ex.Message}");
            }
        }
    }

    private static bool ShouldWriteFile(string filePath, long dataLength, string resumeMode)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return true;

        return resumeMode.ToLowerInvariant() switch
        {
            "skip" => false,
            "overwriteifsmaller" => fileInfo.Length < dataLength,
            _ => true
        };
    }

}
