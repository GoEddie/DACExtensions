﻿//------------------------------------------------------------------------------
// <copyright company="Microsoft">
//   Copyright 2013 Microsoft
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
// </copyright>
//------------------------------------------------------------------------------

using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.CodeAnalysis;
using Microsoft.SqlServer.Dac.Extensibility;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Public.Dac.Samples.Rules.Tests
{
    /// <summary>
    /// Runs a test against the <see cref="CodeAnalysisService"/> - initializes a model, 
    /// runs analysis and then performs some verification action. This class could be extended to
    /// output a results file and compare this to a baseline.
    /// </summary>
    public class RuleTest : IDisposable
    {
        private DisposableList _trash;

        /// <summary>
        /// What type of target should the test run against? Dacpacs are not backed by scripts, so 
        /// the model generated from them will be different from a scripted model. In particular the
        /// <see cref="TSqlFragment"/>s generated by calling <see cref="SqlRuleExecutionContext.ScriptFragment"/>
        /// will be generated from the model instead of representing the script contents, or may return null if
        /// the <see cref="TSqlObject"/> is not a top-level type.
        /// </summary>
        public enum AnalysisTarget
        {
            PublicModel,
            DacpacModel
        }

        public RuleTest(IList<Tuple<string, string>> testScripts, TSqlModelOptions databaseOptions, SqlServerVersion sqlVersion)
        {
            _trash = new DisposableList();
            TestScripts = testScripts;
            DatabaseOptions = databaseOptions ?? new TSqlModelOptions();
            SqlVersion = sqlVersion;
        }

        public void Dispose()
        {
            if (_trash != null)
            {
                _trash.Dispose();
                _trash = null;
            }
        }

        /// <summary>
        /// List of tuples representing scripts and the logical source name for those scripts.
        /// </summary>
        public IList<Tuple<string,string>> TestScripts
        {
            get;
            set;
        }

        /// <summary>
        /// Update the DatabaseOptions if you wish to test with different properties, such as a different collation.
        /// </summary>
        public TSqlModelOptions DatabaseOptions
        {
            get;
            set;
        }

        /// <summary>
        /// Version to target the model at - the model will be compiled against that server version, and rules that do not
        /// support that version will be ignored
        /// </summary>
        public SqlServerVersion SqlVersion { get; set; }


        public AnalysisTarget Target
        {
            get;
            set;
        }

        public TSqlModel ModelForAnalysis
        {
            get;
            set;
        }

        public string DacpacPath
        {
            get;
            set;
        }

        protected void CreateModelUsingTestScripts()
        {
            TSqlModel scriptedModel = CreateScriptedModel();
            ModelForAnalysis = scriptedModel;
            if (Target == AnalysisTarget.DacpacModel)
            {
                ModelForAnalysis = CreateDacpacModel(scriptedModel);
                scriptedModel.Dispose();
            }

            _trash.Add(ModelForAnalysis);
        }

        private TSqlModel CreateScriptedModel()
        {
            TSqlModel model = new TSqlModel(SqlVersion, DatabaseOptions);

            AddScriptsToModel(model);

            AssertModelValid(model);

            return model;
        }

        private static void AssertModelValid(TSqlModel model)
        {
            bool breakingIssuesFound = false;
            var validationMessages = model.Validate();
            if (validationMessages.Count > 0)
            {
                Console.WriteLine("Issues found during model build:");
                foreach (var message in validationMessages)
                {
                    Console.WriteLine("\t" + message.Message);
                    breakingIssuesFound = breakingIssuesFound || message.MessageType == DacMessageType.Error;
                }
            }

            Assert.IsFalse(breakingIssuesFound, "Cannot run analysis if there are model errors");
        }

        /// <summary>
        /// Builds a dacpac and returns the path to that dacpac.
        /// If the file already exists it will be deleted
        /// </summary>
        private string BuildDacpacFromModel(TSqlModel model)
        {
            string path = DacpacPath;
            Assert.IsFalse(string.IsNullOrWhiteSpace(DacpacPath), "DacpacPath must be set if target for analysis is a Dacpac");

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string dacpacDir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dacpacDir))
            {
                Directory.CreateDirectory(dacpacDir);
            }

            DacPackageExtensions.BuildPackage(path, model, new PackageMetadata());
            return path;
        }

        /// <summary>
        /// Creates a new Dacpac file on disk and returns the model from this. If the file exists already it will be deleted.
        /// 
        /// The generated model will be automatically disposed when the ModelManager is disposed
        /// </summary>
        private TSqlModel CreateDacpacModel(TSqlModel model)
        {
            string dacpacPath = BuildDacpacFromModel(model);

            // Note: when running Code Analysis most rules expect a scripted model. Use the
            // static factory method on TSqlModel class to ensure you have scripts. If you
            // didn't do this some rules would still work as expected, some would not, and
            // a warning message would be included in the AnalysisErrors in the result.
            return TSqlModel.LoadFromDacpac(dacpacPath, 
                new ModelLoadOptions(DacSchemaModelStorageType.Memory, loadAsScriptBackedModel: true));
        }

        protected void AddScriptsToModel(TSqlModel model)
        {
            foreach (Tuple<string,string> tuple in TestScripts)
            {
                // Item1 = script, Item2 = (logicl) source file name
                model.AddOrUpdateObjects(tuple.Item1, tuple.Item2, new TSqlObjectOptions());
            }
        }
        
        /// <summary>
        /// RunTest for multiple scripts. 
        /// </summary>
        /// <param name="fullId">ID of the single rule to be run. All other rules will be disabled</param>
        /// <param name="verify">Action that runs verification on the result of analysis</param>
        public virtual void RunTest(string fullId, Action<CodeAnalysisResult, string> verify)
        {
            if (fullId == null)
            {
                throw new ArgumentNullException("fullId");
            }
            if (fullId == null)
            {
                throw new ArgumentNullException("verify");
            }

            CreateModelUsingTestScripts();

            CodeAnalysisService service = CreateCodeAnalysisService(fullId);

            RunRulesAndVerifyResult(service, verify);
        }

        /// <summary>
        /// Sets up the service and disables all rules except the rule you wish to test. 
        /// 
        /// If you want all rules to run then do not change the
        /// <see cref="CodeAnalysisRuleSettings.DisableRulesNotInSettings"/> flag, as it is set to "false" by default which
        /// ensures that all rules are run. 
        /// 
        /// To run some (but not all) of the built-in rules then you could query the 
        /// <see cref="CodeAnalysisService.GetRules"/> method to get a list of all the rules, then set their
        /// <see cref="RuleConfiguration.Enabled"/> and other flags as needed, or alternatively call the 
        /// <see cref="CodeAnalysisService.ApplyRuleSettings"/> method to apply whatever rule settings you wish
        /// 
        /// </summary>
        private CodeAnalysisService CreateCodeAnalysisService(string ruleIdToRun)
        {
            CodeAnalysisServiceFactory factory = new CodeAnalysisServiceFactory();
            var ruleSettings = new CodeAnalysisRuleSettings()
                    {
                        new RuleConfiguration(ruleIdToRun)
                    };
            ruleSettings.DisableRulesNotInSettings = true;
            CodeAnalysisService service = factory.CreateAnalysisService(this.ModelForAnalysis.Version, new CodeAnalysisServiceSettings()
            {
                RuleSettings = ruleSettings
            });

            DumpErrors(service.GetRuleLoadErrors());

            Assert.IsTrue(service.GetRules().Any((rule) => rule.RuleId.Equals(ruleIdToRun, StringComparison.OrdinalIgnoreCase)),
                "Expected rule '{0}' not found by the service", ruleIdToRun);
            return service;
        }

        private void RunRulesAndVerifyResult(CodeAnalysisService service, Action<CodeAnalysisResult, string> verify)
        {
            CodeAnalysisResult analysisResult = service.Analyze(ModelForAnalysis);

            // Only considering analysis errors for now - might want to expand to initialization and suppression errors in the future
            DumpErrors(analysisResult.AnalysisErrors);

            string problemsString = DumpProblemsToString(analysisResult.Problems);


            verify(analysisResult, problemsString);
        }

        private void DumpErrors(IList<ExtensibilityError> errors)
        {
            if (errors.Count > 0)
            {
                bool hasError = false;
                StringBuilder errorMessage = new StringBuilder();
                errorMessage.AppendLine("Errors found:");

                foreach (var error in errors)
                {
                    hasError = true;
                    if (error.Document != null)
                    {
                        errorMessage.AppendFormat("{0}({1}, {2}): ", error.Document, error.Line, error.Column);
                    }
                    errorMessage.AppendLine(error.Message);
                }

                if (hasError)
                {
                    Assert.Fail(errorMessage.ToString());
                }
            }
        }

        private string DumpProblemsToString(IEnumerable<SqlRuleProblem> problems)
        {
            DisplayServices displayServices = this.ModelForAnalysis.DisplayServices;
            List<SqlRuleProblem> problemList = new List<SqlRuleProblem>(problems);

            SortProblemsByFileName(problemList);

            StringBuilder sb = new StringBuilder();
            foreach (SqlRuleProblem problem in problemList)
            {
                AppendOneProblemItem(sb, "Problem description", problem.Description);
                AppendOneProblemItem(sb, "FullID", problem.RuleId);
                AppendOneProblemItem(sb, "Severity", problem.Severity.ToString());
                AppendOneProblemItem(sb, "Model element", displayServices.GetElementName(problem.ModelElement, ElementNameStyle.FullyQualifiedName));

                string fileName = null;
                if (problem.SourceName != null)
                {
                    FileInfo fileInfo = new FileInfo(problem.SourceName);
                    fileName = fileInfo.Name;
                }
                else
                {
                    fileName = string.Empty;
                }

                AppendOneProblemItem(sb, "Script file", fileName);
                AppendOneProblemItem(sb, "Start line", problem.StartLine.ToString());
                AppendOneProblemItem(sb, "Start column", problem.StartColumn.ToString());

                sb.Append("========end of problem========\r\n\r\n");
            }

            return sb.ToString();
        }

        private void AppendOneProblemItem(StringBuilder sb, string name, string content)
        {
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, "{0}: {1}", name, content));
        }

        public static void SortProblemsByFileName(List<SqlRuleProblem> problemList)
        {
            problemList.Sort(new ProblemComparer());

        }

        private class ProblemComparer : IComparer<SqlRuleProblem>
        {
            public int Compare(SqlRuleProblem x, SqlRuleProblem y)
            {
                Int32 compare = string.Compare(x.SourceName, y.SourceName, StringComparison.CurrentCulture);
                if (compare == 0)
                {
                    compare = x.StartLine - y.StartLine;
                    if (compare == 0)
                    {
                        compare = x.StartColumn - y.StartColumn;
                    }
                }
                return compare;
            }

        }
    }
}
