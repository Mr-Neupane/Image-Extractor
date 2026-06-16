namespace ImageExtractor.Models;

public class ExtractionConfig
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = "D:\\ImageFromApp";
    public string ResumeMode { get; set; } = "Skip";
    public int DegreeOfParallelism { get; set; } = 0;
    public List<ColumnMapping> Columns { get; set; } = new();
}
