using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using ArtistFeed.Core;

namespace ArtistFeed.App.Services;

/// <summary>Cover art: downloaded once, shrunk to card size, kept in memory (the most recent few hundred)
/// and on disk, so the feed opens quickly the next time.</summary>
public sealed class ImageCache
{
    readonly HttpClient _client;
    readonly string _folder;
    readonly ConcurrentDictionary<string, Task<Bitmap?>> _loading = new();
    readonly LinkedList<string> _order = new();
    const int Keep = 400;
    const int Width = 400;
    readonly SemaphoreSlim _parallel = new(6);

    public ImageCache(HttpClient client, string folder)
    {
        _client = client;
        _folder = folder;
        try { Directory.CreateDirectory(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string FileName(string url)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash) + ".img";
    }

    public Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("https://") || url.StartsWith("http://"))) return Task.FromResult<Bitmap?>(null);
        var task = _loading.GetOrAdd(url, LoadAsync);
        lock (_order)
        {
            _order.Remove(url);
            _order.AddFirst(url);
            while (_order.Count > Keep)
            {
                var last = _order.Last!.Value;
                _order.RemoveLast();
                _loading.TryRemove(last, out _);
            }
        }
        return task;
    }

    async Task<Bitmap?> LoadAsync(string url)
    {
        var path = Path.Combine(_folder, FileName(url));
        await _parallel.WaitAsync();
        try
        {
            byte[] bytes;
            if (File.Exists(path)) bytes = await File.ReadAllBytesAsync(path);
            else
            {
                using var r = await _client.GetAsync(url);
                if (!r.IsSuccessStatusCode) return null;
                bytes = await r.Content.ReadAsByteArrayAsync();
                try { await File.WriteAllBytesAsync(path, bytes); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            using var ms = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(ms, Width, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            return null;                                     // a missing or unreadable cover shows the letter instead
        }
        finally
        {
            _parallel.Release();
        }
    }
}
