using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CatalogBench;

// Один документ колекції projects. У PostgreSQL ті самі дані лежать у 9 таблицях:
// projects, categories, skills, project_skills, project_attributes, proposals,
// users, client_profiles, freelancer_profiles.
public sealed class ProjectCard
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Budget { get; set; }
    public DateTime Deadline { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Ref Category { get; set; } = new();
    public ClientRef Client { get; set; } = new();
    public List<Ref> Skills { get; set; } = [];
    public List<Attr> Attrs { get; set; } = [];
    public List<Proposal> Proposals { get; set; } = [];
}

[BsonNoId] public sealed class Ref { public long Id { get; set; } public string Name { get; set; } = ""; }
[BsonNoId] public sealed class ClientRef { public long Id { get; set; } public string Name { get; set; } = ""; public string? Company { get; set; } }
[BsonNoId] public sealed class Attr { public string K { get; set; } = ""; public string V { get; set; } = ""; }
[BsonNoId] public sealed class FreelancerRef { public long Id { get; set; } public string Name { get; set; } = ""; public string Title { get; set; } = ""; }

[BsonNoId]
public sealed class Proposal
{
    public long Id { get; set; }
    public FreelancerRef Freelancer { get; set; } = new();
    public string CoverLetter { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Amount { get; set; }
    public int Days { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class CatalogItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Budget { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Category { get; set; } = "";
    public string Client { get; set; } = "";
    public List<string> Skills { get; set; } = [];
    public int ProposalCount { get; set; }
}

[BsonNoId]
public sealed class FreelancerProposal
{
    public long ProposalId { get; set; }
    public long ProjectId { get; set; }
    public string ProjectTitle { get; set; } = "";
    [BsonRepresentation(BsonType.Decimal128)] public decimal Amount { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

[BsonNoId]
public sealed class CategoryStat
{
    public string Category { get; set; } = "";
    public long Proposals { get; set; }
    public long Accepted { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal AvgAmount { get; set; }
}

[BsonNoId]
public sealed class FreelancerStat
{
    public long FreelancerId { get; set; }
    public string Name { get; set; } = "";
    public long Contracts { get; set; }
    [BsonRepresentation(BsonType.Decimal128)] public decimal Total { get; set; }
}

public sealed record StorageInfo(long DataBytes, long IndexBytes);

public sealed record QuerySample(long ProjectId, long[] SkillIds, decimal Min, decimal Max, string Key, string Value, string[] Words, long FreelancerId);

public interface ICatalogStore : IAsyncDisposable
{
    string Name { get; }
    Task LoadAsync(DataGenerator gen);
    Task CreateIndexesAsync();
    Task<StorageInfo> StorageAsync();
    Task FlushAsync();
    Task<string> ExplainAsync(QuerySample q);

    Task<long> CountProjectsAsync();
    Task<long> CountProposalsAsync();

    Task<ProjectCard?> GetCardAsync(long id);
    Task<List<CatalogItem>> CatalogAsync(long[] skillIds, decimal min, decimal max);
    Task<List<CatalogItem>> ByAttributeAsync(string key, string value);
    Task<List<CatalogItem>> SearchAsync(string[] words);
    Task<List<FreelancerProposal>> FreelancerProposalsAsync(long freelancerId);
    Task<List<CategoryStat>> StatsByCategoryAsync();
    Task<List<FreelancerStat>> TopFreelancersAsync();

    Task CreateProjectAsync(ProjectCard card);
    Task<bool> AddProposalAsync(long projectId, Proposal proposal);
    Task<bool> AcceptProposalAsync(long projectId, long proposalId, DateTime at);
    Task RenameFreelancerAsync(long id, string name);
    Task RenameSkillAsync(long id, string name);
    Task DeleteProjectAsync(long id);
}
