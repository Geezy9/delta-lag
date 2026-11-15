using System;
using System.Collections.Generic;
using System.Text;

namespace deltalag
{
    public class Config
    {
        public int Cycles;
        public int Slack;
        public int Threads;
        public PartitionStrategy Strategy;
        public bool InjectNodes; // k-ary tree, inject nodes to break up large fan in/out
        public int K;
        public int MaxFanIn;
        public int MaxFanOut;
        public bool SeperateRootThread;

    }

}
