using Dapper;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

/// <summary>
/// Карта соответствия «сообщение-оригинал ↔ пересланная копия» (см. миграцию M004).
/// Одна запись на каждую пересылку, заполнены обе стороны (MAX и Telegram). Поскольку
/// пересылка двусторонняя, запрос по колонкам исходной платформы всегда находит id копии
/// в целевой — то есть карта работает в обе стороны одной таблицей.
/// </summary>
public sealed class MessageLinkRepository
{
    private readonly SqliteConnectionFactory _factory;

    public MessageLinkRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Сохраняет связку id оригинала и пересланной копии.</summary>
    public async Task AddAsync(string maxChatId, string maxMsgId, string tgChatId, string tgMsgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            INSERT INTO message_links (max_chat_id, max_msg_id, tg_chat_id, tg_msg_id, created_at)
            VALUES (@maxChatId, @maxMsgId, @tgChatId, @tgMsgId, @createdAt);
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            maxChatId,
            maxMsgId,
            tgChatId,
            tgMsgId,
            createdAt = DateTimeOffset.UtcNow.ToString("o")
        }, cancellationToken: ct));
    }

    /// <summary>
    /// По сообщению <paramref name="sourceMsgId"/> в чате <paramref name="sourceChatId"/>
    /// мессенджера <paramref name="sourceMessenger"/> возвращает соответствующую копию
    /// (chatId, msgId) в другом мессенджере, либо null, если связки нет.
    /// </summary>
    public async Task<(string ChatId, string MsgId)?> FindCounterpartAsync(
        MessengerType sourceMessenger, string sourceChatId, string sourceMsgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        var sql = sourceMessenger == MessengerType.Max
            ? "SELECT tg_chat_id AS ChatId, tg_msg_id AS MsgId FROM message_links WHERE max_chat_id = @chatId AND max_msg_id = @msgId ORDER BY id DESC LIMIT 1;"
            : "SELECT max_chat_id AS ChatId, max_msg_id AS MsgId FROM message_links WHERE tg_chat_id = @chatId AND tg_msg_id = @msgId ORDER BY id DESC LIMIT 1;";

        var row = await conn.QuerySingleOrDefaultAsync<CounterpartRow>(new CommandDefinition(sql,
            new { chatId = sourceChatId, msgId = sourceMsgId }, cancellationToken: ct));

        return row is null ? null : (row.ChatId, row.MsgId);
    }

    /// <summary>Удаляет из карты записи для указанного сообщения (после его удаления) — уборка.</summary>
    public async Task RemoveAsync(MessengerType messenger, string chatId, string msgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        var sql = messenger == MessengerType.Max
            ? "DELETE FROM message_links WHERE max_chat_id = @chatId AND max_msg_id = @msgId;"
            : "DELETE FROM message_links WHERE tg_chat_id = @chatId AND tg_msg_id = @msgId;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chatId, msgId }, cancellationToken: ct));
    }

    private sealed class CounterpartRow
    {
        public string ChatId { get; set; } = string.Empty;
        public string MsgId { get; set; } = string.Empty;
    }
}
