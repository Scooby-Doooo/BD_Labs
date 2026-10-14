using FreelanceLab.Data;
using Npgsql;

namespace FreelanceLab.Services;

// Вибір виконавця: кілька репозиторіїв в одній транзакції (Unit of Work).
// Якщо будь-який крок падає, відкочується все: заявка, контракт і етапи.
public class AwardService(NpgsqlDataSource dataSource)
{
    public async Task<long> AwardAsync(long actor, long projectId, long proposalId, List<MilestoneInput> milestones)
    {
        await using var work = await UnitOfWork.BeginAsync(dataSource);
        try
        {
            await work.Proposals.AcceptAsync(actor, proposalId);
            var contractId = await work.Contracts.CreateAsync(actor, proposalId);
            foreach (var milestone in milestones)
                await work.Milestones.CreateAsync(actor, contractId, milestone);
            await work.Projects.SetStatusAsync(actor, projectId, "in_progress");

            await work.CommitAsync();
            return contractId;
        }
        catch
        {
            await work.RollbackAsync();
            throw;
        }
    }
}
