using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using WebInterface.Services;
using WebInterface.Models;
using System.Threading;

namespace WebInterface.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DirectoryController : ControllerBase
{
    private readonly CompressionService _compressionService;
    private readonly StatisticsService _statisticsService;
    private readonly ChunkStorageService _chunkStorageService;
    private readonly DynamicResourceManager _resourceManager;
    private readonly ILogger<DirectoryController> _logger;

    public DirectoryController(
        CompressionService compressionService,
        StatisticsService statisticsService,
        ChunkStorageService chunkStorageService,
        DynamicResourceManager resourceManager,
        ILogger<DirectoryController> logger)
    {
        _compressionService = compressionService;
        _statisticsService = statisticsService;
        _chunkStorageService = chunkStorageService;
        _resourceManager = resourceManager;
        _logger = logger;
    }

    [HttpPost("compress-directory")]
    public async Task CompressDirectory([FromForm] string inputDirectory, [FromForm] string outputDirectory, CancellationToken cancellationToken = default)
    {
        // CRITICAL: Set up SSE streaming FIRST, before any validation or processing
        // This MUST happen before any Response.WriteAsync calls
        
        // Disable ALL response buffering for real-time Server-Sent Events
        var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        if (feature != null)
        {
            feature.DisableBuffering();
        }
        
        // Set response headers for SSE
        Response.StatusCode = 200; // Set OK status first
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // Disable buffering for Nginx (if used)
        Response.ContentType = "text/event-stream; charset=utf-8";
        
        // Initial flush to start streaming immediately
        await Response.Body.FlushAsync(cancellationToken);

        // Now do validation and send errors as SSE events if needed
        if (string.IsNullOrEmpty(inputDirectory))
        {
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = "No input directory provided" })}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = "No output directory provided" })}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        // When running in Docker, paths are accessed via /host mount
        // Prepend /host to absolute paths
        var inputDir = Path.GetFullPath(inputDirectory);
        if (Path.IsPathRooted(inputDir) && Directory.Exists("/host"))
        {
            inputDir = "/host" + inputDir;
        }
        if (!Directory.Exists(inputDir))
        {
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = $"Directory does not exist: {inputDirectory}" })}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        var startTime = DateTime.UtcNow;
        var outputDir = Path.GetFullPath(outputDirectory);
        // When running in Docker, paths are accessed via /host mount
        if (Path.IsPathRooted(outputDir) && Directory.Exists("/host"))
        {
            outputDir = "/host" + outputDir;
        }
        Directory.CreateDirectory(outputDir);

        try
        {
            // Get all files recursively
            var allFiles = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories).ToList();
            var totalFiles = allFiles.Count;
            
            var totalOriginalSize = 0L;
            var totalCompressedSize = 0L;
            var successfulFiles = 0;
            var failedFiles = 0;
            var fileResults = new List<DirectoryFileResult>();

            // Initial progress with actual storage size
            var initialActualStorageSize = _chunkStorageService.GetActualStorageSize();
            await SendProgress(new DirectoryCompressionProgress
            {
                TotalFiles = totalFiles,
                ProcessedFiles = 0,
                Status = "Starting compression...",
                ProgressPercent = 0,
                TotalChunkBytes = initialActualStorageSize // (A) Actual storage
            }, cancellationToken);

            // Process files in parallel for faster compression
            // DYNAMIC: Adjust concurrency based on current CPU and memory usage
            // Base concurrency: 8-16 files depending on CPU cores
            int baseConcurrency = Math.Min(16, Math.Max(4, Environment.ProcessorCount));
            int maxConcurrency = _resourceManager.GetOptimalConcurrency(
                minConcurrency: 2,  // Minimum: 2 files at a time
                maxConcurrency: baseConcurrency * 2,  // Maximum: 2x base
                baseConcurrency: baseConcurrency
            );
            _logger.LogInformation("Dynamic concurrency: {Concurrency} concurrent files (base: {Base})", maxConcurrency, baseConcurrency);
            var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            
            // Memory management: Track total memory used by concurrent file processing
            // Limit to ~2GB of file data in memory at once (configurable)
            var maxMemoryBytes = 2L * 1024 * 1024 * 1024; // 2GB default
            var currentMemoryBytes = 0L;
            var memoryLock = new object();
            var fileTasks = new List<Task>();
            var lockObj = new object();
            var completedCount = 0; // Track completed files

            // Start periodic progress updates (every 500ms) to ensure live updates
            var progressUpdateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progressUpdateTask = Task.Run(async () =>
            {
                while (!progressUpdateCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500, progressUpdateCts.Token); // Update every 500ms
                        
                        DirectoryCompressionProgress periodicProgress;
                        var currentStorageSize = _chunkStorageService.GetActualStorageSize();
                        lock (lockObj)
                        {
                            periodicProgress = new DirectoryCompressionProgress
                            {
                                TotalFiles = totalFiles,
                                ProcessedFiles = completedCount,
                                SuccessfulFiles = successfulFiles,
                                FailedFiles = failedFiles,
                                TotalOriginalSize = totalOriginalSize,
                                TotalCompressedSize = totalCompressedSize,
                                TotalChunkBytes = currentStorageSize,
                                ProgressPercent = totalFiles > 0 ? (double)completedCount / totalFiles * 100 : 0,
                                ElapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                                Status = $"Processing... ({completedCount}/{totalFiles} files completed)"
                            };
                        }
                        await SendProgress(periodicProgress, progressUpdateCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in periodic progress update");
                    }
                }
            }, progressUpdateCts.Token);

            for (int i = 0; i < allFiles.Count; i++)
            {
                var fileIndex = i; // Capture loop variable
                var filePath = allFiles[i];
                var relativePath = Path.GetRelativePath(inputDir, filePath);
                var fileName = Path.GetFileName(filePath);
                var outputPath = Path.Combine(outputDir, relativePath + ".compressed");

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outputDir);

                // Process file in parallel
                var fileTask = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var originalSize = fileInfo.Length;
                        var fileStartTime = DateTime.UtcNow;
                        var fileSize = originalSize; // For memory management

                        // Update original size immediately when file starts processing (before compression)
                        // This ensures live stats show accurate original size even while files are still compressing
                        lock (lockObj)
                        {
                            totalOriginalSize += originalSize;
                        }

                        // Memory management: Check if we have enough memory before reading
                        // Wait if memory limit would be exceeded
                        lock (memoryLock)
                        {
                            while (currentMemoryBytes + fileSize > maxMemoryBytes && !cancellationToken.IsCancellationRequested)
                            {
                                // Wait a bit and check again (another file might finish)
                                Monitor.Wait(memoryLock, 100);
                            }
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                currentMemoryBytes += fileSize;
                            }
                        }
                        
                        try
                        {
                            // Read file
                            var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken);
                        
                            // Compress + stats
                            var (compressedBytes, referencesFound, totalChunks) =
                                await _compressionService.CompressFileWithStatsAsync(fileBytes, cancellationToken);
                            var compressedSize = compressedBytes.Length;

                            // Get actual storage size (may be slightly behind due to background storage)
                            var actualStorageSize = _chunkStorageService.GetActualStorageSize();

                            // Save compressed file using buffered async I/O
                            using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                            {
                                await fileStream.WriteAsync(compressedBytes, 0, compressedBytes.Length, cancellationToken);
                                await fileStream.FlushAsync(cancellationToken);
                            }

                            var fileDuration = (DateTime.UtcNow - fileStartTime).TotalMilliseconds;
                            
                            // Update remaining counters thread-safely (original size already updated above)
                            lock (lockObj)
                            {
                                totalCompressedSize += compressedSize;
                                successfulFiles++;
                                completedCount++;
                            }

                            // Save statistics
                            var stats = new CompressionStatistic
                            {
                                Id = Guid.NewGuid(),
                                FileName = fileName,
                                OriginalSize = originalSize,
                                CompressedSize = compressedSize,
                                CompressionRatio = originalSize > 0 ? (double)compressedSize / originalSize : 0,
                                SavingsBytes = originalSize - compressedSize,
                                SavingsPercent = originalSize > 0 ? (double)(originalSize - compressedSize) / originalSize * 100 : 0,
                                DurationMs = fileDuration,
                                ReferencesFound = referencesFound,
                                TotalChunks = totalChunks,
                                Timestamp = DateTime.UtcNow,
                                Status = "Success"
                            };
                            await _statisticsService.SaveStatisticsAsync(stats);

                            lock (lockObj)
                            {
                                fileResults.Add(new DirectoryFileResult
                                {
                                    FileName = fileName,
                                    RelativePath = relativePath,
                                    OriginalSize = originalSize,
                                    CompressedSize = compressedSize,
                                    CompressionRatio = stats.CompressionRatio,
                                    DurationMs = fileDuration,
                                    Success = true
                                });
                            }

                            // Send a per-file SSE event for the references graph (one point per completed file)
                            // This is intentionally lightweight (no large payloads).
                            try
                            {
                                var options = new System.Text.Json.JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                                };
                                var json = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    type = "file",
                                    file = new
                                    {
                                        index = fileIndex,
                                        fileName,
                                        referencesFound,
                                        totalChunks
                                    }
                                }, options);
                                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                                await Response.Body.FlushAsync(cancellationToken);
                            }
                            catch
                            {
                                // Ignore SSE failures; directory compression should continue.
                            }

                            // Don't send individual progress updates - let the periodic timer handle it
                            // This prevents overwhelming the SSE stream with 50k+ updates
                        }
                        finally
                        {
                            // Release memory
                            lock (memoryLock)
                            {
                                currentMemoryBytes -= fileSize;
                                Monitor.PulseAll(memoryLock); // Notify waiting tasks
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to compress file {FileName}", fileName);
                        lock (lockObj)
                        {
                            failedFiles++;
                            fileResults.Add(new DirectoryFileResult
                            {
                                FileName = fileName,
                                RelativePath = relativePath,
                                Success = false,
                                Error = ex.Message
                            });
                        }

                        lock (lockObj)
                        {
                            failedFiles++;
                            completedCount++;
                        }
                        // Don't send individual failure updates - let the periodic timer handle it
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);

                fileTasks.Add(fileTask);
            }

            // Wait for all files to complete
            await Task.WhenAll(fileTasks);
            
            // Flush any remaining statistics in batch
            await _statisticsService.FlushAllStatisticsAsync();
            
            // Stop periodic progress updates
            progressUpdateCts.Cancel();
            try
            {
                await progressUpdateTask;
            }
            catch (OperationCanceledException) { }

            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Send final result
            var finalResult = new DirectoryCompressionResult
            {
                Success = true,
                TotalFiles = totalFiles,
                SuccessfulFiles = successfulFiles,
                FailedFiles = failedFiles,
                TotalOriginalSize = totalOriginalSize,
                TotalCompressedSize = totalCompressedSize,
                TotalSavings = totalOriginalSize - totalCompressedSize,
                CompressionRatio = totalOriginalSize > 0 ? (double)totalCompressedSize / totalOriginalSize : 0,
                TotalDurationMs = totalDuration,
                OutputDirectory = outputDir,
                Files = fileResults
            };

            await SendProgress(new DirectoryCompressionProgress
            {
                TotalFiles = totalFiles,
                ProcessedFiles = totalFiles,
                SuccessfulFiles = successfulFiles,
                FailedFiles = failedFiles,
                TotalOriginalSize = totalOriginalSize,
                TotalCompressedSize = totalCompressedSize,
                TotalChunkBytes = _chunkStorageService.GetActualStorageSize(), // Final actual storage size
                ProgressPercent = 100,
                ElapsedMs = totalDuration,
                Status = "Complete"
            }, cancellationToken);

            // Send final result as JSON
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { type = "complete", result = finalResult })}\n\n", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Directory compression failed");
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = ex.Message })}\n\n", cancellationToken);
        }
    }

    private async Task SendProgress(DirectoryCompressionProgress progress, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use JsonSerializerOptions to ensure camelCase property names match JavaScript expectations
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // Allow special chars in filenames
            };
            
            // Escape the status message to prevent JSON injection issues
            var safeProgress = new DirectoryCompressionProgress
            {
                TotalFiles = progress.TotalFiles,
                ProcessedFiles = progress.ProcessedFiles,
                SuccessfulFiles = progress.SuccessfulFiles,
                FailedFiles = progress.FailedFiles,
                TotalOriginalSize = progress.TotalOriginalSize,
                TotalCompressedSize = progress.TotalCompressedSize,
                TotalChunkBytes = progress.TotalChunkBytes,
                CurrentFileOriginalSize = progress.CurrentFileOriginalSize,
                CurrentFileCompressedSize = progress.CurrentFileCompressedSize,
                CurrentFileName = progress.CurrentFileName ?? string.Empty, // Ensure not null
                ProgressPercent = progress.ProgressPercent,
                ElapsedMs = progress.ElapsedMs,
                Status = progress.Status ?? string.Empty // Ensure not null
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(new { type = "progress", progress = safeProgress }, options);
            var message = $"data: {json}\n\n";
            
            // Write and flush immediately - this is critical for real-time updates
            await Response.WriteAsync(message, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            
            // Log only occasionally to reduce overhead with large file counts
            if (progress.ProcessedFiles % 100 == 0 || progress.ProcessedFiles == progress.TotalFiles)
            {
                _logger.LogInformation("Sent progress: {ProcessedFiles}/{TotalFiles}, ChunkBytes: {ChunkBytes}, Percent: {Percent}%", 
                    progress.ProcessedFiles, progress.TotalFiles, progress.TotalChunkBytes, progress.ProgressPercent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send progress update: {Error}", ex.Message);
            // Don't throw - we want to continue processing even if one update fails
        }
    }
}
