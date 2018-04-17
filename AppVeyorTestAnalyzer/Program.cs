using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AppVeyor;

namespace AppVeyorTestAnalyzer
{
    class Program
    {
        static string root = ConfigurationManager.AppSettings["ResultRootPath"];

        static async Task Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue;
            int daysToScan = 7;
            DateTimeOffset endDate = DateTime.UtcNow;
            var client = new AppVeyorClient
            {
                ApiKey = ConfigurationManager.AppSettings["AppVeyorApiKey"]
            };

            DateTimeOffset startDate = endDate.AddDays(-daysToScan);
            await Work(client, "azure-functions-host", "v1.x", startDate, endDate);
            await Work(client, "azure-webjobs-sdk", "v2.x", startDate, endDate);
            await Work(client, "azure-functions-host", "dev", startDate, endDate);

            Console.Write("Press any key to exit...");
            Console.ReadKey();
        }

        class Stats
        {
            public int Passed;
            public int Fail;
            public List<string> BuildLinks = new List<string>();
        }

        private static string Clean(string key)
        {
            var i = key.IndexOf('(');
            if (i > 0)
            {
                key = key.Substring(0, i);
            }

            // Only return the class and method
            string[] parts = key.Split('.');
            int l = parts.Length;
            if (l >= 2)
            {
                key = $"{parts[l - 2]}.{parts[l - 1]}";
            }

            return key;
        }

        static async Task Work(AppVeyorClient client, string projectName, string branch, DateTimeOffset startDate, DateTimeOffset endDate, bool ignorePullRequests = true)
        {
            Console.Write($"Finding AppVeyor details for project '{projectName}'.");
            var projects = await client.GetProjectsAsync();
            Console.Write(".");
            var project = await client.GetProjectByNameAsync(projectName);
            Console.Write(".");
            Console.WriteLine("Done");

            int? lastBuildId = null;
            List<Build> buildsToAnalyze = new List<Build>();

            string prString = ignorePullRequests ? "non-PR" : "all";
            Console.Write($"Getting {prString} non-canceled '{project}|{branch}' builds between {startDate} and {endDate}");
            while (true)
            {
                Console.Write(".");
                var history = await client.GetBuildHistoryAsync(project, branch, lastBuildId + 1);

                if (history.builds.Length == 0)
                {
                    break;
                }

                IEnumerable<Build> builds = history.builds.Where(p => p.branch == branch && (p.status == BuildStatus.success || p.status == BuildStatus.failed) && !(ignorePullRequests && p.pullRequestId != null));

                // See if we've pulled any older than this. If so, we've got enough and we'll stop.                
                bool reachedCutoff = builds.Any(b => b.finished < startDate);

                // Only add those that are between the start and end dates
                buildsToAnalyze.AddRange(builds);

                if (reachedCutoff)
                {
                    Console.WriteLine("Done");
                    break;
                }

                lastBuildId = buildsToAnalyze.Min(p => p.buildId);
            }

            // Only analyze those that are between the start and end dates
            buildsToAnalyze = buildsToAnalyze.Where(p => p.finished > startDate && p.finished < endDate).ToList();

            Console.WriteLine($"Found {buildsToAnalyze.Count()} eligible builds.");
            List<JobTestResults> testResultsToAnalyze = new List<JobTestResults>();

            Console.Write("Pulling test results for eligible builds");
            List<Task> requestTasks = new List<Task>();
            string order = string.Empty;
            foreach (var build in buildsToAnalyze)
            {
                Task t = Task.Run(async () =>
                {
                    var jobs = await client.GetJobInfoAsync(project, build.version);
                    foreach (var job in jobs)
                    {
                        var testResults = await client.GetTestResultsAsync(job);
                        order += string.Join(Environment.NewLine, testResults.list.OrderBy(p => p.created).Select(p => $"{p.name} {p.outcome} {p.created}"));
                        order += Environment.NewLine + Environment.NewLine;


                        testResults.BuildNumber = build.version;
                        testResultsToAnalyze.Add(testResults);
                    }
                    Console.Write(".");
                });
                requestTasks.Add(t);
            }

            await Task.WhenAll(requestTasks);
            Console.WriteLine("Done");

            Dictionary<string, Stats> results = new Dictionary<string, Stats>();
            foreach (var testResults in testResultsToAnalyze)
            {
                foreach (var item in testResults.list)
                {
                    var key = item.name;
                    Stats stats;
                    results.TryGetValue(key, out stats);
                    if (stats == null)
                    {
                        stats = new Stats();
                        results[key] = stats;
                    }

                    switch (item.outcome)
                    {
                        case Status.passed:
                            stats.Passed++;
                            break;
                        case Status.failed:
                            stats.Fail++;
                            stats.BuildLinks.Add(ConstructAppVeyorTestLink(project.slug, testResults.BuildNumber));
                            break;
                    }
                }
            }

            // Print summary 
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var path = Path.Combine(root, projectName, branch);
            Directory.CreateDirectory(path);
            var outFile = Path.Combine(path, $"results_{now.ToString("yyyyddMMHHmmss")}.html");

            var pairs = results.OrderByDescending(kv => kv.Value.Fail).ToArray();

            using (var tw = new StreamWriter(outFile))
            {
                string dateFormat = "MM-dd-yy HH:mm:ss UTC";
                tw.WriteLine($"{projectName} {branch}<br/>");
                tw.WriteLine($"Start: {startDate.ToString(dateFormat)}<br/>");
                tw.WriteLine($"End:   {endDate.ToString(dateFormat)}<br/>");
                tw.WriteLine("<table>");
                tw.WriteLine("<tr><th>Name</th><th>Fail</th><th>Pass</th><th>FailLinks</th><th>Owner</th><th>Note</th></tr>");
                foreach (var kv in pairs)
                {
                    tw.Write("<tr>");

                    string linkString = string.Empty;
                    for (int i = 0; i < kv.Value.BuildLinks.Count; i++)
                    {
                        linkString += $"<a href=\"{kv.Value.BuildLinks[i]}\">{i + 1}</a> ";
                    }
                    linkString.Trim();

                    tw.WriteLine($"<td>{Clean(kv.Key)}</td><td>{kv.Value.Fail}</td><td>{kv.Value.Passed}</td><td>{linkString}</td><td></td><td></td>");
                    tw.Write("</tr>");
                }
                tw.WriteLine("</table>");
            }

            Console.WriteLine($"Summary written to {outFile}");
            Console.WriteLine($"Opening {outFile}");
            Process.Start(outFile);
            Console.WriteLine();
        }

        private static string ConstructAppVeyorTestLink(string projectSlug, string buildNumber)
        {
            return $"https://ci.appveyor.com/project/appsvc/{projectSlug}/build/{buildNumber}/tests";
        }
    }
}
