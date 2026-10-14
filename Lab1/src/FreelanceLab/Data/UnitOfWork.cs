using Npgsql;

namespace FreelanceLab.Data;

public interface IUnitOfWork : IAsyncDisposable
{
    IProjectRepository Projects { get; }
    IProposalRepository Proposals { get; }
    IContractRepository Contracts { get; }
    IMilestoneRepository Milestones { get; }
    IPortfolioRepository Portfolio { get; }
    ICommunityRepository Community { get; }
    Task CommitAsync();
    Task RollbackAsync();
}

// Одне підключення + одна транзакція на всі репозиторії.
// Якщо CommitAsync не викликали, транзакція відкочується в DisposeAsync.
public class UnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;

    public IProjectRepository Projects { get; }
    public IProposalRepository Proposals { get; }
    public IContractRepository Contracts { get; }
    public IMilestoneRepository Milestones { get; }
    public IPortfolioRepository Portfolio { get; }
    public ICommunityRepository Community { get; }

    private UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
        Projects = new ProjectRepository(connection, transaction);
        Proposals = new ProposalRepository(connection, transaction);
        Contracts = new ContractRepository(connection, transaction);
        Milestones = new MilestoneRepository(connection, transaction);
        Portfolio = new PortfolioRepository(connection, transaction);
        Community = new CommunityRepository(connection, transaction);
    }

    public static async Task<UnitOfWork> BeginAsync(NpgsqlDataSource dataSource)
    {
        var connection = await dataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        return new UnitOfWork(connection, transaction);
    }

    public Task CommitAsync() => transaction.CommitAsync();

    public Task RollbackAsync() => transaction.RollbackAsync();

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
