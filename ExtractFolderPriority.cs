using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SearchServiceSkills
{
    public class ExtractFolderPriority
    {
        private readonly ILogger<ExtractFolderPriority> _logger;

        // Constructor DI
        public ExtractFolderPriority(ILogger<ExtractFolderPriority> logger)
        {
            _logger = logger;
        }

        // ==== Function Entry Point ====
        [Function("ExtractFolderPriority")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            SkillRequest skillRequest = JsonConvert.DeserializeObject<SkillRequest>(requestBody);

            var response = req.CreateResponse();

            // Load rules from App Settings
            var rules = LoadRulesFromAppSettings();
            int defaultPriority = LoadDefaultPriorityFromAppSettings();

            foreach (var record in skillRequest.Values)
            {
                // Get input field from the skill
                string inputPath = record.Data.ContainsKey("path")
                    ? record.Data["path"]?.ToString()
                    : string.Empty;

                int priority = CalculatePriority(inputPath, rules, defaultPriority);

                record.Data["priority"] = priority;
            }

            var skillResponse = new SkillResponse { Values = skillRequest.Values };
            await response.WriteAsJsonAsync(skillResponse);

            return response;
        }

        // ==== RULE LOADING FROM APP SETTINGS ====

        private Dictionary<string, int> LoadRulesFromAppSettings()
        {
            string rawRules = Environment.GetEnvironmentVariable("FolderPriorityRules");

            if (string.IsNullOrWhiteSpace(rawRules))
            {
                _logger.LogWarning("FolderPriorityRules not defined in App Settings.");
                return new Dictionary<string, int>();
            }

            // Format: "Guides:1;Manuals:5;Reports:20"
            return rawRules
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(rule =>
                {
                    var parts = rule.Split(':', StringSplitOptions.TrimEntries);
                    // parts[0] = folder name, parts[1] = priority
                    return new
                    {
                        Key = parts[0],
                        Value = int.TryParse(parts[1], out int p) ? p : 9999
                    };
                })
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);
        }

        private int LoadDefaultPriorityFromAppSettings()
        {
            string rawDefault = Environment.GetEnvironmentVariable("DefaultPriority");

            if (int.TryParse(rawDefault, out int val))
                return val;

            return 9999; // fallback default
        }

        // ==== PRIORITY LOGIC ====

        private int CalculatePriority(
            string path,
            Dictionary<string, int> rules,
            int defaultPriority)
        {
            if (string.IsNullOrWhiteSpace(path))
                return defaultPriority;

            foreach (var rule in rules)
            {
                if (path.Contains(rule.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return rule.Value;
                }
            }

            return defaultPriority;
        }

        // ==== DATA CONTRACTS ====

        public class SkillRequest
        {
            [JsonProperty("values")]
            public List<SkillRequestRecord> Values { get; set; }
        }

        public class SkillRequestRecord
        {
            [JsonProperty("recordId")]
            public string RecordId { get; set; }

            [JsonProperty("data")]
            public Dictionary<string, object> Data { get; set; }
        }

        public class SkillResponse
        {
            [JsonProperty("values")]
            public List<SkillRequestRecord> Values { get; set; }
        }
    }
}
