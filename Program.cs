using Telegram.Bot;
using Telegram.Bot.Types;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Telegram.Bot.Types.ReplyMarkups;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional:true)
    .AddEnvironmentVariables()
    .Build();

var token = config["Telegram:BotToken"]!;
var adminId = long.Parse(config["Telegram:AdminId"]!);

var bot = new TelegramBotClient(token);

// зберігаємо стан користувачів (дуже простий варіант)
var users = new Dictionary<long, UserState>();

Console.WriteLine("Bot started...");

bot.StartReceiving(
    HandleUpdate,
    HandleError
);

await Task.Delay(-1);

async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    // 🔹 CALLBACK КНОПКИ
    if (update.CallbackQuery is { } callback)
    {
        var data = callback.Data!;
        var parts = data.Split('_');
        var userIdBtn = long.Parse(parts[parts.Length - 1]);

        if (data.StartsWith("form_accept"))
        {
            await botClient.ApproveChatJoinRequest(
                new ChatId(-1003774116486),
                userIdBtn
            );
            await botClient.SendMessage(userIdBtn, "✅ Ваша заявка схвалена адміністрацією!");
            await botClient.AnswerCallbackQuery(callback.Id, "Заявка схвалена");
        }
        else if (data.StartsWith("form_reject"))
        {
            await botClient.SendMessage(userIdBtn, "❌ На жаль, ваша заявка відхилена адміністрацією.");
            await botClient.AnswerCallbackQuery(callback.Id, "Заявка відхилена");
        }

        return;
    }
        // 🔹 MESSAGE
    if (update.Message is not { } msg) return;
    if (msg.Text is null) return;

    var userId = msg.From!.Id;
    var text = msg.Text;

    if (text.StartsWith("/start"))
    {
        users[userId] = new UserState();

        await botClient.SendMessage(userId,
            "Привіт 👋\n\nЯ бот для подачі заявки в групу.\n\nЩоб продовжити, я поставлю 3 простих питання 🙂");

        await botClient.SendMessage(userId, "Як тебе звати?");
        return;
    }

    if (!users.ContainsKey(userId))
        return;

    var state = users[userId];

    if (string.IsNullOrEmpty(state.Name))
    {
        state.Name = text;

        await botClient.SendMessage(userId, "Із якої ти квартири/комерції? (кілька - через кому, якщо немає - 0)");
        return;
    }
    else if (string.IsNullOrEmpty(state.Flat))
    {
        state.Flat = text;
        await botClient.SendMessage(userId, "Чи маєш паркомісце? (кілька - через кому, якщо немає - 0)");
        return;
    }
    else if (string.IsNullOrEmpty(state.Parking))
    {
        state.Parking = text;
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithUrl(
                "📩 Подати заявку",
                "https://t.me/+CpBA0utM54llMjg6"
            )
        );

        await botClient.SendMessage(
                userId,
                "Дані заповнено ✅\nНатисніть кнопку, щоб подати заявку у групу:",
                replyMarkup: keyboard
        );

        var adminKeyboard = new InlineKeyboardMarkup(
            new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData(
                        "✅ Схвалити",
                        $"form_accept_{userId}"
                    ),
                    InlineKeyboardButton.WithCallbackData(
                        "❌ Відхилити",
                        $"form_reject_{userId}"
                    )
                }
            }
        );

        await botClient.SendMessage(
            adminId,
            $"📩 Нова заявка:\n\n👤 Ім'я: {state.Name}\n📝 Квартира: {state.Flat}\nℹ️ Паркомісце: {state.Parking}\n🆔 ID: {userId}",
            replyMarkup: adminKeyboard
        );
        

        users.Remove(userId);
    }
}

Task HandleError(ITelegramBotClient botClient, Exception ex, CancellationToken ct)
{
    Console.WriteLine(ex.Message);
    return Task.CompletedTask;
}

class UserState
{
    public string Name { get; set; } = "";
    public string Flat { get; set; } = "";
    public string Parking { get; set; } = "";
}