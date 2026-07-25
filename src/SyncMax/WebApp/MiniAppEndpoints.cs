using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SyncMax.Services;

namespace SyncMax.WebApp;

/// <summary>
/// HTTP-контур мини-приложения: <c>/api/miniapp/*</c>. Эндпоинты тонкие — разбирают запрос,
/// зовут <see cref="MiniAppService"/> и раскладывают результат по кодам ответа. Проверка
/// подлинности общая для всей группы: у каждого запроса должен быть заголовок
/// <c>Authorization: TmaAuth {tg|max} {initData}</c>, см. <see cref="MiniAppAuth"/>.
/// </summary>
public static class MiniAppEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/miniapp");

        api.MapGet("/me", async (HttpContext ctx, MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            return Results.Ok(await service.GetProfileAsync(caller, ct));
        });

        api.MapPost("/link-code", async (HttpContext ctx, MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            var code = await service.RefreshLinkCodeAsync(caller, ct);
            return code is null
                ? Results.BadRequest(new ErrorResponse("Аккаунты уже связаны."))
                : Results.Ok(new { linkCode = code });
        });

        api.MapPost("/unlink", async (HttpContext ctx, MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            await service.UnlinkAccountsAsync(caller, ct);
            return Results.NoContent();
        });

        api.MapGet("/chat-links", async (HttpContext ctx, MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            return Results.Ok(await service.ListChatLinksAsync(caller, ct));
        });

        api.MapPatch("/chat-links/{id:long}", async (
            HttpContext ctx, long id, UpdateChatLinkRequest request,
            MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            try
            {
                var updated = await service.UpdateChatLinkAsync(caller, id, request, ct);
                return updated is null ? NotFound() : Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        api.MapDelete("/chat-links/{id:long}", async (
            HttpContext ctx, long id, MiniAppAuth auth, MiniAppService service, CancellationToken ct) =>
        {
            if (auth.Authenticate(ctx.Request) is not { } caller)
                return Unauthorized();

            return await service.DeleteChatLinkAsync(caller, id, ct)
                ? Results.NoContent()
                : NotFound();
        });
    }

    private static IResult Unauthorized() =>
        Results.Json(new ErrorResponse("Не удалось подтвердить данные запуска приложения."),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Чужая связка и несуществующая связка отвечают одинаково: иначе по разнице между
    /// 403 и 404 можно было бы перебором выяснить, какие id вообще существуют.
    /// </summary>
    private static IResult NotFound() =>
        Results.Json(new ErrorResponse("Связка не найдена."), statusCode: StatusCodes.Status404NotFound);
}
