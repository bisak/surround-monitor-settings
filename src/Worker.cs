using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MonitorControl
{
    // Background thread for whole-system work (scanning). Monitor I/O runs on Lanes.
    sealed class DdcWorker : IDisposable
    {
        readonly Queue<Action> queue = new Queue<Action>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        volatile bool stopping;

        public DdcWorker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "DDC scan" };
            thread.Start();
        }

        public void Run(Action action)
        {
            lock (queue) queue.Enqueue(action);
            signal.Set();
        }

        void Loop()
        {
            while (!stopping)
            {
                signal.WaitOne();
                while (!stopping)
                {
                    Action a;
                    lock (queue) { if (queue.Count == 0) break; a = queue.Dequeue(); }
                    try { a(); }
                    catch (Exception ex) { Log.Write("Worker: " + ex); }
                }
            }
        }

        public void Dispose() { stopping = true; signal.Set(); }
    }

    // One I/O thread per monitor. Each monitor has its own DDC bus, so the three lanes
    // run in parallel. Within a lane:
    //  - slider drags are coalesced: only the latest pending value per setting is sent;
    //  - while more changes are queued, writes aren't read back; once the lane goes
    //    idle, the final value of each setting is read back once to confirm it took.
    sealed class Lane : IDisposable
    {
        sealed class Job
        {
            public byte Code;
            public int Value;
            public bool Verify;
            public bool Coalesce;
            public Action Action;
        }

        readonly Monitor monitor;
        readonly List<Job> queue = new List<Job>();
        readonly Dictionary<byte, int> unconfirmed = new Dictionary<byte, int>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool stopping;

        // Raised on the lane's thread.
        public event Action<Monitor, byte, int> WriteFailed;

        public Lane(Monitor monitor)
        {
            this.monitor = monitor;
            thread = new Thread(Loop) { IsBackground = true, Name = "DDC " + monitor.Port.Location };
            thread.Start();
        }

        public void Write(byte code, int value, bool verify, bool coalesce)
        {
            lock (queue)
            {
                if (coalesce)
                    foreach (var j in queue)
                        if (j.Action == null && j.Coalesce && j.Code == code) { j.Value = value; j.Verify |= verify; return; }
                queue.Add(new Job { Code = code, Value = value, Verify = verify, Coalesce = coalesce });
            }
            signal.Set();
        }

        public void Post(Action action)
        {
            lock (queue) queue.Add(new Job { Action = action });
            signal.Set();
        }

        bool Idle { get { lock (queue) return queue.Count == 0; } }

        void Loop()
        {
            while (!stopping)
            {
                signal.WaitOne();
                while (!stopping)
                {
                    Job j;
                    lock (queue)
                    {
                        if (queue.Count == 0) break;
                        j = queue[0];
                        queue.RemoveAt(0);
                    }
                    try
                    {
                        if (j.Action != null) { ConfirmPending(); j.Action(); }
                        else Send(j);
                        if (Idle) ConfirmPending();
                    }
                    catch (Exception ex) { Log.Write("Lane " + monitor.Port.Location + ": " + ex); }
                }
            }
        }

        void Send(Job j)
        {
            // While more changes are waiting, write without reading back; confirm at the end.
            if (j.Verify && !Idle)
            {
                bool sent = false;
                for (int i = 0; i < 3 && !sent; i++) { sent = monitor.Port.Write(j.Code, j.Value); if (!sent) Thread.Sleep(60); }
                if (sent) { monitor.SetCached(j.Code, j.Value); unconfirmed[j.Code] = j.Value; return; }
                Fail(j.Code, j.Value);
                return;
            }
            unconfirmed.Remove(j.Code);
            if (!monitor.WriteVerified(j.Code, j.Value, j.Verify)) Fail(j.Code, j.Value);
        }

        void ConfirmPending()
        {
            foreach (var kv in unconfirmed.ToList())
            {
                unconfirmed.Remove(kv.Key);
                int cur, max;
                if (monitor.ReadRetry(kv.Key, out cur, out max, 2) && cur == kv.Value) continue;
                if (!monitor.WriteVerified(kv.Key, kv.Value, true)) Fail(kv.Key, kv.Value);
            }
        }

        void Fail(byte code, int value)
        {
            var h = WriteFailed;
            if (h != null) h(monitor, code, value);
        }

        // Stops the thread after the current command; waits briefly so the port can be closed.
        public void Dispose()
        {
            Stop();
            if (Thread.CurrentThread != thread) thread.Join(2000);
        }

        // Stops without waiting (on exit, where the process ends anyway).
        public void Stop()
        {
            stopping = true;
            signal.Set();
        }

        // Runs perMonitor on each monitor's lane in parallel, then done() once all have finished.
        public static void ForEach(IList<Monitor> monitors, Action<Monitor> perMonitor, Action done)
        {
            var lanes = monitors.Where(m => m.Lane != null).ToList();
            if (lanes.Count == 0) { done(); return; }
            int remaining = lanes.Count;
            foreach (var m in lanes)
            {
                var mon = m;
                mon.Lane.Post(() =>
                {
                    try { if (perMonitor != null) perMonitor(mon); }
                    finally { if (Interlocked.Decrement(ref remaining) == 0) done(); }
                });
            }
        }
    }
}
