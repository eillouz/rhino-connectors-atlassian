﻿/*
 * CHANGE LOG - keep only last 5 threads
 * 
 * RESOURCES
 */
using Gravity.Abstraction.Logging;
using Gravity.Extensions;
using Gravity.Services.Comet;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Rhino.Api;
using Rhino.Api.Contracts.AutomationProvider;
using Rhino.Api.Contracts.Configuration;
using Rhino.Api.Extensions;
using Rhino.Api.Parser;
using Rhino.Connectors.AtlassianClients;
using Rhino.Connectors.Xray.Contracts;
using Rhino.Connectors.Xray.Extensions;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
#if !DEBUG
using System.Threading.Tasks;
#endif

using Utilities = Rhino.Api.Extensions.Utilities;

namespace Rhino.Connectors.Xray.Framework
{
    /// <summary>
    /// XRay connector for using XRay tests as Rhino Specs.
    /// </summary>
    public class XrayAutomationProvider : ProviderManager
    {
        // members: constants
        private const StringComparison Compare = StringComparison.OrdinalIgnoreCase;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        // state: global parameters
        private readonly ILogger logger;
        private readonly RhinoTestCaseFactory testCaseFactory;
        private readonly JiraClient jiraClient;
        private readonly int bucketSize;

        #region *** Public Constants  ***
        public const string TestPlanSchema = "com.xpandit.plugins.xray:tests-associated-with-test-plan-custom-field";
        public const string TestSetSchema = "com.xpandit.plugins.xray:test-sets-tests-custom-field";
        public const string TestCaseSchema = "com.xpandit.plugins.xray:test-sets-custom-field";
        public const string TestExecutionSchema = "com.xpandit.plugins.xray:testexec-tests-custom-field";
        public const string PreconditionSchema = "com.xpandit.plugins.xray:test-precondition-custom-field";
        public const string TestIssueType = "Test";
        public const string SetIssueType = "Test Set";
        public const string PlanIssueType = "Test Plan";
        public const string ExecutionIssueType = "Test Execution";
        #endregion

        #region *** Constructors      ***
        /// <summary>
        /// Creates a new instance of this Rhino.Api.Simulator.Framework.XrayAutomationProvider.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this provider.</param>
        public XrayAutomationProvider(RhinoConfiguration configuration)
            : this(configuration, Utilities.Types)
        { }

        /// <summary>
        /// Creates a new instance of this Rhino.Api.Simulator.Framework.XrayAutomationProvider.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this provider.</param>
        /// <param name="types">A collection of <see cref="Type"/> to load for this repository.</param>
        public XrayAutomationProvider(RhinoConfiguration configuration, IEnumerable<Type> types)
            : this(configuration, types, Utilities.CreateDefaultLogger(configuration))
        { }

        /// <summary>
        /// Creates a new instance of this Rhino.Api.Simulator.Framework.XrayAutomationProvider.
        /// </summary>
        /// <param name="configuration">Rhino.Api.Contracts.Configuration.RhinoConfiguration to use with this provider.</param>
        /// <param name="types">A collection of <see cref="Type"/> to load for this repository.</param>
        /// <param name="logger">Gravity.Abstraction.Logging.ILogger implementation for this provider.</param>
        public XrayAutomationProvider(RhinoConfiguration configuration, IEnumerable<Type> types, ILogger logger)
            : base(configuration, types, logger)
        {
            // setup
            this.logger = logger?.Setup(loggerName: nameof(XrayAutomationProvider));
            testCaseFactory = new RhinoTestCaseFactory(new Orbit(types), logger);
            jiraClient = new JiraClient(configuration.GetJiraAuthentication());
            // TODO: remove on the next Rhino.Api implementation
            TestRun = new RhinoTestRun();

            // capabilities
            bucketSize = GetBuketSize(configuration.ProviderConfiguration.Capabilities);
            PutIssueTypes(configuration.ProviderConfiguration.Capabilities);
        }
        #endregion        

        #region *** GET: Test Cases   ***
        /// <summary>
        /// Returns a list of test cases for a project.
        /// </summary>
        /// <param name="ids">A list of test ids to get test cases by.</param>
        /// <returns>A collection of Rhino.Api.Contracts.AutomationProvider.RhinoTestCase</returns>
        public override IEnumerable<RhinoTestCase> GetTestCases(params string[] ids)
        {
            // setup
            var testCases = new ConcurrentBag<RhinoTestCase>();

            // iterate - one by one on debug, parallel on production
            foreach (var issueKeys in ids.Split(bucketSize))
            {
#if DEBUG
                foreach (var key in issueKeys)
                {
                    testCases.AddRange(GetTests(key));
                }
#endif
#if !DEBUG
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = bucketSize
                };
                Parallel.ForEach(issueKeys, options, key => testCases.AddRange(GetTests(key)));
#endif
            }
            return testCases;
        }

        private IEnumerable<RhinoTestCase> GetTests(string issueKey)
        {
            // get issue type
            var issueType = jiraClient.GetIssueType(issueKey);

            // get fetching method
            var method = GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(i => i.GetCustomAttribute<DescriptionAttribute>() != null)
                .FirstOrDefault(i => i.GetCustomAttribute<DescriptionAttribute>().Description.Equals(issueType, Compare));

            // exit conditions
            if (method == default)
            {
                var message =
                    $"Tests were not loaded. Was not able to find execution method for [{issueKey}] issue type.";
                logger?.Error(message);
                return Array.Empty<RhinoTestCase>();
            }

            // invoke and return results
            return method.Invoke(this, new object[] { issueKey }) as IEnumerable<RhinoTestCase>;
        }

        // process test cases & test sets based on associated test set 
        [Description(PlanIssueType)]
        private IEnumerable<RhinoTestCase> GetByPlan(string issueKey)
        {
            // constants: logging
            const string M = "Total of [{0}] tests found under [{1}] test plan";

            // parse into JObject
            var jsonObject = jiraClient.GetIssue(issueKey);
            if (jsonObject == default)
            {
                return Array.Empty<RhinoTestCase>();
            }

            // find & validate test cases
            var customField = jiraClient.GetCustomField(TestPlanSchema);
            var onTestCases = jsonObject.SelectToken($"..{customField}");
            Logger?.DebugFormat(M, onTestCases.Count(), issueKey);

            // iterate & load tests
            var testCases = new List<RhinoTestCase>();
            foreach (var onTestCase in onTestCases)
            {
                testCases.AddRange(GetOne($"{onTestCase}"));
            }
            return testCases;
        }

        private IEnumerable<RhinoTestCase> GetOne(string issueKey)
        {
            // get issue & exit conditions
            var jObject = jiraClient.GetIssue(issueKey);
            if (jObject == default)
            {
                return Array.Empty<RhinoTestCase>();
            }

            // extract issue type
            var type = jiraClient.GetIssueType(issueKey);

            // setup conditions & exit conditions
            var isTest = type.Equals($"{Configuration.ProviderConfiguration.Capabilities[XrayCapabilities.TestType]}", Compare);
            var isTestSet = type.Equals($"{Configuration.ProviderConfiguration.Capabilities[XrayCapabilities.SetType]}", Compare);
            if (!isTest && !isTestSet)
            {
                return Array.Empty<RhinoTestCase>();
            }

            // return conditions
            return isTest ? new[] { DoGetByTest(issueKey) } : DoGetBySet(issueKey);
        }

        // process test cases based on associated test set
        [Description(SetIssueType)]
        private IEnumerable<RhinoTestCase> GetBySet(string issueKey)
        {
            return DoGetBySet(issueKey);
        }

        // process test cases based on associated test set
        [Description(ExecutionIssueType)]
        private IEnumerable<RhinoTestCase> GetByExecution(string issueKey)
        {
            // constants: logging
            const string M = "Total of [{0}] tests found under [{1}] test execution";

            // parse into JObject
            var jsonObject = jiraClient.GetIssue(issueKey);
            if (jsonObject == default)
            {
                return Array.Empty<RhinoTestCase>();
            }

            // find & validate test cases
            var customField = jiraClient.GetCustomField(TestExecutionSchema);
            var onTestCases = jsonObject.SelectToken($"..{customField}");
            Logger?.DebugFormat(M, onTestCases.Count(), issueKey);

            // parse into connector test case
            var testCases = new List<RhinoTestCase>();
            foreach (var onTestCase in onTestCases.Children())
            {
                testCases.Add(DoGetByTest($"{onTestCase["b"]}"));
            }
            Configuration.TestsRepository = testCases.Select(i => i.Key).Distinct();
            return testCases;
        }

        // process a single test
        [Description(TestIssueType)]
        private IEnumerable<RhinoTestCase> GetByTest(string issueKey)
        {
            // setup
            var testCase = DoGetByTest(issueKey);

            // results
            return new[] { testCase };
        }

        // COMMON METHODS
        private IEnumerable<RhinoTestCase> DoGetBySet(string issueKey)
        {
            // constants: logging
            const string M = "Total of [{0}] tests found under [{1}] test set";

            // parse into JObject
            var jsonObject = jiraClient.GetIssue(issueKey);
            if (jsonObject == default)
            {
                return Array.Empty<RhinoTestCase>();
            }

            // find & validate test cases
            var customField = jiraClient.GetCustomField(TestSetSchema);
            var cases = jsonObject.SelectToken($"..{customField}");
            Logger?.DebugFormat(M, cases.Count(), issueKey);

            // parse into connector test case
            var testCases = new List<RhinoTestCase>();
            foreach (var onTestCase in cases.Children())
            {
                testCases.Add(DoGetByTest($"{onTestCase}"));
            }
            return testCases;
        }

        private RhinoTestCase DoGetByTest(string issueKey)
        {
            // parse into JObject
            var jsonObject = jiraClient.GetIssue(issueKey);

            // parse into connector test case
            var test = jsonObject == default ? new RhinoTestCase { Key = "-1" } : jsonObject.ToRhinoTestCase();
            if (test.Key.Equals("-1"))
            {
                return test;
            }

            // load test set (if available - will take the )
            var customField = jiraClient.GetCustomField(TestCaseSchema);
            var testSet = jsonObject.SelectToken($"..{customField}");
            if (testSet.Any())
            {
                test.TestSuite = $"{testSet.First}";
            }

            // load data-sources (multiple preconditions data loading)
            customField = jiraClient.GetCustomField(PreconditionSchema);
            var preconditions = jsonObject.SelectToken($"..{customField}");
            if (!preconditions.Any())
            {
                return test;
            }

            // load preconditions
            var mergedDataSource = preconditions
                .Select(i => new DataTable().FromMarkDown($"{jiraClient.GetIssue($"{i}").SelectToken("fields.description")}".Trim(), default))
                .Merge();
            test.DataSource = mergedDataSource.ToDictionary().Cast<Dictionary<string, object>>().ToArray();

            // return populated test
            return test;
        }
        #endregion

        #region *** CREATE: Test Run  ***
        /// <summary>
        /// Creates an automation provider test run entity. Use this method to implement the automation
        /// provider test run creation and to modify the loaded Rhino.Api.Contracts.AutomationProvider.RhinoTestRun.
        /// </summary>
        /// <param name="testRun">Rhino.Api.Contracts.AutomationProvider.RhinoTestRun object to modify before creating.</param>
        /// <returns>Rhino.Api.Contracts.AutomationProvider.RhinoTestRun based on provided test cases.</returns>
        public override RhinoTestRun OnCreateTestRun(RhinoTestRun testRun)
        {
            // setup: request body
            var customField = jiraClient.GetCustomField(TestExecutionSchema);
            var testCases = JsonConvert.SerializeObject(testRun.TestCases.Select(i => i.Key));

            // load JSON body
            var requestBody = Assembly.GetExecutingAssembly().ReadEmbeddedResource("create_test_execution_xray.txt")
                .Replace("[project-key]", Configuration.ProviderConfiguration.Project)
                .Replace("[run-title]", TestRun.Title)
                .Replace("[custom-1]", customField)
                .Replace("[tests-repository]", testCases)
                .Replace("[assignee]", Configuration.ProviderConfiguration.User);
            var responseBody = jiraClient.CreateIssue(requestBody);

            // setup
            testRun.Key = $"{responseBody["key"]}";
            testRun.Link = $"{responseBody["self"]}";
            testRun.Context["runtimeid"] = $"{responseBody["id"]}";

            // test steps handler
            foreach (var testCase in TestRun.TestCases)
            {
                testCase.SetRuntimeKeys(testRun.Key);
            }
            return testRun;
        }

        // TODO: implement persistent retry (until all done or until timeout)        
        /// <summary>
        /// Completes automation provider test run results, if any were missed or bypassed.
        /// </summary>
        /// <param name="testRun">Rhino.Api.Contracts.AutomationProvider.RhinoTestRun results object to complete by.</param>
        public override void CompleteTestRun(RhinoTestRun testRun)
        {
            // setup: failed to update
            var inStatus = new[] { "TODO", "EXECUTING" };

            // get all test keys to re-assign outcome
            var testResults = testRun
                .GetTests()
                .Where(i => inStatus.Contains($"{i["status"]}"))
                .Select(i => $"{i["key"]}");

            // iterate
            foreach (var testCase in testRun.TestCases.Where(i => testResults.Contains(i.Key)))
            {
                DoUpdateTestResults(testCase);
            }

            // test plan
            AttachToTestPlan(testRun);
        }

        // TODO: implement raven v2.0 for assign test execution to test plan when available
        private void AttachToTestPlan(RhinoTestRun testRun)
        {
            // attach to plan (if any)            
            var tests = Configuration.TestsRepository.ToArray();
            var planType = $"{Configuration.ProviderConfiguration.Capabilities[XrayCapabilities.PlanType]}";
            var plans = jiraClient
                .GetIssues(bucketSize, issuesKeys: tests)
                .Where(i => $"{i.SelectToken("fields.issuetype.name")}".Equals(planType, Compare))
                .Select(i => $"{i["key"]}");

            // exit conditions
            if (!plans.Any())
            {
                return;
            }

            // build request
            var requests = new List<(string Endpoint, StringContent Content)>();
            const string endpointFormat = "/rest/raven/1.0/testplan/{0}/testexec";
            foreach (var plan in plans)
            {
                var palyload = new
                {
                    Assignee = Configuration.ProviderConfiguration.User,
                    Keys = new[] { testRun.Key }
                };
                var requestBody = JsonConvert.SerializeObject(palyload, JsonSettings);
                var enpoint = string.Format(endpointFormat, plan);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                requests.Add((enpoint, content));
            }

            // send
            var options = new ParallelOptions { MaxDegreeOfParallelism = bucketSize };
            Parallel.ForEach(requests, options, request
                => JiraClient.HttpClient.PostAsync(request.Endpoint, request.Content).GetAwaiter().GetResult());
        }
        #endregion

        #region *** PUT: Test Results ***
        /// <summary>
        /// Updates a single test results iteration under automation provider.
        /// </summary>
        /// <param name="testCase">Rhino.Api.Contracts.AutomationProvider.RhinoTestCase by which to update results.</param>
        public override void UpdateTestResult(RhinoTestCase testCase)
        {
            DoUpdateTestResults(testCase);
        }
        #endregion

        // CAPABILITIES
        private int GetBuketSize(IDictionary<string, object> capabilities)
        {
            // get bucket size value
            if (capabilities?.ContainsKey(ProviderCapability.BucketSize) == false)
            {
                return 15;
            }
            int.TryParse($"{capabilities[ProviderCapability.BucketSize]}", out int bucketSizeOut);

            // return final value
            return bucketSizeOut == 0 ? 15 : bucketSizeOut;
        }

        private void PutIssueTypes(IDictionary<string, object> capabilities)
        {
            if (!capabilities.ContainsKey(XrayCapabilities.TestType))
            {
                capabilities[XrayCapabilities.TestType] = "Test";
            }
            if (!capabilities.ContainsKey(XrayCapabilities.SetType))
            {
                capabilities[XrayCapabilities.SetType] = "Test Set";
            }
            if (!capabilities.ContainsKey(XrayCapabilities.PlanType))
            {
                capabilities[XrayCapabilities.PlanType] = "Test Plan";
            }
            if (!capabilities.ContainsKey(XrayCapabilities.PreconditionsType))
            {
                capabilities[XrayCapabilities.PreconditionsType] = "Pre-Condition";
            }
        }

        // UTILITIES
        private void DoUpdateTestResults(RhinoTestCase testCase)
        {
            try
            {
                // setup
                var notForUploadOutcomes = new[] { "TODO", "EXECUTING", "ABORTED" };

                // exit conditions
                var outcome = "TODO";
                if (testCase.Context.ContainsKey("outcome"))
                {
                    outcome = $"{testCase.Context["outcome"]}";
                }
                testCase.SetOutcome(outcome);

                // attachments
                if (!notForUploadOutcomes.Contains(outcome.ToUpper()))
                {
                    testCase.UploadEvidences();
                }

                // fail message
                if (outcome.Equals("FAIL", Compare) || testCase.Steps.Any(i => i.Exception != default))
                {
                    var comment = testCase.GetFailComment();
                    testCase.PutResultComment(comment);
                }
            }
            catch (Exception e) when (e != null)
            {
                logger?.Error($"Failed to update test results for [{testCase.Key}]", e);
            }
        }
    }
}