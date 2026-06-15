USE [Eltarshouby-Exam];
GO

-- =========================================================================
-- PERFORMANCE TUNING INDEXES FOR ELTARSHOUBY EXAM SYSTEM
-- =========================================================================

-- 1. CompanyTrainees Indexes
-- Optimize trainee listings and lookups by company
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('CompanyTrainees') AND name = 'IX_CompanyTrainees_CompanyId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_CompanyTrainees_CompanyId 
    ON CompanyTrainees (CompanyId)
    INCLUDE (FullName, UserCode, JobTitle, BranchName, Email, Phone);
END
GO

-- Optimize Excel import lookup (check if UserCode exists under Company)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('CompanyTrainees') AND name = 'IX_CompanyTrainees_CompanyId_UserCode')
BEGIN
    CREATE NONCLUSTERED INDEX IX_CompanyTrainees_CompanyId_UserCode 
    ON CompanyTrainees (CompanyId, UserCode);
END
GO


-- 2. UserAttendance Indexes
-- Optimize session attendance loading (joins on SessionId)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'IX_UserAttendance_SessionId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserAttendance_SessionId 
    ON UserAttendance (SessionId);
END
GO

-- Optimize trainee attendance updates and reporting
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'IX_UserAttendance_CompanyTraineeId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserAttendance_CompanyTraineeId 
    ON UserAttendance (CompanyTraineeId);
END
GO

-- Optimize user attendance history lookup
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'IX_UserAttendance_UserId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserAttendance_UserId 
    ON UserAttendance (UserId);
END
GO


-- 3. AttendanceSessions Indexes
-- Optimize attendance sessions list filtering by company, wave, or skill track
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('AttendanceSessions') AND name = 'IX_AttendanceSessions_CompanyId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_AttendanceSessions_CompanyId 
    ON AttendanceSessions (CompanyId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('AttendanceSessions') AND name = 'IX_AttendanceSessions_WaveId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_AttendanceSessions_WaveId 
    ON AttendanceSessions (WaveId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('AttendanceSessions') AND name = 'IX_AttendanceSessions_SkillTrackId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_AttendanceSessions_SkillTrackId 
    ON AttendanceSessions (SkillTrackId);
END
GO


-- 4. Exam Questions & Choices Indexes
-- Optimize choice fetching per question
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('Choices') AND name = 'IX_Choices_QuestionId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Choices_QuestionId 
    ON Choices (QuestionId)
    INCLUDE (ChoiceText, IsCorrect);
END
GO

-- Optimize exam-to-questions mapping
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('ExamQuestions') AND name = 'IX_ExamQuestions_ExamId_QuestionId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_ExamQuestions_ExamId_QuestionId 
    ON ExamQuestions (ExamId, QuestionId);
END
GO

-- Optimize questions categorized lookup
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('Questions') AND name = 'IX_Questions_CategoryId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Questions_CategoryId 
    ON Questions (CategoryId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('Questions') AND name = 'IX_Questions_TopicId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Questions_TopicId 
    ON Questions (TopicId);
END
GO


-- 5. Student Question Details Indexes
-- Optimize joins on QuestionId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('StudentQuestionDetails') AND name = 'IX_StudentQuestionDetails_QuestionId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_StudentQuestionDetails_QuestionId 
    ON StudentQuestionDetails (QuestionId);
END
GO

-- Optimize joins on SelectedChoiceId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('StudentQuestionDetails') AND name = 'IX_StudentQuestionDetails_SelectedChoiceId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_StudentQuestionDetails_SelectedChoiceId 
    ON StudentQuestionDetails (SelectedChoiceId);
END
GO


-- 6. UserExamAttempts Indexes
-- Optimize admin dashboard queries that group/filter attempts by ExamId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserExamAttempts') AND name = 'IX_UserExamAttempts_ExamId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserExamAttempts_ExamId 
    ON UserExamAttempts (ExamId)
    INCLUDE (UserId, Status, Score, FinalScore, IsPassed);
END
GO


-- 7. SkillTrackUsers, UserWaves & UserLectureProgress Indexes
-- Optimize user skill track listings
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('SkillTrackUsers') AND name = 'IX_SkillTrackUsers_SkillTrackId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SkillTrackUsers_SkillTrackId 
    ON SkillTrackUsers (SkillTrackId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('SkillTrackUsers') AND name = 'IX_SkillTrackUsers_UserId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SkillTrackUsers_UserId 
    ON SkillTrackUsers (UserId);
END
GO

-- Optimize user wave listings
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserWaves') AND name = 'IX_UserWaves_WaveId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserWaves_WaveId 
    ON UserWaves (WaveId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserWaves') AND name = 'IX_UserWaves_UserId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserWaves_UserId 
    ON UserWaves (UserId);
END
GO

-- Optimize user lecture progress listings
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserLectureProgress') AND name = 'IX_UserLectureProgress_LectureId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserLectureProgress_LectureId 
    ON UserLectureProgress (LectureId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('UserLectureProgress') AND name = 'IX_UserLectureProgress_UserId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_UserLectureProgress_UserId 
    ON UserLectureProgress (UserId);
END
GO

PRINT 'Successfully created all performance tuning indexes!';
