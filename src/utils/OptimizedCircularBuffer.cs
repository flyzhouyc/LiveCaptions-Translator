using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 优化的循环缓冲区，避免频繁求模运算，提高性能
    /// </summary>
    public class OptimizedCircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _start;
        private int _count;
        private readonly int _capacityMask;
        
        public int Count => _count;
        public int Capacity => _buffer.Length;
        
        /// <summary>
        /// 创建一个新的优化循环缓冲区
        /// </summary>
        /// <param name="capacity">缓冲区容量，会被调整为2的幂</param>
        public OptimizedCircularBuffer(int capacity)
        {
            // 确保容量是2的幂，这样可以用位操作代替求模
            int powerOfTwo = 1;
            while (powerOfTwo < capacity)
                powerOfTwo <<= 1;
                
            _buffer = new T[powerOfTwo];
            _capacityMask = powerOfTwo - 1; // 例如：容量为8，掩码为7 (0b111)
            _start = 0;
            _count = 0;
        }
        
        /// <summary>
        /// 添加元素到缓冲区
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            if (_count == _buffer.Length)
            {
                // 缓冲区已满，覆盖最早的项
                _buffer[_start] = item;
                _start = (_start + 1) & _capacityMask; // 使用位操作代替求模
            }
            else
            {
                // 缓冲区未满，添加到末尾
                _buffer[(_start + _count) & _capacityMask] = item;
                _count++;
            }
        }
        
        /// <summary>
        /// 获取缓冲区中的所有元素
        /// </summary>
        public IEnumerable<T> GetItems()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(_start + i) & _capacityMask];
            }
        }
        
        /// <summary>
        /// 获取指定索引的元素
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetItem(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
                
            return _buffer[(_start + index) & _capacityMask];
        }
        
        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Clear()
        {
            _start = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
        
        /// <summary>
        /// 重置缓冲区，可选择保留一个元素
        /// </summary>
        public void Reset(T itemToKeep = default)
        {
            Clear();
            if (itemToKeep != null && !itemToKeep.Equals(default(T)))
            {
                Add(itemToKeep);
            }
        }
    }
}