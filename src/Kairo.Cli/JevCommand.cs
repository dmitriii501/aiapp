using System.Diagnostics;
using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Secrets;
using Kairo.Host;
using Kairo.Providers.Jev;

namespace Kairo.Cli;

/// <summary><c>kairo jev bench</c>: how fast and how right Jev is on this machine, this network and this language.</summary>
internal static class JevCommand
{
    /// <summary>One screen-control step: a goal, the window, the clickable elements, and which element is right.</summary>
    private sealed record Case(string Goal, string Window, (string Role, string Name)[] Elements, int Expected);

    // The element ids (e1, e2…) are the same in both languages; only what the user would see differs.
    private static readonly Case[] English =
    [
        new("Save the document", "Untitled - Notepad", [("menu", "File"), ("menu", "Edit"), ("menu", "Format"), ("menu", "View"), ("menu", "Help")], 0),
        new("Enter the user's email address", "Sign in - Example", [("textbox", "Email"), ("textbox", "Password"), ("button", "Sign in"), ("link", "Forgot password?"), ("link", "Create account")], 0),
        new("Close the window and throw away the changes", "Notepad", [("button", "Save"), ("button", "Don't save"), ("button", "Cancel")], 1),
        new("Open the Downloads folder", "File Explorer", [("item", "Desktop"), ("item", "Documents"), ("item", "Downloads"), ("item", "Pictures"), ("item", "Music")], 2),
        new("Turn on dark mode", "Settings", [("item", "System"), ("item", "Personalization"), ("item", "Accessibility"), ("item", "Privacy & security")], 1),
        new("Calculate 7 times 8: press the first key", "Calculator", [("button", "7"), ("button", "8"), ("button", "multiply"), ("button", "equals"), ("button", "clear")], 0),
    ];

    private static readonly Case[] Russian =
    [
        new("Сохранить документ", "Безымянный — Блокнот", [("меню", "Файл"), ("меню", "Правка"), ("меню", "Формат"), ("меню", "Вид"), ("меню", "Справка")], 0),
        new("Ввести адрес электронной почты пользователя", "Вход — Пример", [("поле ввода", "Электронная почта"), ("поле ввода", "Пароль"), ("кнопка", "Войти"), ("ссылка", "Забыли пароль?"), ("ссылка", "Создать учётную запись")], 0),
        new("Закрыть окно и отказаться от изменений", "Блокнот", [("кнопка", "Сохранить"), ("кнопка", "Не сохранять"), ("кнопка", "Отмена")], 1),
        new("Открыть папку «Загрузки»", "Проводник", [("элемент", "Рабочий стол"), ("элемент", "Документы"), ("элемент", "Загрузки"), ("элемент", "Изображения"), ("элемент", "Музыка")], 2),
        new("Включить тёмную тему", "Параметры", [("элемент", "Система"), ("элемент", "Персонализация"), ("элемент", "Специальные возможности"), ("элемент", "Конфиденциальность и защита")], 1),
        new("Вычислить 7 умножить на 8: нажать первую клавишу", "Калькулятор", [("кнопка", "7"), ("кнопка", "8"), ("кнопка", "умножить"), ("кнопка", "равно"), ("кнопка", "очистить")], 0),
    ];

    public static async Task<int> RunAsync(CliOptions options, string userConfigDir, ISecretStore secrets)
    {
        if (options.Positional.FirstOrDefault() != "bench")
            return Fail("Usage: kairo jev bench [n]");

        KairoConfig config;
        try
        {
            config = KairoConfig.Load(userConfigDir, userConfigDir, includeProject: false);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }

        if (ProviderFactory.DecisionModelOf(config, secrets) is not JevDecisionModel jev)
            return Fail("No Jev key. Enter it in Settings (\"Ключ Jev\") or set TYPESAFE_API_KEY.");

        var rounds = options.Positional.Count > 1 && int.TryParse(options.Positional[1], out var n) ? Math.Clamp(n, 1, 200) : 12;
        ConsoleUi.Write(ConsoleColor.Magenta, "Kairo · Jev bench");
        ConsoleUi.Write(ConsoleColor.DarkGray, $"  model {config.DecisionModel ?? JevDecisionModel.DefaultModel} · deadline {config.DecisionDeadlineMs ?? 3000} ms · {rounds} rounds\n\n");

        try
        {
            var clock = Stopwatch.StartNew();
            await jev.WarmUpAsync();
            ConsoleUi.Write(ConsoleColor.Gray, $"  connection warm-up         {clock.ElapsedMilliseconds,5} ms\n");

            clock.Restart();
            var first = await jev.DecideAsync(Request(English[0]), default);
            ConsoleUi.Write(ConsoleColor.Gray, $"  first decision             {clock.ElapsedMilliseconds,5} ms  (model {first.Model})\n");

            // One decision at a time, as a live loop does: warm connection, one step after another.
            var warm = new List<double>();
            var (right, total, confidence) = (new int[2], new int[2], new double[2]);
            for (var round = 0; round < rounds; round++)
            {
                for (var language = 0; language < 2; language++)
                {
                    var cases = language == 0 ? English : Russian;
                    var c = cases[round % cases.Length];
                    clock.Restart();
                    var result = await jev.DecideAsync(Request(c), default);
                    warm.Add(clock.Elapsed.TotalMilliseconds);
                    var answer = result.Choice("next");
                    total[language]++;
                    confidence[language] += answer.Confidence;
                    if (answer.Choice == $"e{c.Expected + 1}")
                        right[language]++;
                }
            }
            warm.Sort();
            ConsoleUi.Write(ConsoleColor.Gray, $"  sequential ({warm.Count} decisions)  p50 {At(warm, 0.5):0} ms · p95 {At(warm, 0.95):0} ms · max {warm[^1]:0} ms\n");

            // Several windows at once (a swarm of agents, or a step that asks more than one thing in separate requests).
            clock.Restart();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => jev.DecideAsync(Request(English[i % English.Length]), default)));
            ConsoleUi.Write(ConsoleColor.Gray, $"  8 in parallel              {clock.ElapsedMilliseconds,5} ms in total\n\n");

            foreach (var (name, i) in new[] { ("English UI", 0), ("Russian UI", 1) })
                ConsoleUi.Write(right[i] == total[i] ? ConsoleColor.Green : ConsoleColor.Yellow,
                    $"  {name}: {right[i]}/{total[i]} right, mean confidence {confidence[i] / total[i]:0.00}\n");
            Console.WriteLine();
            return 0;
        }
        catch (DecisionException ex)
        {
            return Fail($"{ex.Kind}: {ex.Message}");
        }
    }

    private static DecisionRequest Request(Case c) => new(
        DecisionState.Json(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["goal"] = c.Goal, ["window"] = c.Window }, BenchJson.Default.DictionaryStringString)),
        new Dictionary<string, DecisionQuestion>
        {
            ["next"] = new ChoiceQuestion(
                "Which element should be used next to get closer to the goal?",
                c.Elements.Select((e, i) => new ChoiceOption($"e{i + 1}", $"{e.Role}: {e.Name}")).ToList()),
        });

    private static double At(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];

    private static int Fail(string message)
    {
        ConsoleUi.Write(ConsoleColor.Red, message + "\n");
        return 1;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class BenchJson : System.Text.Json.Serialization.JsonSerializerContext;
