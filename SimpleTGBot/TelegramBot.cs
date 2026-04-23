using System.Reflection.Metadata.Ecma335;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleTGBot;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class TelegramBot
{
    // Токен TG-бота. Можно получить у @BotFather
    private const string BotToken = "токен";

    private readonly Dictionary<string, CountryInfo> _countries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CountryInfo> _countryList = new();
    private readonly Dictionary<long, QuizState> _quizStates = new();
    private readonly Dictionary<long, int> _scores = new();
    private readonly Dictionary<long, string> _pendingFavorite = new();
    private readonly Dictionary<long, string> _lastCountry = new();
    private string _dataPath = string.Empty;
    private string _logPath = string.Empty;

    /// <summary>
    /// Инициализирует и обеспечивает работу бота до нажатия клавиши Esc
    /// </summary>
    public async Task Run()
    {
        // Если вам нужно хранить какие-то данные во время работы бота (массив информации, логи бота,
        // историю сообщений для каждого пользователя), то это всё надо инициализировать в этом методе.
        _dataPath = Path.Combine(AppContext.BaseDirectory, "data.txt");
        _logPath = Path.Combine(AppContext.BaseDirectory, "logs.txt");
        LoadData();

        // Инициализируем наш клиент, передавая ему токен.
        var botClient = new TelegramBotClient(BotToken);

        // Служебные вещи для организации правильной работы с потоками
        using CancellationTokenSource cts = new CancellationTokenSource();

        // Разрешённые события, которые будет получать и обрабатывать наш бот.
        // Будем получать только сообщения. При желании можно поработать с другими событиями.
        ReceiverOptions receiverOptions = new ReceiverOptions()
        {
            AllowedUpdates = new [] { UpdateType.Message }
        };

        // Привязываем все обработчики и начинаем принимать сообщения для бота
        botClient.StartReceiving(
            updateHandler: OnMessageReceived,
            pollingErrorHandler: OnErrorOccured,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        // Проверяем что токен верный и получаем информацию о боте
        var me = await botClient.GetMeAsync(cancellationToken: cts.Token);
        Console.WriteLine($"Бот @{me.Username} запущен.\nДля остановки нажмите клавишу Esc...");

        // Ждём, пока будет нажата клавиша Esc, тогда завершаем работу бота
        while (Console.ReadKey().Key != ConsoleKey.Escape){}

        // Отправляем запрос для остановки работы клиента.
        cts.Cancel();
    }

    /// <summary>
    /// Обработчик события получения сообщения.
    /// </summary>
    /// <param name="botClient">Клиент, который получил сообщение</param>
    /// <param name="update">Событие, произошедшее в чате. Новое сообщение, голос в опросе, исключение из чата и т. д.</param>
    /// <param name="cancellationToken">Служебный токен для работы с многопоточностью</param>
    async Task OnMessageReceived(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Работаем только с сообщениями. Остальные события игнорируем
        var message = update.Message;
        if (message is null)
        {
            return;
        }
        // Будем обрабатывать только текстовые сообщения.
        // При желании можно обрабатывать стикеры, фото, голосовые и т. д.
        //
        // Обратите внимание на использованную конструкцию. Она эквивалентна проверке на null, приведённой выше.
        // Подробнее об этом синтаксисе: https://medium.com/@mattkenefick/snippets-in-c-more-ways-to-check-for-null-4eb735594c09
        if (message.Text is not { } messageText)
        {
            return;
        }

        // Получаем ID чата, в которое пришло сообщение. Полезно, чтобы отличать пользователей друг от друга.
        var chatId = message.Chat.Id;

        // Печатаем на консоль факт получения сообщения
        Console.WriteLine($"Получено сообщение в чате {chatId}: '{messageText}'");

        var text = messageText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (_quizStates.TryGetValue(chatId, out var quizState))
        {
            var answer = text;
            var matched = quizState.Options.FirstOrDefault(o => string.Equals(o, answer, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(matched))
            {
                if (string.Equals(matched, quizState.CorrectName, StringComparison.OrdinalIgnoreCase))
                {
                    _scores[chatId] = _scores.TryGetValue(chatId, out var s) ? s + 1 : 1;
                    _quizStates.Remove(chatId);
                    await botClient.SendTextMessageAsync(chatId, $"Верно! Твой счет: {_scores[chatId]}", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
                    return;
                }

                _quizStates.Remove(chatId);
                await botClient.SendTextMessageAsync(chatId, $"Неверно. Правильный ответ: {quizState.CorrectName}", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
                return;
            }
            await botClient.SendTextMessageAsync(chatId, "Ответ не из вариантов. Выбери кнопку.", cancellationToken: cancellationToken);
            return;
        }

        if (_pendingFavorite.TryGetValue(chatId, out var pendingCountry))
        {
            if (IsYes(text))
            {
                AddFavorite(chatId, pendingCountry);
                _pendingFavorite.Remove(chatId);
                await botClient.SendTextMessageAsync(chatId, $"Добавил в избранное: {pendingCountry}", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
                return;
            }
            if (IsNo(text))
            {
                _pendingFavorite.Remove(chatId);
                await botClient.SendTextMessageAsync(chatId, "Ок, не добавляю.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
                return;
            }
        }

        if (IsStart(text))
        {
            await botClient.SendTextMessageAsync(chatId, "Привет! Я путеводитель по странам.\nКоманды: /list, /country <название>, /quiz, /favorites, /top.\nМожно просто написать название страны.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            return;
        }

        if (IsList(text))
        {
            await botClient.SendTextMessageAsync(chatId, GetCountryList(), replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            return;
        }

        var countryMatch = Regex.Match(text, "^(?:/country|страна)\\s+(.+)$", RegexOptions.IgnoreCase);
        if (countryMatch.Success)
        {
            var name = countryMatch.Groups[1].Value.Trim();
            await SendCountry(botClient, chatId, name, cancellationToken);
            return;
        }

        if (_countries.ContainsKey(text))
        {
            await SendCountry(botClient, chatId, text, cancellationToken);
            return;
        }

        if (IsCountryButton(text))
        {
            if (_lastCountry.TryGetValue(chatId, out var last))
            {
                AddFavorite(chatId, last);
                await botClient.SendTextMessageAsync(chatId, $"Добавил в избранное: {last}", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Сначала выбери страну через /country.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            }
            return;
        }

        if (IsFavorites(text))
        {
            await botClient.SendTextMessageAsync(chatId, GetFavorites(chatId), replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            return;
        }

        if (IsTop(text))
        {
            await botClient.SendTextMessageAsync(chatId, GetTop(), replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            return;
        }

        if (IsQuiz(text))
        {
            await StartQuiz(botClient, chatId, cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(chatId, "Не понял сообщение. Используй /start или /list.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Обработчик исключений, возникших при работе бота
    /// </summary>
    /// <param name="botClient">Клиент, для которого возникло исключение</param>
    /// <param name="exception">Возникшее исключение</param>
    /// <param name="cancellationToken">Служебный токен для работы с многопоточностью</param>
    /// <returns></returns>
    Task OnErrorOccured(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        // В зависимости от типа исключения печатаем различные сообщения об ошибке
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",

            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);

        // Завершаем работу
        return Task.CompletedTask;
    }

    private void LoadData()
    {
        if (!System.IO.File.Exists(_dataPath))
        {
            return;
        }

        foreach (var line in System.IO.File.ReadAllLines(_dataPath, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split('|');
            if (parts.Length < 7)
            {
                continue;
            }

            var info = new CountryInfo
            {
                Name = parts[0].Trim(),
                Capital = parts[1].Trim(),
                Population = parts[2].Trim(),
                Gdp = parts[3].Trim(),
                Continent = parts[4].Trim(),
                Flag = parts[5].Trim(),
                Facts = parts[6].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };

            if (info.Name.Length == 0)
            {
                continue;
            }

            _countries[info.Name] = info;
            _countryList.Add(info);
        }
    }

    private async Task SendCountry(ITelegramBotClient botClient, long chatId, string name, CancellationToken cancellationToken)
    {
        if (_countries.TryGetValue(name, out var info))
        {
            _lastCountry[chatId] = info.Name;
            LogRequest(chatId, info.Name);
            var text = FormatCountry(info);
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Добавить в избранное", "Викторина" },
                new KeyboardButton[] { "Список", "Топ" }
            }) { ResizeKeyboard = true };
            await botClient.SendTextMessageAsync(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
            _pendingFavorite[chatId] = info.Name;
            await botClient.SendTextMessageAsync(chatId, "Добавить в избранное?", replyMarkup: new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Да", "Нет" }
            }) { ResizeKeyboard = true }, cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(chatId, "Страну не нашел. Используй /list.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
    }

    private string FormatCountry(CountryInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{info.Flag} {info.Name}");
        sb.AppendLine($"Столица: {info.Capital}");
        sb.AppendLine($"Население: {info.Population}");
        sb.AppendLine($"ВВП: {info.Gdp}");
        sb.AppendLine($"Континент: {info.Continent}");
        if (info.Facts.Length > 0)
        {
            sb.AppendLine($"Факт: {info.Facts[0]}");
        }
        if (info.Facts.Length > 1)
        {
            sb.AppendLine($"Факт: {info.Facts[1]}");
        }
        return sb.ToString().TrimEnd();
    }

    private string GetCountryList()
    {
        if (_countryList.Count == 0)
        {
            return "Список пуст.";
        }

        var names = _countryList.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return "Доступные страны:\n" + string.Join(", ", names);
    }

    private void LogRequest(long chatId, string country)
    {
        var line = $"{DateTime.UtcNow:O}|{chatId}|{country}";
        System.IO.File.AppendAllLines(_logPath, new[] { line }, Encoding.UTF8);
    }

    private void AddFavorite(long chatId, string country)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"favorites_{chatId}.txt");
        var existing = System.IO.File.Exists(path) ? new HashSet<string>(System.IO.File.ReadAllLines(path, Encoding.UTF8), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing.Add(country))
        {
            System.IO.File.AppendAllLines(path, new[] { country }, Encoding.UTF8);
        }
    }

    private string GetFavorites(long chatId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"favorites_{chatId}.txt");
        if (!System.IO.File.Exists(path))
        {
            return "Избранное пусто.";
        }

        var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return "Избранное пусто.";
        }

        return "Избранное:\n" + string.Join("\n", lines);
    }

    private string GetTop()
    {
        if (!System.IO.File.Exists(_logPath))
        {
            return "Пока нет запросов.";
        }

        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in System.IO.File.ReadAllLines(_logPath, Encoding.UTF8))
        {
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }
            var country = parts[2].Trim();
            if (country.Length == 0)
            {
                continue;
            }
            dict[country] = dict.TryGetValue(country, out var v) ? v + 1 : 1;
        }

        if (dict.Count == 0)
        {
            return "Пока нет запросов.";
        }

        var top = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(5);
        var sb = new StringBuilder("Топ запросов:\n");
        foreach (var kv in top)
        {
            sb.AppendLine($"{kv.Key} — {kv.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task StartQuiz(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        if (_countryList.Count < 4)
        {
            await botClient.SendTextMessageAsync(chatId, "Недостаточно данных для викторины.", replyMarkup: MainMenu(), cancellationToken: cancellationToken);
            return;
        }

        var options = _countryList.OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
        var correct = options[Random.Shared.Next(options.Count)];
        _quizStates[chatId] = new QuizState
        {
            CorrectName = correct.Name,
            Options = options.Select(o => o.Name).ToArray()
        };

        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { options[0].Name, options[1].Name },
            new KeyboardButton[] { options[2].Name, options[3].Name }
        }) { ResizeKeyboard = true, OneTimeKeyboard = true };

        await botClient.SendTextMessageAsync(chatId, $"Угадай страну по флагу: {correct.Flag}", replyMarkup: keyboard, cancellationToken: cancellationToken);
    }

    private IReplyMarkup MainMenu()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "Список", "Викторина" },
            new KeyboardButton[] { "Избранное", "Топ" }
        }) { ResizeKeyboard = true };
    }

    private bool IsYes(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "да" or "ага" or "ок" or "оке" or "окей" or "угу" or "давай";
    }

    private bool IsNo(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "нет" or "не" or "неа" or "не хочу" or "потом";
    }

    private bool IsStart(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "/start" or "старт" or "начать" or "привет";
    }

    private bool IsList(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "/list" or "список";
    }

    private bool IsQuiz(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "/quiz" or "викторина" or "игра" or "давай викторину";
    }

    private bool IsFavorites(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "/favorites" or "избранное" or "фавориты";
    }

    private bool IsTop(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "/top" or "топ";
    }

    private bool IsCountryButton(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t is "добавить в избранное";
    }

    private class CountryInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Capital { get; init; } = string.Empty;
        public string Population { get; init; } = string.Empty;
        public string Gdp { get; init; } = string.Empty;
        public string Continent { get; init; } = string.Empty;
        public string Flag { get; init; } = string.Empty;
        public string[] Facts { get; init; } = Array.Empty<string>();
    }

    private class QuizState
    {
        public string CorrectName { get; init; } = string.Empty;
        public string[] Options { get; init; } = Array.Empty<string>();
    }
}
