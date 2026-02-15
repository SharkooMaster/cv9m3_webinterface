using System.IO;

namespace WebInterface.Services;

public class ChunkStorageService
{
    private readonly string _chunkStorageDirectory;
    private readonly ILogger<ChunkStorageService> _logger;

    public ChunkStorageService(IConfiguration configuration, ILogger<ChunkStorageService> logger)
    {
        // Get chunk storage directory from environment or config
        // Default to /tmp/crossv9_chunks for local testing, or /data/chunks in Docker
        var storageDir = configuration["ChunkStorage:Directory"] 
            ?? Environment.GetEnvironmentVariable("CHUNK_STORAGE_DIR") 
            ?? "/tmp/crossv9_chunks";
        
        // Use the directory directly (Docker volume is mounted at /data/chunks)
        _chunkStorageDirectory = storageDir;
        
        _logger = logger;
    }

    public long GetActualStorageSize()
    {
        try
        {
            if (!Directory.Exists(_chunkStorageDirectory))
            {
                return 0;
            }

            long totalSize = 0;
            var chunksDir = Path.Combine(_chunkStorageDirectory, "chunks");
            
            if (Directory.Exists(chunksDir))
            {
                // Use EnumerateFiles for better performance (lazy evaluation)
                var files = Directory.EnumerateFiles(chunksDir, "*", SearchOption.AllDirectories);
                
                // Parallel processing for faster calculation
                var lockObj = new object();
                Parallel.ForEach(files, file =>
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var size = fileInfo.Length;
                        lock (lockObj)
                        {
                            totalSize += size;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get size for file {File}", file);
                    }
                });
            }

            return totalSize;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate actual storage size");
            return 0;
        }
    }

    public int GetChunkCount()
    {
        try
        {
            if (!Directory.Exists(_chunkStorageDirectory))
            {
                return 0;
            }

            var chunksDir = Path.Combine(_chunkStorageDirectory, "chunks");
            if (!Directory.Exists(chunksDir))
            {
                return 0;
            }

            return Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories).Length;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get chunk count");
            return 0;
        }
    }

    public bool ClearAllChunks()
    {
        try
        {
            var chunksDir = Path.Combine(_chunkStorageDirectory, "chunks");
            if (Directory.Exists(chunksDir))
            {
                Directory.Delete(chunksDir, true);
                Directory.CreateDirectory(chunksDir);
                _logger.LogInformation("Cleared all chunk files from {ChunksDir}", chunksDir);
                return true;
            }
            return true; // Directory doesn't exist, consider it cleared
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear chunk files");
            return false;
        }
    }
}

