using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ShangCloud.MMO.Threading
{
    public class MmoMessageQueue
    {
        private readonly ConcurrentQueue<MmoMessage> _queue = new ConcurrentQueue<MmoMessage>();

        public void Enqueue(MmoMessage msg)
        {
            _queue.Enqueue(msg);
        }

        public List<MmoMessage> DrainAll()
        {
            var result = new List<MmoMessage>();
            while (_queue.TryDequeue(out var msg))
            {
                result.Add(msg);
            }
            return result;
        }

        public bool IsEmpty => _queue.IsEmpty;
    }
}
