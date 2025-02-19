using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("ExtractFolderPriority function processed a request.");

            // Read and verify the request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrEmpty(requestBody))
            {
                return new BadRequestObjectResult("Request body is empty.");
            }

            SkillRequest skillRequest;
            try
            {
                skillRequest = JsonConvert.DeserializeObject<SkillRequest>(requestBody);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error deserializing request: " + ex.Message);
                return new BadRequestObjectResult("Invalid JSON format in request body.");
            }

            // Prepare the skill response object
            var response = new SkillResponse
            {
                Values = new List<SkillResponseRecord>()
            };

            // Process each record in the input
            foreach (var record in skillRequest.Values)
            {
                int folderPriority = 999; // Default value if folder isn't found or parsed

                if (record.Data != null && record.Data.TryGetValue("storage_path", out var storagePathObj))
                {
                    string storagePath = storagePathObj?.ToString();
                    if (!string.IsNullOrEmpty(storagePath))
                    {
                        try
                        {
                            var uri = new Uri(storagePath);
                            // Split the path into segments (ignoring empty entries)
                            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                            // Look for the "4142_Guides" segment
                            int index = Array.FindIndex(segments, s => s.Equals("4142_Guides", StringComparison.OrdinalIgnoreCase));
                            if (index >= 0 && index < segments.Length - 1)
                            {
                                // Get the folder name (the segment following "4142_Guides")
                                string folderName = Uri.UnescapeDataString(segments[index + 1]);
                                
                                // Instead of a full dictionary lookup, extract the first 5 characters (e.g., "41420", "41421", etc.)
                                if (folderName.Length >= 5 && folderName.StartsWith("4142"))
                                {
                                    string prefix = folderName.Substring(0, 5);
                                    // Try to parse the 5th character as a digit.
                                    if (int.TryParse(prefix.Substring(4, 1), out int digit))
                                    {
                                        // Set folderPriority to digit + 1. For example, "41420" (digit 0) becomes 1, "41421" (digit 1) becomes 2.
                                        folderPriority = digit + 1;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error processing storage_path '{storagePath}': {ex.Message}");
                        }
                    }
                }

                // Add the computed folderPriority to the response
                response.Values.Add(new SkillResponseRecord
                {
                    RecordId = record.RecordId,
                    Data = new Dictionary<string, object>
                    {
                        { "folderPriority", folderPriority }
                    }
                });
            }

            // Return the result as JSON
            return new OkObjectResult(response);
        }
    }

    // Models for the custom skill

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
        public List<SkillResponseRecord> Values { get; set; }
    }

    public class SkillResponseRecord
    {
        [JsonProperty("recordId")]
        public string RecordId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }
    }
}
