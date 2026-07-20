using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProPdfReader.State;

internal sealed class DocumentStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateDirectory;

    public DocumentStateStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProPdfReader");
        _stateDirectory = Path.Combine(root, "state", "v1");
    }

    public async Task<DocumentState> LoadAsync(string pdfPath)
    {
        var normalizedPath = NormalizePath(pdfPath);
        var statePath = GetStatePath(normalizedPath);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(statePath))
            {
                return CreateFreshState(normalizedPath);
            }

            try
            {
                await using var stream = new FileStream(
                    statePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var state = await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonSerializerContext.Default.DocumentState).ConfigureAwait(false);

                if (state is null)
                {
                    PreserveUnreadableState(statePath);
                    return CreateFreshState(normalizedPath);
                }

                if (state.SchemaVersion is 1 or 2)
                {
                    state.SchemaVersion = DocumentState.CurrentSchemaVersion;
                }
                else if (state.SchemaVersion != DocumentState.CurrentSchemaVersion)
                {
                    return CreateFreshState(normalizedPath, isWritable: false);
                }

                state.FilePath = normalizedPath;
                state.Normalize();
                RefreshFileMetadata(state);
                return state;
            }
            catch (JsonException)
            {
                PreserveUnreadableState(statePath);
                return CreateFreshState(normalizedPath);
            }
            catch (IOException)
            {
                return CreateFreshState(normalizedPath, isWritable: false);
            }
            catch (UnauthorizedAccessException)
            {
                return CreateFreshState(normalizedPath, isWritable: false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DocumentState state)
    {
        var snapshot = state.Snapshot();
        if (!snapshot.IsWritable)
        {
            return;
        }

        var statePath = GetStatePath(snapshot.FilePath);
        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            RefreshFileMetadata(snapshot);

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        snapshot,
                        AppJsonSerializerContext.Default.DocumentState).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetStatePath(string normalizedPath)
    {
        var pathBytes = Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant());
        var documentKey = Convert.ToHexString(SHA256.HashData(pathBytes));
        return Path.Combine(_stateDirectory, $"{documentKey}.json");
    }

    private static DocumentState CreateFreshState(string normalizedPath, bool isWritable = true)
    {
        var state = new DocumentState
        {
            FilePath = normalizedPath,
            IsWritable = isWritable
        };
        RefreshFileMetadata(state);
        return state;
    }

    private static void RefreshFileMetadata(DocumentState state)
    {
        var file = new FileInfo(state.FilePath);
        if (!file.Exists)
        {
            return;
        }

        state.FileLength = file.Length;
        state.FileLastWriteUtc = file.LastWriteTimeUtc;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static void PreserveUnreadableState(string statePath)
    {
        try
        {
            var backupPath = $"{statePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(statePath, backupPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
