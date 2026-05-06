using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface ILlmPrioritizationService
    {
        Task<List<DownloadDecision>> ApplyAsync(List<DownloadDecision> sorted);
    }

    public class LlmPrioritizationService : ILlmPrioritizationService
    {
        private readonly IConfigService _configService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public LlmPrioritizationService(IConfigService configService, IHttpClient httpClient, Logger logger)
        {
            _configService = configService;
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

                var firstDecision = sorted.First();
                var series = firstDecision.RemoteEpisode.Series;
                var episodes = firstDecision.RemoteEpisode.Episodes;
                var prompt = BuildPrompt(series, episodes, sorted);

                var payload = new
                {
                    model = model,
                    messages = new[]
                    {
                        new { role = "system", content = "You are a release selection assistant for Sonarr. Given a list of releases, pick the best one. Consider: quality, size, seeders, codec, release group reputation, language, custom format score. Return ONLY: {\"choice\": <1-based index>}" },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.0
                };

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
                var choice = ParseChoice(response.Content, sorted.Count);

                if (choice.HasValue)
                {
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
