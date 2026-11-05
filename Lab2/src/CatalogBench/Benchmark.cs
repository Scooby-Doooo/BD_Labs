using System.Diagnostics;
using System.Globalization;

namespace CatalogBench;

public sealed record Measurement(int Ops, double Seconds, double P50, double P95, double P99)
{
    public double OpsPerSecond => Ops / Seconds;
}

public static class Benchmark
{
    static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);
    static readonly int[] ThreadLevels = [1, 16];
    const int ParamCount = 100_000;

    // Вимірювання на часовому вікні: для читання, що не змінює даних.
    static Task<Measurement> Timed(Func<int, Task> op, int threads, TimeSpan duration) =>
        Run(op, threads, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency), int.MaxValue);

    // Вимірювання на фіксованій кількості операцій: обидві бази виконують однакові зміни.
    static Task<Measurement> Fixed(Func<int, Task> op, int threads, int count) => Run(op, threads, long.MaxValue, count);

    static async Task<Measurement> Run(Func<int, Task> op, int threads, long deadline, int count)
    {
        var next = -1;
        var latencies = new List<long>[threads];
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(async () =>
        {
            var own = latencies[t] = new List<long>(4096);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= count) break;
                var t0 = Stopwatch.GetTimestamp();
                await op(i);
                own.Add(Stopwatch.GetTimestamp() - t0);
            }
        })));
        var seconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        var all = latencies.SelectMany(l => l).Order().ToArray();
        double Pct(double p) => all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(all.Length * p))] * 1000.0 / Stopwatch.Frequency;
        return new Measurement(all.Length, seconds, Pct(0.50), Pct(0.95), Pct(0.99));
    }

    static T[] Params<T>(int seed, Func<Random, T> make, int n = ParamCount)
    {
        var r = new Random(seed);
        var a = new T[n];
        for (var i = 0; i < n; i++) a[i] = make(r);
        return a;
    }

    static List<T> Shuffled<T>(IEnumerable<T> source, int seed)
    {
        var a = source.ToArray();
        new Random(seed).Shuffle(a);
        return [.. a];
    }

    public sealed class Workload
    {
        public required long[] CardUniform;
        public required long[] CardHot;
        public required (long[] Skills, decimal Min, decimal Max)[] Catalog;
        public required (string Key, string Value)[] Attribute;
        public required string[][] Search;
        public required long[] Freelancer;

        public static Workload For(DataGenerator gen) => new()
        {
            CardUniform = Params(11, r => r.NextInt64(1, gen.Projects + 1)),
            // Гарячі 20% проєктів дають близько 80% звернень; нові проєкти популярніші за старі.
            CardHot = Params(12, r => gen.Projects - (long)(gen.Projects * Math.Pow(r.NextDouble(), 7))),
            Catalog = Params(13, r =>
            {
                var a = DataGenerator.Skewed(r, gen.SkillCount, 2) + 1L;
                var b = DataGenerator.Skewed(r, gen.SkillCount, 2) + 1L;
                var min = Math.Round((decimal)Math.Exp(r.NextDouble() * 4 + 5), 2);
                return (new[] { a, b }.Distinct().ToArray(), min, min * 3);
            }),
            Attribute = Params(14, r => gen.AllAttributes[r.Next(gen.AllAttributes.Length)]),
            Search = Params(15, r => new[] { gen.Vocabulary[r.Next(300)], gen.Vocabulary[r.Next(300)] }),
            Freelancer = Params(16, r => gen.FreelancerId(r.Next(gen.Freelancers))),
        };

        public QuerySample Sample() => new(CardUniform[0], Catalog[0].Skills, Catalog[0].Min, Catalog[0].Max,
            Attribute[0].Key, Attribute[0].Value, Search[0], Freelancer[0]);
    }

    public static (string Name, Func<ICatalogStore, int, Task> Op)[] ReadScenarios(Workload w) =>
    [
        ("card_uniform", (s, i) => s.GetCardAsync(w.CardUniform[i % ParamCount])),
        ("card_hot", (s, i) => s.GetCardAsync(w.CardHot[i % ParamCount])),
        ("catalog", (s, i) => { var p = w.Catalog[i % ParamCount]; return s.CatalogAsync(p.Skills, p.Min, p.Max); }),
        ("by_attribute", (s, i) => { var p = w.Attribute[i % ParamCount]; return s.ByAttributeAsync(p.Key, p.Value); }),
        ("search", (s, i) => s.SearchAsync(w.Search[i % ParamCount])),
        ("freelancer_proposals", (s, i) => s.FreelancerProposalsAsync(w.Freelancer[i % ParamCount])),
    ];

    public static async Task RunAsync(DataGenerator gen, ICatalogStore sql, ICatalogStore mongo, ICatalogStore sqlRelaxed, ICatalogStore mongoRelaxed,
        SqlTargets targets, string csvPath, int rounds)
    {
        var w = Workload.For(gen);
        var reads = ReadScenarios(w);
        Func<ICatalogStore, int, Task>[] analytics = [(s, _) => s.StatsByCategoryAsync(), (s, _) => s.TopFreelancersAsync()];
        var culture = CultureInfo.InvariantCulture;
        if (!File.Exists(csvPath)) await File.WriteAllTextAsync(csvPath, "projects,round,db,scenario,threads,ops,seconds,ops_s,p50_ms,p95_ms,p99_ms\n");

        async Task Record(int round, ICatalogStore store, string scenario, int threads, Measurement m)
        {
            var line = string.Format(culture, "{0},{1},{2},{3},{4},{5},{6:F3},{7:F1},{8:F3},{9:F3},{10:F3}\n",
                gen.Projects, round, store.Name, scenario, threads, m.Ops, m.Seconds, m.OpsPerSecond, m.P50, m.P95, m.P99);
            await File.AppendAllTextAsync(csvPath, line);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] r{round} {store.Name,-18} {scenario,-24} t={threads,-2} ops={m.Ops,-7} {m.OpsPerSecond,9:F1}/s  p50={m.P50,9:F2}ms p95={m.P95,9:F2}ms p99={m.P99,9:F2}ms");
        }

        var nextProject = targets.MaxProjectId;
        var nextProposal = targets.MaxProposalId;
        var accept = new Queue<(long Project, long Proposal)>(Shuffled(targets.AcceptTargets, 21));
        var acceptPerRun = Math.Min(500, targets.AcceptTargets.Count / (rounds * ThreadLevels.Length));
        var addTargets = Shuffled(targets.AddTargets, 22);
        var addPerRun = Math.Min(2000, addTargets.Count / 2);
        var opTime = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var round = 1; round <= rounds; round++)
        {
            // Черговість змінюється щораунду, щоб жодна база не мала постійної переваги прогрітої ОС.
            ICatalogStore[] order = round % 2 == 1 ? [sql, mongo] : [mongo, sql];

            // Спільні для обох баз набори змін цього раунду.
            var create = new Dictionary<int, ProjectCard[]>();
            var add = new Dictionary<int, (long Project, Proposal Proposal)[]>();
            var acceptNow = new Dictionary<int, (long Project, long Proposal)[]>();
            foreach (var t in ThreadLevels)
            {
                create[t] = Enumerable.Range(0, 1000).Select(_ => gen.NewProject(++nextProject)).Select(p => { p.Status = "open"; return p; }).ToArray();
                add[t] = BuildAdd(gen, addTargets, addPerRun, round * 100 + t, ref nextProposal, opTime);
                acceptNow[t] = Enumerable.Range(0, acceptPerRun).Select(_ => accept.Dequeue()).ToArray();
            }
            var addRelaxed = BuildAdd(gen, addTargets, addPerRun, round * 100 + 77, ref nextProposal, opTime);
            var mixedAdd = BuildAdd(gen, addTargets, addPerRun, round * 100 + 88, ref nextProposal, opTime);
            var renameFreelancers = Params(1000 + round, r => gen.FreelancerId(r.Next(gen.Freelancers)), 200);

            foreach (var store in order)
            {
                await store.FlushAsync();
                foreach (var (name, op) in reads)
                    foreach (var t in ThreadLevels)
                    {
                        await Timed(i => op(store, i), t, Warmup);
                        await Record(round, store, name, t, await Timed(i => op(store, i), t, Duration));
                    }

                await Record(round, store, "stats_by_category", 1, await Fixed(i => analytics[0](store, i), 1, 3));
                await Record(round, store, "top_freelancers", 1, await Fixed(i => analytics[1](store, i), 1, 3));

                await store.FlushAsync();
                foreach (var t in ThreadLevels)
                {
                    var c = create[t];
                    await Record(round, store, "create_project", t, await Fixed(i => store.CreateProjectAsync(c[i]), t, c.Length));
                    var a = add[t];
                    await Record(round, store, "add_proposal", t, await Fixed(i => store.AddProposalAsync(a[i].Project, a[i].Proposal), t, a.Length));
                    var ac = acceptNow[t];
                    await Record(round, store, "accept_proposal", t, await Fixed(i => store.AcceptProposalAsync(ac[i].Project, ac[i].Proposal, opTime), t, ac.Length));
                }

                // Без очікування диска: той самий запис за послаблених гарантій довговічності.
                var relaxed = store == sql ? sqlRelaxed : mongoRelaxed;
                await Record(round, relaxed, "add_proposal", 16, await Fixed(i => relaxed.AddProposalAsync(addRelaxed[i].Project, addRelaxed[i].Proposal), 16, addRelaxed.Length));

                // Перейменування виконуються парами й повертають початкове значення, тож дані не змінюються.
                await Record(round, store, "rename_freelancer", 1, await Fixed(i =>
                {
                    var id = renameFreelancers[i / 2];
                    return store.RenameFreelancerAsync(id, i % 2 == 0 ? gen.UserName(id) + " renamed" : gen.UserName(id));
                }, 1, renameFreelancers.Length));
                int[] skills = [1, 50];
                await Record(round, store, "rename_skill", 1, await Fixed(i =>
                {
                    var id = skills[i / 2];
                    return store.RenameSkillAsync(id, i % 2 == 0 ? DataGenerator.SkillName(id) + " renamed" : DataGenerator.SkillName(id));
                }, 1, skills.Length * 2));

                // Типова сторінка сайту: 80% картка, 15% каталог, 5% нова заявка.
                var mixedOps = Math.Min(mixedAdd.Length * 20, 30_000);
                await Record(round, store, "mixed_80_15_5", 16, await Fixed(i =>
                {
                    var k = i % 20;
                    if (k == 0) { var m = mixedAdd[i / 20]; return store.AddProposalAsync(m.Project, m.Proposal); }
                    if (k <= 3) { var p = w.Catalog[i % ParamCount]; return store.CatalogAsync(p.Skills, p.Min, p.Max); }
                    return store.GetCardAsync(w.CardHot[i % ParamCount]);
                }, 16, mixedOps));
                await store.FlushAsync();
            }
        }
    }

    static (long Project, Proposal Proposal)[] BuildAdd(DataGenerator gen, List<long> targets, int count, int seed, ref long nextProposal, DateTime at)
    {
        var r = new Random(seed);
        var order = Shuffled(targets, seed).Take(count).ToArray();
        var result = new (long, Proposal)[order.Length];
        for (var i = 0; i < order.Length; i++)
            result[i] = (order[i], gen.NewProposal(r, ++nextProposal, gen.FreelancerId(r.Next(gen.Freelancers)), 1000m, at, "pending"));
        return result;
    }
}

public sealed record SqlTargets(long MaxProjectId, long MaxProposalId, List<(long Project, long Proposal)> AcceptTargets, List<long> AddTargets);
