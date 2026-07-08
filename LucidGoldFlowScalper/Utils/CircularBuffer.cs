using System;

namespace LucidGoldFlowScalper.Utils
{
    /// <summary>
    /// A generic circular buffer backed by a pre-allocated fixed array.
    /// Zero allocations when pushing new items.
    /// </summary>
    /// <typeparam name="T">Type of elements to store.</typeparam>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0");
            _buffer = new T[capacity];
            _head = 0;
            _count = 0;
        }

        public void Push(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        /// <summary>
        /// Gets the item at the specified offset from the most recently added item.
        /// index 0 = most recent, index 1 = second most recent, etc.
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException("Index out of range of valid buffer items.");
                
                int actualIndex = (_head - 1 - index);
                if (actualIndex < 0)
                    actualIndex += Capacity;
                    
                return _buffer[actualIndex];
            }
        }

        public T[] ToArray()
        {
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
            {
                result[i] = this[i];
            }
            return result;
        }
    }
}
