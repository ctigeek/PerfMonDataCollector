using System;

namespace DataCollector
{
    enum ValueType
    {
        Raw,
        NextValue,
        NextSample
    }
    class CounterConfig
    {
        public string Server { get; set; }
        public string Category { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public string PrettyName { get; set; }
        public ValueType ValueType { get; set; }
        public TimeSpan CheckFrequency { get; set; }
        public string OutputFile { get; set; }

        public override string ToString()
        {
            var name = Server + "\\" + Category + "\\" + CounterName;
            if (!string.IsNullOrEmpty(InstanceName))
            {
                name += "\\" + InstanceName;
            }
            return name;
        }
    }
}
