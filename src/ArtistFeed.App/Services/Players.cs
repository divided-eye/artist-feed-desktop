using System.Text.RegularExpressions;

namespace ArtistFeed.App.Services;

/// <summary>What to play: each site's own embeddable player, as in the web app.</summary>
public sealed record PlayItem(string Key, long PostId, string TrackKey, string Source, string Url, string? Embed,
                              string? TrackExtId, string Title, bool NoStream = false);

public sealed record PlayerPage(string Src, int Height, string Hint);

public static class Players
{
    public static PlayerPage? For(PlayItem it)
    {
        switch (it.Source)
        {
            case "bandcamp":
                if (!Regex.IsMatch(it.Embed ?? "", @"^(album|track)=\d+$")) return null;
                var track = it.Embed!.StartsWith("album=") && Regex.IsMatch(it.TrackExtId ?? "", @"^\d+$") ? $"track={it.TrackExtId}/" : "";
                return new PlayerPage($"https://bandcamp.com/EmbeddedPlayer/{it.Embed}/size=large/bgcol=17191d/linkcol=d9f25c/tracklist=false/artwork=small/{track}transparent=true/",
                    120, "Press play in the player to start");
            case "soundcloud":
                if (!(it.Url.StartsWith("https://") || it.Url.StartsWith("http://"))) return null;
                return new PlayerPage("https://w.soundcloud.com/player/?url=" + Uri.EscapeDataString(it.Url) +
                                      "&auto_play=true&color=%23ff5500&hide_related=true&show_comments=false&show_teaser=false&visual=false",
                    166, "");
            case "youtube":
                if (!Regex.IsMatch(it.Embed ?? "", @"^[\w-]{6,20}$")) return null;
                return new PlayerPage($"https://www.youtube-nocookie.com/embed/{it.Embed}?autoplay=1&rel=0", 240, "");
        }
        return null;
    }

    /// <summary>A small page holding the player, so the site sees an ordinary embed.</summary>
    public static string Html(PlayerPage p) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin">
        <style>html,body{margin:0;background:#17191d;overflow:hidden}iframe{border:0;width:100%;height:{{p.Height}}px;display:block}</style>
        </head><body><iframe src="{{System.Net.WebUtility.HtmlEncode(p.Src)}}" allow="autoplay; encrypted-media" allowfullscreen></iframe></body></html>
        """;
}
