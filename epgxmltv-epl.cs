#!/usr/bin/dotnet run
#nullable enable
// EPL EPG Generator — C# File-Based App (.NET 10+)
//
// Usage:
//   dotnet run epgxmltv-epl.cs -- [options]
//
// Options:
//   --days-ahead <n>        Number of days into the future to include (default: 14)
//   --days-back <n>         Number of days in the past to include (default: 1)
//   --output <path>         Output file path for the XMLTV file (default: output/epl.xml)
//   --schedule-url <url>    Override the default EPL schedule URL
//
// Examples:
//   dotnet run epgxmltv-epl.cs
//   dotnet run epgxmltv-epl.cs -- --days-ahead 7 --days-back 3
//   dotnet run epgxmltv-epl.cs -- --days-ahead 30 --output ./full-month.xml

using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Linq;

// ===========================================================================
// Constants
// ===========================================================================

const string EplLeagueLogoUrl = "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/23.png";
const string DefaultScheduleUrl = "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/scoreboard";
const string UserAgent = "epgxmltv-epl/1.0 dotnet-httpclient/1.1";
const string GeneratorName = "epgxmltv-epl/1.0";

// "Big Six" club ESPN IDs — used to boost star ratings for marquee fixtures
var BigSixIds = new HashSet<int> { 359, 363, 364, 382, 360, 367 };

// ===========================================================================
// Teams, Mapping, and Static Data
// Note: Dictionary keys are ESPN internal team ids from schedule JSON. ChannelId is the XMLTV channel @id.
// ===========================================================================

var AllTeams = new Dictionary<int, TeamInfo>
{
  [359] = new("EPL-Arsenal.gb", "Arsenal", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/359.png"),
  [362] = new("EPL-AstonVilla.gb", "Aston Villa", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/362.png"),
  [349] = new("EPL-Bournemouth.gb", "AFC Bournemouth", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/349.png"),
  [337] = new("EPL-Brentford.gb", "Brentford", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/337.png"),
  [331] = new("EPL-Brighton.gb", "Brighton & Hove Albion", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/331.png"),
  [379] = new("EPL-Burnley.gb", "Burnley", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/379.png"),
  [363] = new("EPL-Chelsea.gb", "Chelsea", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/363.png"),
  [384] = new("EPL-CrystalPalace.gb", "Crystal Palace", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/384.png"),
  [368] = new("EPL-Everton.gb", "Everton", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/368.png"),
  [370] = new("EPL-Fulham.gb", "Fulham", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/370.png"),
  [357] = new("EPL-LeedsUnited.gb", "Leeds United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/357.png"),
  [364] = new("EPL-Liverpool.gb", "Liverpool", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/364.png"),
  [382] = new("EPL-ManchesterCity.gb", "Manchester City", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/382.png"),
  [360] = new("EPL-ManchesterUnited.gb", "Manchester United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/360.png"),
  [361] = new("EPL-NewcastleUnited.gb", "Newcastle United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/361.png"),
  [393] = new("EPL-NottinghamForest.gb", "Nottingham Forest", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/393.png"),
  [366] = new("EPL-Sunderland.gb", "Sunderland", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/366.png"),
  [367] = new("EPL-TottenhamHotspur.gb", "Tottenham Hotspur", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/367.png"),
  [371] = new("EPL-WestHamUnited.gb", "West Ham United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/371.png"),
  [380] = new("EPL-Wolves.gb", "Wolverhampton Wanderers", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/380.png"),
};

// ===========================================================================
// Main Execution Flow
// ===========================================================================

if (!TryParseArgs(args, out var options))
  return 1;

var windowStart = DateTimeOffset.UtcNow.AddDays(-options.DaysBack);
var windowEnd = DateTimeOffset.UtcNow.AddDays(options.DaysAhead);

string eplUrl = string.IsNullOrEmpty(options.UrlOverride)
    ? $"{DefaultScheduleUrl}?dates={windowStart:yyyyMMdd}-{windowEnd:yyyyMMdd}&limit=1000"
    : options.UrlOverride;

var entries = await FetchScheduleAsync(eplUrl, options.DaysAhead, options.DaysBack);
Console.WriteLine($"Found {entries.Count} matches in the window ({options.DaysBack} back, {options.DaysAhead} ahead).");

var programmes = entries
    .SelectMany(DerivePair)
    .ToList();

await WriteXmltvAsync(AllTeams.Values, programmes, options.OutputPath);

return 0;

// ===========================================================================
// Extensions
// ===========================================================================

/// <summary>
/// Safely extracts an integer from a JSON node. Handles properties that might be parsed
/// as strings in the raw JSON payload.
/// </summary>
int GetInt(JsonNode? node)
{
  if (node == null) return 0;
  var val = node.AsValue();
  if (val.TryGetValue<int>(out var i)) return i;
  if (val.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var si)) return si;
  return 0;
}

// ===========================================================================
// Local Functions & Helpers
// ===========================================================================

/// <summary>
/// Parses the command line arguments.
/// Returns false if an unknown argument is encountered.
/// </summary>
bool TryParseArgs(IList<string> args, out ScriptOptions options)
{
  options = new ScriptOptions();

  for (int i = 0; i < args.Count; i++)
  {
    switch (args[i])
    {
      case "--days-ahead" when TryReadIntArg(args, ref i, options.DaysAhead, out var daysAhead):
        options = options with { DaysAhead = daysAhead };
        break;
      case "--days-back" when TryReadIntArg(args, ref i, options.DaysBack, out var daysBack):
        options = options with { DaysBack = daysBack };
        break;
      case "--output" when TryReadStringArg(args, ref i, out var outputPath):
        options = options with { OutputPath = outputPath };
        break;
      case "--schedule-url" when TryReadStringArg(args, ref i, out var urlOverride):
        options = options with { UrlOverride = urlOverride };
        break;
      default:
        Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
        Console.Error.WriteLine("Usage: dotnet run epgxmltv-epl.cs -- [--days-ahead <n>] [--days-back <n>] [--output <path>] [--schedule-url <url>]");
        Console.Error.WriteLine("Run with no arguments for defaults (14 days ahead, 1 day back, output/epl.xml).");
        return false;
    }
  }

  return true;
}

/// <summary>
/// Helper to extract an integer value from the argument array at the current index.
/// Advances the index if successful.
/// </summary>
bool TryReadIntArg(IList<string> args, ref int index, int fallback, out int value)
{
  value = fallback;
  return index + 1 < args.Count && int.TryParse(args[++index], out value);
}

/// <summary>
/// Helper to extract a string value from the argument array at the current index.
/// Advances the index if successful and prevents consuming other flags.
/// </summary>
bool TryReadStringArg(IList<string> args, ref int index, out string value)
{
  value = string.Empty;
  if (index + 1 >= args.Count || args[index + 1].StartsWith("--"))
    return false;

  value = args[++index];
  return true;
}

// ===========================================================================
// Scheduler
// ===========================================================================

/// <summary>
/// Downloads and parses the EPL JSON schedule for the specified date window.
/// </summary>
async Task<IReadOnlyList<MatchEntry>> FetchScheduleAsync(string eplUrl, int daysAhead, int daysBack)
{
  Console.WriteLine($"Fetching EPL schedule from {eplUrl} ...");
  using var http = new HttpClient();
  http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
  await using var stream = await http.GetStreamAsync(eplUrl);
  var doc = await JsonNode.ParseAsync(stream);
  return ParseSchedule(doc, daysAhead, daysBack);
}

/// <summary>
/// Iterates over the raw schedule JSON to find matches occurring within our time window.
/// Flattens the array and resolves known teams.
/// </summary>
IReadOnlyList<MatchEntry> ParseSchedule(JsonNode? doc, int daysAhead, int daysBack)
{
  var windowStart = DateTimeOffset.UtcNow.AddDays(-daysBack);
  var windowEnd = DateTimeOffset.UtcNow.AddDays(daysAhead);
  var entries = new List<MatchEntry>();

  var events = doc?["events"]?.AsArray() ?? [];

  foreach (var evt in events)
  {
    if (evt == null) continue;
    var dtStr = (string?)evt["date"];
    if (dtStr == null || !DateTimeOffset.TryParse(dtStr, null, DateTimeStyles.RoundtripKind, out var startUtc)) continue;
    if (startUtc < windowStart || startUtc > windowEnd) continue;

    var competition = evt["competitions"]?[0];
    if (competition == null) continue;

    var competitors = competition["competitors"]?.AsArray();
    if (competitors == null || competitors.Count < 2) continue;

    var homeProp = competitors.FirstOrDefault(c => (string?)c?["homeAway"] == "home");
    var awayProp = competitors.FirstOrDefault(c => (string?)c?["homeAway"] == "away");

    if (homeProp == null || awayProp == null) continue;

    int homeId = GetInt(homeProp["team"]?["id"]);
    int awayId = GetInt(awayProp["team"]?["id"]);

    if (!AllTeams.TryGetValue(homeId, out var homeTeam)) continue;
    if (!AllTeams.TryGetValue(awayId, out var awayTeam)) continue;

    var venue = competition["venue"];
    var status = competition["status"]?["type"];

    var notes = competition["notes"]?.AsArray();
    string matchweekLabel = notes?.FirstOrDefault() != null ? (string?)notes[0]?["headline"] ?? "" : "";

    entries.Add(new MatchEntry(
        StartUtc: startUtc,
        Away: awayTeam,
        Home: homeTeam,
        AwayTeamId: awayId,
        HomeTeamId: homeId,
        AwayRecord: ParseRecord(awayProp),
        HomeRecord: ParseRecord(homeProp),
        StadiumName: (string?)venue?["fullName"] ?? "",
        StadiumCity: (string?)venue?["address"]?["city"] ?? "",
        MatchweekLabel: matchweekLabel,
        MatchStatus: (string?)status?["state"] == "post" ? 3 : 1,
        MatchStatusText: (string?)status?["detail"] ?? ""
    ));
  }

  return entries.OrderBy(e => e.StartUtc).ToList();
}

/// <summary>
/// Maps a team's win/draw/loss stats from JSON.
/// Soccer records are typically formatted as "W-D-L" overall; a two-part "W-L"
/// fallback is also handled for cases where draws are omitted by the API.
/// </summary>
TeamRecord ParseRecord(JsonNode? team)
{
    if (team == null) return new TeamRecord(0, 0, 0);

    string recordStr = "";
    var records = team["records"]?.AsArray();
    if (records != null && records.Count > 0)
    {
        var overall = records.FirstOrDefault(r => (string?)r?["name"] == "overall" || (string?)r?["type"] == "total");
        recordStr = (string?)overall?["summary"] ?? "";
    }

    if (string.IsNullOrEmpty(recordStr))
        recordStr = (string?)team["record"] ?? "0-0-0";

    int wins = 0, draws = 0, losses = 0;
    if (!string.IsNullOrEmpty(recordStr))
    {
        var parts = recordStr.Split('-');
        if (parts.Length >= 3)
        {
            int.TryParse(parts[0], out wins);
            int.TryParse(parts[1], out draws);
            int.TryParse(parts[2], out losses);
        }
        else if (parts.Length == 2)
        {
            int.TryParse(parts[0], out wins);
            int.TryParse(parts[1], out losses);
        }
    }

    return new TeamRecord(wins, draws, losses);
}

// ===========================================================================
// Programme Deriver
// ===========================================================================

/// <summary>
/// Derives two XMLTV programme entries (one per team channel) from a single match event.
/// Contains all logic for computing display titles, ratings, and descriptions.
/// </summary>
IEnumerable<ProgrammeInfo> DerivePair(MatchEntry e)
{
  bool isBigSixAway = BigSixIds.Contains(e.AwayTeamId);
  bool isBigSixHome = BigSixIds.Contains(e.HomeTeamId);
  bool isBigSixClash = isBigSixAway && isBigSixHome;

  string matchup = $"{e.Away.DisplayName} vs {e.Home.DisplayName}";
  string title = "Premier League Soccer";
  string? subTitle = matchup;
  string? episodeNum = string.IsNullOrEmpty(e.MatchweekLabel) ? null : e.MatchweekLabel;
  int length = GetLength();
  int starRating = GetStarRating(isBigSixClash, isBigSixAway || isBigSixHome);
  var categories = BuildCategories(e.MatchweekLabel);
  var keywords = BuildKeywords(e);
  var stopUtc = e.StartUtc.AddMinutes(length);
  string desc = BuildDesc(e);

  var template = new ProgrammeInfo(
      "", e.StartUtc, stopUtc,
      title, subTitle, desc, categories, keywords,
      length, episodeNum, starRating, IsPremiere: false, IsNew: true,
      Country: "GB");

  yield return template with { ChannelId = e.Away.ChannelId };
  yield return template with { ChannelId = e.Home.ChannelId };
}

/// <summary>
/// Returns the broadcast block length in minutes.
/// A Premier League match runs 90 minutes plus stoppage time, halftime, and a pre/post buffer.
/// </summary>
int GetLength() => 130;

/// <summary>
/// Assigns a star rating out of 5 based on fixture prestige.
/// Big Six clashes earn a maximum rating; any Big Six involvement adds an extra star.
/// </summary>
int GetStarRating(bool isBigSixClash, bool hasBigSix)
{
  if (isBigSixClash) return 5;
  if (hasBigSix) return 4;
  return 3;
}

/// <summary>
/// Compiles categorization tags used by DVRs and players.
/// </summary>
IReadOnlyList<string> BuildCategories(string matchweekLabel)
{
  List<string> cats = ["Live", "New", "Sports", "Sports event", "Soccer", "Football", "Premier League", "HD"];
  if (!string.IsNullOrEmpty(matchweekLabel)) cats.Add(matchweekLabel);
  return cats;
}

/// <summary>
/// Compiles search keywords like team names and stadiums for XMLTV.
/// </summary>
IReadOnlyList<string> BuildKeywords(MatchEntry e)
{
  List<string> kw = [e.Away.DisplayName, e.Home.DisplayName];
  if (!string.IsNullOrEmpty(e.StadiumName)) kw.Add(e.StadiumName);
  if (!string.IsNullOrEmpty(e.StadiumCity)) kw.Add(e.StadiumCity);
  if (!string.IsNullOrEmpty(e.MatchweekLabel)) kw.Add(e.MatchweekLabel);
  return kw;
}

/// <summary>
/// Constructs the human-readable description for the EPG detailing team records,
/// the current venue, and matchweek information.
/// Soccer records are expressed as W-D-L (wins, draws, losses).
/// </summary>
string BuildDesc(MatchEntry e)
{
  var sb = new StringBuilder();

  if (!string.IsNullOrEmpty(e.MatchweekLabel))
  {
    sb.Append(e.MatchweekLabel);
    sb.Append(". ");
  }
  else
  {
    sb.Append("Premier League. ");
  }

  sb.Append($"{e.Away.DisplayName} ({e.AwayRecord.Wins}-{e.AwayRecord.Draws}-{e.AwayRecord.Losses})");
  sb.Append(" visit ");
  sb.Append($"{e.Home.DisplayName} ({e.HomeRecord.Wins}-{e.HomeRecord.Draws}-{e.HomeRecord.Losses})");

  if (!string.IsNullOrEmpty(e.StadiumName))
  {
    sb.Append($" at {e.StadiumName}");
    if (!string.IsNullOrEmpty(e.StadiumCity))
      sb.Append($" in {e.StadiumCity}");
  }

  sb.Append('.');

  return sb.ToString();
}

// ===========================================================================
// XMLTV Writer
// ===========================================================================

/// <summary>
/// Builds the XMLTV document from the list of derived programmes using LINQ-to-XML.
/// Writes the raw file and a parallel GZip compressed version.
/// </summary>
async Task WriteXmltvAsync(IEnumerable<TeamInfo> teams, IReadOnlyList<ProgrammeInfo> programmes, string outputPath)
{
  var dir = Path.GetDirectoryName(outputPath);
  if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

  Console.WriteLine($"Writing XMLTV to {outputPath} ...");

  string FormatTime(DateTimeOffset utc) => utc.ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'", CultureInfo.InvariantCulture);

  XElement LangElement(string name, string value) => new XElement(name, new XAttribute("lang", "en"), value);

  var tvElement = new XElement("tv",
      new XAttribute("date", FormatTime(DateTimeOffset.UtcNow)),
      new XAttribute("generator-info-name", GeneratorName),
      new XAttribute("generator-info-url", "https://github.com/philipsaad/epgxmltv"),
      teams.OrderBy(t => t.DisplayName).Select(t =>
          new XElement("channel",
              new XAttribute("id", t.ChannelId),
              new XElement("display-name", t.DisplayName),
              new XElement("icon", new XAttribute("src", t.LogoUrl))
          )
      ),
      programmes.Select(p =>
          new XElement("programme",
              new XAttribute("start", FormatTime(p.StartUtc)),
              new XAttribute("stop", FormatTime(p.StopUtc)),
              new XAttribute("channel", p.ChannelId),
              LangElement("title", p.Title),
              string.IsNullOrEmpty(p.SubTitle) ? null : LangElement("sub-title", p.SubTitle),
              LangElement("desc", p.Desc),
              p.Categories.Select(c => LangElement("category", c)),
              p.Keywords.Select(k => LangElement("keyword", k)),
              new XElement("language", "English"),
              new XElement("length", new XAttribute("units", "minutes"), p.LengthMinutes.ToString(CultureInfo.InvariantCulture)),
              new XElement("icon", new XAttribute("src", EplLeagueLogoUrl)),
              new XElement("country", p.Country),
              string.IsNullOrEmpty(p.EpisodeNum) ? null : new XElement("episode-num", new XAttribute("system", "onscreen"), p.EpisodeNum),
              new XElement("video",
                  new XElement("present", "yes"),
                  new XElement("colour", "yes"),
                  new XElement("aspect", "16:9"),
                  new XElement("quality", "HDTV")
              ),
              new XElement("audio",
                  new XElement("present", "yes"),
                  new XElement("stereo", "stereo")
              ),
              p.IsPremiere ? new XElement("premiere", string.Empty) : null,
              p.IsNew ? new XElement("new", string.Empty) : null,
              new XElement("live", string.Empty),
              new XElement("star-rating", new XElement("value", $"{p.StarRating}/5"))
          )
      )
  );

  var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), tvElement);

  using var ms = new MemoryStream();
  var settings = new XmlWriterSettings
  {
    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    Indent = true,
    NewLineChars = "\n"
  };

  using (var writer = XmlWriter.Create(ms, settings))
  {
    doc.WriteTo(writer);
    writer.Flush();
  }

  var bytes = ms.ToArray();

  await File.WriteAllBytesAsync(outputPath, bytes);

  var gzPath = outputPath + ".gz";
  await using var gzFs = new FileStream(gzPath, FileMode.Create, FileAccess.Write);
  await using var gz = new GZipStream(gzFs, CompressionLevel.Optimal);
  await gz.WriteAsync(bytes);

  Console.WriteLine($"Done. ({bytes.Length:N0} bytes raw → {outputPath} and {gzPath})");
}

// ===========================================================================
// Models
// ===========================================================================

record TeamInfo(string ChannelId, string DisplayName, string LogoUrl, string Country = "GB");

record TeamRecord(int Wins, int Draws, int Losses);

record MatchEntry(DateTimeOffset StartUtc, TeamInfo Away, TeamInfo Home, int AwayTeamId, int HomeTeamId, TeamRecord AwayRecord, TeamRecord HomeRecord, string StadiumName, string StadiumCity, string MatchweekLabel, int MatchStatus, string MatchStatusText);

record ProgrammeInfo(string ChannelId, DateTimeOffset StartUtc, DateTimeOffset StopUtc, string Title, string? SubTitle, string Desc, IReadOnlyList<string> Categories, IReadOnlyList<string> Keywords, int LengthMinutes, string? EpisodeNum, int StarRating, bool IsPremiere, bool IsNew, string Country = "GB");

record ScriptOptions(int DaysAhead = 14, int DaysBack = 1, string OutputPath = "output/epl.xml", string UrlOverride = "");
