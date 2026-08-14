using System.Text;
using FluentAssertions;
using MeetingRecorder.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.UnitTests;

public class LocalChunkStorageServiceTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LocalChunkStorageService _service;

    public LocalChunkStorageServiceTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "MeetingRecorderTests_" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new StorageOptions { RootPath = _tempPath });
        _service = new LocalChunkStorageService(options, NullLogger<LocalChunkStorageService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try
            {
                Directory.Delete(_tempPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }

    [Fact]
    public async Task SaveChunkAsync_SavesChunkContentSuccessfully()
    {
        var batchId = Guid.NewGuid();
        var contentString = "Hello Chunk Storage World!";
        var contentBytes = Encoding.UTF8.GetBytes(contentString);
        using var stream = new MemoryStream(contentBytes);

        await _service.SaveChunkAsync(batchId, 1, stream);

        var exists = await _service.ChunkExistsAsync(batchId, 1);
        exists.Should().BeTrue();

        await using var readStream = await _service.OpenChunkAsync(batchId, 1);
        using var reader = new StreamReader(readStream);
        var readContent = await reader.ReadToEndAsync();
        readContent.Should().Be(contentString);
    }

    [Fact]
    public async Task SaveChunkAsync_ConcurrentWritesToSameBatch_SucceedWithoutErrors()
    {
        var batchId = Guid.NewGuid();
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            var data = Encoding.UTF8.GetBytes($"Chunk data payload {i}");
            using var stream = new MemoryStream(data);
            await _service.SaveChunkAsync(batchId, i, stream);
        });

        await Task.WhenAll(tasks);

        for (int i = 1; i <= 10; i++)
        {
            (await _service.ChunkExistsAsync(batchId, i)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task DeleteBatchAsync_RemovesBatchDirectoryAndCleanUp()
    {
        var batchId = Guid.NewGuid();
        using var stream = new MemoryStream("Test Data"u8.ToArray());
        await _service.SaveChunkAsync(batchId, 1, stream);

        (await _service.ChunkExistsAsync(batchId, 1)).Should().BeTrue();

        await _service.DeleteBatchAsync(batchId);

        (await _service.ChunkExistsAsync(batchId, 1)).Should().BeFalse();
    }
}
