using System;
using System.Collections;
using System.Collections.Generic;

namespace Game.Core.Player
{
    public sealed class RingBuffer<T> : IEnumerable<T>
    {
        readonly T[] _data;
        int _start;
        int _count;

        public int Count => _count;
        public int Capacity => _data.Length;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _data = new T[capacity];
            _start = 0;
            _count = 0;
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        public void Enqueue(in T item)
        {
            if (_count < _data.Length)
            {
                _data[(_start + _count) % _data.Length] = item;
                _count++;
            }
            else
            {
                // overwrite oldest
                _data[_start] = item;
                _start = (_start + 1) % _data.Length;
            }
        }

        public bool TryPeekOldest(out T item)
        {
            if (_count == 0)
            {
                item = default;
                return false;
            }
            item = _data[_start];
            return true;
        }

        public bool TryDequeueOldest(out T item)
        {
            if (_count == 0)
            {
                item = default;
                return false;
            }
            item = _data[_start];
            _start = (_start + 1) % _data.Length;
            _count--;
            return true;
        }

        public T this[int i]
        {
            get
            {
                if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
                return _data[(_start + i) % _data.Length];
            }
            set
            {
                if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
                _data[(_start + i) % _data.Length] = value;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
                yield return _data[(_start + i) % _data.Length];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Remove from front while predicate holds; return how many removed
        public int RemoveFromFrontWhile(Func<T, bool> predicate)
        {
            int removed = 0;
            while (_count > 0 && predicate(_data[_start]))
            {
                _start = (_start + 1) % _data.Length;
                _count--;
                removed++;
            }
            return removed;
        }
    }
}

