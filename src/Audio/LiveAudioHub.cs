using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lumen.Audio
{
    internal sealed class LiveAudioHub
    {
        private readonly object _sync = new object();
        private readonly List<Subscriber> _subscribers = new List<Subscriber>();

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public bool IsConfigured { get; private set; }

        public LiveAudioHub()
        {
            SampleRate = 48000;
            Channels = 2;
            BitsPerSample = 16;
        }

        public int SubscriberCount
        {
            get
            {
                lock (_sync)
                {
                    return _subscribers.Count;
                }
            }
        }

        public void Reset(int sampleRate, int channels)
        {
            lock (_sync)
            {
                foreach (Subscriber subscriber in _subscribers.ToArray())
                {
                    subscriber.Complete();
                }
                _subscribers.Clear();
                SampleRate = sampleRate > 0 ? sampleRate : 48000;
                Channels = channels > 0 ? channels : 2;
                BitsPerSample = 16;
                IsConfigured = true;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                foreach (Subscriber subscriber in _subscribers.ToArray())
                {
                    subscriber.Complete();
                }
                _subscribers.Clear();
                IsConfigured = false;
            }
        }

        public Subscriber Subscribe()
        {
            Subscriber subscriber = new Subscriber();
            lock (_sync)
            {
                if (!IsConfigured)
                {
                    subscriber.Complete();
                    return subscriber;
                }
                _subscribers.Add(subscriber);
            }
            return subscriber;
        }

        public void Unsubscribe(Subscriber subscriber)
        {
            if (subscriber == null) return;
            lock (_sync)
            {
                _subscribers.Remove(subscriber);
            }
            subscriber.Complete();
        }

        public void Publish(byte[] pcm, int count)
        {
            if (pcm == null || count <= 0) return;
            Subscriber[] snapshot;
            lock (_sync)
            {
                if (_subscribers.Count == 0) return;
                snapshot = _subscribers.ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].Enqueue(pcm);
            }
        }

        public byte[] CreateWavHeader()
        {
            int rate = SampleRate;
            short channels = (short)Channels;
            short bits = (short)BitsPerSample;
            short blockAlign = (short)(channels * (bits / 8));
            int byteRate = rate * blockAlign;
            uint dataSize = 0x7FFFF000;

            using (MemoryStream stream = new MemoryStream(44))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(dataSize + 36);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(rate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write(bits);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                writer.Flush();
                return stream.ToArray();
            }
        }

        internal sealed class Subscriber : IDisposable
        {
            private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(16);

            public bool TryTake(out byte[] data, int millisecondsTimeout)
            {
                return _queue.TryTake(out data, millisecondsTimeout);
            }

            public void Enqueue(byte[] data)
            {
                if (data == null) return;
                if (_queue.IsAddingCompleted) return;
                try
                {
                    if (!_queue.TryAdd(data))
                    {
                        byte[] dropped;
                        _queue.TryTake(out dropped);
                        _queue.TryAdd(data);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The subscriber was completed between the checks.
                }
            }

            public void Complete()
            {
                try
                {
                    if (!_queue.IsAddingCompleted)
                    {
                        _queue.CompleteAdding();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
            {
                Complete();
            }
        }
    }
}
