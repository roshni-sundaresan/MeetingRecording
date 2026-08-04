using AutoMapper;
using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Mapping;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingRecorder.UnitTests;

public class BatchUploadServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid BatchId = Guid.NewGuid();

    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IChunkStorageService> _storage = new();
    private readonly IMapper _mapper = new MapperConfiguration(cfg => cfg.AddProfile(new RecordingProfile()), NullLoggerFactory.Instance).CreateMapper();

    private readonly Mock<IRepository<User>> _userRepo = new();
    private readonly Mock<IRepository<UploadBatch>> _batchRepo = new();
    private readonly Mock<IRepository<UploadChunk>> _chunkRepo = new();
    private readonly Mock<IRepository<Recording>> _recRepo = new();

    public BatchUploadServiceTests()
    {
        _uow.Setup(u => u.Repository<User>()).Returns(_userRepo.Object);
        _uow.Setup(u => u.Repository<UploadBatch>()).Returns(_batchRepo.Object);
        _uow.Setup(u => u.Repository<UploadChunk>()).Returns(_chunkRepo.Object);
        _uow.Setup(u => u.Repository<Recording>()).Returns(_recRepo.Object);
    }

    private BatchUploadService CreateSut() => new(_uow.Object, _storage.Object, _mapper);

    private static UploadBatch NewBatch(int totalChunks = 3, UploadStatus status = UploadStatus.InProgress) => new()
    {
        Id = BatchId,
        UserId = UserId,
        FileName = "meeting.mp4",
        Type = RecordingType.Video,
        TotalChunks = totalChunks,
        Status = status
    };

    private static List<UploadChunk> Chunks(int count)
        => Enumerable.Range(1, count).Select(n => new UploadChunk
        {
            Id = Guid.NewGuid(), UploadBatchId = BatchId, ChunkNumber = n, SizeBytes = 100
        }).ToList();

    [Fact]
    public async Task Start_WhenUserMissing_ThrowsNotFound()
    {
        _userRepo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => CreateSut().StartUploadAsync(new StartUploadRequest(UserId, "a.mp4", RecordingType.Video, 3, "en", 300, null));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Start_WithValidUser_CreatesBatch()
    {
        _userRepo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateSut().StartUploadAsync(new StartUploadRequest(UserId, "a.mp4", RecordingType.Video, 3, "en", 300, null));

        result.BatchId.Should().NotBeEmpty();
        result.TotalChunks.Should().Be(3);
        _batchRepo.Verify(r => r.Add(It.IsAny<UploadBatch>()), Times.Once);
    }

    [Fact]
    public async Task UploadChunk_WithMismatchedTotalChunks_Throws()
    {
        var batch = NewBatch(totalChunks: 5);
        _batchRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UploadBatch, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _chunkRepo.Setup(r => r.Query()).Returns(new List<UploadChunk>().AsQueryable());

        var act = () => CreateSut().UploadChunkAsync(new UploadChunkRequest(BatchId, 1, 3, UserId, null, null, null, null, null),
            new MemoryStream(new byte[10]));

        await act.Should().ThrowAsync<AppException>().WithMessage("*does not match*");
    }

    [Fact]
    public async Task Complete_WhenChunksMissing_ThrowsAndListsMissing()
    {
        var batch = NewBatch(totalChunks: 3);
        _batchRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UploadBatch, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _chunkRepo.Setup(r => r.Query()).Returns(Chunks(2).AsQueryable());   // only chunks 1..2 of 3

        var act = () => CreateSut().CompleteUploadAsync(BatchId);

        await act.Should().ThrowAsync<AppException>().WithMessage("*Missing chunk(s): 3*");
    }

    [Fact]
    public async Task Complete_WhenAllChunksPresent_MergesSavesAndCleansUp()
    {
        var batch = NewBatch(totalChunks: 3);
        batch.Summary = "s";
        _batchRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UploadBatch, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _chunkRepo.Setup(r => r.Query()).Returns(Chunks(3).AsQueryable());
        _storage.Setup(s => s.MergeChunksAsync(BatchId, 3, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/final.mp4");

        var result = await CreateSut().CompleteUploadAsync(BatchId);

        result.Title.Should().Be("meeting");
        result.FilePath.Should().Be("/tmp/final.mp4");
        result.TranscriptionStatus.Should().Be(TranscriptionStatus.None);
        batch.Status.Should().Be(UploadStatus.Completed);
        _recRepo.Verify(r => r.Add(It.IsAny<Recording>()), Times.Once);
        _chunkRepo.Verify(r => r.Remove(It.IsAny<UploadChunk>()), Times.Exactly(3));
        _storage.Verify(s => s.DeleteBatchAsync(BatchId, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Complete_WhenAlreadyCompleted_ThrowsConflict()
    {
        var batch = NewBatch(status: UploadStatus.Completed);
        _batchRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UploadBatch, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        var act = () => CreateSut().CompleteUploadAsync(BatchId);

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 409);
    }
}
