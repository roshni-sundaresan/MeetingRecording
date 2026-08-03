-- ============================================
-- MeetingRecorderDb — explore queries
-- Database: MeetingRecorderDb (SQL Server / localhost,1433)
-- Connect: Cmd+Shift+P → "MS SQL: Connect" → choose profile
--          "MeetingRecorderDb (local)" → then "Run Query" (Cmd+Shift+E)
-- ============================================

-- 1. All tables in the database
SELECT t.name AS TableName
FROM sys.tables t
ORDER BY t.name;

-- 2. Users (active only — soft-deleted hidden by default)
SELECT Id, Email, Name, Mobile, Role, CreatedDate
FROM dbo.Users
ORDER BY CreatedDate;

-- 3. Recordings with owner names
SELECT r.Id, u.Email AS Owner, r.Title, r.Type, r.Duration,
       r.Bookmarked, r.TranscriptionStatus, r.CreatedAt
FROM dbo.Recordings r
JOIN dbo.Users u ON u.Id = r.UserId
ORDER BY r.CreatedAt DESC;

-- 4. Bookmarked recordings
SELECT Title, CreatedAt
FROM dbo.Recordings
WHERE Bookmarked = 1;

-- 5. Upload pipeline health (completed batches & bytes)
SELECT FileName, TotalChunks, Status, TotalBytesReceived, CreatedDate
FROM dbo.UploadBatches
ORDER BY CreatedDate DESC;

-- 6. Recordings per user
SELECT u.Email, COUNT(r.Id) AS RecordingCount
FROM dbo.Users u
LEFT JOIN dbo.Recordings r ON r.UserId = u.Id AND r.IsDeleted = 0
GROUP BY u.Email
ORDER BY RecordingCount DESC;

-- 7. Schema of a table (example: Recordings)
SELECT c.name AS ColumnName, ty.name AS DataType, c.max_length, c.is_nullable
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Recordings')
ORDER BY c.column_id;

-- 8. Recent recordings by duration
SELECT TOP 10 Title, Duration, CreatedAt
FROM dbo.Recordings
ORDER BY Duration DESC;
