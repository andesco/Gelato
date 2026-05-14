using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// When a Gelato shortcut item (IsShortcut=true / UseStrmDirectPlay) is streamed,
/// returns HTTP 302 to the CDN URL instead of letting Jellyfin proxy the bytes.
///
/// Gelato overrides sources[0].Id to the primary item's ID, so both the route
/// {itemId} and the ?mediaSourceId query param point to the primary Movie/Episode,
/// not the stream item.  FindShortcutForId() handles both cases: if the ID is the
/// stream item itself it returns it directly; if it's the primary item it queries
/// for the first matching shortcut stream.
/// </summary>
public sealed class StreamRedirectFilter : IAsyncActionFilter, IOrderedFilter
{
    public int Order { get; init; } = 0;

    private static readonly HashSet<string> _streamActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetVideoStream",
        "GetVideoStreamByContainer",
        "HeadVideoStream",
        "HeadVideoStreamByContainer",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamRedirectFilter> _log;

    public StreamRedirectFilter(ILibraryManager libraryManager, ILogger<StreamRedirectFilter> log)
    {
        _libraryManager = libraryManager;
        _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad
            || !_streamActions.Contains(cad.ActionName))
        {
            await next();
            return;
        }

        var video = ResolveShortcut(ctx);
        if (video is null || string.IsNullOrEmpty(video.ShortcutPath))
        {
            await next();
            return;
        }

        _log.LogInformation(
            "StreamRedirectFilter: 302 → CDN for item {ItemId}",
            video.Id
        );
        ctx.Result = new RedirectResult(video.ShortcutPath, permanent: false);
    }

    private Video? ResolveShortcut(ActionExecutingContext ctx)
    {
        var msId = ctx.HttpContext.Request.Query["mediaSourceId"].FirstOrDefault()
            ?? ctx.HttpContext.Request.Query["MediaSourceId"].FirstOrDefault();

        if (!string.IsNullOrEmpty(msId) && Guid.TryParse(msId, out var msGuid))
        {
            var hit = FindShortcutForId(msGuid);
            if (hit != null) return hit;
        }

        if (ctx.RouteData.Values.TryGetValue("itemId", out var idObj)
            && Guid.TryParse(idObj?.ToString(), out var itemId))
        {
            return FindShortcutForId(itemId);
        }

        return null;
    }

    private Video? FindShortcutForId(Guid id)
    {
        if (_libraryManager.GetItemById(id) is not Video v)
            return null;

        // Direct hit: the ID already points to a shortcut stream item.
        if (v.IsShortcut && !string.IsNullOrEmpty(v.ShortcutPath))
            return v;

        // The ID is the primary item — find its first shortcut stream.
        var stremioId = v.GetProviderId("Stremio");
        if (string.IsNullOrEmpty(stremioId))
            return null;

        return _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { { "Stremio", stremioId } },
                IncludeItemTypes = [v.GetBaseItemKind()],
                Tags = [GelatoManager.StreamTag],
                IsDeadPerson = true,
            })
            .OfType<Video>()
            .Where(s => s.IsShortcut && !string.IsNullOrEmpty(s.ShortcutPath))
            .OrderBy(s => s.GelatoData<int?>("index") ?? int.MaxValue)
            .FirstOrDefault();
    }
}
