# Artist Feed (desktop)

A native desktop version of [Artist Feed](https://github.com/divided-eye/artist-feed), written in C# with
[Avalonia](https://avaloniaui.net/), so it runs on Windows, macOS and Linux without Docker or a browser.
It keeps one feed of the newest releases and uploads from the Bandcamp, SoundCloud and YouTube pages of the
artists and labels you track.

This is an experiment, and a first stage: it does what the web version's feed does, but not yet everything
else. See [What's in this version](#whats-in-this-version).

## Running it

You need the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10` on Windows, or from
dotnet.microsoft.com/download/dotnet/10.0).

```
git clone https://github.com/divided-eye/artist-feed-desktop.git
cd artist-feed-desktop
dotnet run --project src/ArtistFeed.App
```

The first start takes a minute while the packages download; later starts take seconds. The app checks your
tracked pages when it starts and then every hour while it is open, with short catch-up visits in between
for pages whose older releases are still loading.

**Ready-to-run copies.** The **Build** workflow (GitHub → Actions → Build → Run workflow) makes copies for
Windows, macOS (Apple silicon and Intel) and Linux that don't need .NET installed. Download one from the run's
Artifacts, unzip it, and run `ArtistFeed` (`ArtistFeed.exe` on Windows). They aren't signed, so Windows
SmartScreen and macOS Gatekeeper ask before the first run (on a Mac: right-click → Open).

## Your data

Everything (artists, feed, what you've hidden and listened to, settings) is kept in one SQLite file,
`feed.db`, the same format as the web version's `data/feed.db`:

| System | Folder |
| --- | --- |
| Windows | `%APPDATA%\ArtistFeed` |
| macOS | `~/Library/Application Support/ArtistFeed` |
| Linux | `~/.config/ArtistFeed` |

Settings shows the exact path, with a button to open the folder. Set `ARTIST_FEED_DATA` to use another folder.

**Bringing over the web version's data.** Stop the web version (`docker compose stop`), then in
**Settings → Your data** choose **Import from the web version…** and pick its `data/feed.db`. The copy takes
over the next time you open the app; what was there before is kept as `feed.db.before-import`. The two
versions shouldn't share one file while both are running.

## What's in this version

- **The feed**: a grid of covers or a sortable list of tracks, newest first; site buttons, artist menu,
  search, Focus Feed, and the filters for year, label, type, length and date (by posted or release date);
  hide no-preview tracks; show and put back hidden items; limit to the first N results; Load more;
  Export Feed to CSV.
- **Listening**: each site's own player opens at the bottom of the window (Bandcamp, SoundCloud, YouTube),
  and **Open on …** opens the page in your browser. Played items are dimmed, as in the web version.
- **Tracked Artists**: the table of artists and their pages, other names in the Artist cell, Focus and
  YouTube search ticks, Check now, removing rows, Import and Export CSV, pasting a list, filtering rows,
  and Save changes / Discard with each problem shown on its row. Same rules as the web version, including
  "two rows for one artist" and keeping both when they are different artists.
- **Workspaces**: named groups of artists (Focus is the first), ticked in Tracked Artists and chosen from the menu beside Search to show only them.
- **Import follows** (Tracked Artists): add the profiles a SoundCloud account follows, or the artists and labels a public Bandcamp fan page follows.
- **Settings**: text size (90% to 150%) and colour scheme (Dark, Light, High contrast, or Match this computer); switching Bandcamp, SoundCloud or YouTube on and off, with Save; where your data is.

Not in this version yet: **Link Finder**, **Find missing links**, the Bandcamp purchases reader and
**Downloads** button, your SoundCloud account's follows, Beatport and Discogs, Related tracks, and choosing
or reordering list columns. They come in the next stage. Ticks and saved answers they use are kept in
`feed.db`, so nothing is lost by using this version in between.

## Known limits

- **Built without access to the real sites.** Like the web version, its page reading is tested against
  stand-in copies of the sites' pages; the logic is a line-by-line port of the web version's, which has
  been seen working on the real sites, but this version itself hasn't been run against them yet.
- **The players need the system's web view**: WebView2 on Windows (built into Windows 10 and 11), WebKit on
  macOS, and WebKitGTK on Linux (`sudo apt install libwebkit2gtk-4.1-0` on Ubuntu). Without it the player
  area says so, and **Open on …** still opens the page in your browser.
- Bandcamp's player can't be started by the app: press play in it.

## Checking it

```
dotnet test tests/ArtistFeed.Tests
```

The tests are the web version's, rewritten: the same stand-in pages and the same expected results. Two of
them compare against the web version directly: 1,446 names tidied by the web app must tidy the same here,
and a database written by the web app must give the same feed and list for every filter and sort. The
window tests drive the app headlessly and save pictures of each window to `artistfeed-shots` in the temp
folder.

## Layout

- `src/ArtistFeed.Core`: storage, the three site readers, the refresh, Tracked Artists' rules. No UI.
- `src/ArtistFeed.App`: the Avalonia windows (MVVM, CommunityToolkit.Mvvm).
- `tests/ArtistFeed.Tests`: xUnit tests, including headless window tests.

MIT licensed, like the web version.
