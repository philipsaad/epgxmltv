#!/usr/bin/dotnet run
#nullable enable
// EPL EPG Generator — C# File-Based App (.NET 10+)
//
// Usage:
//   dotnet run epgxmltv-epl.cs -- [options]
//
// Options:
//   --days-ahead <n>        Number of days into the future to include (default: 14)
//   --days-back <n>         Number of days in the past to include (default: 0)
//   --output <path>         Output file path for the XMLTV file (default: output/epl.xml)
//   --schedule-url <url>    Override the default EPL schedule URL
//   --no-ai                 Disable Gemini descriptions and always use the built-in template
//   --desc-cache <path>     Path to the AI description cache (default: cache/epl-descriptions.json)
//
// Environment variables (AI descriptions):
//   GEMINI_API_KEY          Google AI Studio API key. If unset, template descriptions are used.
//   GEMINI_MODEL            Gemini model id (default: gemini-3.5-flash-lite)
//   GEMINI_DELAY_MS         Delay between Gemini calls to respect free-tier rate limits (default: 4500)
//
// Examples:
//   dotnet run epgxmltv-epl.cs
//   dotnet run epgxmltv-epl.cs -- --days-ahead 7 --days-back 3
//   dotnet run epgxmltv-epl.cs -- --days-ahead 30 --output ./full-month.xml

using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Linq;

// ===========================================================================
// Constants
// ===========================================================================

const string EplLeagueLogoUrl = "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/23.png";
const string DefaultScheduleUrl = "https://site.web.api.espn.com/apis/site/v2/sports/soccer/eng.1/scoreboard";
const string UserAgent = "epgxmltv-epl/1.0 dotnet-httpclient/1.1";
const string GeneratorName = "epgxmltv-epl/1.0";

// Gemini (AI descriptions)
const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/models";
const string DefaultGeminiModel = "gemini-3.5-flash-lite";
const int DefaultGeminiDelayMs = 4500;
const int MaxDescLength = 350;
// Bump this when the prompt changes so cached descriptions are regenerated.
const string AiPromptVersion = "2";
const string AiSystemPrompt = """
  You are an engaging sports broadcast editorial copywriter crafting Electronic Program Guide (EPG) descriptions for TV viewers.
  Write a colorful, compelling 1 to 2 sentence broadcast preview (<desc>) for this matchup (under 280 characters).
  Rules:
  - Add color, drama, and personality: weave in major headlines, superstar players, key rivalries, marquee transfers or debuts, off-pitch drama, and high stakes surrounding the clubs.
  - Tone should be lively, punchy, and broadcast-ready—the kind of engaging preview seen on premium sports network guides.
  - You MUST include both full team names as provided in the facts; do not shorten them to bare nicknames.
  - Do NOT include dates, specific kickoff/tip-off times, TV channels, or generic filler like "Tune in" or "Don't miss it".
  - Plain text only: no markdown, bullet points, emojis, hashtags, or quotation marks.
  - Output ONLY the description text.
  """;

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
  [388] = new("EPL-CoventryCity.gb", "Coventry City", "https://a.espncdn.com/i/teamlogos/soccer/500/388.png"),
  [363] = new("EPL-Chelsea.gb", "Chelsea", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/363.png"),
  [384] = new("EPL-CrystalPalace.gb", "Crystal Palace", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/384.png"),
  [368] = new("EPL-Everton.gb", "Everton", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/368.png"),
  [370] = new("EPL-Fulham.gb", "Fulham", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/370.png"),
  [306] = new("EPL-HullCity.gb", "Hull City", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/306.png"),
  [373] = new("EPL-IpswichTown.gb", "Ipswich Town", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/373.png"),
  [357] = new("EPL-LeedsUnited.gb", "Leeds United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/357.png"),
  [364] = new("EPL-Liverpool.gb", "Liverpool", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/364.png"),
  [382] = new("EPL-ManchesterCity.gb", "Manchester City", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/382.png"),
  [360] = new("EPL-ManchesterUnited.gb", "Manchester United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/360.png"),
  [361] = new("EPL-NewcastleUnited.gb", "Newcastle United", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/361.png"),
  [393] = new("EPL-NottinghamForest.gb", "Nottingham Forest", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/393.png"),
  [366] = new("EPL-Sunderland.gb", "Sunderland", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/366.png"),
  [367] = new("EPL-TottenhamHotspur.gb", "Tottenham Hotspur", "https://a.espncdn.com/i/teamlogos/soccer/500-dark/367.png"),

};

// ===========================================================================
// Main Execution Flow
// ===========================================================================

if (!TryParseArgs(args, out var options))
  return 1;

var windowStart = DateTimeOffset.UtcNow.AddDays(-options.DaysBack);
var windowEnd = DateTimeOffset.UtcNow.AddDays(options.DaysAhead);

var scheduleUrls = new List<string>();
if (!string.IsNullOrEmpty(options.UrlOverride))
  scheduleUrls.Add(options.UrlOverride);
else
{
  for (var date = windowStart.Date; date <= windowEnd.Date; date = date.AddDays(1))
    scheduleUrls.Add($"{DefaultScheduleUrl}?dates={date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}&limit=1000");
}

var entries = await FetchScheduleAsync(scheduleUrls, windowStart, windowEnd);
Console.WriteLine($"Found {entries.Count} matches in the window ({options.DaysBack} back, {options.DaysAhead} ahead).");

var aiDescriptions = await GenerateAiDescriptionsAsync(entries.Select(BuildAiRequest), options);

var programmes = entries
    .SelectMany(e => DerivePair(e, aiDescriptions))
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
      case "--no-ai":
        options = options with { NoAi = true };
        break;
      case "--desc-cache" when TryReadStringArg(args, ref i, out var descCachePath):
        options = options with { DescCachePath = descCachePath };
        break;
      default:
        Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
        Console.Error.WriteLine("Usage: dotnet run epgxmltv-epl.cs -- [--days-ahead <n>] [--days-back <n>] [--output <path>] [--schedule-url <url>] [--no-ai] [--desc-cache <path>]");
        Console.Error.WriteLine("Run with no arguments for defaults (14 days ahead, 0 days back, output/epl.xml).");
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
async Task<IReadOnlyList<MatchEntry>> FetchScheduleAsync(IEnumerable<string> scheduleUrls, DateTimeOffset windowStart, DateTimeOffset windowEnd)
{
  using var http = new HttpClient();
  http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
  var events = new JsonArray();
  var eventIds = new HashSet<string>();

  foreach (var url in scheduleUrls)
  {
    Console.WriteLine($"Fetching EPL schedule from {url} ...");
    await using var stream = await http.GetStreamAsync(url);
    var doc = await JsonNode.ParseAsync(stream);
    if (doc?["events"] is not JsonArray dailyEvents)
      throw new InvalidDataException($"EPL schedule from {url} does not contain an events array.");

    foreach (var evt in dailyEvents)
    {
      if (evt == null) continue;
      var eventId = (string?)evt["id"];
      if (!string.IsNullOrEmpty(eventId) && !eventIds.Add(eventId)) continue;
      events.Add(evt.DeepClone());
    }
  }

  return ParseSchedule(new JsonObject { ["events"] = events }, windowStart, windowEnd);
}

/// <summary>
/// Iterates over the raw schedule JSON to find matches occurring within our time window.
/// Flattens the array and resolves known teams.
/// </summary>
IReadOnlyList<MatchEntry> ParseSchedule(JsonNode? doc, DateTimeOffset windowStart, DateTimeOffset windowEnd)
{
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
        EventId: (string?)evt["id"] ?? "",
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
/// Uses the AI-generated description when one is available, otherwise the template.
/// </summary>
IEnumerable<ProgrammeInfo> DerivePair(MatchEntry e, IReadOnlyDictionary<string, string> aiDescriptions)
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
  string desc = aiDescriptions.TryGetValue(e.EventId, out var aiDesc) ? aiDesc : BuildDesc(e);

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
// AI Descriptions (Google Gemini)
// ===========================================================================

/// <summary>
/// Builds the fact sheet sent to Gemini for a single match. Only data parsed from the
/// ESPN schedule is included, so the model has nothing to work from beyond these facts.
/// Records that are entirely zero (no data yet) are omitted.
/// </summary>
AiDescRequest BuildAiRequest(MatchEntry e)
{
  string FormatRecord(TeamRecord r) => $"{r.Wins} wins, {r.Draws} draws, {r.Losses} losses ({r.Wins}-{r.Draws}-{r.Losses} W-D-L)";
  bool HasRecord(TeamRecord r) => r.Wins + r.Draws + r.Losses > 0;

  var sb = new StringBuilder();
  sb.AppendLine("Sport: Soccer (association football)");
  sb.AppendLine("Competition: Premier League (England)");
  if (!string.IsNullOrEmpty(e.MatchweekLabel)) sb.AppendLine($"Round: {e.MatchweekLabel}");
  sb.AppendLine($"Home team: {e.Home.DisplayName}");
  if (HasRecord(e.HomeRecord)) sb.AppendLine($"Home team league record this season: {FormatRecord(e.HomeRecord)}");
  sb.AppendLine($"Away team: {e.Away.DisplayName}");
  if (HasRecord(e.AwayRecord)) sb.AppendLine($"Away team league record this season: {FormatRecord(e.AwayRecord)}");
  if (!string.IsNullOrEmpty(e.StadiumName))
    sb.AppendLine($"Venue: {e.StadiumName}{(string.IsNullOrEmpty(e.StadiumCity) ? "" : $", {e.StadiumCity}")}");
  if (BigSixIds.Contains(e.HomeTeamId) && BigSixIds.Contains(e.AwayTeamId))
    sb.AppendLine("Fixture note: both clubs are members of the Premier League's traditional \"Big Six\".");

  return new AiDescRequest(e.EventId, sb.ToString().TrimEnd(), [e.Home.DisplayName, e.Away.DisplayName]);
}

/// <summary>
/// Generates editorial EPG descriptions with Google Gemini, keyed by ESPN event id.
/// Descriptions are cached on disk and only regenerated when the facts, model or prompt
/// version change. Any failure (no key, quota, invalid output) leaves the event out of the
/// result so the caller falls back to the template description.
/// </summary>
async Task<Dictionary<string, string>> GenerateAiDescriptionsAsync(IEnumerable<AiDescRequest> requests, ScriptOptions options)
{
  var results = new Dictionary<string, string>();
  if (options.NoAi)
  {
    Console.WriteLine("AI descriptions disabled (--no-ai); using template descriptions.");
    return results;
  }

  var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
  if (string.IsNullOrWhiteSpace(apiKey))
  {
    Console.WriteLine("GEMINI_API_KEY is not set; using template descriptions.");
    return results;
  }

  string model = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } m ? m : DefaultGeminiModel;
  int delayMs = int.TryParse(Environment.GetEnvironmentVariable("GEMINI_DELAY_MS"), out var d) && d >= 0 ? d : DefaultGeminiDelayMs;

  var cache = await LoadDescCacheAsync(options.DescCachePath);
  var updatedCache = new JsonObject();

  using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
  http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

  bool apiAvailable = true;
  int apiCalls = 0, generated = 0, fromCache = 0, fallbacks = 0;

  Console.WriteLine($"Generating AI descriptions with {model} ...");
  foreach (var req in requests)
  {
    if (string.IsNullOrEmpty(req.EventId) || results.ContainsKey(req.EventId)) continue;
    string hash = HashKey($"{AiPromptVersion}\n{model}\n{req.Facts}");

    if (cache[req.EventId] is JsonObject hit && (string?)hit["hash"] == hash && (string?)hit["desc"] is { Length: > 0 } cachedDesc)
    {
      results[req.EventId] = cachedDesc;
      updatedCache[req.EventId] = hit.DeepClone();
      fromCache++;
      continue;
    }

    if (!apiAvailable) { fallbacks++; continue; }

    if (apiCalls++ > 0 && delayMs > 0) await Task.Delay(delayMs);
    var (raw, stopCalling) = await CallGeminiAsync(http, model, req.Facts);
    if (stopCalling) apiAvailable = false;

    var desc = CleanAiDescription(raw, req.RequiredNames);
    if (desc == null)
    {
      if (!string.IsNullOrWhiteSpace(raw))
        Console.Error.WriteLine($"  Rejected AI description for event {req.EventId}: {raw.Trim()}");
      fallbacks++;
      continue;
    }

    results[req.EventId] = desc;
    updatedCache[req.EventId] = new JsonObject
    {
      ["hash"] = hash,
      ["model"] = model,
      ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
      ["desc"] = desc,
    };
    generated++;
  }

  await SaveDescCacheAsync(updatedCache, options.DescCachePath);
  Console.WriteLine($"AI descriptions: {generated} generated, {fromCache} from cache, {fallbacks} template fallback(s).");
  return results;
}

/// <summary>
/// Calls the Gemini generateContent REST endpoint. Retries on rate limiting (429) and
/// server errors with backoff. Returns StopCalling = true when further calls in this run
/// are pointless (invalid key/model, or persistent rate limiting).
/// </summary>
async Task<(string? Text, bool StopCalling)> CallGeminiAsync(HttpClient http, string model, string facts)
{
  const int maxAttempts = 3;
  var url = $"{GeminiEndpoint}/{Uri.EscapeDataString(model)}:generateContent";
  var body = new JsonObject
  {
    ["systemInstruction"] = new JsonObject
    {
      ["parts"] = new JsonArray(new JsonObject { ["text"] = AiSystemPrompt })
    },
    ["contents"] = new JsonArray(new JsonObject
    {
      ["role"] = "user",
      ["parts"] = new JsonArray(new JsonObject { ["text"] = facts })
    }),
  }.ToJsonString();

  for (int attempt = 1; attempt <= maxAttempts; attempt++)
  {
    try
    {
      using var content = new StringContent(body, Encoding.UTF8, "application/json");
      using var resp = await http.PostAsync(url, content);
      var payload = await resp.Content.ReadAsStringAsync();

      if (resp.IsSuccessStatusCode)
        return (ExtractGeminiText(payload), false);

      int code = (int)resp.StatusCode;
      if (code == 429 || code >= 500)
      {
        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15 * attempt);
        Console.Error.WriteLine($"  Gemini HTTP {code}; retrying in {wait.TotalSeconds:N0}s (attempt {attempt}/{maxAttempts}) ...");
        await Task.Delay(wait);
        continue;
      }

      // 400/401/403/404 here almost always mean a bad API key or model id: stop for this run.
      Console.Error.WriteLine($"  Gemini HTTP {code}: {ExtractGeminiError(payload)}");
      return (null, true);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
      Console.Error.WriteLine($"  Gemini request failed: {ex.Message} (attempt {attempt}/{maxAttempts})");
    }
  }

  Console.Error.WriteLine("  Gemini unavailable; remaining matches will use template descriptions.");
  return (null, true);
}

/// <summary>
/// Extracts the concatenated (non-thought) text parts of the first Gemini candidate.
/// </summary>
string? ExtractGeminiText(string payload)
{
  try
  {
    if (JsonNode.Parse(payload)?["candidates"] is not JsonArray { Count: > 0 } candidates) return null;
    if (candidates[0]?["content"]?["parts"] is not JsonArray parts) return null;

    var sb = new StringBuilder();
    foreach (var part in parts)
    {
      if (part?["thought"] is JsonValue t && t.TryGetValue<bool>(out var isThought) && isThought) continue;
      sb.Append((string?)part?["text"]);
    }
    return sb.ToString();
  }
  catch (Exception ex) when (ex is JsonException or InvalidOperationException)
  {
    return null;
  }
}

/// <summary>
/// Extracts error.message from a Gemini error response, falling back to the raw payload.
/// </summary>
string ExtractGeminiError(string payload)
{
  try
  {
    if (JsonNode.Parse(payload)?["error"]?["message"] is JsonValue msg && msg.TryGetValue<string>(out var text))
      return text;
  }
  catch (JsonException) { }
  return payload.Length > 300 ? payload[..300] : payload;
}

/// <summary>
/// Normalizes the model output and rejects anything that is empty, too short/long,
/// or that fails to name both teams exactly (a cheap guard against off-script output).
/// </summary>
string? CleanAiDescription(string? raw, IReadOnlyList<string> requiredNames)
{
  if (string.IsNullOrWhiteSpace(raw)) return null;

  var text = Regex.Replace(raw, @"\s+", " ").Trim().Trim('"', '`', '*', '\u201C', '\u201D').Trim();
  if (text.Length < 40 || text.Length > MaxDescLength) return null;
  if (requiredNames.Any(n => !text.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;

  return text;
}

/// <summary>
/// Short, stable hash used to detect when a cached description's inputs have changed.
/// </summary>
string HashKey(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];

/// <summary>
/// Loads the description cache (event id → { hash, model, generatedUtc, desc }).
/// </summary>
async Task<JsonObject> LoadDescCacheAsync(string path)
{
  if (!File.Exists(path)) return new JsonObject();
  try
  {
    return JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ?? new JsonObject();
  }
  catch (JsonException ex)
  {
    Console.Error.WriteLine($"Ignoring unreadable description cache {path}: {ex.Message}");
    return new JsonObject();
  }
}

/// <summary>
/// Writes the description cache. Only events in the current window are kept, so the
/// file never grows beyond the schedule window.
/// </summary>
async Task SaveDescCacheAsync(JsonObject cache, string path)
{
  var dir = Path.GetDirectoryName(path);
  if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

  var json = cache.ToJsonString(new JsonSerializerOptions
  {
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  });
  await File.WriteAllTextAsync(path, json + "\n");
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

record MatchEntry(string EventId, DateTimeOffset StartUtc, TeamInfo Away, TeamInfo Home, int AwayTeamId, int HomeTeamId, TeamRecord AwayRecord, TeamRecord HomeRecord, string StadiumName, string StadiumCity, string MatchweekLabel, int MatchStatus, string MatchStatusText);

record ProgrammeInfo(string ChannelId, DateTimeOffset StartUtc, DateTimeOffset StopUtc, string Title, string? SubTitle, string Desc, IReadOnlyList<string> Categories, IReadOnlyList<string> Keywords, int LengthMinutes, string? EpisodeNum, int StarRating, bool IsPremiere, bool IsNew, string Country = "GB");

record AiDescRequest(string EventId, string Facts, IReadOnlyList<string> RequiredNames);

record ScriptOptions(int DaysAhead = 14, int DaysBack = 0, string OutputPath = "output/epl.xml", string UrlOverride = "", bool NoAi = false, string DescCachePath = "cache/epl-descriptions.json");
