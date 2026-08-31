using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peach.Core;
using Peach.Core.Cracker;
using Peach.Core.Dom;
using Peach.Core.IO;
using Peach.Pro.Core;
using Peach.LLM.Core;
using NLog;

namespace Peach.LLM.Validations.DataModel
{
    public class Program
    {
        private static Peach.Core.Dom.DataModel _originalDataModel;

        private static Dom _dom;

        private static string _pitFile;
        private static string _dataModelName;
        private static string _seedDir;
        private static int _count;
        private static int _total;

        private static int _passed;

        private static readonly NLog.Logger Logger = LogManager.GetCurrentClassLogger();

        public static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    "Usage: Peach.LLM.Validations.DataModel.exe <pit_file> <data_model_name> <seed_dir>");
                Environment.Exit(1);
            }

            _pitFile = args[0];
            _dataModelName = args[1];
            _seedDir = args[2];

            var dataFiles = Directory.GetFiles(_seedDir);
            _total = dataFiles.Length;

            foreach (var dataFile in dataFiles)
            {
                _count++;
                Test(dataFile);
            }

            var status = _passed == _total ? "PASS" : "FAIL";
            Console.WriteLine($"[{status}] {_passed:000}/{_total:000} tests passed.");
        }

        private static DataElement ParseData(string dataFile)
        {
            ClassLoader.Initialize("./Plugins");

            var parser = new ProPitParser(null, null, _pitFile);

            var parserArgs = new Dictionary<string, object>();

            _dom = parser.asParser(parserArgs, _pitFile);

            if (!_dom.dataModels.Contains(_dataModelName))
            {
                throw new Exception($"Failed to find DataModel '{_dataModelName}' in Pit File.");
            }

            var dataModel = _dom.dataModels[_dataModelName];

            if (!File.Exists(dataFile))
            {
                throw new FileNotFoundException($"Failed to find file : {dataFile}");
            }

            var fileBytes = File.ReadAllBytes(dataFile);
            var dataStream = new BitStream(fileBytes);

            var crackedModel = ObjectCopier.Clone(dataModel);
            crackedModel.dom = _dom;

            _originalDataModel = dataModel;
            _originalDataModel.dom = _dom;

            var cracker = new DataCracker();

            cracker.CrackData(crackedModel, dataStream);

            return crackedModel;
        }

        private static void Test(string dataFile)
        {
            var name = dataFile.Split(Path.DirectorySeparatorChar).Last();
            LogManager.Configuration.Variables["logFileName"] = name + ".log";
            LogManager.ReconfigExistingLoggers();
            
            DataElement dm;
            var fileBytes = File.ReadAllBytes(dataFile);
            try
            {
                dm = ParseData(dataFile);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[{_count:000}/{_total:000}][FAIL] {name}");
                Logger.Info($"Failed to parse file '{name}': {e.Message}");
                Logger.Info($"Bytes: \n{BytesToHex(fileBytes)}");
                return;
            }

            var parsedBytes = dm.Bytes();

            if (!DiffBytes(parsedBytes, fileBytes))
            {
                Console.WriteLine($"[{_count:000}/{_total:000}][FAIL] {name}");
                Logger.Info(
                    $"Parsed bytes do not match original file for '{name}'");
                Logger.Info($"Original Bytes: \n{BytesToHex(fileBytes)}");
                Logger.Info($"Parsed   Bytes: \n{BytesToHex(parsedBytes)}");
            }
            else
            {
                Console.WriteLine(
                    $"[{_count:000}/{_total:000}][PASS] {name}");
                File.Delete("/logs/" + name + ".log");
                _passed++;
            }
        }

        private static bool DiffBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            return !a.Where((t, i) => t != b[i]).Any();
        }

        private static string BytesToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", " ");
        }
    }
}