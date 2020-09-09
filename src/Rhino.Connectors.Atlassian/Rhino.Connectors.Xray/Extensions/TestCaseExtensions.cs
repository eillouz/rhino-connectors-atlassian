﻿/*
 * CHANGE LOG - keep only last 5 threads
 * 
 * RESOURCES
 */
using Gravity.Extensions;
using Gravity.Services.DataContracts;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Rhino.Api.Contracts.AutomationProvider;
using Rhino.Api.Extensions;
using Rhino.Connectors.AtlassianClients;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rhino.Connectors.Xray.Extensions
{
    internal static class TestCaseExtensions
    {
        // members: constants
        private const string RavenExecutionFormat = "/rest/raven/2.0/api/testrun/?testExecIssueKey={0}&testIssueKey={1}";
        private const string RavenRunFormat = "/rest/raven/2.0/api/testrun/{0}";
        private const string RavenAttachmentFormat = "/rest/raven/2.0/api/testrun/{0}/step/{1}/attachment";

        private const StringComparison Compare = StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Set XRay runtime ids on all steps under this RhinoTestCase.
        /// </summary>
        /// <param name="testCase">RhinoTestCase on which to update runtime ids.</param>
        /// <param name="testExecutionKey">Jira test execution key by which to find runtime ids.</param>
        public static void SetRuntimeKeys(this RhinoTestCase testCase, string testExecutionKey)
        {
            // add test step id into test-case context
            var route = string.Format(RavenExecutionFormat, testExecutionKey, testCase.Key);
            var response = JiraClient.HttpClient.GetAsync(route).GetAwaiter().GetResult();

            // exit conditions
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            // setup
            var jsonToken = response.ToObject()["steps"];
            var stepsToken = JArray.Parse($"{jsonToken}");
            var stepsCount = testCase.Steps.Count();

            // apply runtime id to test-step context
            for (int i = 0; i < stepsCount; i++)
            {
                testCase.Steps.ElementAt(i).Context["runtimeid"] = stepsToken[i]["id"].ToObject<long>();
            }

            // apply test run key
            testCase.Context["testRunKey"] = testExecutionKey;
            testCase.Context["runtimeid"] = DoGetExecutionRuntime(testCase);
        }

        /// <summary>
        /// Gets the runtime id of the test execution this test belongs to.
        /// </summary>
        /// <param name="testCase">RhinoTestCase for which to get runtime id.</param>
        /// <returns>XRay runtime id of the test execution this RhinoTestCase belongs to.</returns>
        public static int GetExecutionRuntime(this RhinoTestCase testCase)
        {
            return DoGetExecutionRuntime(testCase);
        }

        /// <summary>
        /// Updates test results comment.
        /// </summary>
        /// <param name="testCase">RhinoTestCase by which to update test results.</param>
        public static void PutResultComment(this RhinoTestCase testCase, string comment)
        {
            // setup: routing
            var routing = string.Format(RavenRunFormat, testCase.Context["runtimeid"]);

            // setup: content
            var requestBody = JsonConvert.SerializeObject(new { Comment = comment }, JiraClient.JsonSettings);
            var content = new StringContent(requestBody, Encoding.UTF8, JiraClient.MediaType);

            // update
            JiraClient.HttpClient.PutAsync(routing, content).GetAwaiter().GetResult();
            Thread.Sleep(1000);
        }

        /// <summary>
        /// Gets a text structure explaining why this test failed. Can be used for comments.
        /// </summary>
        /// <param name="testCase">RhinoTestCase by from which to build text structure.</param>
        /// <returns>text structure explaining why this test failed.</returns>
        public static string GetFailComment(this RhinoTestCase testCase)
        {
            return DoGetFailComment(testCase);
        }

        #region *** Set Outcome      ***
        /// <summary>
        /// Set XRay test execution results of test case by setting steps outcome.
        /// </summary>
        /// <param name="testCase">RhinoTestCase by which to update XRay results.</param>
        /// <param name="outcome"></param>
        /// <returns>-1 if failed to update, 0 for success.</returns>
        /// <remarks>Must contain runtimeid field in the context.</remarks>
        public static int SetOutcome(this RhinoTestCase testCase, string outcome)
        {
            // get request content
            var request = new
            {
                steps = GetUpdateRequestObject(testCase, outcome)
            };
            var requestBody = JsonConvert.SerializeObject(request, JiraClient.JsonSettings);
            var content = new StringContent(requestBody, Encoding.UTF8, JiraClient.MediaType);

            // update fields
            var route = string.Format(RavenRunFormat, $"{testCase.Context["runtimeid"]}");
            var response = JiraClient.HttpClient.PutAsync(route, content).GetAwaiter().GetResult();

            // results
            if (!response.IsSuccessStatusCode)
            {
                return -1;
            }
            return 0;
        }

        private static List<object> GetUpdateRequestObject(RhinoTestCase testCase, string outcome)
        {
            // add exceptions images - if exists or relevant
            if (testCase.Context.ContainsKey(ContextEntry.OrbitResponse))
            {
                testCase.AddExceptionsScreenshot();
            }

            // collect steps
            var steps = new List<object>();
            foreach (var testStep in testCase.Steps)
            {
                steps.Add(testStep.GetUpdateRequest(outcome));
            }
            return steps;
        }
        #endregion

        #region *** Upload Evidences ***
        /// <summary>
        /// Upload evidences into an existing test execution.
        /// </summary>
        /// <param name="testCase">RhinoTestCase by and into which to upload evidences.</param>
        public static void UploadEvidences(this RhinoTestCase testCase)
        {
            DoUploadtEvidences(testCase);
        }
        #endregion

        #region *** Bug Payload      ***
        /// <summary>
        /// Creates a bug based on this RhinoTestCase.
        /// </summary>
        /// <param name="testCase">RhinoTestCase by which to create a bug.</param>
        /// <returns>Bug creation results from Jira.</returns>
        public static JObject CreateBug(this RhinoTestCase testCase, JiraClient jiraClient)
        {
            // setup
            var issueBody = GetBugRequestTemplate(testCase, jiraClient);

            // post
            var response = jiraClient.CreateIssue(issueBody);
            if(response == default)
            {
                return default;
            }

            // link to test case
            jiraClient.CreateIssueLink(linkType: "Blocks", inward: $"{response["key"]}", outward: testCase.Key);

            // add attachments
            var files = GetScreenshots(testCase).ToArray();
            jiraClient.AddAttachments($"{response["key"]}", files);

            // add to context
            testCase.Context["jiraBug"] = response;

            // results
            return response;
        }

        private static string GetBugRequestTemplate(RhinoTestCase testCase, JiraClient jiraClient)
        {
            // load JSON body
            return Assembly.GetExecutingAssembly().ReadEmbeddedResource("create_bug_for_test_jira.txt")
                .Replace("[project-key]", $"{testCase.Context["projectKey"]}")
                .Replace("[test-scenario]", testCase.Scenario)
                .Replace("[test-priority]", GetPriority(testCase, jiraClient))
                .Replace("[test-actions]", GetDescriptionMarkdown(testCase))
                .Replace("[test-environment]", GetEnvironmentMarkdown(testCase))
                .Replace("[test-id]", testCase.Key);
        }

        private static string GetPriority(RhinoTestCase testCase, JiraClient jiraClient)
        {
            // get priority token
            var priorityData = jiraClient.GetIssueTypeFields("Bug", "priority");

            // exit conditions
            if (string.IsNullOrEmpty(priorityData))
            {
                return string.Empty;
            }

            // setup
            var id = Regex.Match(input: testCase.Priority, @"\d+").Value;
            var name = Regex.Match(input: testCase.Priority, @"(?<=\d+\s+-\s+)\w+").Value;

            // extract
            var priority = JObject
                .Parse(priorityData)["allowedValues"]
                .FirstOrDefault(i => $"{i["name"]}".Equals(name, Compare) && $"{i["id"]}".Equals(id, Compare));

            // results
            return $"{priority["id"]}";
        }

        private static string GetEnvironmentMarkdown(RhinoTestCase testCase)
        {
            try
            {
                // setup
                var driverParams = (IDictionary<string, object>)testCase.Context[ContextEntry.DriverParams];

                // setup conditions
                var isWebApp = testCase.Steps.First().Command == ActionType.GoToUrl;
                var isCapabilites = driverParams.ContainsKey("capabilities");
                var isMobApp = !isWebApp
                    && isCapabilites
                    && ((IDictionary<string, object>)driverParams["capabilities"]).ContainsKey("app");

                // get application
                return isMobApp
                    ? $"{((IDictionary<string, object>)driverParams["capabilities"])["app"]}"
                    : ((ActionRule)testCase.Steps.First(i => i.Command == ActionType.GoToUrl).Context[ContextEntry.StepAction]).Argument;
            }
            catch (Exception e) when (e != null)
            {
                return string.Empty;
            }
        }

        private static string GetDescriptionMarkdown(RhinoTestCase testCase)
        {
            try
            {
                // set header
                var header =
                    "\\r\\n----\\r\\n" +
                    "*" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC*\\r\\n" +
                    "*On Iteration*: " + $"{testCase.Iteration}\\r\\n" +
                    "Bug filed on '" + testCase.Scenario + "'\\r\\n" +
                    "----\\r\\n";

                // set steps
                var steps = string.Join("\\r\\n\\r\\n", testCase.Steps.Select(GetStepMarkdown));

                // results
                return header + steps + GetPlatformMarkdown(testCase);
            }
            catch (Exception e) when (e != null)
            {
                return string.Empty;
            }
        }

        private static string GetStepMarkdown(RhinoTestStep testStep)
        {
            // setup action
            var action = "*" + testStep.Action.Replace("{", "\\\\{") + "*\\r\\n";

            // setup
            var expectedResults = Regex
                .Split(testStep.Expected, "(\r\n|\r|\n)")
                .Where(i => !string.IsNullOrEmpty(i) && !Regex.IsMatch(i, "(\r\n|\r|\n)"))
                .ToArray();

            var failedOn = testStep.Context.ContainsKey(ContextEntry.FailedOn)
                ? (IEnumerable<int>)testStep.Context[ContextEntry.FailedOn]
                : Array.Empty<int>();

            // exit conditions
            if (!failedOn.Any())
            {
                return action;
            }

            // build
            var markdown = action + "||Result||Assertion||\\r\\n";
            for (int i = 0; i < expectedResults.Length; i++)
            {
                var outcome = failedOn.Contains(i) ? "(x)" : "(/)";
                markdown += "|" + outcome + "|" + expectedResults[i].Replace("{", "\\\\{") + "|\\r\\n";
            }

            // results
            return markdown.Trim();
        }

        private static string GetPlatformMarkdown(RhinoTestCase testCase)
        {
            // setup
            var driverParams = (IDictionary<string, object>)testCase.Context[ContextEntry.DriverParams];

            // set header
            var header =
                "\\r\\n----\\r\\n" +
                "*On Platform*: " + $"{driverParams["driver"]}\\r\\n" +
                "----\\r\\n";

            // setup conditions
            var isWebApp = testCase.Steps.First().Command == ActionType.GoToUrl;
            var isCapabilites = driverParams.ContainsKey("capabilities");
            var isMobApp = !isWebApp
                && isCapabilites
                && ((IDictionary<string, object>)driverParams["capabilities"]).ContainsKey("app");

            // get application
            var application = isMobApp
                ? ((IDictionary<string, object>)driverParams["capabilities"])["app"]
                : ((ActionRule)testCase.Steps.First(i => i.Command == ActionType.GoToUrl).Context[ContextEntry.StepAction]).Argument;

            // setup environment
            var environment =
                "*Application Under Test*\\r\\n" +
                "||Name||Value||\\r\\n" +
                "|Driver|" + $"{driverParams["driver"]}" + "|\\r\\n" +
                "|Driver Server|" + $"{driverParams["driverBinaries"]}".Replace(@"\", @"\\") + "|\\r\\n" +
                "|Application|" + application + "|\\r\\n";

            var capabilites = isCapabilites
                ? "*Capabilities*\\r\\n" + ((IDictionary<string, object>)driverParams["capabilities"]).ToXrayMarkdown() + "\\r\\n"
                : string.Empty;

            var dataSource = testCase.DataSource.Any()
                ? "*Local Data Source*\\r\\n" + testCase.DataSource.ToXrayMarkdown()
                : string.Empty;

            // results
            return (header + environment + capabilites + dataSource).Trim();
        }
        #endregion

        #region *** Bug/Test Match   ***
        /// <summary>
        /// Return true if a bug meta data match to test meta data.
        /// </summary>
        /// <param name="testCase">RhinoTestCase to match to.</param>
        /// <param name="bug">Bug JSON token to match by.</param>
        /// <returns><see cref="true"/> if match, <see cref="false"/> if not.</returns>
        public static bool IsBugMatch(this RhinoTestCase testCase, JObject bug)
        {
            // setup
            var onBug = $"{bug}";
            var driverParams = (IDictionary<string, object>)testCase.Context[ContextEntry.DriverParams];

            // build fields
            int.TryParse(Regex.Match(input: onBug, pattern: @"(?<=\WOn Iteration\W+)\d+").Value, out int iteration);
            var driver = Regex.Match(input: onBug, pattern: @"(?<=\|Driver\|)\w+(?=\|)").Value;

            // setup conditions
            var isCapabilities = AssertCapabilities(testCase, onBug);
            var isDataSource = AssertDataSource(testCase, onBug);
            var isDriver = $"{driverParams["driver"]}".Equals(driver, Compare);
            var isIteration = testCase.Iteration == iteration;

            // assert
            return isCapabilities && isDataSource && isDriver && isIteration;
        }

        private static bool AssertCapabilities(RhinoTestCase testCase, string onBug)
        {
            // setup
            var driverParams = (IDictionary<string, object>)testCase.Context[ContextEntry.DriverParams];

            // extract test capabilities
            var tstCapabilities = driverParams.ContainsKey("capabilities")
                ? ((IDictionary<string, object>)driverParams["capabilities"]).ToXrayMarkdown()
                : string.Empty;

            // normalize to markdown
            var onTstCapabilities = Regex.Split(string.IsNullOrEmpty(tstCapabilities) ? string.Empty : tstCapabilities, @"\\r\\n");
            tstCapabilities = string.Join(Environment.NewLine, onTstCapabilities);

            // extract bug capabilities
            var bugCapabilities = Regex.Match(
                input: onBug,
                pattern: @"(?<=Capabilities\W+\\r\\n\|\|).*(?=\|.*Local Data Source)|(?<=Capabilities\W+\\r\\n\|\|).*(?=\|)").Value;

            // normalize to markdown
            var onBugCapabilities = Regex.Split(string.IsNullOrEmpty(bugCapabilities) ? string.Empty : "||" + bugCapabilities + "|", @"\\r\\n");
            bugCapabilities = string.Join(Environment.NewLine, onBugCapabilities);

            // convert to data table and than to dictionary collection
            var compareableBugCapabilites = new DataTable().FromMarkDown(bugCapabilities).ToDictionary().ToJson().ToUpper().Sort();
            var compareableTstCapabilites = new DataTable().FromMarkDown(tstCapabilities).ToDictionary().ToJson().ToUpper().Sort();

            // assert
            return compareableBugCapabilites.Equals(compareableTstCapabilites, Compare);
        }

        private static bool AssertDataSource(RhinoTestCase testCase, string onBug)
        {
            // extract test capabilities
            var compareableTstData = testCase.DataSource?.Any() == true
                ? testCase.DataSource.ToJson().ToUpper().Sort()
                : string.Empty;

            // extract bug capabilities
            var bugData = Regex.Match(input: onBug, pattern: @"(?<=Local Data Source\W+\\r\\n\|\|).*(?=\|)").Value;

            // normalize to markdown
            var onBugData = Regex.Split(string.IsNullOrEmpty(bugData) ? string.Empty : "||" + bugData + "|", @"\\r\\n");
            bugData = string.Join(Environment.NewLine, onBugData);

            // convert to data table and than to dictionary collection
            var compareableBugCapabilites = new DataTable()
                .FromMarkDown(bugData)
                .ToDictionary()
                .ToJson()
                .ToUpper()
                .Sort();

            // assert
            return compareableBugCapabilites.Equals(compareableTstData, Compare);
        }
        #endregion

        // UTILITIES
        // execution runtime id
        private static int DoGetExecutionRuntime(RhinoTestCase testCase)
        {
            // exit conditions
            if (testCase == default)
            {
                return default;
            }

            // get test run from JIRA
            var routing = string.Format(RavenExecutionFormat, testCase.Context["testRunKey"], testCase.Key);
            var httpResponseMessage = JiraClient.HttpClient.GetAsync(routing).GetAwaiter().GetResult();

            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                return 0;
            }
            int.TryParse($"{httpResponseMessage.ToObject()["id"]}", out int idOut);
            return idOut;
        }

        // upload evidences
        private static void DoUploadtEvidences(this RhinoTestCase testCase)
        {
            // setup
            var run = 0;
            if (testCase.Context.ContainsKey("runtimeid"))
            {
                int.TryParse($"{testCase.Context["runtimeid"]}", out run);
            }

            // upload
            foreach (var (Id, Data) in GetEvidence(testCase))
            {
                var route = string.Format(RavenAttachmentFormat, run, Id);
                var content = new StringContent(JsonConvert.SerializeObject(Data), Encoding.UTF8, JiraClient.MediaType);
                JiraClient.HttpClient.PostAsync(route, content).GetAwaiter().GetResult();
            }
        }

        private static IEnumerable<(long Id, IDictionary<string, object> Data)> GetEvidence(RhinoTestCase testCase)
        {
            // get screenshots
            var screenshots = GetScreenshots(testCase);

            // get for step
            var evidences = new ConcurrentBag<(long, IDictionary<string, object>)>();
            foreach (var screenshot in screenshots)
            {
                // setup
                var isReference = int.TryParse(Regex.Match(screenshot, @"(?<=-)\d+(?=-)").Value, out int referenceOut);
                if (!isReference)
                {
                    continue;
                }

                // get attachment requests for test case
                var reference = GetStepReference(((WebAutomation)testCase.Context[ContextEntry.WebAutomation]).Actions, referenceOut);
                var evidence = GetEvidenceBody(screenshot);
                var runtimeid = testCase.Steps.ElementAt(reference).Context.ContainsKey("runtimeid")
                    ? (long)testCase.Steps.ElementAt(reference).Context["runtimeid"]
                    : -1;
                evidences.Add((runtimeid, evidence));
            }

            // results
            return evidences;
        }

        private static int GetStepReference(IEnumerable<ActionRule> actions, int reference)
        {
            if (actions.ElementAt(reference).ActionType == ActionType.CloseBrowser || reference < 0)
            {
                return -1;
            }
            if (actions.ElementAt(reference).ActionType != ActionType.Assert)
            {
                return reference;
            }
            return GetStepReference(actions, reference - 1);
        }

        private static IDictionary<string, object> GetEvidenceBody(string screenshot)
        {
            // standalone
            return new Dictionary<string, object>
            {
                ["filename"] = Path.GetFileName(screenshot),
                ["contentType"] = "image/png",
                ["data"] = Convert.ToBase64String(File.ReadAllBytes(screenshot))
            };
        }

        // get comments for failed tests
        private static string DoGetFailComment(RhinoTestCase testCase)
        {
            // setup
            var failedSteps = testCase.Steps.Where(i => !i.Actual).Select(i => ((JToken)i.Context["testStep"])["index"]);

            // exit conditions
            if (!failedSteps.Any())
            {
                return string.Empty;
            }

            // build
            var comment = new StringBuilder();
            comment
                .Append("{noformat}")
                .Append(DateTime.Now)
                .Append(": Test [")
                .Append(testCase.Key)
                .Append("] Failed on iteration [")
                .Append(testCase.Iteration)
                .Append("] ")
                .Append("Steps [")
                .Append(string.Join(",", failedSteps))
                .AppendLine("]")
                .AppendLine()
                .AppendLine("[Driver Parameters]")
                .AppendLine(JsonConvert.SerializeObject(testCase.Context[ContextEntry.DriverParams], JiraClient.JsonSettings))
                .AppendLine()
                .AppendLine("[Local Data Source]")
                .Append(JsonConvert.SerializeObject(testCase.DataSource, JiraClient.JsonSettings))
                .AppendLine("{noformat}");

            // return
            return comment.ToString();
        }

        private static IEnumerable<string> GetScreenshots(RhinoTestCase testCase)
        {
            return ((OrbitResponse)testCase.Context[ContextEntry.OrbitResponse])
                .OrbitRequest
                .Screenshots
                .Select(i => i.Location);
        }
    }
}