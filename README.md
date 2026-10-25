# Artist Feed (desktop)

A native desktop version of [Artist Feed](https://github.com/divided-eye/artist-feed), written in C# with
[Avalonia](https://avaloniaui.net/), so it runs on Windows, macOS and Linux without Docker or a browser.
It keeps one feed of the newest releases and uploads from the Bandcamp, SoundCloud and YouTube pages of the
artists and labels you track.

This is an experiment. It now does everything the web version does: the feed, Tracked Artists and Link
Finder. See [What's in this version](#whats-in-this-version).

**User guide.** The full guide, covering every feature, is kept with the web version:
[read it online](https://github.com/divided-eye/artist-feed/blob/main/docs/Artist%20Feed%20User%20Guide.md), or
[download it as a Word file](https://github.com/divided-eye/artist-feed/raw/main/docs/Artist%20Feed%20User%20Guide.docx).
It describes the app as it appears in a browser; the desktop app has the same features in its own windows, and
the guide's section *The desktop app (no Docker)* covers getting, running and updating it. Each ready-to-run
copy (below) also comes with the guide.

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
for pages whose older releases are still loading. Each site is read in its own queue, so SoundCloud and
YouTube don't wait behind Bandcamp's spaced-out requests; a Bandcamp page read for the first time opens only
its two newest releases (the rest follow on the catch-up visits); and while a refresh runs, **Show new posts**
beside the status line adds what it has found so far. Quiet Bandcamp pages (no release in the last 90 days) are
checked every 6 hours instead of every hour, and when they are due (after the app has been closed for a while,
say) they are read after the active pages, so new releases don't wait behind them; **Check them now**, behind the small ⓘ beside the status line, reads
them straight away. That ⓘ also shows the pages checked on each site and anything that went wrong.
Requests to Bandcamp go one second apart; if Bandcamp keeps asking the app to pause, set the environment variable
`ARTIST_FEED_BANDCAMP_GAP` to `2` (seconds) before starting the app. While it is open it also reads the full track lists of
your Bandcamp purchases in the background (when a Bandcamp username is set in Link Finder) and, with the
Downloads button on, looks for new purchases and pre-orders that have come out.

**Ready-to-run copies.** Download the newest version from the
[Releases page](https://github.com/divided-eye/artist-feed-desktop/releases/latest): pick the zip for your system
(Windows, Mac with Apple silicon, Mac with Intel, or Linux), unzip it, and run `ArtistFeed` (`ArtistFeed.exe` on
Windows). No .NET or GitHub account is needed. They aren't signed, so Windows SmartScreen and macOS Gatekeeper
ask before the first run (on Windows: More info → Run anyway; on a Mac: right-click → Open). The zip also holds the
user guide (`Artist Feed User Guide.docx`, with a `.md` copy), the licence and the icon's notice.

**Publishing a new version** (for the maintainer): push a tag, for example
`git tag -a v0.5.0 -m "What changed"` then `git push origin v0.5.0`. The **Build** workflow tests the app, builds
all four copies and publishes them as a release of that name, with the tag's message as its description. Running
the workflow by hand (Actions → Build → Run workflow) builds the same copies without publishing a release; they
appear under that run's Artifacts.

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
  search, the workspace menu, and the filters for year, label, type, length and date (by posted or release date);
  hide no-preview tracks; show and put back hidden items; limit to the first N results; Load more;
  Export Feed to CSV.
- **Listening**: each site's own player opens at the bottom of the window (Bandcamp, SoundCloud, YouTube),
  and **Open on …** opens the page in your browser. Played items are dimmed, as in the web version.
- **Tracked Artists**: the table of artists and their pages, in name order and sortable by clicking a heading, other names in the Artist cell, workspace and
  YouTube search ticks, Check now, removing rows, Import and Export CSV, pasting a list, filtering rows,
  and Save changes / Discard with each problem shown on its row. Same rules as the web version, including
  "two rows for one artist" and keeping both when they are different artists.
- **Workspaces**: named groups of artists (Focus is the first), ticked in Tracked Artists and chosen from the menu beside Search to show only them.
- **Import follows** (Tracked Artists): add the profiles a SoundCloud account follows, or the artists and labels a public Bandcamp fan page follows.
- **Find missing links** (Tracked Artists): fills empty Bandcamp, SoundCloud and name cells with highlighted guesses
  to check; only the rows shown while the table is filtered, or one row with its own Find links; Rescan and
  remembered cleared cells, as in the web version.
- **Link Finder**: paste a tracklist or choose an Excel or CSV file, and each track's link is looked for in
  your feed, your Bandcamp purchases, Bandcamp search and the artist's own pages, then on the other sites
  ticked in Settings. Skip a row while it runs, set a link yourself (Add link / Edit), keep a link marked for
  review (Looks right), search one row again, see What was tried, and export the results. Your Bandcamp
  username, Read purchases, Read full track lists, the Downloads switch and which artist link to give when a
  track isn't found are all in the Look up tab.
- **Saved results** (Link Finder): every track looked up, with search, filters, sorting by any heading, your
  own spellings, Forget, Import your list (with links and notes) and export.
- **Downloads** button: counts Bandcamp purchases and pre-orders ready to download, and opens your collection.
  Off until switched on in Link Finder.
- **Related tracks**: the + beside a SoundCloud row in the list opens the tracks SoundCloud lists as related,
  three at a time, playable in place.
- **Edit Columns** (list view): choose which columns show and their order; dragging a heading moves it too.
- **Settings**: text size (90% to 150%) and colour scheme (Dark, Light, High contrast, or Match this computer);
  switching sites on and off, including Beatport and Discogs for Link Finder (experimental, off by default);
  which sites Link Finder searches; a Discogs access token; your SoundCloud username, whose follows are
  preferred when a profile has to be picked by name; where your data is.

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

The tests are the web version's, rewritten: the same stand-in pages and the same expected results. Link
Finder's tests go further: the pages the web version's tests were served were recorded
(`tools/record_web_exchanges.py`, saved as `tests/ArtistFeed.Tests/Data/web-exchanges.json`) and are served
back here unchanged, so both versions are checked against exactly the same pages. Two tests
compare against the web version directly: 1,446 names tidied by the web app must tidy the same here,
and a database written by the web app must give the same feed and list for every filter and sort. The
window tests drive the app headlessly and save pictures of each window to `artistfeed-shots` in the temp
folder.

## Layout

- `src/ArtistFeed.Core`: storage, the three site readers, the refresh, Tracked Artists' rules, and Link
  Finder (`Lookup/`). No UI.
- `src/ArtistFeed.App`: the Avalonia windows (MVVM, CommunityToolkit.Mvvm).
- `tests/ArtistFeed.Tests`: xUnit tests, including headless window tests.

MIT licensed, like the web version.
