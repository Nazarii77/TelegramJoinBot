using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
var groupChat = await botClient.GetChat(new ChatId(groupId));
var groupPublicLink = !string.IsNullOrWhiteSpace(groupChat.Username)
    ? $"https://t.me/{groupChat.Username}"
    : null;
Console.WriteLine($"Bot started: {me.Username}");
Console.WriteLine($"Group: {groupChat.Title} ({groupChat.Id}) Username={groupChat.Username ?? "<none>"}");

using var cts = new CancellationTokenSource();

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: new ReceiverOptions
    {
        AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.ChatJoinRequest }
    },
    cancellationToken: cts.Token);

Console.WriteLine("Bot is running.");

var shutdownTcs = new TaskCompletionSource();
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    shutdownTcs.TrySetResult();
});
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    ctx.Cancel = true;
    shutdownTcs.TrySetResult();
});

await shutdownTcs.Task;
cts.Cancel();
Console.WriteLine("Stopping...");

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
    if (update.ChatJoinRequest is { } joinRequest)
    {
        if (joinRequest.Chat.Id != groupId)
            return;

        var requesterId = joinRequest.From.Id;
        var joinState = users.ContainsKey(requesterId) ? users[requesterId] : null;
        var requestTime = DateTime.UtcNow.AddHours(3);

        var adminButtons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "✅ Схвалити",
                    $"form_accept_{requesterId}"
                ),
                InlineKeyboardButton.WithCallbackData(
                    "❌ Відхилити",
                    $"form_reject_{requesterId}"
                )
            }
        };

        var profileUrl = !string.IsNullOrWhiteSpace(joinState?.Username)
            ? $"https://t.me/{joinState.Username}"
            : $"tg://user?id={requesterId}";

        adminButtons.Add(new[]
        {
            InlineKeyboardButton.WithUrl(
                "👤 Відкрити профіль",
                profileUrl
            )
        });

        var caption = joinState is not null
            ? $"📩 Нова заявка на приєднання:\n\n" +
              $"👤 Ім'я: {joinState.Name}\n" +
              $"🏠 Квартира: {joinState.Flat}\n" +
              $"🚗 Паркомісце: {joinState.Parking}\n" +
              $"📱 Телефон: {joinState.Phone}\n" +
              $"🆔 ID: {requesterId}\n" +
              $"Час подачі: {requestTime:dd.MM.yyyy HH:mm:ss}"
            : $"📩 Нова заявка на приєднання від користувача {requesterId}. Дані анкети відсутні.\n";

        foreach (var adminId in adminChatIds)
        {
            if (joinState is not null && !string.IsNullOrWhiteSpace(joinState.AttachmentFileId))
            {
                if (joinState.AttachmentType == "document")
                {
                    await botClient.SendDocument(
                        adminId,
                        joinState.AttachmentFileId,
                        caption: caption,
                        parseMode: ParseMode.Html,
                        replyMarkup: new InlineKeyboardMarkup(adminButtons)
                    );
                }
                else
                {
                    await botClient.SendPhoto(
                        adminId,
                        joinState.AttachmentFileId,
                        caption: caption,
                        parseMode: ParseMode.Html,
                        replyMarkup: new InlineKeyboardMarkup(adminButtons)
                    );
                }
            }
            else
            {
                await botClient.SendMessage(
                    adminId,
                    caption,
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(adminButtons)
                );
            }
        }

        await botClient.SendMessage(requesterId, "Ваша заявка на приєднання отримана. Очікуйте рішення адміністрації.");
        return;
    }

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

        var parts = data.Split('_');
        if (!long.TryParse(parts[parts.Length - 1], out var targetId))
        {
            Console.WriteLine($"Invalid form callback target id: {data}");
            await botClient.AnswerCallbackQuery(callback.Id, "Помилка: невірний формат ідентифікатора.");
            return;
        }

        Console.WriteLine($"Form callback received: {data}, targetId={targetId}");

        if (data.StartsWith("form_accept"))
        {
            try
            {
                await botClient.ApproveChatJoinRequest(
                    new ChatId(groupId),
                    targetId
                );

                await botClient.SendMessage(targetId, "✅ Ваша заявка схвалена адміністрацією!");
                await botClient.AnswerCallbackQuery(callback.Id, "Заявка схвалена");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                Console.WriteLine($"Approval error for {targetId}: {ex.Message}");
                await LogBotAdminStatusAsync(botClient, groupId, cancellationToken);

                try
                {
                    var targetMember = await botClient.GetChatMember(new ChatId(groupId), targetId, cancellationToken);
                    Console.WriteLine($"Target user chat member status: {targetMember.Status}");
                    await botClient.AnswerCallbackQuery(callback.Id, $"Помилка при схваленні: {ex.Message}. Статус користувача: {targetMember.Status}.");
                }
                catch (Exception innerEx)
                {
                    Console.WriteLine($"Failed to get target chat member status: {innerEx.Message}");
                    await botClient.AnswerCallbackQuery(callback.Id, $"Помилка при схваленні: {ex.Message}.");
                }

                await botClient.SendMessage(targetId, "⚠️ Сталася помилка під час обробки заявки. Адміністратор отримав повідомлення.");
                return;
            }

            if (users.ContainsKey(targetId)) users.Remove(targetId);
        }
        else if (data.StartsWith("form_reject"))
        {
            try
            {
                await botClient.DeclineChatJoinRequest(new ChatId(groupId), targetId);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                Console.WriteLine($"Decline error for {targetId}: {ex.Message}");
            }

            await botClient.SendMessage(targetId, "❌ На жаль, ваша заявка відхилена адміністрацією.");
            await botClient.AnswerCallbackQuery(callback.Id, "Заявка відхилена");
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
        Console.WriteLine("PARKING RECEIVED");
        state.Parking = text;
        await botClient.SendMessage(
            userId,
            "Вкажіть номер телефону на випадок надзвичайних ситуацій:"
        );
        return;
    }     
    else if (string.IsNullOrEmpty(state.Phone))
    {  
        Console.WriteLine("PHONE RECEIVED");
        state.Phone = text;
        await botClient.SendMessage(
            userId,
            "Додайте фото документа власності або скріншот оплати комуналки для підтвердження (не pdf, не word, а фото):"
        );
        return;
    } 

    // Фото або зображення, надіслане як файл
    if (msg.Photo != null)
    {  
        Console.WriteLine("PHOTO RECEIVED");
        var photo = msg.Photo.Last();
        state.AttachmentFileId = photo.FileId;
        state.AttachmentType = "photo";
        state.Username = msg.From?.Username ?? string.Empty;

        string? joinUrl = groupPublicLink;
        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            try
            {
                var invite = await botClient.CreateChatInviteLink(
                    new ChatId(groupId),
                    name: "Заявка через бота",
                    expireDate: DateTime.UtcNow.AddHours(2),
                    createsJoinRequest: true,
                    cancellationToken: cancellationToken);
                joinUrl = invite.InviteLink;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                Console.WriteLine($"Failed to create join invite link: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(joinUrl))
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithUrl(
                        "📩 Подати заявку в групу",
                        joinUrl)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Почати заново",
                        "restart")
                }
            });

            await botClient.SendMessage(
                userId,
                "Натисніть кнопку нижче, щоб перейти у групу та подати запит на приєднання.",
                replyMarkup: keyboard
            );
        }
        else
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Почати заново",
                        "restart")
                }
            });

            await botClient.SendMessage(
                userId,
                "Дані збережено. Будь ласка, перейдіть у групу та подайте заявку на приєднання. Після цього адміністратор зможе її схвалити.",
                replyMarkup: keyboard
            );
        }

        return;
    }

    if (msg.Document != null && IsImageDocument(msg.Document))
    {
        Console.WriteLine("IMAGE DOCUMENT RECEIVED");
        state.AttachmentFileId = msg.Document.FileId;
        state.AttachmentType = "document";
        state.Username = msg.From?.Username ?? string.Empty;

        string? joinUrl = groupPublicLink;
        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            try
            {
                var invite = await botClient.CreateChatInviteLink(
                    new ChatId(groupId),
                    name: "Заявка через бота",
                    expireDate: DateTime.UtcNow.AddHours(2),
                    createsJoinRequest: true,
                    cancellationToken: cancellationToken);
                joinUrl = invite.InviteLink;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                Console.WriteLine($"Failed to create join invite link: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(joinUrl))
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithUrl(
                        "📩 Подати заявку в групу",
                        joinUrl)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Почати заново",
                        "restart")
                }
            });

            await botClient.SendMessage(
                userId,
                "Натисніть кнопку нижче, щоб перейти у групу та подати запит на приєднання.",
                replyMarkup: keyboard
            );
        }
        else
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Почати заново",
                        "restart")
                }
            });

            await botClient.SendMessage(
                userId,
                "Дані збережено. Будь ласка, перейдіть у групу та подайте заявку на приєднання. Після цього адміністратор зможе її схвалити.",
                replyMarkup: keyboard
            );
        }

        return;
    }
}

static bool IsImageDocument(Telegram.Bot.Types.Document? document)
{
    return document?.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
}

async Task LogBotAdminStatusAsync(ITelegramBotClient botClient, long groupId, CancellationToken cancellationToken)
{
    try
    {
        var me = await botClient.GetMe(cancellationToken);
        var member = await botClient.GetChatMember(new ChatId(groupId), me.Id, cancellationToken);
        Console.WriteLine($"Bot chat member status: {member.Status}");
        if (member.Status == Telegram.Bot.Types.Enums.ChatMemberStatus.Administrator)
        {
            Console.WriteLine("Bot is admin in the group. Ensure the bot has CanInviteUsers administrator right.");
        }
        else
        {
            Console.WriteLine($"Bot is not admin or does not have sufficient rights in group {groupId}.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to read bot admin status: {ex.Message}");
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
    public string AttachmentFileId { get; set; } = "";
    public string AttachmentType { get; set; } = "";
    public string Username { get; set; } = "";
}