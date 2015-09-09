using System;
using System.Linq;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Timers;
using log4net;
using log4net.Core;
using Nest;
using Newtonsoft.Json.Linq;
using Timer = System.Timers.Timer;

namespace DataCollector
{
    internal class ActionManager : IDisposable
    {
        private static ILog log = LogManager.GetLogger(typeof(ActionManager));
        private static object lockObject = new object();
        private readonly bool debug;
        private readonly bool writeToElasticsearch;
        private readonly bool writeToFile;
        private Timer timer;
        public bool Running { get; private set; }
        private Dictionary<string, DateTime> lastTimeDataCollected;
        private Dictionary<string, PerformanceCounter> performanceCounters;
        private Queue<Tuple<string, string>> resultsCache;

        public ActionManager()
        {
            writeToElasticsearch = ConfigurationManager.AppSettings["SaveToElasticsearch"].ToLower() == "true";
            writeToFile = ConfigurationManager.AppSettings["SaveToFile"].ToLower() == "true";
            Running = false;
            timer = new Timer(200);
            timer.Elapsed += timer_Elapsed;
            lastTimeDataCollected = new Dictionary<string, DateTime>();
            performanceCounters = new Dictionary<string, PerformanceCounter>();
            resultsCache = new Queue<Tuple<string, string>>();
            debug = ((log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository()).Root.Level.Value <= Level.Debug.Value;
        }

        public void Dispose()
        {
            if (Running)
            {
                Stop();
            }
            foreach (var counter in performanceCounters.Values)
            {
                counter.Dispose();
            }
        }

        public void Start()
        {
            if (Running) throw new InvalidOperationException("ActionManager already running.");

            if (writeToElasticsearch)
            {
                CreateIndex();
            }
            
            timer.Start();
            Running = true;
        }

        public void Stop()
        {
            lock (lockObject)
            {
                Running = false;
                if (timer.Enabled) timer.Stop();
            }
        }

        private void timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (Monitor.TryEnter(lockObject))
            {
                try
                {
                    if (Running)
                    {
                        var countersToCheck = GetCounterConfigs();
                        var counterResults = new List<CounterResult>();
                        foreach (var counterConfig in countersToCheck)
                        {
                            if (DateTime.Now.Subtract(GetLastTimeDataWasCollected(counterConfig.ToString())) > counterConfig.CheckFrequency)
                            {
                                try
                                {
                                    var result = CheckAndRecordData(counterConfig);
                                    counterResults.Add(result);
                                }
                                catch (InvalidOperationException)
                                {
                                }
                                RefreshLastTimeDataWasCollected(counterConfig.ToString());
                            }
                        }
                        RecordResults(counterResults);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex.ToString());
                }
                finally
                {
                    Monitor.Exit(lockObject);
                }
            }
        }

        private CounterResult CheckAndRecordData(CounterConfig counterConfig)
        {
            var counter = GetPerformanceCounter(counterConfig);
            var value = (counterConfig.ValueType == ValueType.Raw)
                ? counter.RawValue
                : (long) Math.Round(counter.NextValue());

            var counterResult = new CounterResult
            {
                DateTime = DateTime.Now,
                Counter = counterConfig.PrettyName,
                Instance = counterConfig.InstanceName,
                Machine = counterConfig.Server,
                Value = value,
                CounterConfig = counterConfig
            };
            return counterResult;
        }

        private void RecordResults(List<CounterResult> counterResults)
        {
            if (!counterResults.Any()) return;
            
            foreach (var result in counterResults)
            {
                var dataRow = string.Format("{0}; {1} {2}; {3}", result.CounterConfig, result.DateTime.ToShortDateString(), result.DateTime.ToLongTimeString(), result.Value);
                if (debug)
                {
                    log.Debug(dataRow);
                }

                if (writeToFile)
                {
                    resultsCache.Enqueue(new Tuple<string, string>(result.CounterConfig.OutputFile, dataRow));
                    if (resultsCache.Count > 50)
                    {
                        WriteResultsToFile();
                    }
                }
            }

            if (writeToElasticsearch)
            {
                IndexCounterResults(counterResults);
            }
        }

        private void IndexCounterResults(List<CounterResult> counterResults)
        {
            try
            {
                var ttl = ConfigurationManager.AppSettings["ElasticsearchTTL"] ?? "4w";
                var client = GetElasticsearchClient();
                var result = client.IndexMany(counterResults);  //I think I need to use the raw bulk method to enable TTL.
                //var result2 = client.Index(counterResults[0], d => d.Ttl(ttl));
                if (result.Errors)
                {
                    throw new ApplicationException("Could not save to elasticsearch: " + result.ServerError.Error);
                }
            }
            catch (Exception ex)
            {
                log.ErrorFormat("Error accessing elasticsearch:\r\n{0}", ex);
                elasticClient = null;
            }
        }

        private ElasticClient elasticClient;
        private ElasticClient GetElasticsearchClient()
        {
            if (elasticClient == null)
            {
                var index = ConfigurationManager.AppSettings["ElasticsearchIndex"].ToLower();
                var node = new Uri(ConfigurationManager.AppSettings["ElasticsearchUrl"]);
                var settings = new ConnectionSettings(node,index);
                elasticClient = new ElasticClient(settings);
            }
            return elasticClient;
        }

        private void CreateIndex()
        {
            var indexName = ConfigurationManager.AppSettings["ElasticsearchIndex"].ToLower();
            var client = GetElasticsearchClient();
            var existsResponse = client.IndexExists(indexName);
            if (!existsResponse.Exists)
            {
                log.WarnFormat("Elasticsearch index {0} doesn't exist. Creating....", indexName);
                var r2 = client.CreateIndex(indexName, c => c
                    .AddMapping<CounterResult>(epr => epr
                        .MapFromAttributes()
                        .TimestampField(t => t
                            .Enabled(true)
                            .Path(o => o.DateTime)
                        )));
            }
        }

        //TODO: extract this into a separate class...

        private DateTime lastFileWriteDate = DateTime.Now.Date;

        private void WriteResultsToFile()
        {
            log.DebugFormat("Writing {0} rows to file(s).", resultsCache.Count);
            var writers = new Dictionary<string, StreamWriter>();
            try
            {
                while (resultsCache.Count > 0)
                {
                    var tuple = resultsCache.Dequeue();
                    if (!writers.ContainsKey(tuple.Item1))
                    {
                        writers[tuple.Item1] = new StreamWriter(tuple.Item1, true, Encoding.UTF8);
                    }
                    writers[tuple.Item1].WriteLine(tuple.Item2);
                } 
            }
            finally
            {
                foreach (var writer in writers.Values)
                {
                    writer.Flush();
                    writer.Dispose();
                }
                if (lastFileWriteDate != DateTime.Now.Date)
                {
                    BackupFiles();
                }
                resultsCache.Clear();
                lastFileWriteDate = DateTime.Now.Date;
            }
        }

        private void BackupFiles()
        {
            var yesterday = DateTime.Now.Date.AddDays(-1);
            foreach (var filePath in counterConfigCache.Select(ccc => ccc.OutputFile).Distinct())
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var newFilePath = filePath.Replace(".txt", ".") + yesterday.ToString("yy-MM-dd") + ".txt";
                        File.Move(filePath, newFilePath);
                    }
                }
                catch (Exception ex)
                {
                    log.ErrorFormat("Error while trying to backup file {0}. \r\n {1}",filePath, ex);
                }
            }
        }

        private PerformanceCounter GetPerformanceCounter(CounterConfig counterConfig)
        {
            if (!performanceCounters.ContainsKey(counterConfig.ToString()))
            {
                var counter = new PerformanceCounter(counterConfig.Category, counterConfig.CounterName, counterConfig.InstanceName, counterConfig.Server);
                performanceCounters.Add(counterConfig.ToString(), counter);
                return counter;
            }
            return performanceCounters[counterConfig.ToString()];
        }

        private DateTime GetLastTimeDataWasCollected(string counter)
        {
            if (!lastTimeDataCollected.ContainsKey(counter))
            {
                lastTimeDataCollected[counter] = DateTime.MinValue;
            }
            return lastTimeDataCollected[counter];
        }

        private void RefreshLastTimeDataWasCollected(string counter)
        {
            lastTimeDataCollected[counter] = DateTime.Now;
        }

        private DateTime lastFileLoad = DateTime.MinValue;
        private CounterConfig[] counterConfigCache;

        private CounterConfig[] GetCounterConfigs()
        {
            if (DateTime.Now.Subtract(lastFileLoad) > TimeSpan.FromSeconds(30))
            {
                LoadDataFromFile();
                lastFileLoad = DateTime.Now;
            }
            return counterConfigCache;
        }

        private void LoadDataFromFile()
        {
            var executingPath = Assembly.GetExecutingAssembly().Location.Replace("DataCollector.exe", "");
            var configFilePath = ConfigurationManager.AppSettings["PerformanceCounterConfigFile"];
            var fullPath = Path.Combine(executingPath, configFilePath);
            string filebody;
            using (var reader = new StreamReader(fullPath))
            {
                filebody = reader.ReadToEnd();
            }
            var jobject = JObject.Parse(filebody);
            var configs = jobject["counters"].ToObject<CounterConfig[]>();

            counterConfigCache = configs;
        }
    }
}
