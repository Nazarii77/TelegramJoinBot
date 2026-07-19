using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
     
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var botToken = configuration["BOT_TOKEN"]
    ?? configuration["BotSettings:Token"]
    ?? configuration["BotSettings__Token"];


var adminChatIds = new List<long>();
var adminConfig = configuration["ADMIN_CHAT_ID"]
    ?? configuration["BotSettings:AdminChatId"]
    ?? configuration["BotSettings__AdminChatId"]
    ?? string.Empty;

var groupId = -1003774116486;

var users = new Dictionary<long, UserState>();
foreach (var part in adminConfig.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
{
    var trimmed = part.Trim();
    if (long.TryParse(trimmed, out var id))
    {
        adminChatIds.Add(id);
    }
}

if (string.IsNullOrWhiteSpace(botToken) || adminChatIds.Count == 0)
{
    Console.WriteLine("Please set BOT_TOKEN/ADMIN_CHAT_ID or BotSettings:Token/BotSettings:AdminChatId.");
    return;
}

var botClient = new TelegramBotClient(botToken);
var me = await botClient.GetMe();
Console.WriteLine($"Bot started: {me.Username}");

using var cts = new CancellationTokenSource();

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: new ReceiverOptions { AllowedUpdates = [] },
    cancellationToken: cts.Token);

Console.WriteLine("Bot is running. Press Enter to stop.");
Console.ReadLine();
cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{

    // 🔹 CALLBACK КНОПКИ
    if (update.CallbackQuery is { } callback)
    {
        var data = callback.Data!;

        if (data == "restart")
        {
            var requester = callback.From!.Id;
            users[requester] = new UserState();
            await botClient.AnswerCallbackQuery(callback.Id);
            await botClient.SendMessage(requester, "Розпочинаємо заново. Як вас звати?");
            return;
        }

        if (data.StartsWith("submit_"))
        {
            var partsS = data.Split('_');
            var userIdBtn = long.Parse(partsS[partsS.Length - 1]);

            if (!users.ContainsKey(userIdBtn))
            {
                await botClient.AnswerCallbackQuery(callback.Id, "Помилка: сесія не знайдена.");
                return;
            }

            var stateToSend = users[userIdBtn];
            var profileUrl = $"tg://user?id={userIdBtn}";
            if (!string.IsNullOrWhiteSpace(stateToSend.Username))
            {
                profileUrl = $"https://t.me/{stateToSend.Username}";
            }

            var adminKeyboard = new InlineKeyboardMarkup(
                new[]
                {
                    new []
                    {
                        InlineKeyboardButton.WithCallbackData(
                            "✅ Схвалити",
                            $"form_accept_{userIdBtn}"
                        ),
                        InlineKeyboardButton.WithCallbackData(
                            "❌ Відхилити",
                            $"form_reject_{userIdBtn}"
                        )
                    },
                    new []
                    {
                        InlineKeyboardButton.WithUrl(
                            "👤 Відкрити профіль",
                            profileUrl
                        )
                    }
                }
            );

            foreach (var adminId in adminChatIds)
            {
                await botClient.SendPhoto(
                    adminId,
                    stateToSend.PhotoId,
                    caption:
                    $"📩 Нова заявка:\n\n" +
                    $"👤 Ім'я: {stateToSend.Name}\n" +
                    $"🏠 Квартира: {stateToSend.Flat}\n" +
                    $"🚗 Паркомісце: {stateToSend.Parking}\n" +
                    $"📱 Телефон: {stateToSend.Phone}\n" +
                    $"🆔 ID: {userIdBtn}",
                    parseMode: ParseMode.Html,
                    replyMarkup: adminKeyboard
                );
            }

            await botClient.AnswerCallbackQuery(callback.Id, "Заявка відправлена адміністрації");
            var userConfirmKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔁 Restart", "restart") }
            });
            await botClient.SendMessage(userIdBtn, "Ваша заявка відправлена адміністрації. Очікуйте на відповідь.", replyMarkup: userConfirmKeyboard);
            
            return;
        }

        var parts = data.Split('_');
        var targetId = long.Parse(parts[parts.Length - 1]);

        if (data.StartsWith("form_accept"))
        {
            await botClient.ApproveChatJoinRequest(
                new ChatId(groupId),
                targetId
            );
            await botClient.SendMessage(targetId, "✅ Ваша заявка схвалена адміністрацією!");
            await botClient.AnswerCallbackQuery(callback.Id, "Заявка схвалена");
            // Remove user's session only after admin action
            if (users.ContainsKey(targetId)) users.Remove(targetId);
        }
        else if (data.StartsWith("form_reject"))
        {
            await botClient.SendMessage(targetId, "❌ На жаль, ваша заявка відхилена адміністрацією.");
            await botClient.AnswerCallbackQuery(callback.Id, "Заявка відхилена");
            // Remove user's session only after admin action
            if (users.ContainsKey(targetId)) users.Remove(targetId);
        }

        return;
    }
        // 🔹 MESSAGE
    if (update.Message is not { } msg)
        return;
        
    if (msg.Chat.Type != ChatType.Private)
        return;

    var userId = msg.From!.Id;
    var text = msg.Text ?? "";

    if (text.StartsWith("/start"))
    {
        users[userId] = new UserState();

        await botClient.SendMessage(userId,
            "Привіт 👋\n\nЯ бот для подачі заявки в групу.\n\nЩоб продовжити, я поставлю 3 простих питання 🙂");

        await botClient.SendMessage(userId, "Як вас звати?");
        return;
    }

    if (!users.ContainsKey(userId))
        return;

    var state = users[userId];

    if (string.IsNullOrEmpty(state.Name))
    {
        state.Name = text;

        await botClient.SendMessage(userId, "Вкажіть, будь ласка, номер квартири? (кілька - через кому, якщо немає - 0)");
        return;
    }
    else if (string.IsNullOrEmpty(state.Flat))
    {
        state.Flat = text;
        await botClient.SendMessage(userId, "Вкажіть, будь ласка, номер паркомісця? (кілька - через кому, якщо немає - 0)");
        return;
    }
    else if (string.IsNullOrEmpty(state.Parking))
    {  
        Console.WriteLine("parking RECEIVED");
        state.Parking = text;
        await botClient.SendMessage(
            userId,
            "Вкажіть номер телефону на випадок надзвичайних ситуацій:"
        );
        return;
    }     
    else if (string.IsNullOrEmpty(state.Phone))
    {  
        Console.WriteLine("phone RECEIVED");
        state.Phone = text;
        await botClient.SendMessage(
            userId,
            "Додайте фото документа власності або скріншот оплати комуналки для підтвердження:"
        );
        return;
    } 

    // Фото
    if (msg.Photo != null)
    {  
        Console.WriteLine("PHOTO RECEIVED");
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "📩 Подати заявку",
                    $"submit_{userId}"
                )
            }
        });

        var photo = msg.Photo.Last();
        state.PhotoId = photo.FileId;
        // store username for later admin notification
        state.Username = msg.From?.Username ?? string.Empty;

        // Do not notify admins here — wait for the user to press Submit.
        await botClient.SendMessage(userId, "Фото отримано ✅");
        await botClient.SendMessage(
            userId,
            "Дані заповнено ✅\nНатисніть кнопку, щоб подати заявку у групу:",
            replyMarkup: keyboard
        );

        return;
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    Console.WriteLine($"Polling error: {exception.Message}");
    return Task.CompletedTask;
}
class UserState
{
    public string Name { get; set; } = "";
    public string Flat { get; set; } = "";
    public string Parking { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PhotoId { get; set; } = "";
    public string Username { get; set; } = "";
}