using System;
using System.Collections.Generic;
using System.Text;

namespace deltalag
{
    public class Config
    {
        public int Cycles; // Total Iterations 
        public int Slack; // Maximum allowed distance between node counters
        public int Threads; // Threads to use
        public PartitionStrategy Strategy; // Node thread partition 
        public bool InjectNodes; // k-ary tree, inject nodes to break up large fan in/out
        public int K; // K size of tree
        public int MaxFanIn; // Inject tree when nodes have more parents than this 
        public int MaxFanOut; // Inject tree when nodes have more children than this
        public bool SeparateRootThread; // Isolate a thread on the root for less latency when starting work. 
        public bool SeparateSinkThread; // Isolate a thread on the sink nodes for less latency when finishing work.
        public bool ThreadAffinity; // Pin threads to cores for improved locality and latency

        //wip
        //public WaitStrategy WaitStrategy; // How should Threads wait
        //public bool TrashTaskWhenStalled; // if partition strategy allows diffrent threads to touch nodes this can decrease the likelyhood of progress degregation by throwing out tasks that cant be published(released) after a certain point
        //public int SpinCountBeforeStall; // how long before trashing
        

    }

}
