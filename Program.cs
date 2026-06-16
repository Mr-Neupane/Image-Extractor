using System.Text.Json;
using ImageExtractor.Models;
using ImageExtractor.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string configPath = Path.Combine(AppContext.BaseDirectory, "extraction-config.json");

ExtractionConfig config;

if (File.Exists(configPath))
{
    Console.WriteLine("Existing config found: extraction-config.json");
    Console.WriteLine("  [1] Use existing config and run extraction");
    Console.WriteLine("  [2] Run configuration wizard (overwrite)");
    Console.WriteLine("  [3] Quit");
    Console.Write("Choice [1]: ");
    string? choice = Console.ReadLine()?.Trim();

    switch (choice)
    {
        case "2":
            config = await ConfigWizard.RunAsync(configPath);
            break;
        case "3":
            Console.WriteLine("Exiting.");
            return;
        default:
            try
            {
                string json = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize<ExtractionConfig>(json)
                    ?? throw new InvalidOperationException("Config file is empty or invalid.");
                Console.WriteLine("Using existing config.\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load config: {0}", ex.Message);
                Console.WriteLine("Starting configuration wizard...\n");
                config = await ConfigWizard.RunAsync(configPath);
            }
            break;
    }
}
else
{
    Console.WriteLine("No config found. Starting configuration wizard...\n");
    config = await ConfigWizard.RunAsync(configPath);
}

Console.WriteLine();
var result = await ExporterEngine.RunAsync(config, CancellationToken.None);

Console.WriteLine();
Console.WriteLine(new string('=', 50));
Console.WriteLine("  Extraction Complete");
Console.WriteLine(new string('=', 50));
Console.WriteLine("  Files written:  {0:N0}", result.Written);
Console.WriteLine("  Skipped:        {0:N0}", result.Skipped);
Console.WriteLine("  Failed:         {0:N0}", result.Failed);
Console.WriteLine("  Elapsed:        {0}", result.Elapsed);
if (result.Elapsed.TotalSeconds > 0 && result.Written > 0)
{
    Console.WriteLine("  Speed:          {0:F1} files/s", result.Written / result.Elapsed.TotalSeconds);
}
if (result.Errors.Count > 0)
    Console.WriteLine("  Error log:      extraction-errors.csv");
Console.WriteLine(new string('=', 50));
