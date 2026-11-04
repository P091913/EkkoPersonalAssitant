namespace Ekko.Food.Tests;

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public class FoodWordsTests
{
    [Theory]
    [InlineData("eggs", "egg")]
    [InlineData("Cookies", "cookie")]
    [InlineData("pies", "pie")]
    [InlineData("2 slices of pizza", "slice of pizza 2")]
    [InlineData("cup of rice", "rice cup")]
    [InlineData("Reese's cups", "reeses cup")]
    [InlineData("grams of chicken", "g chicken")]
    [InlineData("sandwiches", "sandwich")]
    [InlineData("tomatoes", "tomato")]
    [InlineData("toast with butter", "butter toast")]
    public void The_same_food_said_two_ways_has_one_key(string said, string other) =>
        Assert.Equal(FoodWords.Key(other), FoodWords.Key(said));

    [Theory]
    [InlineData("2% milk", "milk")]
    [InlineData("hummus", "humm")]
    [InlineData("big mac", "mac")]
    [InlineData("chocolate milk", "milk")]
    public void Different_foods_keep_different_keys(string a, string b) => Assert.NotEqual(FoodWords.Key(a), FoodWords.Key(b));

    [Theory]
    [InlineData("egg", 2, "2 eggs")]
    [InlineData("slice of pizza", 2, "2 slices of pizza")]
    [InlineData("gram of chicken", 200, "200 grams of chicken")]
    [InlineData("sandwich", 0.5, "half a sandwich")]
    [InlineData("big mac", 1, "big mac")]
    [InlineData("berry", 3, "3 berries")]
    [InlineData("fries", 2, "2 fries")]
    public void Amounts_are_said_the_way_people_say_them(string name, double quantity, string said) =>
        Assert.Equal(said, FoodWords.Amount(name, quantity));

    [Fact]
    public void Calories_keep_decimals_only_when_small() =>
        Assert.Equal(["1.65 calories", "1,450 calories", "1 calorie"], [FoodWords.Calories(1.65), FoodWords.Calories(1450.4), FoodWords.Calories(1)]);
}

public class FoodDiaryTests
{
    private readonly Clock _clock = new();
    private readonly FoodDiary _diary;

    public FoodDiaryTests() => _diary = new FoodDiary(Path.Combine(Path.GetTempPath(), $"ekko-food-{Guid.NewGuid():N}.db"), _clock);

    private Answer Log(params LogItem[] items) => _diary.Log(items);

    [Fact]
    public void A_food_not_on_the_list_logs_nothing_until_it_is_saved()
    {
        var answer = Log(new LogItem("egg", 2), new LogItem("big mac"));
        Assert.Equal("unknown", answer.Status);
        Assert.Equal(["egg", "big mac"], answer.Unknown);
        Assert.Equal("empty", _diary.Day(0).Status); // all or nothing: the eggs weren't logged either

        answer = Log(new LogItem("egg", 2, CaloriesEach: 70, Save: true), new LogItem("big mac", CaloriesEach: 590, Save: true));
        Assert.Equal("Logged 2 eggs and big mac: 730 calories. 730 calories today.", answer.Message);

        // Known now, said differently: matched in code, with the saved numbers.
        Assert.Equal("Logged 3 eggs: 210 calories. 940 calories today.", Log(new LogItem("Eggs", 3)).Message);
    }

    [Fact]
    public void A_number_the_user_gave_counts_for_that_entry_and_leaves_the_list_alone()
    {
        Log(new LogItem("protein bar", CaloriesEach: 200, Save: true));
        Assert.Equal("Logged protein bar: 300 calories. 500 calories today.", Log(new LogItem("protein bar", CaloriesEach: 300)).Message);
        Assert.Equal("Logged protein bar: 200 calories. 700 calories today.", Log(new LogItem("protein bar")).Message);
    }

    [Fact]
    public void Days_follow_the_clock_and_undo_takes_back_the_whole_last_log()
    {
        Log(new LogItem("toast", CaloriesEach: 80, Save: true));
        _clock.Now = _clock.Now.AddDays(1);
        Log(new LogItem("egg", 2, CaloriesEach: 70, Save: true), new LogItem("toast"));

        Assert.Equal("220 calories today: 2 eggs and toast.", _diary.Day(0).Message);
        Assert.Equal("80 calories yesterday: toast.", _diary.Day(1).Message);

        Assert.Equal("Took off 2 eggs and toast. 0 calories today.", _diary.Undo().Message);
        Assert.Equal("empty", _diary.Undo().Status); // yesterday's toast is never undone from today
        Assert.Equal("ok", _diary.Day(1).Status);
    }

    [Fact]
    public void Changing_a_food_keeps_what_was_already_eaten()
    {
        Log(new LogItem("big mac", CaloriesEach: 590, Save: true));
        Assert.Equal("big mac is now 560 calories each.", _diary.SetCalories("Big Macs", 560).Message);
        Assert.Equal("Saved shake at 250 calories each.", _diary.SetCalories("shake", 250).Message);

        Log(new LogItem("big mac"));
        Assert.Equal("1,150 calories today: big mac and big mac.", _diary.Day(0).Message);

        var found = _diary.Find("Big Macs");
        Assert.Equal(("found", "big mac", 560d), (found.Status, found.Name, found.CaloriesEach));
        Assert.Equal(["fries"], _diary.Find("fries").Unknown);
    }

    [Fact]
    public void Usual_foods_are_the_most_eaten_lately_with_their_current_calories()
    {
        Assert.Equal("empty", _diary.Usual(6).Status);
        Log(new LogItem("cake", CaloriesEach: 400, Save: true)); // eaten 3 times, but too long ago
        Log(new LogItem("cake"));
        Log(new LogItem("cake"));
        _clock.Now = _clock.Now.AddDays(UsualWindow);
        Log(new LogItem("toast", CaloriesEach: 80, Save: true));
        Log(new LogItem("egg", 2, CaloriesEach: 70, Save: true), new LogItem("coffee", CaloriesEach: 5, Save: true));
        Log(new LogItem("egg"));
        _diary.SetCalories("eggs", 75);

        var usual = _diary.Usual(6);
        // Eggs twice; toast and coffee once each, coffee more recently. Quantity doesn't count, only how often.
        Assert.Equal([new UsualFood("egg", 75), new UsualFood("coffee", 5), new UsualFood("toast", 80)], usual.Foods);
        Assert.Equal([new UsualFood("egg", 75)], _diary.Usual(1).Foods);

        Assert.Equal(295, _diary.Day(0).Total); // 80 + 2 x 70 + 5 + 70: logged eggs keep the old 70
        Assert.Equal(370, Log(new LogItem("egg")).Total);
        Assert.Equal(295, _diary.Undo().Total);
    }

    private const int UsualWindow = FoodDiary.UsualDays;

    [Fact]
    public void A_long_day_lists_the_first_foods_and_counts_the_rest()
    {
        for (var i = 0; i < 10; i++)
            Log(new LogItem($"food {(char)('a' + i)}", CaloriesEach: 10, Save: true));
        Assert.EndsWith("food g, food h and 2 more.", _diary.Day(0).Message);
    }
}

public class RequestTests
{
    [Theory]
    [InlineData(null, "items is required.")]
    [InlineData("", "food is required.")]
    [InlineData("of the", "food has no words to match.")]
    [InlineData("egg\n", "food has control characters.")]
    public void Foods_are_checked_at_the_edge(string? food, string problem) =>
        Assert.Equal(problem, Requests.Problem(new LogRequest(food is null ? null : [new LogItem(food)])));

    [Fact]
    public void Numbers_are_checked_at_the_edge()
    {
        Assert.Equal("quantity must be more than 0 and at most 2000.", Requests.Problem(new LogRequest([new LogItem("egg", 0)])));
        Assert.Equal("caloriesEach is required.", Requests.Problem(new LogRequest([new LogItem("egg", Save: true)])));
        Assert.Equal("caloriesEach must be from 0 to 3000.", Requests.Problem(new LogRequest([new LogItem("egg", CaloriesEach: 5000)])));
        Assert.Equal("items can have at most 10 foods.", Requests.Problem(new LogRequest(Enumerable.Repeat<LogItem?>(new LogItem("egg"), 11).ToList())));
        Assert.Null(Requests.Problem(new LogRequest([new LogItem("gram of chicken", 200)])));
        Assert.Equal("caloriesEach is required.", Requests.Problem(new FoodRequest("shake", null)));
    }
}
