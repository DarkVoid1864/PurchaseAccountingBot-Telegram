using System.Text;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net.Http;
// Бот в тг - @bopdv_bot

// Юнікод для консолі
Console.OutputEncoding = Encoding.UTF8;

string botToken = "7952828542:AAH6pO30aFf0h_SQP_yjdYuWBkZN3NTWCew";
string apiBaseUrl = "https://localhost:7255/api/items";

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
using HttpClient apiClient = new HttpClient(handler);

var botClient = new TelegramBotClient(botToken);
using var cts = new CancellationTokenSource();

Console.WriteLine("БОП from DV запущений! (Натисніть Enter для виходу)");

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync, // Виправлено
    receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
    cancellationToken: cts.Token
);

Console.ReadLine();
cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    if (update.Type == UpdateType.Message && update.Message is { Text: { } messageText })
    {
        long chatId = update.Message.Chat.Id;
        Console.WriteLine($"Отримано текст: '{messageText}' від {chatId}");

        if (messageText == "/start")
        {
            await bot.SendMessage(chatId,
                "Привіт! Я БОП from DV — твій бот для пошуку та обліку покупок.\n\n" +
                "Команди:\n" +
                "/find [назва] — знайти товар на Amazon\n" +
                "/list — показати мій список покупок\n" +
                "/delete [ID] — видалити покупку зі списку",
                cancellationToken: ct);
        }
        else if (messageText.StartsWith("/find"))
        {
            string query = messageText.Replace("/find", "").Trim();
            if (string.IsNullOrEmpty(query))
            {
                await bot.SendMessage(chatId, "Напиши, що шукати. Приклад: /find laptop", cancellationToken: ct);
            }
            else
            {
                await bot.SendMessage(chatId, "Шукаю на Amazon... ⏳", cancellationToken: ct);
                try
                {
                    // ВИПРАВЛЕНО: додаємо /search, щоб не було конфлікту з POST на головному маршруті
                    var response = await apiClient.GetStringAsync($"{apiBaseUrl}/search?q={query}");
                    var products = JsonConvert.DeserializeObject<List<BotAmazonProduct>>(response);

                    if (products == null || products.Count == 0)
                    {
                        await bot.SendMessage(chatId, "Нічого не знайдено 😕", cancellationToken: ct);
                    }
                    else
                    {
                        foreach (var p in products.Take(5))
                        {
                            // Обрізаємо назву для кнопки, щоб не перевищити ліміт 64 символи
                            // Беремо перші 25 символів, щоб точно влізло разом з ціною
                            string shortTitle = p.Title.Length > 25 ? p.Title.Substring(0, 25) : p.Title;
                            string callbackData = $"save|{shortTitle}|{p.Price}";

                            var keyboard = new InlineKeyboardMarkup(
                                InlineKeyboardButton.WithCallbackData("✅ Зберегти", callbackData)
                            );

                            // В самому повідомленні (тексті) назву залишаємо повною
                            await bot.SendMessage(chatId, $"📦 {p.Title}\n💰 Ціна: {p.Price}", replyMarkup: keyboard, cancellationToken: ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await bot.SendMessage(chatId, "Помилка зв'язку з API. Перевір порт!", cancellationToken: ct);
                    Console.WriteLine("API Error: " + ex.Message);
                }
            }
        }
        else if (messageText == "/list")
        {
            try
            {
                var response = await apiClient.GetStringAsync($"{apiBaseUrl}/saved");
                var savedItems = JsonConvert.DeserializeObject<List<BotMyItem>>(response);

                if (savedItems == null || savedItems.Count == 0)
                {
                    await bot.SendMessage(chatId, "Твій список порожній 🛒", cancellationToken: ct);
                }
                else
                {
                    var sb = new StringBuilder("📋 Твої покупки:\n\n");
                    foreach (var item in savedItems)
                    {
                        sb.AppendLine($"ID: {item.Id} | {item.Title} — {item.Price}");
                    }
                    await bot.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                }
            }
            catch
            {
                await bot.SendMessage(chatId, "Помилка завантаження списку.", cancellationToken: ct);
            }
        }
        else if (messageText.StartsWith("/delete"))
        {
            // Отримуємо ID після команди (наприклад: /delete 5)
            string idStr = messageText.Replace("/delete", "").Trim();

            if (int.TryParse(idStr, out int id))
            {
                try
                {
                    // Шлемо DELETE запит на твій API
                    var resp = await apiClient.DeleteAsync($"{apiBaseUrl}/{id}");

                    if (resp.IsSuccessStatusCode)
                    {
                        await bot.SendMessage(chatId, $"✅ Покупку з ID {id} успішно видалено зі списку!", cancellationToken: ct);
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await bot.SendMessage(chatId, $"❌ Помилка: Покупку з ID {id} не знайдено.", cancellationToken: ct);
                    }
                }
                catch (Exception ex)
                {
                    await bot.SendMessage(chatId, "❌ Помилка при видаленні. Перевір зв'язок з сервером.", cancellationToken: ct);
                    Console.WriteLine(ex.Message);
                }
            }
            else
            {
                await bot.SendMessage(chatId, "⚠️ Будь ласка, вкажи номер (ID). Приклад: /delete 1", cancellationToken: ct);
            }
        }
    }

    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { Data: { } data })
    {
        if (data.StartsWith("save|"))
        {
            var parts = data.Split('|');
            var newItem = new BotMyItem { Title = parts[1], Price = parts[2] };
            var content = new StringContent(JsonConvert.SerializeObject(newItem), Encoding.UTF8, "application/json");

            try
            {
                var resp = await apiClient.PostAsync(apiBaseUrl, content);
                if (resp.IsSuccessStatusCode)
                {
                    await bot.AnswerCallbackQuery(update.CallbackQuery.Id, "Збережено! ✅", cancellationToken: ct);
                    await bot.SendMessage(update.CallbackQuery.Message!.Chat.Id, $"Додано: {newItem.Title}", cancellationToken: ct);
                }
            }
            catch
            {
                await bot.AnswerCallbackQuery(update.CallbackQuery.Id, "Помилка збереження!", cancellationToken: ct);
            }
        }
    }
}

// ВИПРАВЛЕНО сигнатуру під версію 22.x (додано HandleErrorSource)
Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
{
    Console.WriteLine($"Помилка Telegram API ({source}): {ex.Message}");
    return Task.CompletedTask;
}

public class BotAmazonProduct 
{ 
    public string Title { get; set; } 
    public string Price { get; set; } 
}
public class BotMyItem 
{ 
    public int Id { get; set; } 
    public string Title { get; set; } 
    public string Price { get; set; } 
}









