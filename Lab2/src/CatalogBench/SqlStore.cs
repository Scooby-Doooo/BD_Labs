using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace CatalogBench;

public sealed class SqlStore(string connectionString, string name = "PostgreSQL") : ICatalogStore
{
    readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(connectionString);
    public string Name => name;

    // Той самий набір полів, що й у проєкції MongoDB, на всі сторінки каталогу.
    const string ListSelect = """
        SELECT p.id, p.title, p.budget, p.created_at, c.name, u.display_name,
               (SELECT array_agg(s.name ORDER BY s.id) FROM project_skills ps JOIN skills s ON s.id = ps.skill_id WHERE ps.project_id = p.id),
               (SELECT count(*) FROM proposals pr WHERE pr.project_id = p.id)
        FROM projects p
        JOIN categories c ON c.id = p.category_id
        JOIN users u ON u.id = p.client_id

        """;
    const string SqlCatalog = ListSelect + """
        WHERE p.status = 'open' AND p.budget BETWEEN $1 AND $2
          AND EXISTS (SELECT 1 FROM project_skills ps WHERE ps.project_id = p.id AND ps.skill_id = ANY($3))
        ORDER BY p.created_at DESC LIMIT 20
        """;
    const string SqlByAttribute = ListSelect + """
        WHERE p.status = 'open'
          AND EXISTS (SELECT 1 FROM project_attributes a WHERE a.project_id = p.id AND a.key = $1 AND a.value = $2)
        ORDER BY p.created_at DESC LIMIT 20
        """;
    const string SqlSearch = ListSelect + """
        WHERE p.status = 'open' AND p.search_vector @@ to_tsquery('simple', $1)
        ORDER BY p.created_at DESC LIMIT 20
        """;
    const string SqlFreelancerProposals = """
        SELECT pr.id, pr.project_id, p.title, pr.proposed_amount, pr.status, pr.created_at
        FROM proposals pr JOIN projects p ON p.id = pr.project_id
        WHERE pr.freelancer_id = $1
        ORDER BY pr.created_at DESC, pr.id DESC LIMIT 20
        """;
    const string SqlStatsByCategory = """
        SELECT c.name, count(*), count(*) FILTER (WHERE pr.status = 'accepted'), round(avg(pr.proposed_amount), 2)
        FROM proposals pr
        JOIN projects p ON p.id = pr.project_id
        JOIN categories c ON c.id = p.category_id
        GROUP BY c.name ORDER BY c.name COLLATE "C"
        """;
    const string SqlTopFreelancers = """
        SELECT pr.freelancer_id, u.display_name, count(*), sum(pr.proposed_amount)
        FROM proposals pr JOIN users u ON u.id = pr.freelancer_id
        WHERE pr.status = 'accepted'
        GROUP BY pr.freelancer_id, u.display_name
        ORDER BY sum(pr.proposed_amount) DESC, pr.freelancer_id LIMIT 20
        """;
    static readonly string[] SqlCard =
    [
        """
        SELECT p.id, p.title, p.description, p.budget, p.deadline, p.status, p.created_at, p.updated_at,
               c.id, c.name, p.client_id, u.display_name, cp.company_name
        FROM projects p
        JOIN categories c ON c.id = p.category_id
        JOIN users u ON u.id = p.client_id
        JOIN client_profiles cp ON cp.user_id = p.client_id
        WHERE p.id = $1
        """,
        "SELECT s.id, s.name FROM project_skills ps JOIN skills s ON s.id = ps.skill_id WHERE ps.project_id = $1 ORDER BY s.id",
        "SELECT a.key, a.value FROM project_attributes a WHERE a.project_id = $1 ORDER BY a.key COLLATE \"C\"",
        """
        SELECT pr.id, pr.freelancer_id, u.display_name, fp.title, pr.cover_letter, pr.proposed_amount, pr.estimated_days, pr.status, pr.created_at
        FROM proposals pr
        JOIN users u ON u.id = pr.freelancer_id
        JOIN freelancer_profiles fp ON fp.user_id = pr.freelancer_id
        WHERE pr.project_id = $1
        ORDER BY pr.id
        """,
    ];

    static NpgsqlBatchCommand Cmd(string sql, params object[] args)
    {
        var c = new NpgsqlBatchCommand(sql);
        foreach (var a in args) c.Parameters.Add(new NpgsqlParameter { Value = a });
        return c;
    }

    // Батч виконується за один мережевий обмін і в одній неявній транзакції.
    NpgsqlBatch Batch(params NpgsqlBatchCommand[] commands)
    {
        var b = _db.CreateBatch();
        foreach (var c in commands) b.BatchCommands.Add(c);
        return b;
    }

    static string SqlFile(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sql", file), Encoding.UTF8);

    async Task Exec(string sql)
    {
        await using var c = _db.CreateCommand(sql);
        c.CommandTimeout = 0;
        await c.ExecuteNonQueryAsync();
    }

    public async Task LoadAsync(DataGenerator gen)
    {
        await Exec("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await Exec(SqlFile("01_schema.sql"));

        await Copy("COPY categories (id, name) FROM STDIN (FORMAT BINARY)", async w =>
        {
            for (var i = 1; i <= gen.CategoryCount; i++) await w.WriteRowAsync(default, (long)i, DataGenerator.CategoryName(i));
        });
        await Copy("COPY skills (id, name) FROM STDIN (FORMAT BINARY)", async w =>
        {
            for (var i = 1; i <= gen.SkillCount; i++) await w.WriteRowAsync(default, (long)i, DataGenerator.SkillName(i));
        });
        await Copy("COPY users (id, email, display_name) FROM STDIN (FORMAT BINARY)", async w =>
        {
            for (long i = 1; i <= gen.UserCount; i++) await w.WriteRowAsync(default, i, $"user{i}@example.com", gen.UserName(i));
        });
        await Copy("COPY client_profiles (user_id, company_name) FROM STDIN (FORMAT BINARY)", async w =>
        {
            for (long i = 1; i <= gen.Clients; i++)
            {
                await w.StartRowAsync();
                await w.WriteAsync(i);
                if (gen.Company(i) is { } company) await w.WriteAsync(company); else await w.WriteNullAsync();
            }
        });
        await Copy("COPY freelancer_profiles (user_id, title, hourly_rate) FROM STDIN (FORMAT BINARY)", async w =>
        {
            for (var i = 0; i < gen.Freelancers; i++)
            {
                var id = gen.FreelancerId(i);
                await w.WriteRowAsync(default, id, gen.FreelancerTitle(id), gen.HourlyRate(id));
            }
        });

        // Чотири таблиці заповнюються паралельно з одного проходу по генератору.
        await using var c1 = await _db.OpenConnectionAsync();
        await using var c2 = await _db.OpenConnectionAsync();
        await using var c3 = await _db.OpenConnectionAsync();
        await using var c4 = await _db.OpenConnectionAsync();
        await using var projects = await c1.BeginBinaryImportAsync("COPY projects (id, client_id, category_id, title, description, budget, deadline, status, created_at, updated_at) FROM STDIN (FORMAT BINARY)");
        await using var skills = await c2.BeginBinaryImportAsync("COPY project_skills (project_id, skill_id) FROM STDIN (FORMAT BINARY)");
        await using var attrs = await c3.BeginBinaryImportAsync("COPY project_attributes (project_id, key, value) FROM STDIN (FORMAT BINARY)");
        await using var proposals = await c4.BeginBinaryImportAsync("COPY proposals (id, project_id, freelancer_id, cover_letter, proposed_amount, estimated_days, status, created_at) FROM STDIN (FORMAT BINARY)");
        foreach (var p in gen.AllProjects())
        {
            await projects.StartRowAsync();
            await projects.WriteAsync(p.Id);
            await projects.WriteAsync(p.Client.Id);
            await projects.WriteAsync(p.Category.Id);
            await projects.WriteAsync(p.Title);
            await projects.WriteAsync(p.Description);
            await projects.WriteAsync(p.Budget);
            await projects.WriteAsync(p.Deadline, NpgsqlDbType.Date);
            await projects.WriteAsync(p.Status);
            await projects.WriteAsync(p.CreatedAt);
            await projects.WriteAsync(p.UpdatedAt);
            foreach (var s in p.Skills) await skills.WriteRowAsync(default, p.Id, s.Id);
            foreach (var a in p.Attrs) await attrs.WriteRowAsync(default, p.Id, a.K, a.V);
            foreach (var o in p.Proposals)
            {
                await proposals.StartRowAsync();
                await proposals.WriteAsync(o.Id);
                await proposals.WriteAsync(p.Id);
                await proposals.WriteAsync(o.Freelancer.Id);
                await proposals.WriteAsync(o.CoverLetter);
                await proposals.WriteAsync(o.Amount);
                await proposals.WriteAsync(o.Days);
                await proposals.WriteAsync(o.Status);
                await proposals.WriteAsync(o.CreatedAt);
            }
        }
        await projects.CompleteAsync();
        await skills.CompleteAsync();
        await attrs.CompleteAsync();
        await proposals.CompleteAsync();
    }

    async Task Copy(string sql, Func<NpgsqlBinaryImporter, Task> write)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var w = await conn.BeginBinaryImportAsync(sql);
        await write(w);
        await w.CompleteAsync();
    }

    public async Task CreateIndexesAsync()
    {
        await Exec(SqlFile("02_indexes.sql"));
        await Exec("VACUUM ANALYZE");
        await Exec("CHECKPOINT");
    }

    public async Task<StorageInfo> StorageAsync()
    {
        await using var c = _db.CreateCommand("""
            SELECT sum(pg_table_size(oid))::bigint, sum(pg_indexes_size(oid))::bigint
            FROM pg_class WHERE relkind = 'r' AND relnamespace = 'public'::regnamespace
            """);
        await using var r = await c.ExecuteReaderAsync();
        await r.ReadAsync();
        return new StorageInfo(r.GetInt64(0), r.GetInt64(1));
    }

    public Task FlushAsync() => Exec("CHECKPOINT");

    async Task<long> Scalar(string sql)
    {
        await using var c = _db.CreateCommand(sql);
        return (long)(await c.ExecuteScalarAsync())!;
    }
    public Task<long> CountProjectsAsync() => Scalar("SELECT count(*) FROM projects");
    public Task<long> CountProposalsAsync() => Scalar("SELECT count(*) FROM proposals");

    // Читаємо картку за один мережевий обмін: чотири запити в одному батчі.
    public async Task<ProjectCard?> GetCardAsync(long id)
    {
        await using var b = Batch(SqlCard.Select(s => Cmd(s, id)).ToArray());
        await using var r = await b.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var card = new ProjectCard
        {
            Id = r.GetInt64(0),
            Title = r.GetString(1),
            Description = r.GetString(2),
            Budget = r.GetDecimal(3),
            Deadline = DateTime.SpecifyKind(r.GetFieldValue<DateTime>(4), DateTimeKind.Utc),
            Status = r.GetString(5),
            CreatedAt = r.GetDateTime(6),
            UpdatedAt = r.GetDateTime(7),
            Category = new Ref { Id = r.GetInt64(8), Name = r.GetString(9) },
            Client = new ClientRef { Id = r.GetInt64(10), Name = r.GetString(11), Company = r.IsDBNull(12) ? null : r.GetString(12) },
        };
        await r.NextResultAsync();
        while (await r.ReadAsync()) card.Skills.Add(new Ref { Id = r.GetInt64(0), Name = r.GetString(1) });
        await r.NextResultAsync();
        while (await r.ReadAsync()) card.Attrs.Add(new Attr { K = r.GetString(0), V = r.GetString(1) });
        await r.NextResultAsync();
        while (await r.ReadAsync())
            card.Proposals.Add(new Proposal
            {
                Id = r.GetInt64(0),
                Freelancer = new FreelancerRef { Id = r.GetInt64(1), Name = r.GetString(2), Title = r.GetString(3) },
                CoverLetter = r.GetString(4),
                Amount = r.GetDecimal(5),
                Days = r.GetInt32(6),
                Status = r.GetString(7),
                CreatedAt = r.GetDateTime(8),
            });
        return card;
    }

    async Task<List<CatalogItem>> List(string sql, params object[] args)
    {
        await using var b = Batch(Cmd(sql, args));
        await using var r = await b.ExecuteReaderAsync();
        var items = new List<CatalogItem>(20);
        while (await r.ReadAsync())
            items.Add(new CatalogItem
            {
                Id = r.GetInt64(0),
                Title = r.GetString(1),
                Budget = r.GetDecimal(2),
                CreatedAt = r.GetDateTime(3),
                Category = r.GetString(4),
                Client = r.GetString(5),
                Skills = r.GetFieldValue<string[]>(6).ToList(),
                ProposalCount = (int)r.GetInt64(7),
            });
        return items;
    }

    public Task<List<CatalogItem>> CatalogAsync(long[] skillIds, decimal min, decimal max) => List(SqlCatalog, min, max, skillIds);
    public Task<List<CatalogItem>> ByAttributeAsync(string key, string value) => List(SqlByAttribute, key, value);
    public Task<List<CatalogItem>> SearchAsync(string[] words) => List(SqlSearch, string.Join(" | ", words));

    public async Task<List<FreelancerProposal>> FreelancerProposalsAsync(long freelancerId)
    {
        await using var b = Batch(Cmd(SqlFreelancerProposals, freelancerId));
        await using var r = await b.ExecuteReaderAsync();
        var items = new List<FreelancerProposal>(20);
        while (await r.ReadAsync())
            items.Add(new FreelancerProposal
            {
                ProposalId = r.GetInt64(0),
                ProjectId = r.GetInt64(1),
                ProjectTitle = r.GetString(2),
                Amount = r.GetDecimal(3),
                Status = r.GetString(4),
                CreatedAt = r.GetDateTime(5),
            });
        return items;
    }

    public async Task<List<CategoryStat>> StatsByCategoryAsync()
    {
        await using var c = _db.CreateCommand(SqlStatsByCategory);
        c.CommandTimeout = 0;
        await using var r = await c.ExecuteReaderAsync();
        var items = new List<CategoryStat>();
        while (await r.ReadAsync())
            items.Add(new CategoryStat { Category = r.GetString(0), Proposals = r.GetInt64(1), Accepted = r.GetInt64(2), AvgAmount = r.GetDecimal(3) });
        return items;
    }

    public async Task<List<FreelancerStat>> TopFreelancersAsync()
    {
        await using var c = _db.CreateCommand(SqlTopFreelancers);
        c.CommandTimeout = 0;
        await using var r = await c.ExecuteReaderAsync();
        var items = new List<FreelancerStat>();
        while (await r.ReadAsync())
            items.Add(new FreelancerStat { FreelancerId = r.GetInt64(0), Name = r.GetString(1), Contracts = r.GetInt64(2), Total = r.GetDecimal(3) });
        return items;
    }

    public async Task CreateProjectAsync(ProjectCard p)
    {
        await using var b = Batch(
            Cmd("""
                INSERT INTO projects (id, client_id, category_id, title, description, budget, deadline, status, created_at, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7::date, $8, $9, $10)
                """, p.Id, p.Client.Id, p.Category.Id, p.Title, p.Description, p.Budget, p.Deadline, p.Status, p.CreatedAt, p.UpdatedAt),
            Cmd("INSERT INTO project_skills SELECT $1, unnest($2::bigint[])", p.Id, p.Skills.Select(s => s.Id).ToArray()),
            Cmd("INSERT INTO project_attributes SELECT $1, unnest($2::text[]), unnest($3::text[])", p.Id, p.Attrs.Select(a => a.K).ToArray(), p.Attrs.Select(a => a.V).ToArray()));
        await b.ExecuteNonQueryAsync();
    }

    // Правило "не більше 50 заявок" вимагає блокування рядка проєкту: без нього два паралельні
    // запити порахують по 49 заявок і обидва вставлять свою.
    public async Task<bool> AddProposalAsync(long projectId, Proposal o)
    {
        await using var b = Batch(
            Cmd("SELECT 1 FROM projects WHERE id = $1 AND status = 'open' FOR UPDATE", projectId),
            Cmd($"""
                INSERT INTO proposals (id, project_id, freelancer_id, cover_letter, proposed_amount, estimated_days, status, created_at)
                SELECT $1, $2, $3, $4, $5, $6, 'pending', $7
                WHERE EXISTS (SELECT 1 FROM projects WHERE id = $2 AND status = 'open')
                  AND (SELECT count(*) FROM proposals WHERE project_id = $2) < {DataGenerator.MaxProposals}
                ON CONFLICT (project_id, freelancer_id) DO NOTHING
                """, o.Id, projectId, o.Freelancer.Id, o.CoverLetter, o.Amount, o.Days, o.CreatedAt));
        await b.ExecuteNonQueryAsync();
        return b.BatchCommands[1].RecordsAffected == 1;
    }

    public async Task<bool> AcceptProposalAsync(long projectId, long proposalId, DateTime at)
    {
        await using var b = Batch(
            Cmd("UPDATE projects SET status = 'in_progress', updated_at = $2 WHERE id = $1 AND status = 'open'", projectId, at),
            Cmd("""
                UPDATE proposals SET status = CASE WHEN id = $2 THEN 'accepted' ELSE 'rejected' END
                WHERE project_id = $1 AND status = 'pending'
                  AND EXISTS (SELECT 1 FROM projects WHERE id = $1 AND status = 'in_progress' AND updated_at = $3)
                """, projectId, proposalId, at));
        await b.ExecuteNonQueryAsync();
        return b.BatchCommands[0].RecordsAffected == 1;
    }

    public async Task RenameFreelancerAsync(long id, string name)
    {
        await using var b = Batch(Cmd("UPDATE users SET display_name = $2 WHERE id = $1", id, name));
        await b.ExecuteNonQueryAsync();
    }

    public async Task RenameSkillAsync(long id, string name)
    {
        await using var b = Batch(Cmd("UPDATE skills SET name = $2 WHERE id = $1", id, name));
        await b.ExecuteNonQueryAsync();
    }

    public async Task DeleteProjectAsync(long id)
    {
        await using var b = Batch(
            Cmd("DELETE FROM proposals WHERE project_id = $1", id),
            Cmd("DELETE FROM project_skills WHERE project_id = $1", id),
            Cmd("DELETE FROM project_attributes WHERE project_id = $1", id),
            Cmd("DELETE FROM projects WHERE id = $1", id));
        await b.ExecuteNonQueryAsync();
    }

    public async Task<string> ExplainAsync(QuerySample q)
    {
        var text = new StringBuilder();
        async Task Plan(string title, string sql, params object[] args)
        {
            text.AppendLine($"--- {title}");
            await using var b = Batch(Cmd("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) " + sql, args));
            await using var r = await b.ExecuteReaderAsync();
            while (await r.ReadAsync()) text.AppendLine(r.GetString(0));
        }
        for (var i = 0; i < SqlCard.Length; i++) await Plan($"card, query {i + 1} of {SqlCard.Length}", SqlCard[i], q.ProjectId);
        await Plan("catalog", SqlCatalog, q.Min, q.Max, q.SkillIds);
        await Plan("by_attribute", SqlByAttribute, q.Key, q.Value);
        await Plan("search", SqlSearch, string.Join(" | ", q.Words));
        await Plan("freelancer_proposals", SqlFreelancerProposals, q.FreelancerId);
        await Plan("stats_by_category", SqlStatsByCategory);
        await Plan("top_freelancers", SqlTopFreelancers);
        return text.ToString();
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();
}
