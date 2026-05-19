#!/usr/bin/dotnet run
#nullable enable
// NBA EPG Generator — C# File-Based App (.NET 10+)
//
// Usage:
//   dotnet run epgxmltv-nba.cs -- [options]
//
// Options:
//   --days-ahead <n>        Number of days into the future to include (default: 14)
//   --days-back <n>         Number of days in the past to include (default: 1)
//   --output <path>         Output file path for the XMLTV file (default: output/nba.xml)
//   --schedule-url <url>    Override the default NBA schedule URL
//
// Examples:
//   dotnet run epgxmltv-nba.cs
//   dotnet run epgxmltv-nba.cs -- --days-ahead 7 --days-back 3
//   dotnet run epgxmltv-nba.cs -- --days-ahead 30 --output ./full-month.xml

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

const string NbaLogoUrl = "https://a.espncdn.com/i/teamlogos/leagues/500-dark/nba.png";
const string DefaultScheduleUrl = "https://site.api.espn.com/apis/site/v2/sports/basketball/nba/scoreboard";
const string UserAgent = "epgxmltv-nba/1.0 dotnet-httpclient/1.1";
const string GeneratorName = "epgxmltv-nba/1.0";

// ===========================================================================
// Teams, Mapping, and Static Data
// Note: Dictionary keys are NBA internal team ids from schedule JSON. ChannelId is the XMLTV channel @id.
// ===========================================================================

var AllTeams = new Dictionary<int, TeamInfo>
{
  [1] = new("NBA-AtlantaHawks.us", "Atlanta Hawks", "https://a.espncdn.com/i/teamlogos/nba/500-dark/atl.png"),
  [2] = new("NBA-BostonCeltics.us", "Boston Celtics", "https://a.espncdn.com/i/teamlogos/nba/500-dark/bos.png"),
  [17] = new("NBA-BrooklynNets.us", "Brooklyn Nets", "https://a.espncdn.com/i/teamlogos/nba/500-dark/bkn.png"),
  [30] = new("NBA-CharlotteHornets.us", "Charlotte Hornets", "https://a.espncdn.com/i/teamlogos/nba/500-dark/cha.png"),
  [4] = new("NBA-ChicagoBulls.us", "Chicago Bulls", "https://a.espncdn.com/i/teamlogos/nba/500-dark/chi.png"),
  [5] = new("NBA-ClevelandCavaliers.us", "Cleveland Cavaliers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/cle.png"),
  [6] = new("NBA-DallasMavericks.us", "Dallas Mavericks", "https://a.espncdn.com/i/teamlogos/nba/500-dark/dal.png"),
  [7] = new("NBA-DenverNuggets.us", "Denver Nuggets", "https://a.espncdn.com/i/teamlogos/nba/500-dark/den.png"),
  [8] = new("NBA-DetroitPistons.us", "Detroit Pistons", "https://a.espncdn.com/i/teamlogos/nba/500-dark/det.png"),
  [9] = new("NBA-GoldenStateWarriors.us", "Golden State Warriors", "https://a.espncdn.com/i/teamlogos/nba/500-dark/gs.png"),
  [10] = new("NBA-HoustonRockets.us", "Houston Rockets", "https://a.espncdn.com/i/teamlogos/nba/500-dark/hou.png"),
  [11] = new("NBA-IndianaPacers.us", "Indiana Pacers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/ind.png"),
  [12] = new("NBA-LAClippers.us", "LA Clippers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/lac.png"),
  [13] = new("NBA-LosAngelesLakers.us", "Los Angeles Lakers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/lal.png"),
  [29] = new("NBA-MemphisGrizzlies.us", "Memphis Grizzlies", "https://a.espncdn.com/i/teamlogos/nba/500-dark/mem.png"),
  [14] = new("NBA-MiamiHeat.us", "Miami Heat", "https://a.espncdn.com/i/teamlogos/nba/500-dark/mia.png"),
  [15] = new("NBA-MilwaukeeBucks.us", "Milwaukee Bucks", "https://a.espncdn.com/i/teamlogos/nba/500-dark/mil.png"),
  [16] = new("NBA-MinnesotaTimberwolves.us", "Minnesota Timberwolves", "https://a.espncdn.com/i/teamlogos/nba/500-dark/min.png"),
  [3] = new("NBA-NewOrleansPelicans.us", "New Orleans Pelicans", "https://a.espncdn.com/i/teamlogos/nba/500-dark/no.png"),
  [18] = new("NBA-NewYorkKnicks.us", "New York Knicks", "https://a.espncdn.com/i/teamlogos/nba/500-dark/ny.png"),
  [25] = new("NBA-OklahomaCityThunder.us", "Oklahoma City Thunder", "https://a.espncdn.com/i/teamlogos/nba/500-dark/okc.png"),
  [19] = new("NBA-OrlandoMagic.us", "Orlando Magic", "https://a.espncdn.com/i/teamlogos/nba/500-dark/orl.png"),
  [20] = new("NBA-Philadelphia76ers.us", "Philadelphia 76ers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/phi.png"),
  [21] = new("NBA-PhoenixSuns.us", "Phoenix Suns", "https://a.espncdn.com/i/teamlogos/nba/500-dark/phx.png"),
  [22] = new("NBA-PortlandTrailBlazers.us", "Portland Trail Blazers", "https://a.espncdn.com/i/teamlogos/nba/500-dark/por.png"),
  [23] = new("NBA-SacramentoKings.us", "Sacramento Kings", "https://a.espncdn.com/i/teamlogos/nba/500-dark/sac.png"),
  [24] = new("NBA-SanAntonioSpurs.us", "San Antonio Spurs", "https://a.espncdn.com/i/teamlogos/nba/500-dark/sa.png"),
  [28] = new("NBA-TorontoRaptors.us", "Toronto Raptors", "https://a.espncdn.com/i/teamlogos/nba/500-dark/tor.png", "CA"),
  [26] = new("NBA-UtahJazz.us", "Utah Jazz", "https://a.espncdn.com/i/teamlogos/nba/500-dark/utah.png"),
  [27] = new("NBA-WashingtonWizards.us", "Washington Wizards", "https://a.espncdn.com/i/teamlogos/nba/500-dark/wsh.png"),
};

// ===========================================================================
// Main Execution Flow
// ===========================================================================

if (!TryParseArgs(args, out var options))
  return 1;

var windowStart = DateTimeOffset.UtcNow.AddDays(-options.DaysBack);
var windowEnd = DateTimeOffset.UtcNow.AddDays(options.DaysAhead);

string nbaUrl = string.IsNullOrEmpty(options.UrlOverride)
    ? $"{DefaultScheduleUrl}?dates={windowStart:yyyyMMdd}-{windowEnd:yyyyMMdd}&limit=1000"
    : options.UrlOverride;

var entries = await FetchScheduleAsync(nbaUrl, options.DaysAhead, options.DaysBack);
Console.WriteLine($"Found {entries.Count} games in the window ({options.DaysBack} back, {options.DaysAhead} ahead).");

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
        Console.Error.WriteLine("Usage: dotnet run epgxmltv-nba.cs -- [--days-ahead <n>] [--days-back <n>] [--output <path>] [--schedule-url <url>]");
        Console.Error.WriteLine("Run with no arguments for defaults (14 days ahead, 1 day back, output/nba.xml).");
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
/// Downloads and parses the NBA JSON schedule for the specified date window.
/// </summary>
async Task<IReadOnlyList<GameEntry>> FetchScheduleAsync(string nbaUrl, int daysAhead, int daysBack)
{
  Console.WriteLine($"Fetching NBA schedule from {nbaUrl} ...");
  using var http = new HttpClient();
  http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
  await using var stream = await http.GetStreamAsync(nbaUrl);
  var doc = await JsonNode.ParseAsync(stream);
  return ParseSchedule(doc, daysAhead, daysBack);
}

/// <summary>
/// Iterates over the raw schedule JSON to find games occurring within our time window.
/// Flattens the array and resolves known teams.
/// </summary>
IReadOnlyList<GameEntry> ParseSchedule(JsonNode? doc, int daysAhead, int daysBack)
{
  var windowStart = DateTimeOffset.UtcNow.AddDays(-daysBack);
  var windowEnd = DateTimeOffset.UtcNow.AddDays(daysAhead);
  var entries = new List<GameEntry>();

  var games = doc?["events"]?.AsArray() ?? [];

  foreach (var game in games)
  {
    if (game == null) continue;
    var dtStr = (string?)game["date"];
    if (dtStr == null || !DateTimeOffset.TryParse(dtStr, null, DateTimeStyles.RoundtripKind, out var startUtc)) continue;
    if (startUtc < windowStart || startUtc > windowEnd) continue;

    var competition = game["competitions"]?[0];
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
    
    var series = competition["series"];
    string seriesText = (string?)series?["summary"] ?? "";
    
    var notes = competition["notes"]?.AsArray();
    string gameLabel = notes?.FirstOrDefault() != null ? (string?)notes[0]?["headline"] ?? "" : "";
    
    int seriesGameNumber = 0;
    if (gameLabel.Contains(" - Game ")) {
      var parts = gameLabel.Split(new[] { " - Game " }, StringSplitOptions.None);
      if (parts.Length == 2 && int.TryParse(parts[1], out var gn)) {
        seriesGameNumber = gn;
      }
    }

    entries.Add(new GameEntry(
        StartUtc: startUtc,
        Away: awayTeam,
        Home: homeTeam,
        AwayRecord: ParseRecord(awayProp),
        HomeRecord: ParseRecord(homeProp),
        ArenaName: (string?)venue?["fullName"] ?? "",
        ArenaCity: (string?)venue?["address"]?["city"] ?? "",
        ArenaState: (string?)venue?["address"]?["state"] ?? "",
        SeriesText: seriesText,
        GameLabel: gameLabel,
        SeriesGameNumber: seriesGameNumber,
        GameStatus: (string?)status?["state"] == "post" ? 3 : 1,
        GameStatusText: (string?)status?["detail"] ?? ""
    ));
  }

  return entries.OrderBy(e => e.StartUtc).ToList();
}

/// <summary>
/// Maps a team's win/loss/seed stats from JSON.
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
    {
        recordStr = (string?)team["record"] ?? "0-0";
    }

    int wins = 0;
    int losses = 0;
    if (!string.IsNullOrEmpty(recordStr) && recordStr.Contains("-"))
    {
        var parts = recordStr.Split('-');
        if (parts.Length == 2)
        {
            int.TryParse(parts[0], out wins);
            int.TryParse(parts[1], out losses);
        }
    }
    
    return new TeamRecord(wins, losses, 0);
}

// ===========================================================================
// Programme Deriver
// ===========================================================================

/// <summary>
/// Derives two XMLTV programme entries (one per team channel) from a single game event.
/// Contains all logic for computing display titles, ratings, and descriptions.
/// </summary>
IEnumerable<ProgrammeInfo> DerivePair(GameEntry e)
{
  var meta = DeriveGameMeta(e);
  bool isCompetitiveSeries = meta.IsPlayoff
      && meta.GameNumber >= 5
      && Math.Abs(e.AwayRecord.Wins - e.HomeRecord.Wins) <= 1;

  string matchup = $"{e.Away.DisplayName} @ {e.Home.DisplayName}";
  string title = "NBA Basketball";
  string? subTitle = meta.IsPlayoff
      ? $"{matchup} - Game {meta.GameNumber}"
      : matchup;
  string? episodeNum = meta.IsPlayoff ? $"Game {meta.GameNumber}" : null;
  int length = GetLength(meta.Round);
  int starRating = GetStarRating(meta.Round, isCompetitiveSeries);
  var categories = BuildCategories(e.GameLabel, meta.IsPlayoff);
  var keywords = BuildKeywords(e);
  var stopUtc = e.StartUtc.AddMinutes(length);

  string desc = BuildDesc(e, meta.IsPlayoff, meta.IsElimination);

  var template = new ProgrammeInfo(
      "", e.StartUtc, stopUtc,
      title, subTitle, desc, categories, keywords,
      length, episodeNum, starRating, meta.IsPremiere, IsNew: true,
      Country: e.Home.Country);

  yield return template with { ChannelId = e.Away.ChannelId };
  yield return template with { ChannelId = e.Home.ChannelId };
}

/// <summary>
/// Determines if the game is a playoff event and analyzes specific details
/// such as game number and whether it could be an elimination match.
/// </summary>
GameMeta DeriveGameMeta(GameEntry e)
{
  var round = ParseRound(e.GameLabel);
  bool isPlayoff = round != PlayoffRound.RegularSeason
      || e.SeriesGameNumber > 0
      || !string.IsNullOrEmpty(e.SeriesText);

  if (!isPlayoff)
    return new GameMeta(false, round, 0, false, false);

  int gameNumber = ResolveSeriesGameNumber(e);
  return new GameMeta(
      IsPlayoff: true,
      Round: round,
      GameNumber: gameNumber,
      IsElimination: IsEliminationGame(e, gameNumber),
      IsPremiere: gameNumber == 1);
}

/// <summary>
/// Computes the current game number in a playoff series, falling back to 
/// win/loss totals if 'SeriesGameNumber' is unexpectedly omitted from the data.
/// </summary>
int ResolveSeriesGameNumber(GameEntry e)
{
  if (e.SeriesGameNumber > 0)
    return e.SeriesGameNumber;

  int completedGames = e.AwayRecord.Wins + e.HomeRecord.Wins;
  if (completedGames <= 0)
    return 1;

  return IsFinal(e) ? completedGames : completedGames + 1;
}

/// <summary>
/// Checks if the game could eliminate a team by verifying if any team has won 3 games
/// (or 4 if the game is already final).
/// </summary>
bool IsEliminationGame(GameEntry e, int gameNumber)
{
  if (gameNumber < 4)
    return false;

  int leadingWins = Math.Max(e.AwayRecord.Wins, e.HomeRecord.Wins);
  return IsFinal(e) ? leadingWins >= 4 : leadingWins == 3;
}

/// <summary>
/// Checks common game status indicators to see if the match has concluded.
/// </summary>
bool IsFinal(GameEntry e) => e.GameStatus == 3
    || string.Equals(e.GameStatusText, "Final", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Converts the text-based game label into a distinct playoff round.
/// </summary>
PlayoffRound ParseRound(string gameLabel) => gameLabel switch
{
  var s when s.Contains("First Round") => PlayoffRound.FirstRound,
  var s when s.Contains("Semifinals") => PlayoffRound.ConferenceSemifinals,
  var s when s.Contains("Conference Finals") || s.Contains("Conf. Finals") => PlayoffRound.ConferenceFinals,
  var s when s.Contains("Finals") => PlayoffRound.Finals,
  _ => PlayoffRound.RegularSeason,
};

/// <summary>
/// Assigns the broadcast length in minutes. Playoff games generally receive
/// longer broadcast blocks.
/// </summary>
int GetLength(PlayoffRound round) => round switch
{
  PlayoffRound.RegularSeason => 150,
  PlayoffRound.FirstRound or PlayoffRound.ConferenceSemifinals => 180,
  PlayoffRound.ConferenceFinals or PlayoffRound.Finals => 210,
  _ => 150,
};

/// <summary>
/// Calculate a rating out of 5 to boost discoverability for important games
/// (e.g. later rounds or tightly contested series).
/// </summary>
int GetStarRating(PlayoffRound round, bool isCompetitiveSeries)
{
  int stars = round switch
  {
    PlayoffRound.RegularSeason => 2,
    PlayoffRound.FirstRound or PlayoffRound.ConferenceSemifinals => 3,
    PlayoffRound.ConferenceFinals or PlayoffRound.Finals => 4,
    _ => 2,
  };
  return Math.Min(5, stars + (isCompetitiveSeries ? 1 : 0));
}

/// <summary>
/// Compiles categorization tags used by DVRs and players.
/// </summary>
IReadOnlyList<string> BuildCategories(string gameLabel, bool isPlayoff)
{
  List<string> cats = ["Live", "New", "Sports", "Sports event", "Basketball", "NBA", "HD"];
  if (isPlayoff) cats.Add("Playoffs");
  if (!string.IsNullOrEmpty(gameLabel)) cats.Add(gameLabel);
  return cats;
}

/// <summary>
/// Compiles search keywords like team names and arenas for XMLTV.
/// </summary>
IReadOnlyList<string> BuildKeywords(GameEntry e)
{
  List<string> kw = [e.Away.DisplayName, e.Home.DisplayName];
  if (!string.IsNullOrEmpty(e.ArenaName)) kw.Add(e.ArenaName);
  if (!string.IsNullOrEmpty(e.ArenaCity)) kw.Add(e.ArenaCity);
  if (!string.IsNullOrEmpty(e.GameLabel)) kw.Add(e.GameLabel);
  return kw;
}

/// <summary>
/// Constructs the human-readable description for the EPG detailing team records,
/// the current venue, and major stakes like elimination.
/// </summary>
string BuildDesc(GameEntry e, bool isPlayoff, bool isElimination)
{
  var sb = new StringBuilder();

  if (isElimination)
    sb.Append("Elimination game. ");

  if (!string.IsNullOrEmpty(e.GameLabel))
  {
    sb.Append(e.GameLabel);
  }
  else if (isPlayoff)
  {
    sb.Append("NBA Playoffs");
  }
  else
  {
    sb.Append("NBA");
  }

  sb.Append(". ");

  if (isPlayoff)
  {
    sb.Append(e.SeriesText);
    sb.Append(". ");
  }

  if (isPlayoff && e.AwayRecord.Seed > 0) sb.Append($"#{e.AwayRecord.Seed} ");
  sb.Append($"{e.Away.DisplayName} ({e.AwayRecord.Wins}-{e.AwayRecord.Losses})");

  sb.Append(" visit ");

  if (isPlayoff && e.HomeRecord.Seed > 0) sb.Append($"#{e.HomeRecord.Seed} ");
  sb.Append($"{e.Home.DisplayName} ({e.HomeRecord.Wins}-{e.HomeRecord.Losses})");

  if (!string.IsNullOrEmpty(e.ArenaName))
  {
    sb.Append($" at {e.ArenaName}");
    if (!string.IsNullOrEmpty(e.ArenaCity))
    {
      sb.Append($" in {e.ArenaCity}");
      if (!string.IsNullOrEmpty(e.ArenaState))
        sb.Append($", {e.ArenaState}");
    }
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
              new XElement("icon", new XAttribute("src", NbaLogoUrl)),
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

record TeamInfo(string ChannelId, string DisplayName, string LogoUrl, string Country = "US");

record TeamRecord(int Wins, int Losses, int Seed);

record GameEntry(DateTimeOffset StartUtc, TeamInfo Away, TeamInfo Home, TeamRecord AwayRecord, TeamRecord HomeRecord, string ArenaName, string ArenaCity, string ArenaState, string SeriesText, string GameLabel, int SeriesGameNumber, int GameStatus, string GameStatusText);

enum PlayoffRound { RegularSeason, FirstRound, ConferenceSemifinals, ConferenceFinals, Finals }

record ProgrammeInfo(string ChannelId, DateTimeOffset StartUtc, DateTimeOffset StopUtc, string Title, string? SubTitle, string Desc, IReadOnlyList<string> Categories, IReadOnlyList<string> Keywords, int LengthMinutes, string? EpisodeNum, int StarRating, bool IsPremiere, bool IsNew, string Country = "US");

record GameMeta(bool IsPlayoff, PlayoffRound Round, int GameNumber, bool IsElimination, bool IsPremiere);

record ScriptOptions(int DaysAhead = 14, int DaysBack = 1, string OutputPath = "output/nba.xml", string UrlOverride = "");
