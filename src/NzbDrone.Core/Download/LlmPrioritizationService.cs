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
            var url = _configService.LlmApiUrl;

            if (url.IsNullOrWhiteSpace())
            {
                return sorted;
            }

            if (sorted.Count <= 1)
            {
                return sorted;
            }

            try
            {
                var apiKey = _configService.LlmApiKey;
                var model = _configService.LlmModel;
                var timeout = _configService.LlmTimeout;
                var maxTokens = _configService.LlmMaxTokens;
                var temperature = _configService.LlmTemperature;

                var firstDecision = sorted.First();
                var series = firstDecision.RemoteEpisode.Series;
                var episodes = firstDecision.RemoteEpisode.Episodes;

                var systemPrompt = GetSystemPromptForProfile(series.QualityProfileId);

                if (systemPrompt.IsNullOrWhiteSpace())
                {
                    return sorted;
                }

                var prompt = BuildPrompt(series, episodes, sorted);

                var cacheKey = ComputeCacheKey(model, systemPrompt, prompt);
                if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
                {
                    _logger.Debug("LLM cache hit for '{0}', reusing choice", series.Title);
                    if (!cached.choice.HasValue)
                    {
                        return sorted;
                    }

                    if (cached.choice.Value == 0)
                    {
                        _logger.Info("LLM (cached) declined all releases for '{0}', skipping download", series.Title);
                        return new List<DownloadDecision>();
                    }

                    var cachedSelected = sorted[cached.choice.Value - 1];
                    var cachedReordered = new List<DownloadDecision> { cachedSelected };
                    cachedReordered.AddRange(sorted.Where(d => d != cachedSelected));
                    return cachedReordered;
                }

                var payload = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = prompt }
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

                _logger.Debug("LLM sending request for '{0}' (profile prompt: {1} chars, user prompt: {2} chars)",
                    series.Title, systemPrompt.Length, prompt.Length);
                _logger.Debug("LLM user prompt:\n{0}", prompt);

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
                request.RequestTimeout = TimeSpan.FromSeconds(timeout);

                var response = await _httpClient.ExecuteAsync(request);
                _logger.Debug("LLM raw response: {0}", response.Content);
                var choice = ParseChoice(response.Content, sorted.Count);

                _cache[cacheKey] = (choice, DateTime.UtcNow.AddMinutes(5));

                if (choice.HasValue)
                {
                    if (choice.Value == 0)
                    {
                        _logger.Info("LLM declined all releases (choice: 0) for '{0}', skipping download", series.Title);
                        return new List<DownloadDecision>();
                    }

                    var selected = sorted[choice.Value - 1];
                    _logger.Info("LLM selected release #{0}: {1}", choice.Value, selected.RemoteEpisode.Release.Title);

                    var reordered = new List<DownloadDecision> { selected };
                    reordered.AddRange(sorted.Where(d => d != selected));
                    return reordered;
                }

                _logger.Warn("LLM response could not be parsed, using default priority");
                return sorted;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "LLM prioritization failed, using default priority");
                return sorted;
            }
        }

        private static string ComputeCacheKey(string model, string systemPrompt, string userPrompt)
        {
            var raw = model + "|" + systemPrompt + "|" + userPrompt;
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

            var episodeList = string.Join(", ", episodes.Select(e => $"S{e.SeasonNumber:D2}E{e.EpisodeNumber:D2} - {e.Title}"));
            sb.AppendLine($"Episodes: {episodeList}");
            sb.AppendLine();
            sb.AppendLine("Releases (sorted by priority, #1 is top pick):");
            sb.AppendLine();

            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                var remote = d.RemoteEpisode;
                var release = remote.Release;
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
                sb.AppendLine($"  Quality: {quality} | Size: {sizeGb} GB | Seeders: {seeders} | Score: {customFormatScore}");
                sb.AppendLine($"  Formats: {customFormats} | Languages: {languages}");
                sb.AppendLine($"  Flags: {indexerFlags} | Age: {ageMinutes} min");
            }

            return sb.ToString();
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
