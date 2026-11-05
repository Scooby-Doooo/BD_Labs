using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CatalogBench;
using Npgsql;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;
ThreadPool.SetMinThreads(64, 64);

var SqlConnection = Environment.GetEnvironmentVariable("CATALOG_SQL") ?? "Host=localhost;Port=55432;Database=catalog;Username=postgres;Password=postgres;Max Auto Prepare=30;Auto Prepare Min Usages=2;Command Timeout=0;Maximum Pool Size=32";
var MongoConnection = Environment.GetEnvironmentVariable("CATALOG_MONGO") ?? "mongodb://localhost:57017/?maxPoolSize=32&socketTimeoutMS=0";

var command = args.ElementAtOrDefault(0);
var projects = int.Parse(args.ElementAtOrDefault(1) ?? "0", CultureInfo.InvariantCulture);
var rounds = int.Parse(args.ElementAtOrDefault(2) ?? "3", CultureInfo.InvariantCulture);
var resultsDir = Path.GetFullPath(Environment.GetEnvironmentVariable("CATALOG_RESULTS") ?? "measurements");
Directory.CreateDirectory(resultsDir);

var gen = new DataGenerator(Math.Max(projects, 1));
await using var sql = new SqlStore(SqlConnection);
await using var mongo = new MongoStore(MongoConnection);

switch (command)
{
    case "load":
        await Load(sql);
        await Load(mongo);
        break;
    case "verify":
        await Verify();
        break;
    case "explain":
        await Explain();
        break;
    case "bench":
        await using (var sqlRelaxed = new SqlStore(SqlConnection + ";Options=-c synchronous_commit=off", "PostgreSQL relaxed"))
        await using (var mongoRelaxed = new MongoStore(MongoConnection, journaled: false, name: "MongoDB relaxed"))
            await Benchmark.RunAsync(gen, sql, mongo, sqlRelaxed, mongoRelaxed, await ReadTargets(), Path.Combine(resultsDir, $"raw_{projects}.csv"), rounds);
        break;
    default:
        Console.WriteLine("Використання: load N | verify N | explain N | bench N [раунди]");
        return 1;
}
return 0;

async Task Load(ICatalogStore store)
{
    var t = Stopwatch.StartNew();
    await store.LoadAsync(gen);
    var loaded = t.Elapsed.TotalSeconds;
    t.Restart();
    await store.CreateIndexesAsync();
    var indexed = t.Elapsed.TotalSeconds;
    var size = await store.StorageAsync();
    var (docs, proposals) = (await store.CountProjectsAsync(), await store.CountProposalsAsync());
    Console.WriteLine($"{store.Name}: проєктів {docs}, заявок {proposals}, завантаження {loaded:F1} с, індекси {indexed:F1} с, дані {size.DataBytes / 1048576.0:F0} МБ, індекси {size.IndexBytes / 1048576.0:F0} МБ");
    var path = Path.Combine(resultsDir, "load.csv");
    if (!File.Exists(path)) await File.WriteAllTextAsync(path, "projects,db,proposals,load_s,index_s,data_mb,index_mb\n");
    await File.AppendAllTextAsync(path, string.Create(CultureInfo.InvariantCulture,
        $"{projects},{store.Name},{proposals},{loaded:F1},{indexed:F1},{size.DataBytes / 1048576.0:F0},{size.IndexBytes / 1048576.0:F0}\n"));
}

async Task<SqlTargets> ReadTargets()
{
    await using var ds = NpgsqlDataSource.Create(SqlConnection);
    await using var conn = await ds.OpenConnectionAsync();
    async Task<long> Max(string q) { await using var c = new NpgsqlCommand(q, conn); return (long)(await c.ExecuteScalarAsync())!; }
    var maxProject = await Max("SELECT max(id) FROM projects");
    var maxProposal = await Max("SELECT max(id) FROM proposals");
    var accept = new List<(long, long)>();
    await using (var c = new NpgsqlCommand("""
        SELECT p.id, min(pr.id) FROM projects p JOIN proposals pr ON pr.project_id = p.id
        WHERE p.status = 'open' AND pr.status = 'pending'
        GROUP BY p.id HAVING count(*) >= 2 AND count(*) < 30 ORDER BY p.id
        """, conn))
    await using (var r = await c.ExecuteReaderAsync())
        while (await r.ReadAsync()) accept.Add((r.GetInt64(0), r.GetInt64(1)));
    // Половина проєктів іде під прийняття заявок, решта під додавання, щоб сценарії не заважали один одному.
    var acceptTargets = accept.Where((_, i) => i % 2 == 0).ToList();
    var addTargets = accept.Where((_, i) => i % 2 == 1).Select(a => a.Item1).ToList();
    return new SqlTargets(maxProject, maxProposal, acceptTargets, addTargets);
}

async Task Verify()
{
    var json = new JsonSerializerOptions();
    json.Converters.Add(new MoneyConverter());
    string J<T>(T v) => JsonSerializer.Serialize(v, json);
    var failures = 0;
    void Check(string what, string a, string b)
    {
        if (a == b) return;
        if (failures++ < 5) Console.WriteLine($"РОЗБІЖНІСТЬ {what}:\n  SQL:   {a[..Math.Min(a.Length, 600)]}\n  Mongo: {b[..Math.Min(b.Length, 600)]}");
    }

    Check("проєктів", (await sql.CountProjectsAsync()).ToString(), (await mongo.CountProjectsAsync()).ToString());
    Check("заявок", (await sql.CountProposalsAsync()).ToString(), (await mongo.CountProposalsAsync()).ToString());

    var w = Benchmark.Workload.For(gen);
    const int samples = 500;
    for (var i = 0; i < samples; i++)
    {
        var id = w.CardUniform[i];
        var (a, b) = (await sql.GetCardAsync(id), await mongo.GetCardAsync(id));
        b?.Proposals.Sort((x, y) => x.Id.CompareTo(y.Id));
        Check($"картка {id}", J(a), J(b));
        var p = w.Catalog[i];
        Check($"каталог {i}", J(await sql.CatalogAsync(p.Skills, p.Min, p.Max)), J(await mongo.CatalogAsync(p.Skills, p.Min, p.Max)));
        var at = w.Attribute[i];
        Check($"характеристика {at}", J(await sql.ByAttributeAsync(at.Key, at.Value)), J(await mongo.ByAttributeAsync(at.Key, at.Value)));
        Check($"пошук {i}", J(await sql.SearchAsync(w.Search[i])), J(await mongo.SearchAsync(w.Search[i])));
        Check($"заявки фрілансера {w.Freelancer[i]}", J(await sql.FreelancerProposalsAsync(w.Freelancer[i])), J(await mongo.FreelancerProposalsAsync(w.Freelancer[i])));
    }
    Check("статистика", J(await sql.StatsByCategoryAsync()), J(await mongo.StatsByCategoryAsync()));
    Check("топ", J(await sql.TopFreelancersAsync()), J(await mongo.TopFreelancersAsync()));

    // Паралельні заявки на проєкт із 49 заявками: межу 50 не можна перевищити.
    foreach (var store in new ICatalogStore[] { sql, mongo })
    {
        const long testId = 900_000_000;
        var card = gen.NewProject(testId);
        card.Status = "open";
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var r = new Random(5);
        await store.DeleteProjectAsync(testId);
        await store.CreateProjectAsync(card);
        var nextId = 900_000_000L;
        for (var i = 0; i < 49; i++)
            await store.AddProposalAsync(testId, gen.NewProposal(r, nextId++, gen.FreelancerId(i), 100m, at, "pending"));
        var attempts = await Task.WhenAll(Enumerable.Range(49, 16).Select(i =>
        {
            var o = gen.NewProposal(new Random(i), nextId + i, gen.FreelancerId(i), 100m, at, "pending");
            return Task.Run(() => store.AddProposalAsync(testId, o));
        }));
        var total = (await store.GetCardAsync(testId))!.Proposals.Count;
        var duplicate = await store.AddProposalAsync(testId, gen.NewProposal(r, nextId + 100, gen.FreelancerId(0), 100m, at, "pending"));
        await store.DeleteProjectAsync(testId);
        Console.WriteLine($"{store.Name}: 16 паралельних заявок на 50-те місце, успішних {attempts.Count(x => x)}, у проєкті {total}, повторна заявка прийнята: {duplicate}");
        if (attempts.Count(x => x) != 1 || total != 50 || duplicate) failures++;
    }

    Console.WriteLine(failures == 0 ? $"Перевірка пройдена: {samples} вибірок по кожному типу запиту дають однакові результати." : $"Розбіжностей: {failures}");
    if (failures > 0) Environment.Exit(1);
}

async Task Explain()
{
    var q = Benchmark.Workload.For(gen).Sample();
    var text = new StringBuilder($"Параметри: {JsonSerializer.Serialize(q)}\n\n=== PostgreSQL ===\n");
    text.Append(await sql.ExplainAsync(q)).Append("\n=== MongoDB ===\n").Append(await mongo.ExplainAsync(q));
    var path = Path.Combine(resultsDir, $"explain_{projects}.txt");
    await File.WriteAllTextAsync(path, text.ToString());
    Console.WriteLine(text);
}

// Порівняння грошей незалежно від кількості знаків: 100.5 і 100.50 це одне число.
sealed class MoneyConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.GetDecimal();
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions o) =>
        writer.WriteStringValue(value.ToString("F2", CultureInfo.InvariantCulture));
}
