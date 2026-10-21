using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ArtistFeed.App.Services;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

/// <summary>One cover in the grid.</summary>
public partial class PostCardViewModel : ObservableObject
{
    public FeedPost Post { get; }
    public string SourceLabel { get; }
    public IBrush BadgeBrush { get; }
    public PlayItem Item { get; }
    public bool CanPlay { get; }

    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPlayed;
    [ObservableProperty] private bool _isHidden;

    public PostCardViewModel(FeedPost p, ISource? source, ImageCache images, string? playingKey)
    {
        Post = p;
        SourceLabel = source?.Label ?? p.Source;
        BadgeBrush = new SolidColorBrush(Color.Parse(source?.Color ?? "#888888"));
        Item = new PlayItem("p" + p.Id, p.Id, "", p.Source, p.Url, p.Embed, null, $"{p.Title} — {(string.IsNullOrEmpty(p.Author) ? p.Artist : p.Author)}");
        CanPlay = Players.For(Item) != null;
        _isPlayed = p.Played;
        _isHidden = p.Hidden;
        _isPlaying = playingKey == Item.Key;
        _ = LoadCoverAsync(images);
    }

    async Task LoadCoverAsync(ImageCache images)
    {
        var bmp = await images.GetAsync(Post.Image);
        if (bmp != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => Cover = bmp);
    }

    public string Letter => string.IsNullOrWhiteSpace(Post.Title) ? "?" : Post.Title.Trim()[..1].ToUpperInvariant();
    public string Title => Post.Title;

    public string Who
    {
        get
        {
            var who = !string.IsNullOrEmpty(Post.Author) && !string.Equals(Post.Author, Post.Artist, StringComparison.OrdinalIgnoreCase)
                ? $"{Post.Artist} · {Post.Author}" : Post.Artist;
            return string.Join(" · ", new[] { who, Post.Label }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }

    /// <summary>Something to know about it, such as "Not for sale on this page".</summary>
    public string Note => Post.Note ?? "";
    public bool HasNote => !string.IsNullOrEmpty(Post.Note);
    public string? NoteTip => Post.Note == ArtistFeed.Core.Bandcamp.NotForSale
        ? "This page shows the release but has no tracks and nothing to buy, and doesn't say where it is sold. A Bandcamp search didn't find the page that sells it."
        : null;

    public string Details
    {
        get
        {
            var size = Post.TrackCount > 1 ? $"{Post.TrackCount} tracks" : Post.Kind ?? "";
            var when = Post.PublishedAt != null ? Show.Ago(Post.PublishedAt) : "seen " + Show.Ago(Post.FirstSeen);
            return string.Join(" · ", new[] { size, Show.Clock(Post.Duration), when }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }

    public string PlayGlyph => CanPlay ? "▶" : "↗";
    public string HideGlyph => IsHidden ? "↺" : "✕";
    public string HideTip => IsHidden ? "Put back in the feed" : "Hide from the feed";

    partial void OnIsHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(HideGlyph));
        OnPropertyChanged(nameof(HideTip));
    }
}

/// <summary>One track in the list.</summary>
public partial class TrackRowViewModel : ObservableObject
{
    public TrackRow Row { get; }
    public string Site { get; }
    public PlayItem Item { get; }
    public bool CanPlay { get; }

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPlayed;

    public TrackRowViewModel(TrackRow r, ISource? source, string? playingKey)
    {
        Row = r;
        Site = source?.Label ?? r.Source;
        var key = r.TrackId != null ? "t" + r.TrackId : "p" + r.PostId;
        var title = string.IsNullOrEmpty(r.Track) ? r.Release : r.Track;
        Item = new PlayItem(key, r.PostId, r.TrackKey, r.Source, r.Url, r.Embed, r.TrackExtId, $"{title} — {r.Artist}",
            NoStream: r.Source == "bandcamp" && r.TrackId != null && r.Duration == null);
        CanPlay = Players.For(Item) != null;
        _isPlayed = r.Played;
        _isPlaying = playingKey == Item.Key;
    }

    public string Artist => Row.Artist;
    public string Label => Row.Label;
    public string Release => string.IsNullOrEmpty(Row.Note) ? Row.Release : $"{Row.Release} · {Row.Note}";
    public string Track => Row.Track;
    public string Duration => Show.Clock(Row.Duration);
    public string Released => Day(Row.Released);
    public string Posted => Day(Row.Posted);
    public bool IsHidden => Row.Hidden;
    public string PlayGlyph => CanPlay ? "▶" : "↗";
    public string HideGlyph => Row.Hidden ? "↺" : "✕";

    static string Day(string? iso) => iso is { Length: >= 10 } ? iso[..10] : "";
}

/// <summary>A site button at the top of the feed.</summary>
public partial class SiteChip(string? key, string label, string color) : ObservableObject
{
    public string? Key { get; } = key;
    public string Label { get; } = label;
    public IBrush Dot { get; } = new SolidColorBrush(Color.Parse(color));
    public bool HasDot => Key != null;
    [ObservableProperty] private bool _isChecked;
}

public sealed record Option(string Value, string Text)
{
    public override string ToString() => Text;
}

public sealed record ArtistOption(long? Id, string Name)
{
    public override string ToString() => Name;
}
