using System.Collections.Concurrent;
using Npgsql;
using WebInterface.Models;

namespace WebInterface.Services;

public class StatisticsService
{
    private readonly string _connectionString;
    private readonly ILogger<StatisticsService> _logger;
    private readonly ChunkStorageService _chunkStorageService;
    
    // Batch statistics for bulk writes
    private readonly ConcurrentQueue<CompressionStatistic> _statisticsQueue = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly int _batchSize = 100; // Write in batches of 100
    private readonly TimeSpan _batchFlushInterval = TimeSpan.FromSeconds(5); // Flush every 5 seconds
    private Timer? _batchFlushTimer;

    public StatisticsService(IConfiguration configuration, ILogger<StatisticsService> logger, ChunkStorageService chunkStorageService)
    {
        _logger = logger;
        _chunkStorageService = chunkStorageService;
        
        // Get database connection from environment or config
        var dbHost = configuration["Database:Host"] ?? "postgres";
        var dbPort = configuration["Database:Port"] ?? "5432";
        var dbUser = configuration["Database:User"] ?? "crossuser";
        var dbPassword = configuration["Database:Password"] ?? "crosspass";
        var dbName = configuration["Database:Database"] ?? "compressiondb";

        // Optimized connection string with pooling for high-throughput operations
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = dbHost,
            Port = int.Parse(dbPort),
            Username = dbUser,
            Password = dbPassword,
            Database = dbName,
            // Connection pooling optimization
            Pooling = true,
            MinPoolSize = 5, // Keep minimum connections ready
            MaxPoolSize = 50, // Allow up to 50 concurrent connections
            ConnectionIdleLifetime = 300, // 5 minutes
            ConnectionPruningInterval = 10, // Check every 10 seconds
            Timeout = 30, // 30 second connection timeout
            CommandTimeout = 60, // 60 second command timeout
            // Performance optimizations
            NoResetOnClose = true, // Don't reset connection state on close (faster)
            Enlist = false // Don't enlist in transactions (faster for bulk operations)
        };
        
        _connectionString = connectionStringBuilder.ConnectionString;
        
        // Ensure statistics table exists
        _ = Task.Run(async () => await EnsureTableExistsAsync());
        
        // Start batch flush timer
        _batchFlushTimer = new Timer(async _ => await FlushBatchAsync(), null, _batchFlushInterval, _batchFlushInterval);
    }

    private async Task EnsureTableExistsAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var createTableCmd = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS compression_statistics (
                    id UUID PRIMARY KEY,
                    file_name VARCHAR(512) NOT NULL,
                    original_size BIGINT NOT NULL,
                    compressed_size BIGINT NOT NULL,
                    compression_ratio DOUBLE PRECISION NOT NULL,
                    savings_bytes BIGINT NOT NULL,
                    savings_percent DOUBLE PRECISION NOT NULL,
                    duration_ms DOUBLE PRECISION NOT NULL,
                    references_found INTEGER NOT NULL DEFAULT 0,
                    total_chunks INTEGER NOT NULL DEFAULT 0,
                    timestamp TIMESTAMP NOT NULL,
                    status VARCHAR(50) DEFAULT 'Success',
                    created_at TIMESTAMP DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_statistics_timestamp ON compression_statistics(timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_statistics_file_name ON compression_statistics(file_name);
            ", conn);

            await createTableCmd.ExecuteNonQueryAsync();

            // Backfill/upgrade existing DBs (idempotent)
            var alterCmd = new NpgsqlCommand(@"
                ALTER TABLE compression_statistics
                    ADD COLUMN IF NOT EXISTS references_found INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE compression_statistics
                    ADD COLUMN IF NOT EXISTS total_chunks INTEGER NOT NULL DEFAULT 0;
            ", conn);
            await alterCmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Statistics table verified/created in PostgreSQL");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure statistics table exists");
        }
    }

    public async Task SaveStatisticsAsync(CompressionStatistic statistic)
    {
        // Add to batch queue for bulk write
        _statisticsQueue.Enqueue(statistic);
        
        // Flush if batch is full
        if (_statisticsQueue.Count >= _batchSize)
        {
            await FlushBatchAsync();
        }
    }
    
    private async Task FlushBatchAsync()
    {
        if (_statisticsQueue.IsEmpty)
            return;
            
        if (!await _batchLock.WaitAsync(0)) // Try to acquire lock, don't wait
            return; // Another flush is in progress
            
        var batch = new List<CompressionStatistic>();
        try
        {
            // Collect all queued statistics
            while (_statisticsQueue.TryDequeue(out var stat))
            {
                batch.Add(stat);
            }
            
            if (batch.Count == 0)
                return;
                
            // Bulk insert using parameterized batch insert (more compatible than COPY)
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            
            // Build batch insert query
            var values = new List<string>();
            var parameters = new List<NpgsqlParameter>();
            int paramIndex = 0;
            
            foreach (var stat in batch)
            {
                var baseIndex = paramIndex * 12;
                values.Add($"(@id{baseIndex}, @fileName{baseIndex}, @originalSize{baseIndex}, @compressedSize{baseIndex}, @compressionRatio{baseIndex}, @savingsBytes{baseIndex}, @savingsPercent{baseIndex}, @durationMs{baseIndex}, @referencesFound{baseIndex}, @totalChunks{baseIndex}, @timestamp{baseIndex}, @status{baseIndex})");
                
                parameters.Add(new NpgsqlParameter($"@id{baseIndex}", stat.Id));
                parameters.Add(new NpgsqlParameter($"@fileName{baseIndex}", stat.FileName));
                parameters.Add(new NpgsqlParameter($"@originalSize{baseIndex}", stat.OriginalSize));
                parameters.Add(new NpgsqlParameter($"@compressedSize{baseIndex}", stat.CompressedSize));
                parameters.Add(new NpgsqlParameter($"@compressionRatio{baseIndex}", stat.CompressionRatio));
                parameters.Add(new NpgsqlParameter($"@savingsBytes{baseIndex}", stat.SavingsBytes));
                parameters.Add(new NpgsqlParameter($"@savingsPercent{baseIndex}", stat.SavingsPercent));
                parameters.Add(new NpgsqlParameter($"@durationMs{baseIndex}", stat.DurationMs));
                parameters.Add(new NpgsqlParameter($"@referencesFound{baseIndex}", stat.ReferencesFound));
                parameters.Add(new NpgsqlParameter($"@totalChunks{baseIndex}", stat.TotalChunks));
                parameters.Add(new NpgsqlParameter($"@timestamp{baseIndex}", stat.Timestamp));
                parameters.Add(new NpgsqlParameter($"@status{baseIndex}", stat.Status));
                
                paramIndex++;
            }
            
            var sql = $@"
                INSERT INTO compression_statistics 
                (id, file_name, original_size, compressed_size, compression_ratio, savings_bytes, savings_percent, duration_ms, references_found, total_chunks, timestamp, status)
                VALUES {string.Join(", ", values)}
                ON CONFLICT (id) DO UPDATE SET
                    file_name = EXCLUDED.file_name,
                    original_size = EXCLUDED.original_size,
                    compressed_size = EXCLUDED.compressed_size,
                    compression_ratio = EXCLUDED.compression_ratio,
                    savings_bytes = EXCLUDED.savings_bytes,
                    savings_percent = EXCLUDED.savings_percent,
                    duration_ms = EXCLUDED.duration_ms,
                    references_found = EXCLUDED.references_found,
                    total_chunks = EXCLUDED.total_chunks,
                    timestamp = EXCLUDED.timestamp,
                    status = EXCLUDED.status
            ";
            
            var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddRange(parameters.ToArray());
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Bulk saved {Count} statistics records", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush statistics batch");
            // Re-queue failed statistics for retry
            foreach (var stat in batch)
            {
                _statisticsQueue.Enqueue(stat);
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }
    
    public async Task FlushAllStatisticsAsync()
    {
        // Force flush all remaining statistics
        await FlushBatchAsync();
    }

    public async Task<CompressionStatistic?> GetStatisticAsync(Guid id)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = new NpgsqlCommand(@"
                SELECT id, file_name, original_size, compressed_size, compression_ratio, 
                       savings_bytes, savings_percent, duration_ms, references_found, total_chunks, timestamp, status
                FROM compression_statistics
                WHERE id = @id
            ", conn);

            cmd.Parameters.AddWithValue("@id", id);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new CompressionStatistic
                {
                    Id = reader.GetGuid(0),
                    FileName = reader.GetString(1),
                    OriginalSize = reader.GetInt64(2),
                    CompressedSize = reader.GetInt64(3),
                    CompressionRatio = reader.GetDouble(4),
                    SavingsBytes = reader.GetInt64(5),
                    SavingsPercent = reader.GetDouble(6),
                    DurationMs = reader.GetDouble(7),
                    ReferencesFound = reader.GetInt32(8),
                    TotalChunks = reader.GetInt32(9),
                    Timestamp = reader.GetDateTime(10),
                    Status = reader.GetString(11)
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get statistic for {Id}", id);
            return null;
        }
    }

    public async Task<List<CompressionStatistic>> GetStatisticsAsync(int limit = 100)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = new NpgsqlCommand(@"
                SELECT id, file_name, original_size, compressed_size, compression_ratio, 
                       savings_bytes, savings_percent, duration_ms, references_found, total_chunks, timestamp, status
                FROM compression_statistics
                ORDER BY timestamp DESC
                LIMIT @limit
            ", conn);

            cmd.Parameters.AddWithValue("@limit", limit);

            var stats = new List<CompressionStatistic>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stats.Add(new CompressionStatistic
                {
                    Id = reader.GetGuid(0),
                    FileName = reader.GetString(1),
                    OriginalSize = reader.GetInt64(2),
                    CompressedSize = reader.GetInt64(3),
                    CompressionRatio = reader.GetDouble(4),
                    SavingsBytes = reader.GetInt64(5),
                    SavingsPercent = reader.GetDouble(6),
                    DurationMs = reader.GetDouble(7),
                    ReferencesFound = reader.GetInt32(8),
                    TotalChunks = reader.GetInt32(9),
                    Timestamp = reader.GetDateTime(10),
                    Status = reader.GetString(11)
                });
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get statistics");
            return new List<CompressionStatistic>();
        }
    }

    public async Task<StatisticsSummary> GetSummaryAsync()
    {
        try
        {
            // Get actual storage size from local filesystem
            var actualStorageBytes = _chunkStorageService.GetActualStorageSize();
            var chunkCount = _chunkStorageService.GetChunkCount();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = new NpgsqlCommand(@"
                SELECT 
                    COUNT(*) as total_compressions,
                    COALESCE(SUM(original_size), 0) as total_original_size,
                    COALESCE(SUM(compressed_size), 0) as total_compressed_size,
                    COALESCE(AVG(compression_ratio), 0) as avg_compression_ratio,
                    COALESCE(AVG(savings_percent), 0) as avg_savings_percent,
                    COALESCE(AVG(duration_ms), 0) as avg_duration_ms,
                    COALESCE(MIN(compression_ratio), 0) as best_compression_ratio,
                    COALESCE(MAX(compression_ratio), 0) as worst_compression_ratio
                FROM compression_statistics
            ", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var totalCompressions = reader.GetInt32(0);
                
                if (totalCompressions == 0)
                {
                    return new StatisticsSummary
                    {
                        TotalChunkBytes = actualStorageBytes,
                        TotalChunksStored = chunkCount
                    };
                }

                return new StatisticsSummary
                {
                    TotalCompressions = totalCompressions,
                    TotalOriginalSize = reader.GetInt64(1),
                    TotalCompressedSize = reader.GetInt64(2),
                    TotalChunksStored = chunkCount,
                    TotalChunkBytes = actualStorageBytes,
                    AverageCompressionRatio = reader.GetDouble(3),
                    AverageSavingsPercent = reader.GetDouble(4),
                    AverageDurationMs = reader.GetDouble(5),
                    BestCompressionRatio = reader.GetDouble(6),
                    WorstCompressionRatio = reader.GetDouble(7)
                };
            }

            return new StatisticsSummary
            {
                TotalChunkBytes = actualStorageBytes,
                TotalChunksStored = chunkCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get summary statistics");
            // Return basic summary with chunk storage info even if DB query fails
            return new StatisticsSummary
            {
                TotalChunkBytes = _chunkStorageService.GetActualStorageSize(),
                TotalChunksStored = _chunkStorageService.GetChunkCount()
            };
        }
    }

    public async Task<bool> ClearStatisticsAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = new NpgsqlCommand("TRUNCATE TABLE compression_statistics", conn);
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Cleared compression_statistics table");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear statistics");
            return false;
        }
    }
}

