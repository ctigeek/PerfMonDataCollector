using System;

namespace DataCollector
{
    class CounterResult
    {
        public DateTime DateTime { get; set; }
        public string Machine { get; set; }
        public string Counter { get; set; }
        public string Instance { get; set; }
        public long Value { get; set; }
        
        public CounterConfig CounterConfig { get; set; }
        public bool ShouldSerializeCounterConfig()
        {
            return false;
        }
    }
}
