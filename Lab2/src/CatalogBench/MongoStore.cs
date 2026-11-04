using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace CatalogBench;

public sealed class MongoStore : ICatalogStore
{
    static MongoStore() => ConventionRegistry.Register("camelCase",
        new ConventionPack { new CamelCaseElementNameConvention(), new IgnoreExtraElementsConvention(true) }, _ => true);

    readonly MongoClient _client;
    readonly IMongoDatabase _db;
    readonly IMongoCollection<ProjectCard> _projects;
    public string Name { get; }

    public MongoStore(string connectionString, bool journaled = true, string name = "MongoDB")
    {
        Name = name;
        _client = new MongoClient(connectionString);
        _db = _client.GetDatabase("catalog");
        // j:true: запис підтверджується після скидання журналу на диск, як COMMIT у PostgreSQL.
        _projects = _db.GetCollection<ProjectCard>("projects").WithWriteConcern(new WriteConcern(1, journal: journaled));
    }

    static readonly BsonDocument ListProjection = BsonDocument.Parse("""
        { title: 1, budget: 1, createdAt: 1, category: "$category.name", client: "$client.name",
          skills: "$skills.name", proposalCount: { $size: "$proposals" } }
        """);
    static readonly BsonDocument NewestFirst = new("createdAt", -1);

    static BsonDocument CatalogFilter(long[] skillIds, decimal min, decimal max) => new()
    {
        { "status", "open" },
        { "skills.id", new BsonDocument("$in", new BsonArray(skillIds)) },
        { "budget", new BsonDocument { { "$gte", new BsonDecimal128(min) }, { "$lte", new BsonDecimal128(max) } } },
    };
    static BsonDocument AttributeFilter(string key, string value) => new()
    {
        { "attrs", new BsonDocument("$elemMatch", new BsonDocument { { "k", key }, { "v", value } }) },
        { "status", "open" },
    };
    // Логічне АБО: фраза в лапках у $text шукає підрядок, тому точного І за словами MongoDB не має.
    static BsonDocument SearchFilter(string[] words) => new()
    {
        { "$text", new BsonDocument("$search", string.Join(' ', words)) },
        { "status", "open" },
    };

    static BsonDocument[] FreelancerPipeline(long id) =>
    [
        new("$match", new BsonDocument("proposals.freelancer.id", id)),
        new("$unwind", "$proposals"),
        new("$match", new BsonDocument("proposals.freelancer.id", id)),
        new("$sort", new BsonDocument { { "proposals.createdAt", -1 }, { "proposals.id", -1 } }),
        new("$limit", 20),
        BsonDocument.Parse("""
            { $project: { _id: 0, proposalId: "$proposals.id", projectId: "$_id", projectTitle: "$title",
                          amount: "$proposals.amount", status: "$proposals.status", createdAt: "$proposals.createdAt" } }
            """),
    ];
    static readonly BsonDocument[] StatsPipeline = ParsePipeline("""
        [ { $project: { category: "$category.name", "proposals.amount": 1, "proposals.status": 1 } },
          { $unwind: "$proposals" },
          { $group: { _id: "$category", proposals: { $sum: 1 },
                      accepted: { $sum: { $cond: [ { $eq: [ "$proposals.status", "accepted" ] }, 1, 0 ] } },
                      avgAmount: { $avg: "$proposals.amount" } } },
          { $sort: { _id: 1 } },
          { $project: { _id: 0, category: "$_id", proposals: 1, accepted: 1, avgAmount: { $round: [ "$avgAmount", 2 ] } } } ]
        """);
    static readonly BsonDocument[] TopFreelancersPipeline = ParsePipeline("""
        [ { $match: { "proposals.status": "accepted" } },
          { $unwind: "$proposals" },
          { $match: { "proposals.status": "accepted" } },
          { $group: { _id: "$proposals.freelancer.id", name: { $first: "$proposals.freelancer.name" },
                      contracts: { $sum: 1 }, total: { $sum: "$proposals.amount" } } },
          { $sort: { total: -1, _id: 1 } },
          { $limit: 20 },
          { $project: { _id: 0, freelancerId: "$_id", name: 1, contracts: 1, total: 1 } } ]
        """);

    static BsonDocument[] ParsePipeline(string json) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(json).Select(v => v.AsBsonDocument).ToArray();

    public async Task LoadAsync(DataGenerator gen)
    {
        await _db.DropCollectionAsync("projects");
        var batch = new List<ProjectCard>(1000);
        foreach (var p in gen.AllProjects())
        {
            batch.Add(p);
            if (batch.Count < 1000) continue;
            await _projects.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false });
            batch.Clear();
        }
        if (batch.Count > 0) await _projects.InsertManyAsync(batch);
    }

    public async Task CreateIndexesAsync()
    {
        await _projects.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ProjectCard>(BsonDocument.Parse("""{ status: 1, "skills.id": 1, createdAt: -1, budget: 1 }""")),
            new CreateIndexModel<ProjectCard>(BsonDocument.Parse("""{ "attrs.k": 1, "attrs.v": 1, status: 1, createdAt: -1 }""")),
            new CreateIndexModel<ProjectCard>(BsonDocument.Parse("""{ "proposals.freelancer.id": 1 }""")),
            new CreateIndexModel<ProjectCard>(BsonDocument.Parse("""{ title: "text", description: "text" }"""),
                new CreateIndexOptions { DefaultLanguage = "none" }),
        });
    }

    public async Task<StorageInfo> StorageAsync()
    {
        var s = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", "projects"));
        return new StorageInfo(s["storageSize"].ToInt64(), s["totalIndexSize"].ToInt64());
    }

    public Task FlushAsync() => _client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("fsync", 1));

    public Task<long> CountProjectsAsync() => _projects.CountDocumentsAsync(FilterDefinition<ProjectCard>.Empty);

    public async Task<long> CountProposalsAsync()
    {
        var r = await _projects.Aggregate<BsonDocument>(ParsePipeline("""[ { $group: { _id: null, n: { $sum: { $size: "$proposals" } } } } ]""")).FirstAsync();
        return r["n"].ToInt64();
    }

    public async Task<ProjectCard?> GetCardAsync(long id) =>
        await _projects.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();

    Task<List<CatalogItem>> List(BsonDocument filter) =>
        _projects.Find(filter).Sort(NewestFirst).Project<CatalogItem>(ListProjection).Limit(20).ToListAsync();

    public Task<List<CatalogItem>> CatalogAsync(long[] skillIds, decimal min, decimal max) => List(CatalogFilter(skillIds, min, max));
    public Task<List<CatalogItem>> ByAttributeAsync(string key, string value) => List(AttributeFilter(key, value));
    public Task<List<CatalogItem>> SearchAsync(string[] words) => List(SearchFilter(words));

    public Task<List<FreelancerProposal>> FreelancerProposalsAsync(long id) =>
        _projects.Aggregate<FreelancerProposal>(FreelancerPipeline(id)).ToListAsync();

    public Task<List<CategoryStat>> StatsByCategoryAsync() =>
        _projects.Aggregate<CategoryStat>(StatsPipeline, new AggregateOptions { AllowDiskUse = true }).ToListAsync();

    public Task<List<FreelancerStat>> TopFreelancersAsync() =>
        _projects.Aggregate<FreelancerStat>(TopFreelancersPipeline, new AggregateOptions { AllowDiskUse = true }).ToListAsync();

    public Task CreateProjectAsync(ProjectCard card) => _projects.InsertOneAsync(card);

    // Усі перевірки в одному фільтрі, тому зміна атомарна без блокувань на боці застосунку.
    public async Task<bool> AddProposalAsync(long projectId, Proposal proposal)
    {
        var filter = new BsonDocument
        {
            { "_id", projectId },
            { "status", "open" },
            { $"proposals.{DataGenerator.MaxProposals - 1}", new BsonDocument("$exists", false) },
            { "proposals.freelancer.id", new BsonDocument("$ne", proposal.Freelancer.Id) },
        };
        var update = Builders<ProjectCard>.Update.Push(p => p.Proposals, proposal);
        return (await _projects.UpdateOneAsync(filter, update)).ModifiedCount == 1;
    }

    public async Task<bool> AcceptProposalAsync(long projectId, long proposalId, DateTime at)
    {
        var filter = new BsonDocument { { "_id", projectId }, { "status", "open" } };
        var update = new BsonDocument("$set", new BsonDocument
        {
            { "status", "in_progress" },
            { "updatedAt", at },
            { "proposals.$[win].status", "accepted" },
            { "proposals.$[lose].status", "rejected" },
        });
        var options = new UpdateOptions
        {
            ArrayFilters =
            [
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument { { "win.id", proposalId }, { "win.status", "pending" } }),
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument { { "lose.id", new BsonDocument("$ne", proposalId) }, { "lose.status", "pending" } }),
            ],
        };
        return (await _projects.UpdateOneAsync(filter, update, options)).ModifiedCount == 1;
    }

    // Ціна денормалізації: ім'я скопійоване в кожну заявку фрілансера.
    public Task RenameFreelancerAsync(long id, string name) => _projects.UpdateManyAsync(
        new BsonDocument("proposals.freelancer.id", id),
        new BsonDocument("$set", new BsonDocument("proposals.$[o].freelancer.name", name)),
        new UpdateOptions { ArrayFilters = [new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("o.freelancer.id", id))] });

    public Task RenameSkillAsync(long id, string name) => _projects.UpdateManyAsync(
        new BsonDocument("skills.id", id),
        new BsonDocument("$set", new BsonDocument("skills.$[s].name", name)),
        new UpdateOptions { ArrayFilters = [new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("s.id", id))] });

    public Task DeleteProjectAsync(long id) => _projects.DeleteOneAsync(new BsonDocument("_id", id));

    public async Task<string> ExplainAsync(QuerySample q)
    {
        var text = new StringBuilder();
        async Task Plan(string title, BsonDocument command)
        {
            var r = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "explain", command },
                { "verbosity", "executionStats" },
            });
            var stages = new List<string>();
            Walk(Find(r, "winningPlan") ?? r, stages);
            var stats = Find(r, "executionStats");
            text.AppendLine($"--- {title}");
            text.AppendLine("plan: " + string.Join(" <- ", stages));
            if (stats is not null)
                text.AppendLine($"returned={stats.GetValue("nReturned", 0)} keysExamined={stats.GetValue("totalKeysExamined", 0)} docsExamined={stats.GetValue("totalDocsExamined", 0)} ms={stats.GetValue("executionTimeMillis", 0)}");
        }
        BsonDocument FindCommand(BsonDocument filter) => new()
        {
            { "find", "projects" }, { "filter", filter }, { "sort", NewestFirst }, { "projection", ListProjection }, { "limit", 20 },
        };
        BsonDocument AggregateCommand(BsonDocument[] pipeline) => new()
        {
            { "aggregate", "projects" }, { "pipeline", new BsonArray(pipeline) }, { "cursor", new BsonDocument() },
        };
        await Plan("card", new BsonDocument { { "find", "projects" }, { "filter", new BsonDocument("_id", q.ProjectId) }, { "limit", 1 } });
        await Plan("catalog", FindCommand(CatalogFilter(q.SkillIds, q.Min, q.Max)));
        await Plan("by_attribute", FindCommand(AttributeFilter(q.Key, q.Value)));
        await Plan("search", FindCommand(SearchFilter(q.Words)));
        await Plan("freelancer_proposals", AggregateCommand(FreelancerPipeline(q.FreelancerId)));
        await Plan("stats_by_category", AggregateCommand(StatsPipeline));
        await Plan("top_freelancers", AggregateCommand(TopFreelancersPipeline));
        return text.ToString();
    }

    static BsonDocument? Find(BsonValue value, string key)
    {
        if (value is BsonDocument d)
        {
            if (d.TryGetValue(key, out var hit) && hit is BsonDocument found) return found;
            foreach (var e in d) if (Find(e.Value, key) is { } nested) return nested;
        }
        else if (value is BsonArray a)
            foreach (var v in a) if (Find(v, key) is { } nested) return nested;
        return null;
    }

    static void Walk(BsonValue value, List<string> stages)
    {
        if (value is BsonDocument d)
        {
            if (d.TryGetValue("stage", out var s))
                stages.Add(d.TryGetValue("indexName", out var i) ? $"{s}({i})" : s.ToString()!);
            foreach (var e in d) if (e.Value.IsBsonDocument || e.Value.IsBsonArray) Walk(e.Value, stages);
        }
        else if (value is BsonArray a)
            foreach (var v in a) Walk(v, stages);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
