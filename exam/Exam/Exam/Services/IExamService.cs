using System.Collections.Generic;
using System.Threading.Tasks;
using Exam.DTOs;
using Exam.Models;

namespace Exam.Services
{
    public interface IExamService
    {
        Task<int> AddExamAsync(ExamCreateDTO dto);
        Task<int> CreateExamWithQuestionsAsync(ExamCreateDTO dto);
        Task<int> AddQuestionAsync(int examId, QuestionCreateDTO question);
        Task AddChoiceAsync(int questionId, ChoiceCreateDTO choice);
        Task<int> AddQuestionForExistingExamAsync(int examId, string questionText, int points, int questionTypeId, int difficulty, int? topicId = null);
        Task<IEnumerable<TopicDto>> GetTopicsByCategoryAsync(int categoryId);
        Task CreateTopicAsync(string topicName, int categoryId);
        Task UpdateTopicAsync(int topicId, string topicName);
        Task DeleteTopicAsync(int topicId);
        Task<int> AddChoiceForExistingQuestionAsync(int questionId, string choiceText, bool isCorrect);
        Task DeleteQuestionAsync(int questionId);
        Task DeleteAllQuestionsForExamAsync(int examId);
        Task DeleteChoiceAsync(int choiceId);
        Task<ExamDetailsDto> GetSmartQuestionsByRoleAsync(int examId, string userId, int attemptId, string role);
        Task<IEnumerable<StudentDto>> GetAllStudentsAsync();
        Task AssignExamToStudentAsync(int examId, string studentId, System.DateTime? startTime = null, System.DateTime? endTime = null);
        Task<adminExamDto> GetExamByIdAsync(int id);
        Task<IEnumerable<adminExamDto>> GetAllExamsAsync();
        Task UpdateExamAsync(adminExamDto dto);
        Task<IEnumerable<ResultDetailDto>> GetResultReportAsync(int attemptId);
        Task<ExamDetailsDto> GetExamDetailsAsync(int examId);
        Task UpdateQuestionAsync(int examId, int questionId, string questionText, int points, int difficulty, int categoryId, int? topicId = null);
        Task<ExamQuestionStatsDto> GetExamQuestionStatsAsync(int examId);
        Task UpdateChoiceAsync(int choiceId, int questionId, string choiceText, bool isCorrect);
        Task<IEnumerable<ExamDto>> GetStudentExamsByStudentIdAsync(string studentId);
        Task<IEnumerable<ExamDropdownDto>> GetActiveExamsForDropdownAsync(int? typeId = null, int? month = null, int? year = null);
        Task<IEnumerable<ExamResultRowDto>> GetExamResultsByExamIdAsync(int examId);
        Task<IEnumerable<StudentExamReviewRowDto>> GetStudentExamReviewAsync(int examId, string studentId, int? attemptId = null);
        Task<IEnumerable<ExamTypeDto>> GetAllExamTypesAsync();
        Task<IEnumerable<QuestionTypeDto>> GetAllQuestionTypesAsync();
        Task<IEnumerable<UserWithRoleDto>> GetAllUsersWithRolesAsync();
        Task<IEnumerable<RoleDto>> GetAllRolesAsync();
        Task<IEnumerable<ExamResultRowDto>> GetExamResultsAsync();
        Task UpdateUserRoleByIdAsync(string userId, string roleId);
        Task<IEnumerable<ShiftDto>> GetAllShiftsAsync();
        Task UpdateUserShiftAsync(string userId, int newShiftId);
        Task<UserShiftDto?> GetUserShiftAsync(string userId);
        Task UpdateAttemptStatusOnlyAsync(int attemptId, string status);
        Task<ExamDetailsDto> GetRandomExamWithChoicesAsync(int examId, string userId, int attemptId);
        Task<IEnumerable<BranchDto>> GetAllBranchesAsync();

        Task<ExamDetailsDto> GetRandomQuestionsPoolAAsync(int examId);
        Task<ExamDetailsDto> GetRandomQuestionsPoolBAsync(int examId);
        Task<int> CloneExamAsync(int oldExamId, int newWaveId, System.DateTime newStartTime, System.DateTime newEndTime, string newTitle);
        Task<int> CloneWaveAsync(int oldWaveId, string newWaveName, System.DateTime newStartDate);
        Task SubmitFinalAsync(int attemptId, string status);
        Task SaveStudentAnswerAsync(int attemptId, int questionId, int selectedChoiceId);
        Task BatchSaveStudentAnswersAsync(int attemptId, List<AnswerModel> answers);
        Task<UserAttemptSummaryDto?> GetExistingAttemptAsync(int examId, string userId);
        Task<ExamAssignmentDto?> GetStudentAssignmentAsync(int examId, string studentId);
        Task<int> CreateStudentAttemptAsync(int examId, string studentId);
        Task RecordSeenQuestionsAsync(int attemptId, string userId, IEnumerable<int> questionIds);
        Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync(int? examTypeId = null);
        Task<IEnumerable<ExamTypeDto>> getallexamtypes();

        Task createexamtype(string examtype);
        Task Createnewcategory(string categoryname, int? examTypeId = null);
        Task DeleteCategoryAsync(int id);

        Task DeleteWaveAsync(int waveid);

        Task DeleteExamAsync(int examid);
        Task UpdateCategoryAsync(int id, string name, int? examTypeId = null);
        Task DeleteExamTypeAsync(int id);
        Task UpdateExamTypeAsync(int id, string type);

        Task<string> GetInstructionsAsync();
        Task UpdateInstructionsAsync(string instructions);


        Task<IEnumerable<int>> GetQuestionsUserHasSeenForExamAsync(int examId, string userId);
        Task<IEnumerable<int>> GetQuestionsForAttemptAsync(int attemptId);

        // Admin filtering: exams types/waves
        Task<IEnumerable<Exam.DTOs.WaveDto>> GetAllWavesAsync();
        Task<IEnumerable<Exam.DTOs.WaveStudentResultDto>> GetWaveAggregateResultsAsync(int waveId);
        Task<IEnumerable<Exam.DTOs.LiveMonitorRowDto>> GetLiveMonitorDataAsync(
            int? branchId = null, int? shiftId = null, string roleName = null,
            string status = null, int? waveId = null, DateTime? date = null, int? examId = null);
        Task<IEnumerable<adminExamDto>> GetAllExamsWithDetailsAsync();
        Task<IEnumerable<adminExamDto>> GetExamsByWaveIdAsync(int waveId);
        Task<IEnumerable<adminExamDto>> GetExamsByTypeAsync(int typeId);
        Task<int> AssignUsersToWaveAsync(int waveId, List<string> userIds, string siteUrl = "");
        Task<IEnumerable<UserDto>> GetUsersByWaveIdAsync(int waveId);
        Task<int> AssignExamToStudentsAsync(int examId, List<string> studentIds, string siteUrl = "");
        Task<DashboardDto> GetDashboardDataAsync();
        Task DeactivateUserAsync(string userId);
        Task ActivateUserAsync(string userId);
        Task DeleteUserCascadeAsync(string userId);
        Task<UserWithRoleDto?> GetUserWithRoleByIdAsync(string userId);
        Task SendExamAssignmentEmailAsync(string userId, int examId, string siteUrl = "");
        Task WipeStudentExamDataAsync(int examId, string studentId);
        Task<UserAttemptSummaryDto?> GetAttemptByIdAsync(int attemptId);
        Task<int> GetAssignmentCountAsync(int examId, string studentId);
        Task EnsureDatabaseSchemaUpdatedAsync(System.IServiceProvider serviceProvider = null);
        Task<bool> HasPermissionAsync(IList<string> roles, string controller, string action);
        Task<IEnumerable<RolePermission>> GetPermissionsForRoleAsync(string roleName);
        Task SavePermissionsForRoleAsync(string roleName, List<RolePermission> permissions);
    }
}
