namespace ImageExtractor.Models;

public class ColumnMapping
{
    public string BinaryColumn { get; set; } = string.Empty;
    public string FileNamePattern { get; set; } = string.Empty;
    public string FileExtension { get; set; } = ".jpg";
    public string? SubDirectory { get; set; }
}
