using System.Globalization;
using System.Text;
using FreelanceLab;
using FreelanceLab.Data;
using FreelanceLab.Services;
using Npgsql;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

// Рядок підключення можна задати через змінну середовища FREELANCE_CONNECTION
var connectionString = Environment.GetEnvironmentVariable("FREELANCE_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=freelance_lab;Username=postgres;Password=postgres";
await using var dataSource = NpgsqlDataSource.Create(connectionString);

Console.WriteLine("Лабораторна робота №1. Інтернет-платформа для фрілансерів");
await using (var read = await UnitOfWork.BeginAsync(dataSource))
    Print(await read.Community.UsersAsync());
Console.WriteLine("1, 4 - замовники; 2, 3 - фрілансери; 5 - обидві ролі");
var actor = ReadId("ID користувача");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - Проєкти / пошук        2 - Створити проєкт        3 - Опублікувати проєкт");
    Console.WriteLine("4 - Подати заявку          5 - Заявки на проєкт       6 - Обрати виконавця (UoW)");
    Console.WriteLine("7 - Контракти та етапи     8 - Видалити / відновити проєкт");
    Console.WriteLine("9 - Портфоліо              10 - Виконання та оплата  11 - Повідомлення / відгуки");
    Console.WriteLine("12 - Змінити користувача   13 - Редагувати проєкт    14 - Відкликати заявку");
    Console.WriteLine("0 - Вихід");
    var option = Ask("Команда");
    if (option == "0" || option is null) break;

    try
    {
        if (option == "12")
        {
            actor = ReadId("ID користувача");
            continue;
        }
        if (option == "6")
        {
            var projectId = ReadId("ID проєкту");
            var proposalId = ReadId("ID заявки");
            var count = (int)ReadId("Кількість етапів");
            var milestones = new List<MilestoneInput>();
            for (var i = 0; i < count; i++)
                milestones.Add(new MilestoneInput(Required("Назва етапу"), ReadMoney("Сума, грн"), ReadDate("Строк (yyyy-MM-dd)")));
            var contractId = await new AwardService(dataSource).AwardAsync(actor, projectId, proposalId, milestones);
            Console.WriteLine($"Створено контракт {contractId}");
            continue;
        }

        await using var work = await UnitOfWork.BeginAsync(dataSource);
        switch (option)
        {
            case "1":
                var search = Ask("Пошук (Enter - усі)");
                Print(await work.Projects.ListAsync(string.IsNullOrEmpty(search) ? null : search));
                break;
            case "2":
                var newId = await work.Projects.CreateAsync(actor, ReadId("Категорія (1 - веб, 2 - дизайн, 3 - БД)"),
                    Required("Назва"), Required("Опис"), ReadMoney("Бюджет, грн"), ReadDate("Строк (yyyy-MM-dd)"));
                Console.WriteLine($"Створено проєкт {newId}");
                break;
            case "3":
                await work.Projects.SetStatusAsync(actor, ReadId("ID проєкту"), "open");
                break;
            case "4":
                var proposal = await work.Proposals.CreateAsync(actor, ReadId("ID проєкту"), Required("Супровідний лист"),
                    ReadMoney("Вартість, грн"), (int)ReadId("Кількість днів"));
                Console.WriteLine($"Створено заявку {proposal}");
                break;
            case "5":
                Print(await work.Proposals.ListAsync(ReadId("ID проєкту")));
                break;
            case "7":
                Print(await work.Contracts.ListAsync());
                Print(await work.Milestones.ListAsync(ReadId("ID контракту")));
                break;
            case "8":
                var action = Required("delete / restore");
                var id = ReadId("ID проєкту");
                if (action == "delete") await work.Projects.SoftDeleteAsync(actor, id);
                else if (action == "restore") await work.Projects.RestoreAsync(actor, id);
                else Console.WriteLine("Невідома дія");
                break;
            case "9":
                Print(await work.Portfolio.ListAsync());
                var portfolioAction = Required("list / create / delete / restore");
                if (portfolioAction == "create")
                    Console.WriteLine($"Створено роботу {await work.Portfolio.CreateAsync(actor, Required("Назва"), Required("Опис"), Ask("URL"))}");
                else if (portfolioAction == "delete") await work.Portfolio.SoftDeleteAsync(actor, ReadId("ID роботи"));
                else if (portfolioAction == "restore") await work.Portfolio.RestoreAsync(actor, ReadId("ID роботи"));
                break;
            case "10":
                var operation = Required("submit / approve / pay / complete / submissions");
                if (operation == "submit")
                    Console.WriteLine($"Результат {await work.Milestones.SubmitAsync(actor, ReadId("ID етапу"), Required("Опис результату"), Ask("URL"))}");
                else if (operation == "approve") await work.Milestones.ApproveAsync(actor, ReadId("ID етапу"));
                else if (operation == "pay")
                    Console.WriteLine($"Оплата {await work.Milestones.PayAsync(actor, ReadId("ID етапу"), ReadMoney("Сума, грн"), Required("Номер платежу"))}");
                else if (operation == "complete") await work.Contracts.CompleteAsync(actor, ReadId("ID контракту"));
                else if (operation == "submissions") Print(await work.Community.SubmissionsAsync(ReadId("ID етапу")));
                break;
            case "11":
                var communityAction = Required("messages / send / reviews / review");
                if (communityAction == "messages") Print(await work.Community.MessagesAsync(ReadId("ID контракту")));
                else if (communityAction == "send")
                    await work.Community.SendAsync(actor, ReadId("ID контракту"), ReadId("ID отримувача"), Required("Текст"));
                else if (communityAction == "reviews") Print(await work.Community.ReviewsAsync());
                else if (communityAction == "review")
                    await work.Community.ReviewAsync(actor, ReadId("ID контракту"), (short)ReadId("Оцінка 1-5"), Required("Коментар"));
                break;
            case "13":
                await work.Projects.UpdateAsync(actor, ReadId("ID проєкту"), Required("Назва"), Required("Опис"),
                    ReadMoney("Бюджет, грн"), ReadDate("Строк (yyyy-MM-dd)"));
                break;
            case "14":
                await work.Proposals.WithdrawAsync(actor, ReadId("ID заявки"));
                break;
            default:
                Console.WriteLine("Невідома команда");
                break;
        }
        await work.CommitAsync();
    }
    catch (PostgresException ex)
    {
        Console.WriteLine($"Помилка БД: {ex.MessageText}");
    }
    catch (FormatException)
    {
        Console.WriteLine("Неправильний формат введення");
    }
}

static string? Ask(string prompt)
{
    Console.Write($"{prompt}: ");
    return Console.ReadLine()?.Trim();
}

static string Required(string prompt)
{
    var text = Ask(prompt);
    if (string.IsNullOrEmpty(text)) throw new FormatException();
    return text;
}

static long ReadId(string prompt) => long.Parse(Required(prompt));

static decimal ReadMoney(string prompt) => decimal.Parse(Required(prompt).Replace(',', '.'), CultureInfo.InvariantCulture);

static DateOnly ReadDate(string prompt) => DateOnly.ParseExact(Required(prompt), "yyyy-MM-dd", CultureInfo.InvariantCulture);

// Виводить список записів у вигляді таблиці (колонки - властивості record)
static void Print<T>(List<T> rows)
{
    if (rows.Count == 0)
    {
        Console.WriteLine("Записів немає");
        return;
    }
    var props = typeof(T).GetProperties();
    var cells = rows.Select(r => props.Select(p => Format(p.GetValue(r))).ToArray()).ToList();
    var widths = props.Select((p, i) => Math.Max(p.Name.Length, cells.Max(c => c[i].Length))).ToArray();

    Console.WriteLine(string.Join(" | ", props.Select((p, i) => p.Name.PadRight(widths[i]))));
    Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
    foreach (var row in cells)
        Console.WriteLine(string.Join(" | ", row.Select((c, i) => c.PadRight(widths[i]))));
}

static string Format(object? value) => value switch
{
    null => "",
    DateTime d => d.ToString("dd.MM.yyyy HH:mm"),
    DateOnly d => d.ToString("dd.MM.yyyy"),
    decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
    _ => value.ToString() ?? ""
};
