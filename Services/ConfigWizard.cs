using System.Text.Json;
using ImageExtractor.Models;
using Microsoft.Data.SqlClient;

namespace ImageExtractor.Services;

public class ConfigWizard
{
    public static async Task<ExtractionConfig> RunAsync(string? existingConfigPath = null)
    {
        Console.WriteLine("=== ImageExtractor Configuration Wizard ===\n");

        var config = new ExtractionConfig();

        string serverConnStr = await PromptServerConnectionAsync();
        string dbName = await PromptDatabaseAsync(serverConnStr);
        string fullConnStr = serverConnStr.TrimEnd(';') + ";Database=" + dbName;
        config.ConnectionString = fullConnStr;

        var table = await PromptTableSelectionAsync(fullConnStr);
        config.Table = table.FullName;

        var allColumns = await SchemaDiscovery.GetTableColumnsAsync(
            fullConnStr, table.Schema, table.Name);

        var binaryColumns = allColumns
            .Where(c => c.DataType is "image" or "varbinary")
            .ToList();
        var namingColumns = allColumns
            .Where(c => c.DataType is not "image" and not "varbinary")
            .ToList();

        config.Columns = await PromptNamingConfigAsync(binaryColumns, namingColumns);

        await PromptOutputSettingsAsync(config);

        await SaveConfigAsync(config, existingConfigPath);

        return config;
    }

    // ─── Step 1: Server connection ────────────────────────────

    private static async Task<string> PromptServerConnectionAsync()
    {
        Console.WriteLine("Step 1: Connect to SQL Server");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Console.Write("Server connection string (or press Enter for default):\n> ");
            string? connStr = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(connStr))
                connStr = "Server=.;Integrated Security=SSPI;";

            var builder = new SqlConnectionStringBuilder(connStr);
            builder.Remove("Database");
            builder.Remove("Initial Catalog");
            string masterConnStr = builder.ConnectionString;

            string? result = await TryConnectWithSslFix(masterConnStr);
            if (result != null)
            {
                Console.WriteLine("OK\n");
                return result;
            }

            if (attempt < 2)
                Console.WriteLine("Please try again.");
        }

        Console.WriteLine("Failed to connect after 3 attempts. Exiting.");
        Environment.Exit(1);
        return null!;
    }

    private static async Task<string?> TryConnectWithSslFix(string connStr)
    {
        Console.Write("Connecting... ");
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            return connStr;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");

            if (!connStr.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Retrying with TrustServerCertificate=True;Encrypt=Optional;");
                string fixedConnStr = connStr.TrimEnd(';')
                    + ";TrustServerCertificate=True;Encrypt=Optional;";

                try
                {
                    await using var conn2 = new SqlConnection(fixedConnStr);
                    await conn2.OpenAsync();
                    Console.WriteLine("Connected with SSL fix.");
                    return fixedConnStr;
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"Still failed: {ex2.Message}");
                    return null;
                }
            }

            return null;
        }
    }

    // ─── Step 2: Database selection ────────────────────────────

    private static async Task<string> PromptDatabaseAsync(string masterConnStr)
    {
        Console.WriteLine("Step 2: Select Database");

        var databases = await SchemaDiscovery.GetDatabasesAsync(masterConnStr);

        if (databases.Count == 0)
        {
            Console.WriteLine("No user databases found. Exiting.");
            Environment.Exit(1);
        }

        for (int i = 0; i < databases.Count; i++)
            Console.WriteLine("  [{0}] {1}", i + 1, databases[i]);

        while (true)
        {
            Console.Write("Enter database number [1-{0}]: ", databases.Count);
            if (int.TryParse(Console.ReadLine(), out int index) && index >= 1 && index <= databases.Count)
            {
                Console.WriteLine("Selected: {0}\n", databases[index - 1]);
                return databases[index - 1];
            }
            Console.WriteLine("Invalid selection.");
        }
    }

    // ─── Step 3: Table selection ──────────────────────────────

    private static async Task<DiscoveredTable> PromptTableSelectionAsync(string connectionString)
    {
        Console.WriteLine("Step 3: Select Table");

        var tables = await SchemaDiscovery.GetTablesWithBinaryColumnsAsync(connectionString);

        if (tables.Count == 0)
        {
            Console.WriteLine("No tables with binary (image/varbinary) columns found in this database. Exiting.");
            Environment.Exit(1);
        }

        for (int i = 0; i < tables.Count; i++)
        {
            Console.WriteLine("  [{0}] {1} ({2} binary column{3})",
                i + 1,
                tables[i].FullName,
                tables[i].BinaryColumnCount,
                tables[i].BinaryColumnCount == 1 ? "" : "s");
        }

        while (true)
        {
            Console.Write("Enter table number [1-{0}]: ", tables.Count);
            if (int.TryParse(Console.ReadLine(), out int index) && index >= 1 && index <= tables.Count)
            {
                Console.WriteLine("Selected: {0}\n", tables[index - 1].FullName);
                return tables[index - 1];
            }
            Console.WriteLine("Invalid selection.");
        }
    }

    // ─── Step 4: Image naming configuration ────────────────────

    private static async Task<List<ColumnMapping>> PromptNamingConfigAsync(
        List<ColumnInfo> binaryColumns, List<ColumnInfo> namingColumns)
    {
        Console.WriteLine("Step 4: Configure Image Naming");

        Console.WriteLine("Binary columns in table:");
        for (int i = 0; i < binaryColumns.Count; i++)
        {
            var c = binaryColumns[i];
            Console.WriteLine("  [{0}] {1,-20} Type: {2,-15} Nullable: {3}",
                i + 1, c.Name,
                c.MaxLength.HasValue ? $"{c.DataType}({c.MaxLength})" : c.DataType,
                c.IsNullable ? "Yes" : "No");
        }

        Console.Write("\nEnter numbers of columns to extract (comma-separated, e.g. 1,2): ");
        var input = Console.ReadLine() ?? "";
        var selections = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 1 && n <= binaryColumns.Count)
            .ToList();

        if (selections.Count == 0)
        {
            Console.WriteLine("No columns selected. Extracting all binary columns.");
            selections = Enumerable.Range(1, binaryColumns.Count).ToList();
        }

        Console.WriteLine("\nAvailable identifier columns: " +
            string.Join(", ", namingColumns.Select(c => c.Name)));

        var mappings = new List<ColumnMapping>();
        foreach (var sel in selections)
        {
            var col = binaryColumns[sel - 1];
            Console.WriteLine("\n--- {0} ---", col.Name);

            string idColumn = PromptIdentifierColumn(namingColumns, col.Name);
            int mode = PromptNamingMode(col.Name);

            string pattern = mode switch
            {
                1 => $"{{{idColumn}}}",
                2 => $"{{{idColumn}}}_{PromptSuffix()}",
                3 => $"{{{idColumn}}}_{{Guid}}",
                4 => $"{{{idColumn}}}_{{BinaryColumnName}}",
                _ => $"{{{idColumn}}}"
            };

            Console.Write("File extension [.jpg]: ");
            string? ext = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            if (!ext.StartsWith('.')) ext = "." + ext;

            Console.Write("Subdirectory (optional, press Enter for none): ");
            string? sub = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(sub)) sub = null;

            mappings.Add(new ColumnMapping
            {
                BinaryColumn = col.Name,
                FileNamePattern = pattern,
                FileExtension = ext,
                SubDirectory = sub
            });
        }

        return mappings;
    }

    private static string PromptIdentifierColumn(List<ColumnInfo> namingColumns, string binaryColName)
    {
        string defaultId = namingColumns.FirstOrDefault()?.Name ?? "Id";
        Console.Write("Identifier column [{0}]: ", defaultId);
        string? input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return defaultId;

        if (namingColumns.Any(c => c.Name.Equals(input, StringComparison.OrdinalIgnoreCase)))
            return input;

        Console.WriteLine("Column '{0}' not found. Using '{1}'.", input, defaultId);
        return defaultId;
    }

    private static int PromptNamingMode(string binaryColName)
    {
        Console.WriteLine("Naming modes:");
        Console.WriteLine("  [1] {{Id only}}            →  {{Id}}.jpg");
        Console.WriteLine("  [2] {{Id}}_suffix          →  {{Id}}_custom.jpg");
        Console.WriteLine("  [3] {{Id}}_{{Guid}}        →  {{Id}}_a1b2c3d4.jpg");
        Console.WriteLine("  [4] {{Id}}_{{ColName}}     →  {{Id}}_{0}.jpg", binaryColName);

        while (true)
        {
            Console.Write("Choose mode [1-4]: ");
            string? input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out int mode) && mode >= 1 && mode <= 4)
                return mode;
            Console.WriteLine("Enter a number between 1 and 4.");
        }
    }

    private static string PromptSuffix()
    {
        Console.Write("Enter suffix string: ");
        return Console.ReadLine()?.Trim() ?? "suffix";
    }

    // ─── Step 5: Output settings ──────────────────────────────

    private static async Task PromptOutputSettingsAsync(ExtractionConfig config)
    {
        Console.WriteLine("\nStep 5: Output Settings");

        Console.Write("Output directory [{0}]: ", config.OutputRoot);
        string? outDir = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(outDir)) config.OutputRoot = outDir;

        try
        {
            Directory.CreateDirectory(config.OutputRoot);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Warning: Could not create output directory: {0}", ex.Message);
        }

        Console.Write("Resume mode (Skip / Overwrite / OverwriteIfSmaller) [{0}]: ", config.ResumeMode);
        string? resume = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(resume)) config.ResumeMode = resume;

        Console.Write("Parallelism (0 = auto, based on CPU cores) [0]: ");
        string? par = Console.ReadLine()?.Trim();
        if (int.TryParse(par, out int p) && p >= 0) config.DegreeOfParallelism = p;
    }

    // ─── Step 6: Save ─────────────────────────────────────────

    private static async Task SaveConfigAsync(ExtractionConfig config, string? configPath)
    {
        Console.WriteLine("\nStep 6: Save & Run");
        string path = configPath ?? Path.Combine(AppContext.BaseDirectory, "extraction-config.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine("Config saved to: {0}", path);
    }
}
