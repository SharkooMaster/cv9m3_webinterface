namespace WebInterface.Models;

public class DirectoryCompressionProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SuccessfulFiles { get; set; }
    public int FailedFiles { get; set; }
    public long TotalOriginalSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public long TotalChunkBytes { get; set; } // (A) Actual storage size
    public long CurrentFileOriginalSize { get; set; }
    public long CurrentFileCompressedSize { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public double ProgressPercent { get; set; }
    public double ElapsedMs { get; set; }
    public string Status { get; set; } = "Processing";
}

public class DirectoryCompressionResult
{
    public bool Success { get; set; }
    public int TotalFiles { get; set; }
    public int SuccessfulFiles { get; set; }
    public int FailedFiles { get; set; }
    public long TotalOriginalSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public long TotalSavings { get; set; }
    public double CompressionRatio { get; set; }
    public double TotalDurationMs { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public List<DirectoryFileResult> Files { get; set; } = new();
}

public class DirectoryFileResult
{
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public double CompressionRatio { get; set; }
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
}

