using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SearchServiceSkills
{
    public class ExtractFolderPriority
    {
        private readonly ILogger<ExtractFolderPriority> _logger;

        public ExtractFolderPriority(ILogger<ExtractFolderPriority> logger)
        {
            _logger = logger;
        }

        [Function("ExtractFolderPriority")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            string body = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogDebug("Request body: {body}", body);

            // Try to detect if this is the skill "values" wrapper or a simple object
            SkillRequest skillRequest = null;
            List<SkillRequestRecord> singleRecordWrapper = null;

            try
            {
                skillRequest = JsonConvert.DeserializeObject<SkillRequest>(body);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to deserialize into SkillRequest; will attempt other shapes.");
            }

            // If not a SkillRequest or no values, try to parse as generic JSON and create a single record
            if (skillRequest == null || skillRequest.Values == null || skillRequest.Values.Count == 0)
            {
                try
                {
                    var parsed = JObject.Parse(body);

                    // If it looks like a single record (has storagePath or metadata_storage_path), wrap it
                    var dataDict = new Dictionary<string, object>();
                    foreach (var prop in parsed.Properties())
                    {
                        // If top-level has "values", fall back to original deserialization (already tried)
                        if (string.Equals(prop.Name, "values", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        dataDict[prop.Name] = prop.Value.Type == JTokenType.String ? (object)prop.Value.ToString() : prop.Value;
                    }

                    singleRecordWrapper = new List<SkillRequestRecord>
                    {
                        new SkillRequestRecord
                        {
                            RecordId = parsed["recordId"]?.ToString() ?? "1",
                            Data = dataDict
                        }
                    };

                    skillRequest = new SkillRequest { Values = singleRecordWrapper };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Request body was not JSON or could not be parsed into a record.");
                    // respond with an error 400
                    var badResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                    await badResp.WriteStringAsync("Invalid request payload - expected skill 'values' array or single JSON object.");
                    return badResp;
                }
            }

            var response = req.CreateResponse();
            var rules = LoadRulesFromAppSettings();
            int defaultPriority = LoadDefaultPriorityFromAppSettings();

            foreach (var record in skillRequest.Values)
            {
                string path = ExtractPathFromRecord(record);
                if (string.IsNullOrEmpty(path))
                {
                    _logger.LogInformation("No path field found in record {recordId}. Using default priority {defaultPriority}.", record.RecordId, defaultPriority);
                    record.Data["priority"] = defaultPriority;
                    continue;
                }

                int priority = CalculatePriority(path, rules, defaultPriority);
                record.Data["priority"] = priority;
                _logger.LogInformation("Record {recordId} path '{path}' -> priority {priority}.", record.RecordId, path, priority);
            }

            var skillResponse = new SkillResponse { Values = skillRequest.Values };
            await response.WriteAsJsonAsync(skillResponse);
            return response;
        }

        // Try multiple common field names
        private string ExtractPathFromRecord(SkillRequestRecord record)
        {
            if (record?.Data == null) return null;

            string[] candidates = new[] { "storagePath", "metadata_storage_path", "path", "blobUri", "blobUriOriginal", "data" };

            foreach (var key in candidates)
            {
                if (record.Data.TryGetValue(key, out object val) && val != null)
                {
                    return val.ToString();
                }
            }

            // Also check if "data" contains nested objects or typical Cognitive Search shape (like "document" or "metadata" nodes)
            // Try to search recursively for a value that looks like a path (contains '/')
            foreach (var kv in record.Data)
            {
                if (kv.Value == null) continue;
                var s = kv.Value.ToString();
                if (s.Contains("/")) return s;
            }

            return null;
        }

        private Dictionary<string, int> LoadRulesFromAppSettings()
        {
            string rawRules = Environment.GetEnvironmentVariable("FolderPriorityRules");

            if (string.IsNullOrWhiteSpace(rawRules))
            {
                _logger.LogWarning("FolderPriorityRules not defined in App Settings.");
                return new Dictionary<string, int>();
            }

            // Format: "41420_:1;41421_:2;..."
            var dict = rawRules
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(rule =>
                {
                    var parts = rule.Split(':', 2, StringSplitOptions.TrimEntries);
                    string key = parts.Length > 0 ? parts[0] : string.Empty;
                    int val = 9999;
                    if (parts.Length > 1)
                        int.TryParse(parts[1], out val);
                    return new { Key = key, Value = val };
                })
                .Where(p => !string.IsNullOrEmpty(p.Key))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);

            return dict;
        }

        private int LoadDefaultPriorityFromAppSettings()
        {
            string rawDefault = Environment.GetEnvironmentVariable("DefaultFolderPriority");
            if (int.TryParse(rawDefault, out int val))
                return val;
            return 9999;
        }

        private int CalculatePriority(string path, Dictionary<string, int> rules, int defaultPriority)
        {
            if (string.IsNullOrWhiteSpace(path))
                return defaultPriority;

            // Prefer the longest matching rule key (more specific) in case of overlapping keys
            var orderedRules = rules.OrderByDescending(r => r.Key.Length);

            foreach (var rule in orderedRules)
            {
                if (path.IndexOf(rule.Key, StringComparison.OrdinalIgnoreCase) >= 0)
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
            public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        }

        public class SkillResponse
        {
            [JsonProperty("values")]
            public List<SkillRequestRecord> Values { get; set; }
        }
    }
}
