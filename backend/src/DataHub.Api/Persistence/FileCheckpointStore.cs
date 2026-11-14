using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using DataHub.Api.Configuration;

namespace DataHub.Api.Persistence;

/// <summary>
/// 檔案型 checkpoint。目錄內會有：
/// <list type="bullet">
/// <item><c>checkpoint.json</c>：目前有效嘅 checkpoint（primary）。</item>
/// <item><c>checkpoint.json.bak</c>：上一份有效 checkpoint，primary 損壞時作 fallback。</item>
/// <item><c>checkpoint.json.tmp</c>：寫入中嘅暫存檔，驗證完成先會取代 primary。</item>
/// </list>
/// 保存流程：寫 tmp（write-through + flush）→ 以 SHA-256 驗證 tmp → File.Replace 取代 primary 並將舊 primary 輪替成 .bak。
/// 原子性取決於檔案系統：本機 NTFS / Linux volume 一般有保證；SMB / UNC share 嘅 rename 語意由 share 決定，PoC 唔作保證。
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly string _directory;
    private readonly string _primaryPath;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly ILogger<FileCheckpointStore> _logger;

    // null = 未知；true/false = 最近一次保存或載入時 primary 嘅狀態。用嚟避免將損壞嘅 primary 輪替去 .bak、蓋走最後一份好嘅 checkpoint。
    private bool? _primaryKnownGood;
    private bool _futureSchemaSeen;

    public FileCheckpointStore(IOptions<DataHubOptions> options, IHostEnvironment environment, ILogger<FileCheckpointStore> logger)
    {
        var checkpoint = options.Value.Checkpoint;
        _directory = Path.IsPathRooted(checkpoint.Directory)
            ? checkpoint.Directory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, checkpoint.Directory));
        _primaryPath = Path.Combine(_directory, checkpoint.FileName);
        _backupPath = _primaryPath + ".bak";
        _tempPath = _primaryPath + ".tmp";
        _logger = logger;
    }

    public string Directory => _directory;

    public async Task<CheckpointLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        foreach (var (label, path) in new[] { ("primary", _primaryPath), ("backup", _backupPath) })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var payload = CheckpointSerializer.Deserialize(bytes);
                if (label == "primary")
                {
                    _primaryKnownGood = true;
                }

                return new CheckpointLoadResult(payload, label, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is FutureCheckpointFormatException) _futureSchemaSeen = true;
                if (label == "primary")
                {
                    _primaryKnownGood = false;
                }

                var message = $"{label} checkpoint '{Path.GetFileName(path)}' is unusable: {Sanitize(ex.Message)}";
                warnings.Add(message);
                _logger.LogWarning(ex, "{Message}", message);
            }
        }

        return new CheckpointLoadResult(null, null, warnings);
    }

    public async Task<CheckpointSaveResult> SaveAsync(CheckpointPayload payload, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { _primaryPath, _backupPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken));
            }
            catch (FutureCheckpointFormatException)
            {
                _futureSchemaSeen = true;
            }
            catch (CheckpointFormatException)
            {
                // Existing corruption is handled by the normal promotion and backup policy.
            }
        }
        if (_futureSchemaSeen)
            throw new IOException("Checkpoint save disabled: an unsupported future schema was found.");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var bytes = CheckpointSerializer.Serialize(payload);
            System.IO.Directory.CreateDirectory(_directory);

            await using (var stream = new FileStream(_tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough | FileOptions.Asynchronous,
                BufferSize = 0,
            }))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await VerifyTempAsync(bytes, cancellationToken);
            await PromoteAsync(cancellationToken);

            return new CheckpointSaveResult(bytes.LongLength, payload.RecordCount, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 完整 exception（含路徑）留喺 log；狀態同 API 只見到已經去除目錄路徑嘅訊息。
            throw new IOException($"Checkpoint save failed: {Sanitize(ex.Message)}", ex);
        }
    }

    private async Task VerifyTempAsync(byte[] expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(_tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.LongLength)
        {
            throw new IOException($"Temp checkpoint length mismatch (expected {expected.LongLength}, found {stream.Length}).");
        }

        var onDisk = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(onDisk, SHA256.HashData(expected)))
        {
            throw new IOException("Temp checkpoint failed verification after write.");
        }
    }

    private async Task PromoteAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_primaryPath))
        {
            _primaryKnownGood ??= await IsValidAsync(_primaryPath, cancellationToken);

            if (_primaryKnownGood == true)
            {
                File.Replace(_tempPath, _primaryPath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                // primary 已經損壞：直接取代，保留現有 .bak（可能係最後一份好嘅）。
                File.Move(_tempPath, _primaryPath, overwrite: true);
            }
        }
        else
        {
            File.Move(_tempPath, _primaryPath);
        }

        _primaryKnownGood = true;
    }

    private static async Task<bool> IsValidAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private string Sanitize(string message)
    {
        // 例外訊息有時只提及目錄嘅上層路徑（例如 "Cannot create '<上層>'"），所以連同所有上層路徑一齊遮蔽，避免經 API 洩漏檔案系統結構。
        var sanitized = message.Replace(_directory, "<checkpoint-dir>", StringComparison.OrdinalIgnoreCase);

        var trimmed = Path.TrimEndingDirectorySeparator(_directory);
        var root = Path.GetPathRoot(trimmed);
        for (var parent = Path.GetDirectoryName(trimmed);
             !string.IsNullOrEmpty(parent) && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
             parent = Path.GetDirectoryName(parent))
        {
            sanitized = sanitized.Replace(parent, "<path>", StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }
}
