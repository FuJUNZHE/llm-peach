using System;
using System.Linq;
using Peach.Core;
using Peach.Core.Dom;
using Peach.Core.Cracker;
using Peach.Core.IO;
using Peach.Pro.Core;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Peach.LLM.Core.Mutators;
using NLog;

using DM = Peach.Core.Dom.DataModel;

namespace Peach.LLM.Validations.Mutator
{
    class Program
    {
        static readonly List<Type> mutators = new List<Type>();

        static DM originalDataModel = null;

        static Dom dom = null;

        class TestInfo
        {
            public DataElement Element;
            public Type MutatorType;
            public string DataFile;
        }

        enum TestResult
        {
            Pass,
            Fail,
            Error
        }

        class TestLogInfo
        {
            public string DataFile;
            public string ElementFullName;
            public int Iteration;
            public string Message;
            public string StackTrace;
        }

        class ReplayResult
        {
            public bool Reproduced;
            public int Attempts;
            public string ExpectedStatus;
            public string DataFile;
            public string ElementFullName;
            public string MutatorName;
            public string MutatedFilePath;
            public string ReplayMessage;
            public string PittoolCommandOriginal;
            public string PittoolCommandMutated;
            public string OriginalCrackOutput;
            public string MutatedCrackOutput;
        }

        class CrackCommandResult
        {
            public string Command;
            public string Output;
        }

        class MockStrategy : MutationStrategy
        {
            public MockStrategy() : base(new Dictionary<string, Variant>())
            {
                var context = new RunContext();
                context.config = new RunConfiguration();
                Initialize(context, null);
                SeedRandom();
            }

            public override bool UsesRandomSeed => throw new NotImplementedException();

            public override bool IsDeterministic => throw new NotImplementedException();

            public override uint Count => throw new NotImplementedException();

            public override uint Iteration { get; set; } = 0;
        }

        static ConcurrentDictionary<TestInfo, List<TestResult>> testResults = new ConcurrentDictionary<TestInfo, List<TestResult>>();

        static ConcurrentDictionary<string, ConcurrentBag<TestLogInfo>> failLogs = new ConcurrentDictionary<string, ConcurrentBag<TestLogInfo>>();

        static ConcurrentDictionary<string, ConcurrentBag<TestLogInfo>> errorLogs = new ConcurrentDictionary<string, ConcurrentBag<TestLogInfo>>();

        static Dictionary<string, int> mutatorTestCounts = new Dictionary<string, int>();

        static readonly List<TestInfo> tests = new List<TestInfo>();

        static readonly Dictionary<string, ReplayResult> replayResults = new Dictionary<string, ReplayResult>();

        static readonly NLog.Logger NLogger = LogManager.GetCurrentClassLogger();

        static string pitFilePath;

        static string dataModelName;

        static string dataFileFolder;

        static void Main(string[] args)
        {
            if (args.Length != 3 && args.Length != 4)
            {
                Console.WriteLine("Usage: Peach.LLM.Validations.Mutator <pitFilePath> <dataFileFolder> <dataModelName> [<testIterations>]");
                return;
            }

            ClassLoader.Initialize("./Plugins");

            pitFilePath = args[0];
            dataFileFolder = args[1];
            dataModelName = args[2];

            if (args.Length == 4)
            {
                if (!int.TryParse(args[3], out int iterations))
                {
                    Console.WriteLine($"Invalid test iterations value: {args[3]}");
                    return;
                }
                testIterations = iterations;
            }

            FindMutators();

            Console.WriteLine($"Found {mutators.Count} mutators to test.");

            if (!Directory.Exists(dataFileFolder))
            {
                Console.WriteLine($"Data file folder does not exist: {dataFileFolder}");
                return;
            }
            var dataFiles = Directory.GetFiles(dataFileFolder);
            Console.WriteLine($"Found {dataFiles.Length} data files to test.");

            Console.WriteLine($"Testing with {Environment.ProcessorCount} cpu cores");

            foreach (var dataFile in dataFiles)
            {
                Console.WriteLine($"Testing data file: {dataFile}");
                TestSingleFile(dataFile);
                if (mutators.Count <= mutatorTestCounts.Count)
                {
                    Console.WriteLine("All mutators have reached the test limit. Ending tests.");
                    break;
                }
            }

            ShowResults();
        }

        static void TestSingleFile(string dataFilePath)
        {
            DataElement root = null;

            try
            {
                root = ParseData(pitFilePath, dataFilePath, dataModelName);
                Console.WriteLine($"Root Element Name: {root?.Name}");
                Console.WriteLine($"Data Length: {root?.Value.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to parse data: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return;
            }

            foreach (var elem in root.PreOrderTraverse())
            {
                foreach (var mutatorType in mutators)
                {
                    var supportedDataElementFn = mutatorType.GetMethod("supportedDataElement", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (supportedDataElementFn == null)
                    {
                        Console.WriteLine($"Mutator {mutatorType.FullName} is missing static method 'supportedDataElement'");
                        continue;
                    }
                    bool isSupported = (bool)supportedDataElementFn.Invoke(null, new object[] { elem });
                    if (isSupported)
                    {
                        if (!mutatorTestCounts.ContainsKey(mutatorType.FullName))
                            mutatorTestCounts[mutatorType.FullName] = 0;
                        if (mutatorTestCounts[mutatorType.FullName] >= 1)
                            continue;
                        mutatorTestCounts[mutatorType.FullName]++;
                        tests.Add(new TestInfo()
                        {
                            Element = elem,
                            MutatorType = mutatorType,
                            DataFile = dataFilePath
                        });
                    }
                }
            }

            Console.WriteLine($"Found {tests.Count} tests to run in file {dataFilePath}.");

            Test(root);

            tests.Clear();
        }

        static readonly object consoleLock = new object();

        static int testIterations = 100;

        static int replayMaxAttempts = 100;

        static void Test(DataElement root)
        {
            // Initialize test results for all tests first
            foreach (var test in tests)
            {
                testResults.TryAdd(test, new List<TestResult>());
            }

            // Run tests in parallel
            Parallel.ForEach(tests, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (test, loopState) =>
            {
                var testIndex = tests.IndexOf(test) + 1;
                var testResultList = testResults[test];

                for (int i = 0; i < testIterations; i++)
                {
                    if (i % 10 == 0)
                    {
                        lock (consoleLock)
                        {
                            WriteColored($"TESTING [{testIndex:000}/{tests.Count:000}][{i + 1:000}/{testIterations}] {test.MutatorType.Name} on {test.Element.fullName}\n", ConsoleColor.Yellow);
                        }
                    }

                    var rootClone = ObjectCopier.Clone(root) as DM;
                    rootClone.dom = dom;
                    var elem = rootClone.find(test.Element.fullName);
                    if (!(Activator.CreateInstance(test.MutatorType, new object[] { elem }) is LLMMutator mutator))
                    {
                        var mutatorName = test.MutatorType.Name;
                        var logs = errorLogs.GetOrAdd(mutatorName, _ => new ConcurrentBag<TestLogInfo>());
                        logs.Add(new TestLogInfo
                        {
                            DataFile = test.DataFile,
                            ElementFullName = elem.fullName,
                            Iteration = i + 1,
                            Message = $"Failed to create mutator of type {test.MutatorType.FullName}",
                            StackTrace = null
                        });
                        lock (testResultList)
                        {
                            testResultList.Add(TestResult.Error);
                        }
                        continue;
                    }
                    mutator.context = new MockStrategy();
                    BitwiseStream v;
                    try
                    {
                        mutator.randomMutation(elem);
                        v = rootClone.Value;
                    }
                    catch (Exception ex)
                    {
                        var mutatorName = test.MutatorType.Name;
                        var logs = errorLogs.GetOrAdd(mutatorName, _ => new ConcurrentBag<TestLogInfo>());
                        logs.Add(new TestLogInfo
                        {
                            DataFile = test.DataFile,
                            ElementFullName = elem.fullName,
                            Iteration = i + 1,
                            Message = ex.Message,
                            StackTrace = ex.StackTrace
                        });
                        lock (testResultList)
                        {
                            testResultList.Add(TestResult.Error);
                        }
                        continue;
                    }
                    var dataCracker = new DataCracker();
                    try
                    {
                        var dataModelClone = ObjectCopier.Clone(originalDataModel);
                        dataModelClone.dom = dom;
                        dataCracker.CrackData(dataModelClone, new BitStream(v));
                        lock (testResultList)
                        {
                            testResultList.Add(TestResult.Pass);
                        }
                    }
                    catch (Exception ex)
                    {
                        var mutatorName = test.MutatorType.Name;
                        var logs = failLogs.GetOrAdd(mutatorName, _ => new ConcurrentBag<TestLogInfo>());
                        logs.Add(new TestLogInfo
                        {
                            DataFile = test.DataFile,
                            ElementFullName = elem.fullName,
                            Iteration = i + 1,
                            Message = ex.Message,
                            StackTrace = ex.StackTrace
                        });
                        lock (testResultList)
                        {
                            testResultList.Add(TestResult.Fail);
                        }
                    }
                }
            });
        }

        static void ShowResults()
        {
            ReplayFailuresAndErrors();
            WriteFailureAndErrorLogs();

            Console.WriteLine("Mutator Test Results:");
            var res = testResults.GroupBy(kvp => kvp.Key.MutatorType.Name).Select(g => new
            {
                Mutator = g.Key,
                Pass = g.Sum(kvp => kvp.Value.Count(r => r == TestResult.Pass)),
                Fail = g.Sum(kvp => kvp.Value.Count(r => r == TestResult.Fail)),
                Error = g.Sum(kvp => kvp.Value.Count(r => r == TestResult.Error)),
                Total = g.Sum(kvp => kvp.Value.Count()),
                Status = g.Any(kvp => kvp.Value.Any(r => r == TestResult.Error)) ? "ERROR" : (g.All(kvp => kvp.Value.All(r => r == TestResult.Pass)) ? "PASS" : "FAIL")
            }).ToDictionary(x => x.Mutator, x => x);

            foreach (var t in mutators)
            {
                if (res.TryGetValue(t.Name, out var r))
                {
                    if (r.Status == "PASS")
                    {
                        WriteColored($"[PASS][{r.Pass:000}/{r.Total:000}]", ConsoleColor.Green);
                        Console.WriteLine($" {t.Name}");
                    }
                    else if (r.Status == "FAIL")
                    {
                        WriteColored($"[FAIL][{r.Pass:000}/{r.Total:000}]", ConsoleColor.Red);
                        Console.WriteLine($" {t.Name}");
                    }
                    else
                    {
                        WriteColored($"[ERROR]", ConsoleColor.Magenta);
                        Console.WriteLine($" {t.Name}");
                    }
                }
                else
                {
                    WriteColored($"[SKIP][000/000]", ConsoleColor.Blue);
                    Console.WriteLine($" {t.Name}");
                }
            }
        }

        static void WriteFailureAndErrorLogs()
        {
            foreach (var mutator in mutators)
            {
                var mutatorName = mutator.Name;
                var safeMutatorName = SanitizeFileName(mutatorName);
                var failFilePath = Path.Combine("fail", $"{safeMutatorName}.log");
                var errorFilePath = Path.Combine("error", $"{safeMutatorName}.log");

                if (failLogs.TryGetValue(mutatorName, out var failEntries) && failEntries.Count > 0)
                {
                    replayResults.TryGetValue(GetReplayKey(mutatorName, "FAIL"), out var failReplayResult);
                    WriteLogFileWithNLog(failFilePath, FormatLogContent(mutatorName, "FAIL", failEntries, failReplayResult));
                }
                else if (File.Exists(failFilePath))
                {
                    File.Delete(failFilePath);
                }

                if (errorLogs.TryGetValue(mutatorName, out var errorEntries) && errorEntries.Count > 0)
                {
                    replayResults.TryGetValue(GetReplayKey(mutatorName, "ERROR"), out var errorReplayResult);
                    WriteLogFileWithNLog(errorFilePath, FormatLogContent(mutatorName, "ERROR", errorEntries, errorReplayResult));
                }
                else if (File.Exists(errorFilePath))
                {
                    File.Delete(errorFilePath);
                }
            }

            LogManager.Flush();
        }

        static void WriteLogFileWithNLog(string filePath, string content)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            LogManager.Configuration.Variables["logFileName"] = filePath;
            LogManager.ReconfigExistingLoggers();

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            NLogger.Info(content);
        }

        static string FormatLogContent(string mutatorName, string status, IEnumerable<TestLogInfo> entries, ReplayResult replayResult)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Mutator: {mutatorName}");
            sb.AppendLine($"Status: {status}");
            sb.AppendLine($"Count: {entries.Count()}");
            sb.AppendLine();

            if (replayResult != null)
            {
                sb.AppendLine("Replay:");
                sb.AppendLine($"ExpectedStatus: {replayResult.ExpectedStatus}");
                sb.AppendLine($"Reproduced: {replayResult.Reproduced}");
                sb.AppendLine($"Attempts: {replayResult.Attempts}");
                sb.AppendLine($"DataFile: {replayResult.DataFile}");
                sb.AppendLine($"Element: {replayResult.ElementFullName}");
                sb.AppendLine($"Mutator: {replayResult.MutatorName}");
                if (!string.IsNullOrEmpty(replayResult.MutatedFilePath))
                {
                    sb.AppendLine($"MutatedFile: {replayResult.MutatedFilePath}");
                }
                if (!string.IsNullOrEmpty(replayResult.ReplayMessage))
                {
                    sb.AppendLine($"ReplayMessage: {replayResult.ReplayMessage}");
                }
                if (!string.IsNullOrEmpty(replayResult.PittoolCommandOriginal))
                {
                    sb.AppendLine($"OriginalCrackCommand: {replayResult.PittoolCommandOriginal}");
                }
                if (!string.IsNullOrEmpty(replayResult.OriginalCrackOutput))
                {
                    sb.AppendLine("OriginalCrackOutput:");
                    sb.AppendLine(replayResult.OriginalCrackOutput);
                }
                if (!string.IsNullOrEmpty(replayResult.PittoolCommandMutated))
                {
                    sb.AppendLine($"MutatedCrackCommand: {replayResult.PittoolCommandMutated}");
                }
                if (!string.IsNullOrEmpty(replayResult.MutatedCrackOutput))
                {
                    sb.AppendLine("MutatedCrackOutput:");
                    sb.AppendLine(replayResult.MutatedCrackOutput);
                }
                sb.AppendLine(new string('=', 80));
            }
            else
            {
                sb.AppendLine("Replay:");
                sb.AppendLine("No replay result available.");
                sb.AppendLine(new string('=', 80));
            }

            return sb.ToString();
        }

        static void ReplayFailuresAndErrors()
        {
            Console.WriteLine("Replaying failures and errors...");

            replayResults.Clear();

            foreach (var kvp in failLogs)
            {
                var sample = kvp.Value
                    .OrderBy(e => e.DataFile, StringComparer.Ordinal)
                    .ThenBy(e => e.ElementFullName, StringComparer.Ordinal)
                    .ThenBy(e => e.Iteration)
                    .FirstOrDefault();
                if (sample == null)
                    continue;

                var result = ReplaySingleIssue(kvp.Key, "FAIL", sample);
                replayResults[GetReplayKey(kvp.Key, "FAIL")] = result;
            }

            foreach (var kvp in errorLogs)
            {
                var sample = kvp.Value
                    .OrderBy(e => e.DataFile, StringComparer.Ordinal)
                    .ThenBy(e => e.ElementFullName, StringComparer.Ordinal)
                    .ThenBy(e => e.Iteration)
                    .FirstOrDefault();
                if (sample == null)
                    continue;

                var result = ReplaySingleIssue(kvp.Key, "ERROR", sample);
                replayResults[GetReplayKey(kvp.Key, "ERROR")] = result;
            }
        }

        static string GetReplayKey(string mutatorName, string status)
        {
            return string.Format("{0}|{1}", mutatorName, status);
        }

        static ReplayResult ReplaySingleIssue(string mutatorName, string expectedStatus, TestLogInfo sample)
        {
            lock (consoleLock)
            {
                WriteColored($"[REPLAY] Starting replay for {mutatorName} ({expectedStatus})\n", ConsoleColor.Cyan);
            }

            var result = new ReplayResult
            {
                Reproduced = false,
                Attempts = 0,
                ExpectedStatus = expectedStatus,
                DataFile = sample.DataFile,
                ElementFullName = sample.ElementFullName,
                MutatorName = mutatorName,
                ReplayMessage = "No replay attempts performed."
            };

            if (!File.Exists(sample.DataFile))
            {
                result.ReplayMessage = "Replay skipped: data file does not exist.";
                return result;
            }

            var mutatorType = mutators.FirstOrDefault(m => m.Name == mutatorName);
            if (mutatorType == null)
            {
                result.ReplayMessage = "Replay skipped: mutator type not found.";
                return result;
            }

            DataElement replayRoot;
            try
            {
                replayRoot = ParseData(pitFilePath, sample.DataFile, dataModelName);
            }
            catch (Exception ex)
            {
                result.ReplayMessage = string.Format("Replay skipped: unable to parse seed file. {0}", ex.Message);
                return result;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "peach-mutator-replay");
            Directory.CreateDirectory(tempRoot);

            var attempts = Math.Max(replayMaxAttempts, testIterations);

            for (int i = 1; i <= attempts; i++)
            {
                result.Attempts = i;

                if (i % 10 == 1 || i <= 3)
                {
                    lock (consoleLock)
                    {
                        WriteColored($"[REPLAY] Attempt {i}/{attempts} for {mutatorName}\n", ConsoleColor.Cyan);
                    }
                }

                var rootClone = ObjectCopier.Clone(replayRoot) as DM;
                rootClone.dom = dom;

                var elem = rootClone.find(sample.ElementFullName);
                if (elem == null)
                {
                    result.ReplayMessage = "Replay failed: target element not found in cloned model.";
                    return result;
                }

                if (!(Activator.CreateInstance(mutatorType, new object[] { elem }) is LLMMutator mutator))
                {
                    if (expectedStatus == "ERROR")
                    {
                        result.Reproduced = true;
                        result.ReplayMessage = "Replay reproduced mutator creation error.";
                        lock (consoleLock)
                        {
                            WriteColored($"[REPLAY] Reproduced ERROR for {mutatorName} at attempt {i}\n", ConsoleColor.Green);
                        }
                        return result;
                    }

                    continue;
                }

                mutator.context = new MockStrategy();
                BitwiseStream v = null;

                try
                {
                    mutator.randomMutation(elem);
                    v = rootClone.Value;
                }
                catch (Exception ex)
                {
                    if (expectedStatus == "ERROR")
                    {
                        var mutatedFile = BuildReplayTempFilePath(tempRoot, mutatorName, sample.DataFile, i);
                        TryWriteMutatedFile(v, mutatedFile);

                        result.Reproduced = true;
                        result.MutatedFilePath = mutatedFile;
                        result.ReplayMessage = string.Format("Replay reproduced mutation error: {0}", ex.Message);
                        lock (consoleLock)
                        {
                            WriteColored($"[REPLAY] Reproduced FAIL for {mutatorName} at attempt {i}\n", ConsoleColor.Green);
                        }
                        FillCrackDetails(result, sample.DataFile, mutatedFile);
                        return result;
                    }

                    continue;
                }

                var mutatedPath = BuildReplayTempFilePath(tempRoot, mutatorName, sample.DataFile, i);
                TryWriteMutatedFile(v, mutatedPath);

                var cracker = new DataCracker();
                try
                {
                    var dmClone = ObjectCopier.Clone(originalDataModel);
                    dmClone.dom = dom;
                    cracker.CrackData(dmClone, new BitStream(v));
                }
                catch (Exception ex)
                {
                    if (expectedStatus == "FAIL")
                    {
                        result.Reproduced = true;
                        result.MutatedFilePath = mutatedPath;
                        result.ReplayMessage = string.Format("Replay reproduced re-crack failure: {0}", ex.Message);
                        lock (consoleLock)
                        {
                            WriteColored($"[REPLAY] Reproduced FAIL for {mutatorName} at attempt {i}\n", ConsoleColor.Green);
                        }
                        FillCrackDetails(result, sample.DataFile, mutatedPath);
                        return result;
                    }
                }
            }

            lock (consoleLock)
            {
                if (result.Reproduced)
                {
                    WriteColored($"[REPLAY] REPRODUCED {mutatorName} ({expectedStatus}) in {result.Attempts} attempts\n", ConsoleColor.Green);
                }
                else
                {
                    WriteColored($"[REPLAY] FAILED to reproduce {mutatorName} ({expectedStatus}) after {result.Attempts} attempts\n", ConsoleColor.Yellow);
                }
            }

            result.ReplayMessage = "Replay could not reproduce the same outcome within max attempts.";
            return result;
        }

        static string BuildReplayTempFilePath(string tempRoot, string mutatorName, string seedFilePath, int attempt)
        {
            var safeMutatorName = SanitizeFileName(mutatorName);
            var seedFileName = SanitizeFileName(Path.GetFileName(seedFilePath));
            return Path.Combine(tempRoot, string.Format("{0}_{1}_attempt{2:000}.raw", safeMutatorName, seedFileName, attempt));
        }

        static void TryWriteMutatedFile(BitwiseStream data, string filePath)
        {
            if (data == null)
                return;
            var position = data.PositionBits;
            try
            {
                data.SeekBits(0, SeekOrigin.Begin);
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    data.CopyTo(fs);
                }
            }
            finally
            {
                data.SeekBits(position, SeekOrigin.Begin);
            }
        }

        static void FillCrackDetails(ReplayResult result, string originalFilePath, string mutatedFilePath)
        {
            var original = RunPittoolCrack(originalFilePath);
            result.PittoolCommandOriginal = original.Command;
            result.OriginalCrackOutput = original.Output;

            if (!string.IsNullOrEmpty(mutatedFilePath) && File.Exists(mutatedFilePath))
            {
                var mutated = RunPittoolCrack(mutatedFilePath);
                result.PittoolCommandMutated = mutated.Command;
                result.MutatedCrackOutput = mutated.Output;
            }
        }

        static CrackCommandResult RunPittoolCrack(string sampleFilePath)
        {
            var commandText = string.Format("./pittool crack -v \"{0}\" \"{1}\" \"{2}\"", pitFilePath, dataModelName, sampleFilePath);

            var psi = new ProcessStartInfo
            {
                FileName = "./pittool",
                Arguments = string.Format("crack -v \"{0}\" \"{1}\" \"{2}\"", pitFilePath, dataModelName, sampleFilePath),
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    var output = new StringBuilder();
                    output.AppendLine(string.Format("ExitCode: {0}", process.ExitCode));
                    if (!string.IsNullOrEmpty(stdout))
                    if (!string.IsNullOrEmpty(stderr))
                    {
                        output.AppendLine(stderr);
                    }
                    return new CrackCommandResult
                    {
                        Command = commandText,
                        Output = output.ToString()
                    };
                }
            }
            catch (Exception ex)
            {
                return new CrackCommandResult
                {
                    Command = commandText,
                    Output = string.Format("Failed to run pittool crack: {0}", ex)
                };
            }
        }

        static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        static void WriteColored(string message, ConsoleColor color)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(message);
            Console.ForegroundColor = originalColor;
        }

        static void FindMutators()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var allTypes = new List<Type>();

            foreach (var assembly in assemblies)
            {
                try
                {
                    allTypes.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    allTypes.AddRange(ex.Types.Where(t => t != null));
                }
                catch
                {
                    // Ignore assemblies that cannot be reflected.
                }
            }

            var m = allTypes
                .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(LLMMutator)));

            mutators.AddRange(m);
            var unique = mutators.Distinct().ToList();
            mutators.Clear();
            mutators.AddRange(unique);
            mutators.Sort((a, b) => a.Name.CompareTo(b.Name));
        }

        static DataElement ParseData(string pitFile, string dataFile, string modelName)
        {
            var parser = new ProPitParser(null, null, pitFile);

            var parserArgs = new Dictionary<string, object>();

            dom = parser.asParser(parserArgs, pitFile);

            Console.WriteLine("DOM parsed successfully");

            if (!dom.dataModels.Contains(modelName))
            {
                throw new Exception($"Failed to find DataModel '{modelName}' in Pit File.");
            }
            DM dataModel = dom.dataModels[modelName];

            if (!File.Exists(dataFile))
            {
                throw new FileNotFoundException($"Failed to find file : {dataFile}");
            }

            byte[] fileBytes = File.ReadAllBytes(dataFile);
            var dataStream = new BitStream(fileBytes);

            var crackedModel = ObjectCopier.Clone(dataModel);
            crackedModel.dom = dom;

            originalDataModel = dataModel;
            originalDataModel.dom = dom;

            var cracker = new DataCracker();

            cracker.CrackData(crackedModel, dataStream);

            return crackedModel;
        }
    }
}