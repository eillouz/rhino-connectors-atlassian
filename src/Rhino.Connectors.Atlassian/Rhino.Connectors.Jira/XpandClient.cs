﻿/*
 * CHANGE LOG - keep only last 5 threads
 * 
 * TODO: Factor HTTP Client
 * https://stackoverflow.com/questions/51478525/httpclient-this-instance-has-already-started-one-or-more-requests-properties-ca
 */
using Gravity.Abstraction.Logging;
using Gravity.Extensions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Rhino.Connectors.AtlassianClients;
using Rhino.Connectors.AtlassianClients.Contracts;
using Rhino.Connectors.AtlassianClients.Extensions;
using Rhino.Connectors.Xray.Cloud.Extensions;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Rhino.Connectors.Xray.Cloud
{
    internal class XpandClient
    {
        // constants
        private const StringComparison Compare = StringComparison.OrdinalIgnoreCase;
        private const string StepsFormat = "/api/internal/test/{0}/steps?startAt=0&maxResults=100";
        private const string SetsFromTestsFormat = "/api/internal/issuelinks/testset/{0}/tests?direction=inward";
        private const string PlansFromTestsFormat = "/api/internal/issuelinks/testPlan/{0}/tests?direction=inward";
        private const string PreconditionsFormat = "/api/internal/issuelinks/test/{0}/preConditions";
        private const string TestsBySetFormat = "/api/internal/issuelinks/testset/{0}/tests";
        private const string TestsByPlanFormat = "/api/internal/testplan/{0}/tests";

        // members
        private readonly ILogger logger;
        private readonly JiraClient jiraClient;

        /// <summary>
        /// Gets the JSON serialization settings used by this JiraClient.
        /// </summary>
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        /// <summary>
        /// Gets the HTTP requests media type used by this JiraClient.
        /// </summary>
        public const string MediaType = "application/json";

        #region *** Constructors      ***
        /// <summary>
        /// Creates a new instance of JiraClient.
        /// </summary>
        /// <param name="authentication">Authentication information by which to connect and fetch data from Jira.</param>
        public XpandClient(JiraAuthentication authentication)
            : this(authentication, logger: default)
        { }

        /// <summary>
        /// Creates a new instance of JiraClient.
        /// </summary>
        /// <param name="authentication">Authentication information by which to connect and fetch data from Jira.</param>
        /// <param name="logger">Logger implementation for this client.</param>
        public XpandClient(JiraAuthentication authentication, ILogger logger)
        {
            // setup
            this.logger = logger?.CreateChildLogger(loggerName: nameof(XpandClient));
            jiraClient = new JiraClient(authentication, logger);
            Authentication = jiraClient.Authentication;
        }
        #endregion

        #region *** Properties        ***
        /// <summary>
        /// Jira authentication information.
        /// </summary>
        public JiraAuthentication Authentication { get; }
        #endregion

        #region *** Get Tests         ***
        public JObject GetTestCase(string issueKey)
        {
            return DoGetTestCases(bucketSize: 1, issueKey).FirstOrDefault();
        }

        public IEnumerable<JObject> GetTestsBySets(int bucketSize, params string[] issueKeys)
        {
            return DoGetByPlanOrSet(bucketSize, TestsBySetFormat, issueKeys);
        }

        public IEnumerable<JObject> GetTestsByPlans(int bucketSize, params string[] issueKeys)
        {
            return DoGetByPlanOrSet(bucketSize, TestsByPlanFormat, issueKeys);
        }

        public IEnumerable<JObject> GetTestCases(int bucketSize, params string[] issueKeys)
        {
            return DoGetTestCases(bucketSize, issueKeys);
        }

        public IEnumerable<JObject> DoGetByPlanOrSet(int bucketSize, string endpointFormar, params string[] issueKeys)
        {
            // setup
            var testSets = jiraClient.GetIssues(bucketSize, issueKeys);

            // exit conditions
            if (!testSets.Any())
            {
                logger?.Warn("Was not able to get test cases from set/plan. Sets/Plans were not found or error occurred.");
                return Array.Empty<JObject>();
            }

            // get requests list
            var data = testSets.Select(i => (Key: $"{i["key"]}", Endpoint: string.Format(endpointFormar, $"{i["id"]}")));

            // get all tests
            var tests = new ConcurrentBag<string>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = bucketSize };
            Parallel.ForEach(data, options, d =>
            {
                var client = GetClientWithToken(d.Key);
                var response = client.GetAsync(d.Endpoint).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }
                var testsArray = JArray.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var onTests = testsArray.Select(i => $"{i["id"]}");
                tests.AddRange(onTests);
            });

            // get issue keys
            var testCases = jiraClient.GetIssues(bucketSize, tests.ToArray()).Select(i => $"{i["key"]}");

            // get test cases
            return DoGetTestCases(bucketSize, testCases.ToArray());
        }

        private IEnumerable<JObject> DoGetTestCases(int bucketSize, params string[] issueKeys)
        {
            // exit conditions
            if (issueKeys.Length == 0)
            {
                return Array.Empty<JObject>();
            }

            // setup
            var issues = jiraClient.GetIssues(bucketSize, issueKeys);
            var testCases = new ConcurrentBag<JObject>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = bucketSize };

            if (!issues.Any())
            {
                logger?.Warn("No issues of test type found.");
                return Array.Empty<JObject>();
            }

            // queue
            var queue = new ConcurrentQueue<JObject>();
            foreach (var issue in issues)
            {
                queue.Enqueue(issue);
            }

            // client
            var client = GetClientWithToken($"{issues.First()["key"]}");

            // get
            var attempts = 0;
            while (queue.Count > 0 && attempts < queue.Count * 5)
            {
                Parallel.ForEach(queue, options, _ =>
                {
                    queue.TryDequeue(out JObject issueOut);
                    var route = string.Format(StepsFormat, $"{issueOut["id"]}");
                    var response = client.GetAsync(route).GetAwaiter().GetResult();
                    var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    // validate
                    if (!response.IsSuccessStatusCode && responseBody.Contains("Authentication request has expired"))
                    {
                        queue.Enqueue(issueOut);
                        attempts++;
                        return;
                    }
                    else if (!response.IsSuccessStatusCode)
                    {
                        attempts++;
                        return;
                    }

                    // parse
                    var onIssue = JObject.Parse($"{issueOut}");
                    onIssue.Add("steps", JObject.Parse(responseBody).SelectToken("steps"));

                    // results
                    testCases.Add(onIssue);
                });

                // reset token and client
                if (queue.Count > 0)
                {
                    client?.Dispose();
                    client = GetClientWithToken($"{issues.First()["id"]}");
                }
            }

            // cleanup
            client?.Dispose();

            // results
            return testCases;
        }
        #endregion

        #region *** Get Sets          ***
        /// <summary>
        /// Get test sets list based on test case response.
        /// </summary>
        /// <param name="testCase">Test case response body.</param>
        /// <returns>Test sets list (issue ids).</returns>
        public IEnumerable<string> GetSetsByTest(JObject testCase)
        {
            // setup
            var id = $"{testCase["id"]}";
            var key = $"{testCase["key"]}";

            // get client > send request
            var client = GetClientWithToken(key);
            var endpoint = string.Format(SetsFromTestsFormat, id);
            var response = client.GetAsync(endpoint).GetAwaiter().GetResult();

            // validate
            if (!response.IsSuccessStatusCode)
            {
                logger?.Error($"Was unable to get test sets for [{key}].");
                return Array.Empty<string>();
            }

            // extract
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var responseObjt = JArray.Parse(responseBody);
            if (!responseObjt.Any())
            {
                logger?.Debug($"No tests set for test [{key}].");
                return Array.Empty<string>();
            }
            client.Dispose();
            return responseObjt.Select(i => $"{i.SelectToken("id")}");
        }
        #endregion

        #region *** Get Plans         ***
        /// <summary>
        /// Get test plans list based on test case response.
        /// </summary>
        /// <param name="testCase">Test case response body.</param>
        /// <returns>Test plans list (issue ids).</returns>
        public IEnumerable<string> GetPlansByTest(JObject testCase)
        {
            // setup
            var id = $"{testCase["id"]}";
            var key = $"{testCase["key"]}";

            // get client > send request
            var client = GetClientWithToken(key);
            var endpoint = string.Format(PlansFromTestsFormat, id);
            var response = client.GetAsync(endpoint).GetAwaiter().GetResult();

            // validate
            if (!response.IsSuccessStatusCode)
            {
                logger?.Error($"Was unable to get plans for [{key}].");
                return Array.Empty<string>();
            }

            // extract
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var responseObjt = JArray.Parse(responseBody);
            if (!responseObjt.Any())
            {
                logger?.Debug($"No test plans for test [{key}].");
                return Array.Empty<string>();
            }
            client.Dispose();
            return responseObjt.Select(i => $"{i.SelectToken("id")}");
        }
        #endregion

        #region *** Get Preconditions ***
        /// <summary>
        /// Get preconditions list based on test case response.
        /// </summary>
        /// <param name="testCase">Test case response body.</param>
        /// <returns>Preconditions list (issue ids).</returns>
        public IEnumerable<string> GetPreconditionsByTest(JObject testCase)
        {
            // setup
            var id = $"{testCase["id"]}";
            var key = $"{testCase["key"]}";

            // get client > send request
            var client = GetClientWithToken(key);
            var endpoint = string.Format(PreconditionsFormat, id);
            var response = client.GetAsync(endpoint).GetAwaiter().GetResult();

            // validate
            if (!response.IsSuccessStatusCode)
            {
                logger?.Error($"Was unable to preconditions for [{key}].");
                return Array.Empty<string>();
            }

            // extract
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var responseObjt = JArray.Parse(responseBody);
            if (!responseObjt.Any())
            {
                logger?.Debug($"No preconditions for test [{key}].");
                return Array.Empty<string>();
            }
            client.Dispose();
            return responseObjt.Select(i => $"{i.SelectToken("id")}");
        }
        #endregion

        // UTILITIES
        private string GetToken(string issueKey)
        {
            // constants
            var errorMessage =
                "Was not able to get authentication token for use [" + jiraClient.Authentication.User + "].";

            try
            {
                // get request
                var requestBody = Assembly
                    .GetExecutingAssembly()
                    .ReadEmbeddedResource("get_token.txt")
                    .Replace("[project-key]", jiraClient.Authentication.Project)
                    .Replace("[issue-key]", issueKey);

                // setup: request content
                var content = new StringContent(content: requestBody, Encoding.UTF8, MediaType);

                // get response
                var response = JiraClient.HttpClient.PostAsync("/rest/gira/1/", content).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    logger?.Fatal(errorMessage);
                    return string.Empty;
                }

                // parse out authentication token
                var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var responseObjt = JObject.Parse(responseBody);
                var options = responseObjt.SelectTokens("..options").First().ToString();

                // get token
                return JObject.Parse(options).SelectToken("contextJwt").ToString();
            }
            catch (Exception e) when (e != null)
            {
                logger?.Fatal(errorMessage, e);
                return string.Empty;
            }
        }

        private HttpClient GetClientWithToken(string issueKey)
        {
            // get token
            var token = GetToken(issueKey);

            // new client for each api cycle (since header will change)
            var client = new HttpClient
            {
                BaseAddress = new Uri("https://xray.cloud.xpand-it.com")
            };
            client.DefaultRequestHeaders.Authorization = Authentication.GetAuthenticationHeader();
            client.DefaultRequestHeaders.Add("X-acpt", token);

            // results
            return client;
        }
    }
}