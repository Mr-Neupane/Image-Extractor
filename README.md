# Image Extractor

A .NET 8 console application that extracts images from binary data (`image` / `varbinary` columns) stored in a SQL Server database and writes them to disk as files.

## Features

- **Interactive configuration wizard** — connects to SQL Server, discovers databases and tables, and builds the extraction config for you.
- **Automatic schema discovery** — finds tables containing `image` / `varbinary` columns and lets you pick which ones to extract.
- **Multi-column extraction** — pull several binary columns from a single table in one run.
- **Parallel extraction** — one producer reads rows while multiple consumer tasks write files (via a bounded `Channel`).
- **Resumable runs** — choose how existing output files are handled instead of always starting over.
- **Live progress** — written / skipped / failed counts, throughput, elapsed time and ETA.
- **Error log** — per-row failures are written to `extraction-errors.csv`.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Access to a SQL Server instance containing `image` or `varbinary` data
- Only dependency: `Microsoft.Data.SqlClient` 7.0.1 (restored automatically by `dotnet`)

## Build & Run

```bash
dotnet build
dotnet run
```

On first run there is no config, so the configuration wizard starts automatically and saves its result to `extraction-config.json` next to the executable. On later runs you are prompted:

```
Existing config found: extraction-config.json
  [1] Use existing config and run extraction
  [2] Run configuration wizard (overwrite)
  [3] Quit
Choice [1]:
```

Press `Ctrl+C` at any time to cancel; the app stops after finishing the current work.

## How It Works

The wizard runs in six steps:

1. **Connect to SQL Server** — enter a connection string (or press Enter for `Server=.;Integrated Security=SSPI;`). If the connection fails, the wizard retries with `TrustServerCertificate=True;Encrypt=Optional;`.
2. **Select database** — pick from user databases (system databases are excluded).
3. **Select table** — pick from tables that contain binary columns.
4. **Configure image naming** — choose which binary columns to extract and how to name each output file.
5. **Output settings** — output directory, resume mode and parallelism.
6. **Save & run** — writes `extraction-config.json` and starts the extraction.

Extraction uses a producer/consumer pipeline: a producer reads rows sequentially from the source table and pushes image records into a bounded channel, while consumer tasks write files in parallel. Rows whose naming columns are `NULL` are skipped.

## Configuration

The config is stored as JSON (`extraction-config.json`):

```json
{
  "ConnectionString": "Server=.;Database=MyDb;Integrated Security=SSPI;",
  "Table": "[dbo].[Documents]",
  "OutputRoot": "D:\\ImageFromApp",
  "ResumeMode": "Skip",
  "DegreeOfParallelism": 0,
  "Columns": [
    {
      "BinaryColumn": "Photo",
      "FileNamePattern": "{Id}",
      "FileExtension": ".jpg",
      "SubDirectory": "photos"
    }
  ]
}
```

| Field | Description |
| --- | --- |
| `ConnectionString` | Full SQL Server connection string including the database. |
| `Table` | Fully qualified table name, e.g. `[dbo].[Documents]`. |
| `OutputRoot` | Root directory for extracted files. |
| `ResumeMode` | How to handle files that already exist (see below). |
| `DegreeOfParallelism` | Number of writer tasks; `0` = auto (CPU core count). |
| `Columns` | One entry per binary column to extract. |

Each `Columns` entry (`ColumnMapping`):

| Field | Description |
| --- | --- |
| `BinaryColumn` | Name of the source `image` / `varbinary` column. |
| `FileNamePattern` | Output filename template (without extension). |
| `FileExtension` | Output extension, e.g. `.jpg` (default). |
| `SubDirectory` | Optional subdirectory under `OutputRoot`. |

## File Naming

`FileNamePattern` supports column placeholders and two reserved tokens:

- `{ColumnName}` — replaced with the value of that column from the current row.
- `{Guid}` — replaced with a new random GUID.
- `{BinaryColumnName}` — replaced with the binary column's name.

The wizard offers four naming modes:

| Mode | Pattern | Example |
| --- | --- | --- |
| 1 | `{Id}` | `123.jpg` |
| 2 | `{Id}_suffix` | `123_custom.jpg` |
| 3 | `{Id}_{Guid}` | `123_a1b2c3d4e5f6.jpg` |
| 4 | `{Id}_{BinaryColumnName}` | `123_Photo.jpg` |

Invalid filename characters are replaced with `_`.

## Resume Modes

| Mode | Behavior for an existing file |
| --- | --- |
| `Skip` | Leave it untouched (default). |
| `OverwriteIfSmaller` | Rewrite only if the existing file is smaller than the new data. |
| `Overwrite` | Always rewrite. |

## Output

After a run the console prints a summary:

```
  Files written:  ...
  Skipped:        ...
  Failed:         ...
  Elapsed:        ...
  Speed:          ... files/s
```

If any rows failed, details are written to `extraction-errors.csv` with the columns `RowKey,BinaryColumn,FilePath,ErrorMessage`.

## Project Layout

```
ImageExtractor.csproj
Program.cs                 # Entry point: config load / wizard / run
Models/
  ExtractionConfig.cs      # Top-level config model
  ColumnMapping.cs         # Per-column mapping model
Services/
  ConfigWizard.cs          # Interactive configuration wizard
  SchemaDiscovery.cs       # Database / table / column discovery
  ExporterEngine.cs        # Producer/consumer extraction engine
  ImageNaming.cs           # Filename pattern resolution
  Progress.cs              # Progress reporting
```
