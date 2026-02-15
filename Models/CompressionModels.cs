namespace WebInterface.Models;

public class CompressionResponse
{
    public bool Success { get; set; }
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public double CompressionRatio { get; set; }
    public long SavingsBytes { get; set; }
    public double SavingsPercent { get; set; }
    public double DurationMs { get; set; }
    public int ReferencesFound { get; set; }
    public int TotalChunks { get; set; }
    public string CompressedData { get; set; } = string.Empty;
    public Guid StatisticId { get; set; }
}

public class DecompressRequest
{
    public string CompressedData { get; set; } = string.Empty;
}

public class DecompressResponse
{
    public bool Success { get; set; }
    public long DecompressedSize { get; set; }
    public double DurationMs { get; set; }
    public string DecompressedData { get; set; } = string.Empty;
}

public class CompressionStatistic
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public double CompressionRatio { get; set; }
    public long SavingsBytes { get; set; }
    public double SavingsPercent { get; set; }
    public double DurationMs { get; set; }
    public int ReferencesFound { get; set; }
    public int TotalChunks { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "Success";
}

public class StatisticsSummary
{
    public int TotalCompressions { get; set; }
    public long TotalOriginalSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public long TotalChunksStored { get; set; }  // Total unique chunks stored
    public long TotalChunkBytes { get; set; }    // (A) Actual storage size - total bytes of chunks on disk
    public double AverageCompressionRatio { get; set; }
    public double AverageSavingsPercent { get; set; }
    public double AverageDurationMs { get; set; }
    public double BestCompressionRatio { get; set; }
    public double WorstCompressionRatio { get; set; }
}

