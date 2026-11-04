using Microsoft.Data.Sqlite;

namespace Ekko.Food;

/// <param name="CaloriesEach">For one of it. Required with <paramref name="Save"/>; without it the food must be on the list.</param>
/// <param name="Save">Put the food on the list with <paramref name="CaloriesEach"/>: only after the user confirmed that number.</param>
public sealed record LogItem(string Food, double Quantity = 1, double? CaloriesEach = null, bool Save = false);

public sealed record LogRequest(IReadOnlyList<LogItem?>? Items);

public sealed record FoodRequest(string? Food, double? CaloriesEach);

/// <param name="Status">ok | found | unknown | empty | error.</param>
/// <param name="Message">A sentence ready to read out.</param>
/// <param name="Unknown">With "unknown": the foods not on the list, exactly as they were sent.</param>
/// <param name="Name">With "found": the food as the list names it, and its calories for one.</param>
/// <param name="Total">With a day, a log or an undo: that day's calories as a number, for screens.</param>
/// <param name="Foods">With usual foods: the list, most eaten first.</param>
public sealed record Answer(string Status, string Message, IReadOnlyList<string>? Unknown = null, string? Name = null, double? CaloriesEach = null,
    double? Total = null, IReadOnlyList<UsualFood>? Foods = null);

public sealed record UsualFood(string Name, double CaloriesEach);

/// <summary>
/// The food list and the diary, in one SQLite file. A food is saved once, with its calories for one of it. An entry keeps
/// its own calories, so changing a food later never rewrites what was already eaten. Calls run one at a time.
/// </summary>
public sealed class FoodDiary
{
    private const int ListedFoods = 8;

    private readonly string _connection;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private sealed record Food(string Name, double Calories);

    private sealed record Entry(string Name, double Quantity, double Calories);

    public FoodDiary(string path, TimeProvider clock)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // No pooling: closing a connection mid-log then really closes it, and SQLite rolls the half-done log back.
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _clock = clock;
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS foods (key TEXT PRIMARY KEY, name TEXT NOT NULL, calories REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS entries (id INTEGER PRIMARY KEY, log INTEGER NOT NULL, day TEXT NOT NULL, at TEXT NOT NULL,
                name TEXT NOT NULL, quantity REAL NOT NULL, calories REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_day ON entries (day);
            """);
    }

    /// <summary>All or nothing: if any food isn't on the list (and isn't being saved), nothing is logged and the answer names it.</summary>
    public Answer Log(IReadOnlyList<LogItem> items)
    {
        lock (_gate)
        {
            using var db = Open();
            var known = items.Select(i => Find(db, FoodWords.Key(i.Food))).ToList();
            var unknown = items.Where((item, n) => known[n] is null && !item.Save).Select(i => i.Food).ToList();
            if (unknown.Count > 0)
                return new Answer("unknown", $"{FoodWords.Join(unknown)} {(unknown.Count == 1 ? "isn't" : "aren't")} on your food list yet.", unknown);

            Execute(db, "BEGIN");
            var log = (long)Scalar(db, "SELECT COALESCE(MAX(log), 0) + 1 FROM entries")!;
            var now = _clock.GetLocalNow();
            var day = Day(now);
            var logged = new List<Entry>();
            for (var n = 0; n < items.Count; n++)
            {
                var item = items[n];
                var name = known[n]?.Name ?? item.Food.Trim();
                if (item.Save)
                    Save(db, item.Food, name, item.CaloriesEach!.Value);
                // A number the user gave counts for this entry, whatever the list says.
                var entry = new Entry(name, item.Quantity, (item.CaloriesEach ?? known[n]!.Calories) * item.Quantity);
                Execute(db, "INSERT INTO entries (log, day, at, name, quantity, calories) VALUES ($log, $day, $at, $name, $quantity, $calories)",
                    ("$log", log), ("$day", day), ("$at", now.ToString("O")), ("$name", entry.Name), ("$quantity", entry.Quantity), ("$calories", entry.Calories));
                logged.Add(entry);
            }
            Execute(db, "COMMIT");
            var total = Total(db, day);
            return new Answer("ok", $"Logged {Said(logged)}: {FoodWords.Calories(logged.Sum(e => e.Calories))}. {FoodWords.Calories(total)} today.", Total: total);
        }
    }

    public Answer Day(int daysAgo)
    {
        lock (_gate)
        {
            using var db = Open();
            var when = daysAgo == 0 ? "today" : "yesterday";
            var entries = Entries(db, "day = $day", ("$day", Day(_clock.GetLocalNow().AddDays(-daysAgo))));
            if (entries.Count == 0)
                return new Answer("empty", $"Nothing logged {when}.", Total: 0);
            var listed = entries.Take(ListedFoods).Select(e => FoodWords.Amount(e.Name, e.Quantity)).ToList();
            if (entries.Count > ListedFoods)
                listed.Add($"{entries.Count - ListedFoods} more");
            var total = entries.Sum(e => e.Calories);
            return new Answer("ok", $"{FoodWords.Calories(total)} {when}: {FoodWords.Join(listed)}.", Total: total);
        }
    }

    /// <summary>Takes back the last log of today, every food in it: "undo that" means the message, not one item of it.</summary>
    public Answer Undo()
    {
        lock (_gate)
        {
            using var db = Open();
            var day = Day(_clock.GetLocalNow());
            if (Scalar(db, "SELECT MAX(log) FROM entries WHERE day = $day", ("$day", day)) is not long log)
                return new Answer("empty", "Nothing's logged today to undo.", Total: 0);
            var removed = Entries(db, "log = $log", ("$log", log));
            Execute(db, "DELETE FROM entries WHERE log = $log", ("$log", log));
            var total = Total(db, day);
            return new Answer("ok", $"Took off {Said(removed)}. {FoodWords.Calories(total)} today.", Total: total);
        }
    }

    /// <summary>
    /// The foods on the list eaten most often in the last <see cref="UsualDays"/> days, for one-tap logging (the desk's quick
    /// food log). Ties go to the one eaten most recently.
    /// </summary>
    public Answer Usual(int count)
    {
        lock (_gate)
        {
            using var db = Open();
            // Entries carry the list's name for a food (Log writes it), so the join finds the food's current calories.
            using var command = Command(db, """
                SELECT f.name, f.calories FROM entries e JOIN foods f ON f.name = e.name
                WHERE e.day >= $since GROUP BY f.key ORDER BY COUNT(*) DESC, MAX(e.id) DESC LIMIT $count
                """, ("$since", Day(_clock.GetLocalNow().AddDays(1 - UsualDays))), ("$count", count));
            using var reader = command.ExecuteReader();
            var foods = new List<UsualFood>();
            while (reader.Read())
                foods.Add(new UsualFood(reader.GetString(0), reader.GetDouble(1)));
            return foods.Count == 0
                ? new Answer("empty", $"Nothing logged in the last {UsualDays} days.", Foods: foods)
                : new Answer("ok", $"You usually have {FoodWords.Join(foods.Select(f => f.Name).ToList())}.", Foods: foods);
        }
    }

    public const int UsualDays = 30;

    /// <summary>A food on the list, so a change can be read back against the number it replaces.</summary>
    public Answer Find(string food)
    {
        lock (_gate)
        {
            using var db = Open();
            return Find(db, FoodWords.Key(food)) is { } found
                ? new Answer("found", $"{found.Name} is {FoodWords.Calories(found.Calories)} each.", Name: found.Name, CaloriesEach: found.Calories)
                : new Answer("unknown", $"{food.Trim()} isn't on your food list yet.", [food]);
        }
    }

    /// <summary>Saves a food's calories for one of it. What was already logged keeps its own number.</summary>
    public Answer SetCalories(string food, double each)
    {
        lock (_gate)
        {
            using var db = Open();
            var existing = Find(db, FoodWords.Key(food));
            var name = existing?.Name ?? food.Trim();
            Save(db, food, name, each);
            return new Answer("ok", existing is null
                ? $"Saved {name} at {FoodWords.Calories(each)} each."
                : $"{name} is now {FoodWords.Calories(each)} each.");
        }
    }

    private static string Said(List<Entry> entries) => FoodWords.Join(entries.Select(e => FoodWords.Amount(e.Name, e.Quantity)).ToList());

    private static double Total(SqliteConnection db, string day) =>
        Convert.ToDouble(Scalar(db, "SELECT COALESCE(SUM(calories), 0) FROM entries WHERE day = $day", ("$day", day)));

    // Days are the house's (the container's TZ), so a late snack counts for the day it was eaten in.
    private static string Day(DateTimeOffset local) => local.ToString("yyyy-MM-dd");

    private static Food? Find(SqliteConnection db, string key)
    {
        using var command = Command(db, "SELECT name, calories FROM foods WHERE key = $key", ("$key", key));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Food(reader.GetString(0), reader.GetDouble(1)) : null;
    }

    private static void Save(SqliteConnection db, string food, string name, double each) =>
        Execute(db, "INSERT INTO foods (key, name, calories) VALUES ($key, $name, $calories) ON CONFLICT (key) DO UPDATE SET calories = $calories",
            ("$key", FoodWords.Key(food)), ("$name", name), ("$calories", each));

    private static List<Entry> Entries(SqliteConnection db, string where, params (string, object)[] values)
    {
        using var command = Command(db, $"SELECT name, quantity, calories FROM entries WHERE {where} ORDER BY id", values);
        using var reader = command.ExecuteReader();
        var entries = new List<Entry>();
        while (reader.Read())
            entries.Add(new Entry(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
        return entries;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static object? Scalar(SqliteConnection db, string sql, params (string, object)[] values)
    {
        using var command = Command(db, sql, values);
        return command.ExecuteScalar() is var value and not DBNull ? value : null;
    }

    private static void Execute(SqliteConnection db, string sql, params (string, object)[] values)
    {
        using var command = Command(db, sql, values);
        command.ExecuteNonQuery();
    }

    // Always parameters: food names come from what someone said.
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value);
        return command;
    }
}
