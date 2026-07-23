namespace SyncMax.Services;

/// <summary>
/// Простейшая локализация интерфейса. Язык хранится в профиле пользователя.
/// Плейсхолдеры {0}, {1} подставляются через string.Format.
/// </summary>
public static class Localization
{
    public const string Fallback = "ru";

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["ru"] = new()
        {
            ["welcome"] =
                "👋 Привет! Я связываю аккаунты MAX и Telegram.\n\n" +
                "Чтобы связать этот аккаунт со вторым мессенджером, откройте второй бот " +
                "и отправьте ему код:\n\n" +
                "🔑 {0}\n\n" +
                "Неважно, в каком мессенджере вводить код — связать можно любой с любым.",
            ["already_linked"] =
                "✅ Этот аккаунт уже связан со вторым мессенджером.",
            ["link_success"] =
                "🎉 Готово! Аккаунты MAX и Telegram успешно связаны.",
            ["link_invalid"] =
                "❌ Код не найден. Проверьте правильность и попробуйте снова, " +
                "либо запросите новый код командой /start во втором мессенджере.",
            ["rate_limited"] =
                "⏳ Слишком часто. Подождите пару секунд и попробуйте снова.",
            ["help"] =
                "Отправьте /start, чтобы получить код связки, " +
                "или введите 6-значный код из второго мессенджера.",
            ["chat_link_need_account_link"] =
                "❌ Сначала свяжите аккаунты между собой: отправьте /start и введите " +
                "полученный код во втором мессенджере.",
            ["chat_link_await_second_side"] =
                "📌 Чат «{0}» принят. Теперь сделайте то же самое во втором мессенджере — " +
                "перешлите сообщение из чата или канала, который нужно связать.",
            ["chat_link_source_unknown"] =
                "❌ Не удалось определить исходный чат этого сообщения. Перешлите сообщение " +
                "из группового чата или канала (не личное сообщение и не ответ).",
            ["chat_link_already_exists"] =
                "ℹ️ Такая связка чатов уже существует.",
            ["chat_link_created"] =
                "🎉 Связка чатов «{0}» создана и активна.",
            ["remember_admin"] =
                "🎉 Вы уже сделали бота участником группы, не забудьте сделать его администратором, иначе он не сможет писать",
            ["admin_congratulation"] =
                "🎉 Отлично! Вы сделали бота администратором группы.",
            ["all_links_deleted"] =
                "🗑 Все связки чатов удалены.",
            ["settings_reset"] =
                "♻️ Все настройки сброшены: связь между аккаунтами и связки чатов удалены.",
        },
        ["en"] = new()
        {
            ["welcome"] =
                "👋 Hi! I link MAX and Telegram accounts.\n\n" +
                "To link this account with the other messenger, open the second bot " +
                "and send it this code:\n\n" +
                "🔑 {0}\n\n" +
                "It doesn't matter where you enter it — any messenger can be linked to any other.",
            ["already_linked"] =
                "✅ This account is already linked with the other messenger.",
            ["link_success"] =
                "🎉 Done! Your MAX and Telegram accounts are now linked.",
            ["link_invalid"] =
                "❌ Code not found. Check it and try again, " +
                "or request a new one with /start in the other messenger.",
            ["rate_limited"] =
                "⏳ Too fast. Please wait a couple of seconds and try again.",
            ["help"] =
                "Send /start to get a linking code, " +
                "or enter the 6-digit code from the other messenger.",
            ["chat_link_need_account_link"] =
                "❌ Link your accounts first: send /start and enter the code you get " +
                "in the other messenger.",
            ["chat_link_await_second_side"] =
                "📌 Chat \"{0}\" accepted. Now do the same in the other messenger — " +
                "forward a message from the chat or channel you want to link.",
            ["chat_link_source_unknown"] =
                "❌ Couldn't determine the source chat of this message. Forward a message " +
                "from a group chat or channel (not a DM, not a reply).",
            ["chat_link_already_exists"] =
                "ℹ️ This chat link already exists.",
            ["chat_link_created"] =
                "🎉 Chat link \"{0}\" created and active."
            ,
            ["remember_admin"] =
                "🎉 You've already added the bot to the group. Don't forget to make it an administrator, otherwise it won't be able to send messages.",
            ["admin_congratulation"] =
                "🎉 Great! You made the bot a group administrator.",
            ["all_links_deleted"] =
                "🗑 All chat links have been deleted.",
            ["settings_reset"] =
                "♻️ All settings have been reset: the account link and all chat links were removed.",
        }
    };

    public static string Get(string? language, string key)
    {
        var lang = language is not null && Strings.ContainsKey(language) ? language : Fallback;
        if (Strings[lang].TryGetValue(key, out var value))
            return value;
        return Strings[Fallback].TryGetValue(key, out var fb) ? fb : key;
    }

    public static string Format(string? language, string key, params object[] args)
        => string.Format(Get(language, key), args);
}
