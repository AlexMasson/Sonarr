using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Configuration;
using Sonarr.Http.REST;

namespace Sonarr.Api.V3.Config
{
    public class DownloadClientConfigResource : RestResource
    {
        public string DownloadClientWorkingFolders { get; set; }

        public bool EnableCompletedDownloadHandling { get; set; }
        public bool AutoRedownloadFailed { get; set; }
        public bool AutoRedownloadFailedFromInteractiveSearch { get; set; }

        // LLM Prioritization
        public string LlmApiUrl { get; set; }
        public string LlmApiKey { get; set; }
        public string LlmModel { get; set; }
        public int LlmTimeout { get; set; }
        public int LlmMaxTokens { get; set; }
        public double LlmTemperature { get; set; }

        // Derived, read-only: parsed list of providers (from CSV configs)
        // Ignored on save (no matching key in IConfigService).
        public List<LlmProviderInfo> LlmProviders { get; set; }
    }

    public class LlmProviderInfo
    {
        public int Index { get; set; }
        public string Url { get; set; }
        public string Model { get; set; }
        public bool HasApiKey { get; set; }
    }

    public static class DownloadClientConfigResourceMapper
    {
        public static DownloadClientConfigResource ToResource(IConfigService model)
        {
            return new DownloadClientConfigResource
            {
                DownloadClientWorkingFolders = model.DownloadClientWorkingFolders,

                EnableCompletedDownloadHandling = model.EnableCompletedDownloadHandling,
                AutoRedownloadFailed = model.AutoRedownloadFailed,
                AutoRedownloadFailedFromInteractiveSearch = model.AutoRedownloadFailedFromInteractiveSearch,

                // LLM Prioritization
                LlmApiUrl = model.LlmApiUrl,
                LlmApiKey = model.LlmApiKey,
                LlmModel = model.LlmModel,
                LlmTimeout = model.LlmTimeout,
                LlmMaxTokens = model.LlmMaxTokens,
                LlmTemperature = model.LlmTemperature,

                LlmProviders = BuildProviders(model.LlmApiUrl, model.LlmApiKey, model.LlmModel)
            };
        }

        private static List<LlmProviderInfo> BuildProviders(string urls, string keys, string models)
        {
            var urlList = Split(urls);
            var keyList = Split(keys);
            var modelList = Split(models);

            var list = new List<LlmProviderInfo>();
            for (var i = 0; i < urlList.Count; i++)
            {
                list.Add(new LlmProviderInfo
                {
                    Index = i + 1,
                    Url = urlList[i],
                    Model = i < modelList.Count ? modelList[i] : string.Empty,
                    HasApiKey = i < keyList.Count && !string.IsNullOrWhiteSpace(keyList[i])
                });
            }

            return list;
        }

        private static List<string> Split(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            return raw.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }
    }
}
