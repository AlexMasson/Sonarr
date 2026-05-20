using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download
{
    public interface ILlmPrioritizationService
    {
        Task<List<DownloadDecision>> ApplyAsync(List<DownloadDecision> sorted);
    }

    public class LlmPrioritizationService : ILlmPrioritizationService
    {
        private readonly IConfigService _configService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, (int? choice, DateTime expiry)> _cache = new ConcurrentDictionary<string, (int? choice, DateTime expiry)>();

        public LlmPrioritizationService(IConfigService configService, IQualityProfileService qualityProfileService, IHttpClient httpClient, Logger logger)
        {
            _configService = configService;
            _qualityProfileService = qualityProfileService;
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> ApplyAsync(List<DownloadDecision> sorted)
        {
            var urls = SplitConfig(_configService.LlmApiUrl);

            if (urls.Count == 0 || sorted.Count <= 1)
            {
                return sorted;
            }

            // Group decisions per (series, season-set) so each LLM call has a coherent prompt:
            // - Single-episode search: Sonarr already rejects season packs upstream
            //   (SingleEpisodeSearchMatchSpecification -> FullSeason), so the group only
            //   contains single-episode releases for that one episode.
            // - Season search: individual episode releases AND the season pack are all
            //   accepted by SeasonMatchSpecification, so they land in the same group and
            //   the LLM compares pack vs splits together.
            // - Series search: one LLM call per season; each season's candidates (packs +
            //   individual eps) are ranked independently.
            // - RSS sync (mixed series/seasons): one call per (series, season). Packs and
            //   single-ep releases can coexist here too.
            // Cache keys are per-group, so an unchanged release list for one season = cache
            // hit = no API call, regardless of activity on other seasons/series.
            var groupOrder = new List<string>();
            var groups = new Dictionary<string, List<DownloadDecision>>();

            foreach (var d in sorted)
            {
                var key = BuildGroupKey(d);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<DownloadDecision>();
                    groups[key] = list;
                    groupOrder.Add(key);
                }

                list.Add(d);
            }

            var apiKeys = SplitConfig(_configService.LlmApiKey);
            var models = SplitConfig(_configService.LlmModel);
            var timeout = _configService.LlmTimeout;
            var maxTokens = _configService.LlmMaxTokens;
            var temperature = _configService.LlmTemperature;

            var result = new List<DownloadDecision>(sorted.Count);

            foreach (var key in groupOrder)
            {
                var ranked = await RankGroupAsync(groups[key], urls, apiKeys, models, timeout, maxTokens, temperature);
                result.AddRange(ranked);
            }

            return result;
        }

        private static string BuildGroupKey(DownloadDecision d)
        {
            var re = d.RemoteEpisode;
            var seriesId = re.Series?.Id ?? 0;
            // Group by distinct season numbers the release covers. A single-episode release
            // (S01E05) and a season pack (S01 COMPLETE) both map to "<id>:1" and are ranked
            // together. A full-series pack (covers S01..S05) lands in its own group "<id>:1,2,3,4,5".
            var seasons = re.Episodes != null && re.Episodes.Count > 0
                ? string.Join(",", re.Episodes.Select(e => e.SeasonNumber).Distinct().OrderBy(x => x))
                : string.Empty;
            return seriesId + ":" + seasons;
        }

        private async Task<List<DownloadDecision>> RankGroupAsync(
            List<DownloadDecision> group,
            IReadOnlyList<string> urls,
            IReadOnlyList<string> apiKeys,
            IReadOnlyList<string> models,
            int timeout,
            int maxTokens,
            double temperature)
        {
            if (group.Count <= 1)
            {
                return group;
            }

            var firstDecision = group.First();
            var series = firstDecision.RemoteEpisode.Series;

            // Union of episodes covered by ANY release in this group. Used for the prompt
            // scope header (so the LLM knows what season(s) this group spans) and for
            // de-duplicating across releases that overlap (e.g. season pack + single ep).
            var allEpisodes = group
                .SelectMany(d => d.RemoteEpisode.Episodes ?? Enumerable.Empty<Tv.Episode>())
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .ToList();

            var seasons = allEpisodes.Select(e => e.SeasonNumber).Distinct().OrderBy(x => x).ToList();
            var seasonLabel = seasons.Count == 1
                ? $"Season {seasons[0]:D2}"
                : "Seasons " + string.Join(", ", seasons.Select(s => s.ToString("D2")));
            var itemTitle = $"{series.Title} - {seasonLabel}";

            try
            {
                var systemPrompt = GetSystemPromptForProfile(series.QualityProfileId);

                if (systemPrompt.IsNullOrWhiteSpace())
                {
                    return group;
                }

                var prompt = BuildPrompt(series, allEpisodes, group);
                var cacheKey = ComputeCacheKey(systemPrompt, prompt);

                if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
                {
                    _logger.Debug("LLM cache hit for '{0}', reusing choice", itemTitle);
                    return ApplyChoice(group, cached.choice, itemTitle, fromCache: true);
                }

                _logger.Debug("LLM dispatching to {0} provider(s) for '{1}' (system prompt: {2} chars, user prompt: {3} chars)",
                    urls.Count, itemTitle, systemPrompt.Length, prompt.Length);
                _logger.Debug("LLM user prompt:\n{0}", prompt);

                var choice = await TryProvidersAsync(urls, apiKeys, models, systemPrompt, prompt, group.Count, itemTitle, timeout, maxTokens, temperature);

                if (!choice.HasValue)
                {
                    _logger.Warn("LLM all {0} provider(s) failed or returned unparseable responses for '{1}', using default priority", urls.Count, itemTitle);
                    return group;
                }

                _cache[cacheKey] = (choice, DateTime.UtcNow.AddMinutes(60));

                return ApplyChoice(group, choice, itemTitle, fromCache: false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "LLM prioritization failed for '{0}', using default priority", itemTitle);
                return group;
            }
        }

        private List<DownloadDecision> ApplyChoice(List<DownloadDecision> group, int? choice, string itemTitle, bool fromCache)
        {
            if (!choice.HasValue)
            {
                return group;
            }

            if (choice.Value == 0)
            {
                _logger.Info("LLM {0}declined all releases for '{1}', skipping", fromCache ? "(cached) " : string.Empty, itemTitle);
                return new List<DownloadDecision>();
            }

            var selected = group[choice.Value - 1];
            if (!fromCache)
            {
                _logger.Info("LLM selected release #{0} for '{1}': {2}", choice.Value, itemTitle, selected.RemoteEpisode.Release.Title);
            }

            var reordered = new List<DownloadDecision>(group.Count) { selected };
            reordered.AddRange(group.Where(d => d != selected));
            return reordered;
        }

        private async Task<int?> TryProvidersAsync(
            IReadOnlyList<string> urls,
            IReadOnlyList<string> apiKeys,
            IReadOnlyList<string> models,
            string systemPrompt,
            string userPrompt,
            int releaseCount,
            string itemTitle,
            int timeoutSec,
            int maxTokens,
            double temperature)
        {
            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                var apiKey = i < apiKeys.Count ? apiKeys[i] : string.Empty;
                var model = i < models.Count ? models[i] : string.Empty;
                var providerLabel = $"#{i + 1} ({url}, model={model})";

                try
                {
                    _logger.Debug("LLM provider {0} sending request for '{1}'", providerLabel, itemTitle);
                    var response = await CallProviderAsync(url, apiKey, model, systemPrompt, userPrompt, timeoutSec, maxTokens, temperature);
                    _logger.Debug("LLM provider {0} raw response: {1}", providerLabel, response.Content);
                    var choice = ParseChoice(response.Content, releaseCount);

                    if (choice.HasValue)
                    {
                        if (i > 0)
                        {
                            _logger.Info("LLM fallback succeeded for '{0}' with provider {1}", itemTitle, providerLabel);
                        }

                        return choice;
                    }

                    _logger.Warn("LLM provider {0} returned unparseable response for '{1}', trying next provider", providerLabel, itemTitle);
                }
                catch (Exception ex)
                {
                    _logger.Warn("LLM provider {0} failed for '{1}': {2} ({3}). Trying next provider.", providerLabel, itemTitle, ex.GetType().Name, ex.Message);
                }
            }

            return null;
        }

        private Task<HttpResponse> CallProviderAsync(
            string url,
            string apiKey,
            string model,
            string systemPrompt,
            string userPrompt,
            int timeoutSec,
            int maxTokens,
            double temperature)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                ["temperature"] = temperature,
                ["response_format"] = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "release_choice",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new { choice = new { type = "integer" } },
                            required = new[] { "choice" },
                            additionalProperties = false
                        }
                    }
                }
            };

            if (maxTokens > 0)
            {
                payload["max_tokens"] = maxTokens;
            }

            var request = new HttpRequestBuilder($"{url.TrimEnd('/')}/chat/completions")
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = HttpMethod.Post;
            request.Headers.ContentType = "application/json";

            if (apiKey.IsNotNullOrWhiteSpace())
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            request.SetContent(payload.ToJson());
            request.RequestTimeout = TimeSpan.FromSeconds(timeoutSec);

            return ExecuteWithRetryAsync(request);
        }

        private static List<string> SplitConfig(string raw)
        {
            if (raw.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            return raw.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        private async Task<HttpResponse> ExecuteWithRetryAsync(HttpRequest request)
        {
            // 3 attempts total (initial + 2 retries) with exponential backoff: 1s, 2s.
            // Retries are limited to transient failures: 429, 404, 5xx, network errors, per-request timeouts.
            // Per-attempt timeout is request.RequestTimeout (LlmTimeout); we do not enforce a global deadline.
            const int maxAttempts = 3;
            var backoffs = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await _httpClient.ExecuteAsync(request);
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex, out var retryAfter))
                {
                    var delay = retryAfter ?? backoffs[attempt - 1];
                    if (delay > TimeSpan.FromSeconds(10))
                    {
                        _logger.Warn("LLM transient error '{0}' but Retry-After {1:F1}s > 10s, giving up", ex.GetType().Name, delay.TotalSeconds);
                        throw;
                    }

                    _logger.Warn("LLM transient error '{0}' on attempt {1}/{2}, retrying in {3:F1}s", ex.GetType().Name, attempt, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay);
                }
            }
        }

        private static bool IsTransient(Exception ex, out TimeSpan? retryAfter)
        {
            retryAfter = null;

            switch (ex)
            {
                case TooManyRequestsException tooMany:
                    retryAfter = tooMany.RetryAfter > TimeSpan.Zero ? (TimeSpan?)tooMany.RetryAfter : null;
                    return true;
                case HttpException http when http.Response != null:
                    var code = (int)http.Response.StatusCode;
                    return code == 404 || code == 408 || code == 425 || (code >= 500 && code <= 599);
                case HttpRequestException _:
                case TaskCanceledException _:
                case OperationCanceledException _:
                case IOException _:
                    return true;
                default:
                    return false;
            }
        }

        private static string ComputeCacheKey(string systemPrompt, string userPrompt)
        {
            var raw = systemPrompt + "|" + userPrompt;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }

        private string GetSystemPromptForProfile(int qualityProfileId)
        {
            try
            {
                var profile = _qualityProfileService.Get(qualityProfileId);
                var profileName = profile?.Name;

                if (profileName.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var safeName = Regex.Replace(profileName, @"[^\w\-]", "_");
                var promptPath = Path.Combine("/config", "llm-prompts", safeName + ".txt");

                if (!File.Exists(promptPath))
                {
                    _logger.Debug("No LLM prompt file for profile '{0}' (looked for {1}), skipping LLM", profileName, promptPath);
                    return null;
                }

                var content = File.ReadAllText(promptPath).Trim();
                _logger.Debug("Loaded LLM prompt for profile '{0}' from {1}", profileName, promptPath);
                return content;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to load LLM prompt for profile {0}", qualityProfileId);
                return null;
            }
        }

        private string BuildPrompt(Tv.Series series, List<Tv.Episode> episodes, List<DownloadDecision> decisions)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Series: {series.Title}");

            var seasonNums = episodes.Select(e => e.SeasonNumber).Distinct().OrderBy(x => x).ToList();
            var scope = seasonNums.Count == 1
                ? $"Season {seasonNums[0]:D2}"
                : "Seasons " + string.Join(", ", seasonNums.Select(s => s.ToString("D2")));
            sb.AppendLine($"Scope: {scope} ({episodes.Count} episode(s) in scope)");
            sb.AppendLine();
            sb.AppendLine("Releases (sorted by Sonarr priority, #1 is top pick). Each release may cover a single episode, a multi-episode set, a season pack, or a full-series pack:");
            sb.AppendLine();

            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                var remote = d.RemoteEpisode;
                var release = remote.Release;
                var covers = FormatCoverage(remote.Episodes);
                var quality = remote.ParsedEpisodeInfo?.Quality?.Quality?.Name ?? "Unknown";
                var sizeGb = release.Size > 0 ? (release.Size / 1073741824.0).ToString("F2") : "?";
                var seeders = TorrentInfo.GetSeeders(release)?.ToString() ?? "N/A";
                var customFormatScore = remote.CustomFormatScore;
                var customFormats = remote.CustomFormats?.Any() == true
                    ? string.Join(", ", remote.CustomFormats.Select(cf => cf.Name))
                    : "None";
                var languages = remote.Languages?.Any() == true
                    ? string.Join(", ", remote.Languages.Select(l => l.Name))
                    : "Unknown";
                var indexerFlags = release.IndexerFlags != 0 ? release.IndexerFlags.ToString() : "None";
                var ageMinutes = release.AgeMinutes.ToString("F0");

                sb.AppendLine($"#{i + 1}: {release.Title}");
                sb.AppendLine($"  Covers: {covers}");
                sb.AppendLine($"  Quality: {quality} | Size: {sizeGb} GB | Seeders: {seeders} | Score: {customFormatScore}");
                sb.AppendLine($"  Formats: {customFormats} | Languages: {languages}");
                sb.AppendLine($"  Flags: {indexerFlags} | Age: {ageMinutes} min");
            }

            return sb.ToString();
        }

        private static string FormatCoverage(List<Tv.Episode> episodes)
        {
            if (episodes == null || episodes.Count == 0)
            {
                return "Unknown";
            }

            if (episodes.Count == 1)
            {
                var e = episodes[0];
                return $"S{e.SeasonNumber:D2}E{e.EpisodeNumber:D2} ({e.Title})";
            }

            // Multi-episode: collapse per season into Sxx E01-E10 form.
            var bySeason = episodes
                .GroupBy(e => e.SeasonNumber)
                .OrderBy(g => g.Key);

            var parts = bySeason.Select(g =>
            {
                var nums = g.Select(e => e.EpisodeNumber).OrderBy(n => n).ToList();
                var first = nums.First();
                var last = nums.Last();
                var contiguous = nums.Count == (last - first + 1);
                if (contiguous && nums.Count > 1)
                {
                    return $"S{g.Key:D2}E{first:D2}-E{last:D2} ({nums.Count} eps)";
                }
                return $"S{g.Key:D2} {string.Join(",", nums.Select(n => $"E{n:D2}"))}";
            });

            return string.Join("; ", parts);
        }

        private int? ParseChoice(string content, int count)
        {
            try
            {
                var response = Json.Deserialize<LlmChatResponse>(content);
                var message = response?.Choices?.FirstOrDefault()?.Message?.Content;

                if (message.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var choiceResponse = Json.Deserialize<LlmChoiceResult>(message);

                if (choiceResponse?.Choice == 0)
                {
                    return 0; // LLM explicitly requests no download / no upgrade
                }

                if (choiceResponse?.Choice >= 1 && choiceResponse.Choice <= count)
                {
                    return choiceResponse.Choice;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    public class LlmChatResponse
    {
        public List<LlmChatChoice> Choices { get; set; }
    }

    public class LlmChatChoice
    {
        public LlmChatMessage Message { get; set; }
    }

    public class LlmChatMessage
    {
        public string Content { get; set; }
    }

    public class LlmChoiceResult
    {
        public int Choice { get; set; }
    }
}
