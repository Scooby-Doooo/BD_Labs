namespace CatalogBench;

// Детермінований генератор: при однаковому N обидві бази отримують однакові дані.
public sealed class DataGenerator
{
    public const int MaxProposals = 50;
    static readonly DateTime Start = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static readonly (string Name, (string Key, string[] Values)[] Attrs)[] CategoryDefs =
    [
        ("Web development", [("stack", ["dotnet", "node", "python", "php", "go", "java"]), ("repo_access", ["github", "gitlab", "none"]), ("hosting", ["aws", "azure", "vps", "client"])]),
        ("Mobile apps", [("platform", ["ios", "android", "both"]), ("framework", ["native", "flutter", "react_native"]), ("store_release", ["yes", "no"])]),
        ("Design", [("format", ["figma", "sketch", "psd", "ai"]), ("pages", ["1", "5", "10", "20"]), ("brandbook", ["yes", "no"])]),
        ("Databases", [("engine", ["postgresql", "mysql", "mongodb", "mssql"]), ("size_gb", ["1", "10", "100", "1000"]), ("migration", ["yes", "no"])]),
        ("DevOps", [("cloud", ["aws", "azure", "gcp", "onprem"]), ("orchestrator", ["kubernetes", "nomad", "compose"]), ("iac", ["terraform", "ansible", "none"])]),
        ("Data science", [("task", ["forecast", "classification", "nlp", "cv"]), ("dataset_gb", ["1", "10", "100"]), ("gpu_needed", ["yes", "no"])]),
        ("Copywriting", [("language", ["uk", "en", "pl", "de"]), ("words", ["500", "1000", "5000"]), ("seo", ["yes", "no"])]),
        ("Translation", [("from", ["uk", "en", "pl", "de"]), ("to", ["uk", "en", "pl", "de"]), ("certified", ["yes", "no"])]),
        ("Marketing", [("channel", ["ads", "seo", "email", "social"]), ("monthly_budget", ["500", "2000", "10000"]), ("region", ["ua", "eu", "us"])]),
        ("Video", [("length_min", ["1", "5", "30"]), ("style", ["motion", "interview", "screen"]), ("subtitles", ["yes", "no"])]),
        ("QA", [("kind", ["manual", "automation", "load"]), ("tool", ["playwright", "selenium", "k6"]), ("devices", ["1", "5", "20"])]),
        ("Consulting", [("domain", ["fintech", "health", "retail"]), ("hours", ["5", "20", "100"]), ("nda", ["yes", "no"])]),
    ];

    public int Projects { get; }
    public int Clients { get; }
    public int Freelancers { get; }
    public int SkillCount => 200;
    public int CategoryCount => CategoryDefs.Length;
    public int UserCount => Clients + Freelancers;
    public string[] Vocabulary { get; }
    public (string Key, string Value)[] AllAttributes { get; } =
        CategoryDefs.SelectMany(c => c.Attrs.SelectMany(a => a.Values.Select(v => (a.Key, v)))).ToArray();

    public DataGenerator(int projects)
    {
        Projects = projects;
        Clients = Math.Max(50, projects / 20);
        Freelancers = Math.Max(100, projects / 10);
        var rnd = new Random(1);
        string[] syllables = ["ka", "lo", "mi", "ter", "sta", "dex", "ron", "vi", "pol", "nar", "bel", "gur", "zen", "fi", "tor", "qua"];
        Vocabulary = Enumerable.Range(0, 3000)
            .Select(_ => string.Concat(Enumerable.Range(0, rnd.Next(2, 5)).Select(_ => syllables[rnd.Next(syllables.Length)])))
            .Distinct().ToArray();
    }

    // Степеневий розподіл: малі індекси вибираються значно частіше за великі.
    public static int Skewed(Random r, int n, double power) => Math.Min(n - 1, (int)(n * Math.Pow(r.NextDouble(), power)));

    string Text(Random r, int min, int max) =>
        string.Join(' ', Enumerable.Range(0, r.Next(min, max)).Select(_ => Vocabulary[Skewed(r, Vocabulary.Length, 3)]));

    public static string CategoryName(int id) => CategoryDefs[id - 1].Name;
    public static string SkillName(int id) => $"skill-{id:000}";
    public long FreelancerId(int index) => Clients + index + 1;
    public string UserName(long id) => id <= Clients ? $"Client {id}" : $"Freelancer {id}";
    public string? Company(long id) => id % 3 == 0 ? null : $"Company {id}";
    public string FreelancerTitle(long id) => $"Specialist {id % 40}";
    public decimal HourlyRate(long id) => 10 + id % 90;

    public ProjectCard NewProject(long id)
    {
        var r = new Random(unchecked((int)(id * 2654435761L)));
        var created = Start.AddMinutes(id);
        var categoryId = r.Next(CategoryDefs.Length) + 1;
        long clientId = Skewed(r, Clients, 1.5) + 1;
        var skillIds = new SortedSet<int>();
        var skillTotal = r.Next(1, 6);
        while (skillIds.Count < skillTotal) skillIds.Add(Skewed(r, SkillCount, 2) + 1);
        var roll = r.Next(100);
        return new ProjectCard
        {
            Id = id,
            Title = Text(r, 3, 7),
            Description = Text(r, 40, 120),
            Budget = Math.Round((decimal)Math.Exp(r.NextDouble() * 5.5 + 5), 2),
            Deadline = created.Date.AddDays(r.Next(30, 180)),
            Status = roll < 5 ? "draft" : roll < 35 ? "open" : roll < 55 ? "in_progress" : roll < 95 ? "completed" : "cancelled",
            CreatedAt = created,
            UpdatedAt = created,
            Category = new Ref { Id = categoryId, Name = CategoryName(categoryId) },
            Client = new ClientRef { Id = clientId, Name = UserName(clientId), Company = Company(clientId) },
            Skills = skillIds.Select(s => new Ref { Id = s, Name = SkillName(s) }).ToList(),
            Attrs = CategoryDefs[categoryId - 1].Attrs
                .Select(a => new Attr { K = a.Key, V = a.Values[r.Next(a.Values.Length)] })
                .OrderBy(a => a.K, StringComparer.Ordinal).ToList(),
        };
    }

    public Proposal NewProposal(Random r, long proposalId, long freelancerId, decimal budget, DateTime at, string status) => new()
    {
        Id = proposalId,
        Freelancer = new FreelancerRef { Id = freelancerId, Name = UserName(freelancerId), Title = FreelancerTitle(freelancerId) },
        CoverLetter = Text(r, 15, 60),
        Amount = Math.Round(Math.Max(budget * (decimal)(0.7 + r.NextDouble() * 0.5), 10), 2),
        Days = r.Next(1, 90),
        Status = status,
        CreatedAt = at,
    };

    public IEnumerable<ProjectCard> AllProjects()
    {
        long proposalId = 0;
        for (long id = 1; id <= Projects; id++)
        {
            var card = NewProject(id);
            var r = new Random(unchecked((int)(id * 40503L)));
            // Експоненційний розподіл із середнім 5 і межею 50 (бізнес-правило платформи).
            var count = card.Status == "draft" ? 0 : Math.Min(MaxProposals, (int)(-Math.Log(1 - r.NextDouble()) * 5));
            var seen = new HashSet<long>();
            var winner = r.Next(Math.Max(count, 1));
            for (var i = 0; i < count; i++)
            {
                long freelancer;
                do freelancer = FreelancerId(Skewed(r, Freelancers, 1.5)); while (!seen.Add(freelancer));
                var status = card.Status switch
                {
                    "open" => r.Next(20) == 0 ? "withdrawn" : "pending",
                    "in_progress" or "completed" => i == winner ? "accepted" : "rejected",
                    _ => "rejected",
                };
                card.Proposals.Add(NewProposal(r, ++proposalId, freelancer, card.Budget, card.CreatedAt.AddMinutes(r.Next(1, 20000)), status));
            }
            yield return card;
        }
    }
}
