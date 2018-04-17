using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AppVeyor
{
    public class AppVeyorClient
    {
        public string ApiKey { get; set; }
        public HttpClient HttpClient { get; set; } = new HttpClient();
        public string Endpoint { get; set; } = "https://ci.appveyor.com/api";


        public async Task<Project> GetProjectByNameAsync(string name)
        {
            var projects = await GetProjectsAsync();
            return projects.Where(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        }

        public async Task<Project[]> GetProjectsAsync()
        {
            var url = "/projects";
            var list = await SendAsync<Project[]>(HttpMethod.Get, url);
            return list;
        }

        internal async Task<T> SendAsync<T>(HttpMethod method, string url)
        {
            var fullUrl = this.Endpoint + "/" + url;
            HttpRequestMessage request = new HttpRequestMessage(method, fullUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", this.ApiKey);

            var response = await this.HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Boo: " + url);
            }

            string json = await response.Content.ReadAsStringAsync();
            T result = JsonConvert.DeserializeObject<T>(json);
            return result;
        }

        public async Task<HistoryResponse> GetBuildHistoryAsync(Project project, string branch, int? startBuildId = null)
        {
            // GET /api/projects/appsvc/azure-webjobs-sdk-script-y8o14/history?recordsNumber=10&startbuildId=9817916
            var url = $"/projects/{project.accountName}/{project.slug}/history?recordsNumber=50&branch={branch}";
            if (startBuildId != null)
            {
                url += "&startbuildId=" + startBuildId;
            }

            var list = await SendAsync<HistoryResponse>(HttpMethod.Get, url);
            return list;
        }

        public async Task<Job[]> GetJobInfoAsync(Project project, string buildVersion)
        {
            // GET /api/projects/appsvc/azure-webjobs-sdk-script-y8o14/build/1.0.11033-sshumfpu
            var url = $"/projects/{project.accountName}/{project.slug}/build/{buildVersion}";

            var list = await SendAsync<BuildDetailsReponse>(HttpMethod.Get, url);
            return list.build.jobs;
        }

        public async Task<JobTestResults> GetTestResultsAsync(Job job)
        {
            //  GET https://ci.appveyor.com/api/buildjobs/a6od8bwe9rg0oy0i/tests HTTP/1.1
            var url = $"/buildjobs/{job.jobId}/tests";

            var list = await SendAsync<JobTestResults>(HttpMethod.Get, url);
            return list;
        }
    }

    public class HistoryResponse
    {
        public Project project { get; set; }
        public Build[] builds { get; set; }
    }

    public class BuildDetailsReponse
    {
        public Project project { get; set; }
        public Build build { get; set; }
    }

    public class Build
    {
        public string authorName { get; set; }
        public string branch { get; set; }
        public int buildId { get; set; }
        public int buildNumber { get; set; }
        public BuildStatus status { get; set; } // "failed"
        public string version { get; set; } // "1.0.11033-sshumfpu"
        public string pullRequestId { get; set; }
        public DateTimeOffset finished { get; set; }
        public Job[] jobs { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum Status
    {
        passed,
        failed,
        running,
        skipped
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BuildStatus
    {
        success,
        failed,
        queued,
        running,
        cancelled,
    }

    public class Job
    {
        public int failedTestsCount { get; set; }
        public string jobId { get; set; }
        public string status { get; set; } // Failed
        public int testsCount { get; set; }
    }

    public class JobTestResults
    {
        public int failed { get; set; }
        public int passed { get; set; }
        public int total { get; set; }

        public Entry[] list { get; set; }

        public class Entry
        {
            public string fileName { get; set; }
            public string name { get; set; }
            public Status outcome { get; set; } // "passed"
            public int duration { get; set; }
            public DateTimeOffset created { get; set; }
        }

        // These are added by us to help with logging
        public string BuildNumber { get; set; }
    }



    public class Project
    {
        public string accountId { get; set; }
        public string accountName { get; set; }

        public string name { get; set; }


        public string projectId { get; set; }
        public string slug { get; set; } // "azure-webjobs-sdk-script-y8o14"

        public string repositoryName { get; set; } // "Azure/azure-webjobs-sdk-script"      
        public string repositoryBranch { get; set; } // "dev"      


        public override string ToString()
        {
            return this.name;
        }
    }
}
