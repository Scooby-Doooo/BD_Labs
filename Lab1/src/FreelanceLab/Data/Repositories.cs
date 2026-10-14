using Npgsql;
using NpgsqlTypes;

namespace FreelanceLab.Data;

// Базовий клас: усі репозиторії працюють з одним підключенням і транзакцією з UnitOfWork.
// Читання - тільки з View, зміни - тільки через CALL процедур.
public abstract class Repository
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;

    protected Repository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    private NpgsqlCommand CreateCommand(string sql, object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            var parameter = new NpgsqlParameter { Value = value ?? DBNull.Value };
            if (value is null) parameter.NpgsqlDbType = NpgsqlDbType.Text;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    // CALL процедури без результату
    protected async Task Execute(string sql, params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        await command.ExecuteNonQueryAsync();
    }

    // CALL процедури, яка повертає id нового запису
    protected async Task<long> ExecuteWithId(string sql, params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // SELECT з View
    protected async Task<List<T>> Query<T>(string sql, Func<NpgsqlDataReader, T> map, params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(map(reader));
        return result;
    }
}

public interface IProjectRepository
{
    Task<List<Project>> ListAsync(string? search = null);
    Task<long> CreateAsync(long actor, long category, string title, string description, decimal budget, DateOnly deadline);
    Task UpdateAsync(long actor, long id, string title, string description, decimal budget, DateOnly deadline);
    Task SetStatusAsync(long actor, long id, string status);
    Task SoftDeleteAsync(long actor, long id);
    Task RestoreAsync(long actor, long id);
}

public class ProjectRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), IProjectRepository
{
    public Task<List<Project>> ListAsync(string? search = null) => Query(
        "SELECT id, client_id, title, budget, deadline, status, updated_at, updated_by FROM vw_active_projects " +
        "WHERE $1::text IS NULL OR search_vector @@ plainto_tsquery('simple', $1::text) ORDER BY id",
        r => new Project(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDecimal(3), r.GetFieldValue<DateOnly>(4),
            r.GetString(5), r.GetDateTime(6), r.GetInt64(7)),
        search);

    public Task<long> CreateAsync(long actor, long category, string title, string description, decimal budget, DateOnly deadline) =>
        ExecuteWithId("CALL sp_project_create($1, $2, $3, $4, $5, $6, NULL)", actor, category, title, description, budget, deadline);

    public Task UpdateAsync(long actor, long id, string title, string description, decimal budget, DateOnly deadline) =>
        Execute("CALL sp_project_update($1, $2, $3, $4, $5, $6)", actor, id, title, description, budget, deadline);

    public Task SetStatusAsync(long actor, long id, string status) =>
        Execute("CALL sp_project_set_status($1, $2, $3)", actor, id, status);

    public Task SoftDeleteAsync(long actor, long id) => Execute("CALL sp_project_soft_delete($1, $2)", actor, id);

    public Task RestoreAsync(long actor, long id) => Execute("CALL sp_project_restore($1, $2)", actor, id);
}

public interface IProposalRepository
{
    Task<List<Proposal>> ListAsync(long projectId);
    Task<long> CreateAsync(long actor, long projectId, string letter, decimal amount, int days);
    Task AcceptAsync(long actor, long id);
    Task WithdrawAsync(long actor, long id);
}

public class ProposalRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), IProposalRepository
{
    public Task<List<Proposal>> ListAsync(long projectId) => Query(
        "SELECT id, project_id, freelancer_id, freelancer_name, proposed_amount, estimated_days, status, updated_at, updated_by " +
        "FROM vw_project_proposals WHERE project_id = $1 ORDER BY id",
        r => new Proposal(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetDecimal(4), r.GetInt32(5),
            r.GetString(6), r.GetDateTime(7), r.GetInt64(8)),
        projectId);

    public Task<long> CreateAsync(long actor, long projectId, string letter, decimal amount, int days) =>
        ExecuteWithId("CALL sp_proposal_create($1, $2, $3, $4, $5, NULL)", actor, projectId, letter, amount, days);

    public Task AcceptAsync(long actor, long id) => Execute("CALL sp_proposal_accept($1, $2)", actor, id);

    public Task WithdrawAsync(long actor, long id) => Execute("CALL sp_proposal_withdraw($1, $2)", actor, id);
}

public interface IContractRepository
{
    Task<List<Contract>> ListAsync();
    Task<long> CreateAsync(long actor, long proposalId);
    Task CompleteAsync(long actor, long id);
}

public class ContractRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), IContractRepository
{
    public Task<List<Contract>> ListAsync() => Query(
        "SELECT id, project_id, project_title, client_id, freelancer_id, agreed_amount, status, paid_amount, outstanding_amount " +
        "FROM vw_contract_summary ORDER BY id",
        r => new Contract(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetDecimal(5),
            r.GetString(6), r.GetDecimal(7), r.GetDecimal(8)));

    public Task<long> CreateAsync(long actor, long proposalId) =>
        ExecuteWithId("CALL sp_contract_create($1, $2, NULL)", actor, proposalId);

    public Task CompleteAsync(long actor, long id) => Execute("CALL sp_contract_complete($1, $2)", actor, id);
}

public interface IMilestoneRepository
{
    Task<List<Milestone>> ListAsync(long contractId);
    Task<long> CreateAsync(long actor, long contractId, MilestoneInput input);
    Task<long> SubmitAsync(long actor, long milestoneId, string description, string? url);
    Task ApproveAsync(long actor, long id);
    Task<long> PayAsync(long actor, long milestoneId, decimal amount, string reference);
}

public class MilestoneRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), IMilestoneRepository
{
    public Task<List<Milestone>> ListAsync(long contractId) => Query(
        "SELECT id, contract_id, title, amount, due_date, status, paid_amount FROM vw_milestones WHERE contract_id = $1 ORDER BY id",
        r => new Milestone(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDecimal(3), r.GetFieldValue<DateOnly>(4),
            r.GetString(5), r.GetDecimal(6)),
        contractId);

    public Task<long> CreateAsync(long actor, long contractId, MilestoneInput input) =>
        ExecuteWithId("CALL sp_milestone_create($1, $2, $3, $4, $5, NULL)", actor, contractId, input.Title, input.Amount, input.DueDate);

    public Task<long> SubmitAsync(long actor, long milestoneId, string description, string? url) =>
        ExecuteWithId("CALL sp_submission_create($1, $2, $3, $4, NULL)", actor, milestoneId, description, url);

    public Task ApproveAsync(long actor, long id) => Execute("CALL sp_milestone_approve($1, $2)", actor, id);

    public Task<long> PayAsync(long actor, long milestoneId, decimal amount, string reference) =>
        ExecuteWithId("CALL sp_payment_register($1, $2, $3, $4, NULL)", actor, milestoneId, amount, reference);
}

public interface IPortfolioRepository
{
    Task<List<PortfolioItem>> ListAsync();
    Task<long> CreateAsync(long actor, string title, string description, string? url);
    Task SoftDeleteAsync(long actor, long id);
    Task RestoreAsync(long actor, long id);
}

public class PortfolioRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), IPortfolioRepository
{
    public Task<List<PortfolioItem>> ListAsync() => Query(
        "SELECT id, freelancer_id, title, updated_at, updated_by FROM vw_portfolio ORDER BY id",
        r => new PortfolioItem(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDateTime(3), r.GetInt64(4)));

    public Task<long> CreateAsync(long actor, string title, string description, string? url) =>
        ExecuteWithId("CALL sp_portfolio_create($1, $2, $3, $4, NULL)", actor, title, description, url);

    public Task SoftDeleteAsync(long actor, long id) => Execute("CALL sp_portfolio_soft_delete($1, $2)", actor, id);

    public Task RestoreAsync(long actor, long id) => Execute("CALL sp_portfolio_restore($1, $2)", actor, id);
}

public interface ICommunityRepository
{
    Task<List<User>> UsersAsync();
    Task<List<Review>> ReviewsAsync();
    Task<List<Message>> MessagesAsync(long contractId);
    Task<List<Submission>> SubmissionsAsync(long milestoneId);
    Task<long> ReviewAsync(long actor, long contractId, short rating, string comment);
    Task<long> SendAsync(long actor, long contractId, long recipient, string body);
}

public class CommunityRepository(NpgsqlConnection c, NpgsqlTransaction t) : Repository(c, t), ICommunityRepository
{
    public Task<List<User>> UsersAsync() => Query(
        "SELECT id, display_name FROM vw_users ORDER BY id",
        r => new User(r.GetInt64(0), r.GetString(1)));

    public Task<List<Review>> ReviewsAsync() => Query(
        "SELECT id, contract_id, author_id, rating, comment FROM vw_reviews ORDER BY id",
        r => new Review(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt16(3), r.GetString(4)));

    public Task<List<Message>> MessagesAsync(long contractId) => Query(
        "SELECT id, contract_id, sender_id, recipient_id, body FROM vw_messages WHERE contract_id = $1 ORDER BY id",
        r => new Message(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)),
        contractId);

    public Task<List<Submission>> SubmissionsAsync(long milestoneId) => Query(
        "SELECT id, milestone_id, description FROM vw_submissions WHERE milestone_id = $1 ORDER BY id",
        r => new Submission(r.GetInt64(0), r.GetInt64(1), r.GetString(2)),
        milestoneId);

    public Task<long> ReviewAsync(long actor, long contractId, short rating, string comment) =>
        ExecuteWithId("CALL sp_review_create($1, $2, $3, $4, NULL)", actor, contractId, rating, comment);

    public Task<long> SendAsync(long actor, long contractId, long recipient, string body) =>
        ExecuteWithId("CALL sp_message_send($1, $2, $3, $4, NULL)", actor, contractId, recipient, body);
}
